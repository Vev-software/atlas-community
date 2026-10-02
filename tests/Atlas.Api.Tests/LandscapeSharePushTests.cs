using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vev.Atlas.Api.Sharing;
using Vev.Atlas.Contracts;
using Vev.Atlas.Domain.Sharing;
using Vev.Atlas.Domain.Sharing.Push;
using Vev.Atlas.Fabric.Dev;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Outbound push of the landscape digest to connected consumers (atlas#176): activation with a one-time code, signed pushes with a rising
/// sequence, stopping when either side revokes or suspends, retry with backoff, nothing sent while nothing is connected, one audit record
/// per push, and no payload content in logs.
/// </summary>
public sealed class LandscapeSharePushTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Destination = "https://consumer.example/api/atlas";

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeConsumerClient : IShareConsumerClient
    {
        public List<(string Url, string Code, string KeyId, string PublicKey)> Activations { get; } = [];
        public List<(string Url, string Credential, byte[] Body)> Pushes { get; } = [];
        public List<(string Url, string Credential)> Revocations { get; } = [];
        public Func<ConsumerActivationResult> Activation { get; set; } = () => new(true, "enroll-1", "secret-credential-1", null, null, null);
        public Func<PushResult> Push { get; set; } = () => new(PushOutcome.Accepted);
        public bool RevokeFails { get; set; }

        public Task<ConsumerActivationResult> ActivateAsync(string destinationUrl, string activationCode, string keyId, string publicKey, CancellationToken ct = default)
        {
            Activations.Add((destinationUrl, activationCode, keyId, publicKey));
            return Task.FromResult(Activation());
        }

        public Task<PushResult> PushAsync(string destinationUrl, string credential, byte[] signedDigestJson, CancellationToken ct = default)
        {
            Pushes.Add((destinationUrl, credential, signedDigestJson));
            return Task.FromResult(Push());
        }

        public Task RevokeAsync(string destinationUrl, string credential, CancellationToken ct = default)
        {
            Revocations.Add((destinationUrl, credential));
            return RevokeFails ? throw new HttpRequestException("down") : Task.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
        public void Dispose() { }
    }

    private sealed class Harness : IDisposable
    {
        public ManualTime Time { get; } = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        public FakeConsumerClient Client { get; } = new();
        public CapturingLogger Logs { get; } = new();
        public WebApplicationFactory<Program> Host { get; }

        // Its own host and database: a scheduling round looks at every tenant's consumers, so tests must not share a database.
        private readonly AtlasApiFactory _root = new();

        public Harness()
        {
            Host = _root.WithWebHostBuilder(b =>
            {
                // The timer is parked: the tests drive scheduling rounds themselves.
                b.UseSetting("Atlas:Share:Push:PollSeconds", "3600");
                b.UseSetting("Atlas:Share:Push:DebounceSeconds", "60");
                b.ConfigureLogging(l => l.AddProvider(Logs));
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(Time);
                    s.RemoveAll<IShareConsumerClient>();
                    s.AddSingleton<IShareConsumerClient>(Client);
                });
            });
        }

        public HttpClient Api(string tenant, string roles = "AtlasArchitect")
        {
            var client = Host.CreateClient();
            client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
            client.DefaultRequestHeaders.Add("X-Principal-Id", "admin");
            client.DefaultRequestHeaders.Add("X-Principal-Roles", roles);
            return client;
        }

        public Task RunRoundAsync() => Host.Services.GetServices<IHostedService>().OfType<LandscapePushBackgroundService>().Single().RunOnceAsync(CancellationToken.None);

        public InMemoryAuditSink Audit => Host.Services.GetRequiredService<InMemoryAuditSink>();

        public void Dispose()
        {
            Host.Dispose();
            _root.Dispose();
        }
    }

    private static async Task<JsonNode> ConnectAsync(HttpClient api, string url = Destination, string code = "AAAA-BBBB", string[]? kinds = null, string[]? tags = null)
    {
        var response = await api.PostAsJsonAsync("/api/v1/share/consumers", new { name = "Partner", destinationUrl = url, activationCode = code, kinds, tags }, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonNode> GetConsumerAsync(HttpClient api, string id) =>
        JsonNode.Parse(await api.GetStringAsync("/api/v1/share/consumers"))!.AsArray().Single(c => c!["id"]!.GetValue<string>() == id)!;

    private static async Task AddAppAsync(HttpClient api, string id, string name) =>
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsJsonAsync("/api/v1/assets",
            new Asset(id, AssetKind.Application, name, Lifecycle.Active, Application: new ApplicationDetails(Vendor: "Example Corp")), Json)).StatusCode);

    private static JsonNode Body(byte[] json) => JsonNode.Parse(json)!;

    private static bool SignatureVerifies(JsonNode signed)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(FromBase64Url(signed["signature"]!["publicKey"]!.GetValue<string>()), out _);
        return rsa.VerifyData(Jcs.CanonicalizeUtf8(signed["digest"]!), FromBase64Url(signed["signature"]!["value"]!.GetValue<string>()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static byte[] FromBase64Url(string text) =>
        Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=').Replace('-', '+').Replace('_', '/'));

    // ---- Authorization and activation ---------------------------------------------------------

    [Fact]
    public async Task A_read_only_customer_cannot_list_connect_or_push()
    {
        using var h = new Harness();
        var reader = h.Api("t-push-denied", "AtlasCustomer");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/share/consumers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/share/consumers", new { destinationUrl = Destination, activationCode = "X" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/v1/share/consumers/x/push", null)).StatusCode);
        Assert.Empty(h.Client.Activations);
    }

    [Fact]
    public async Task Connecting_redeems_the_code_stores_the_credential_and_never_shows_it()
    {
        using var h = new Harness();
        var api = h.Api("t-push-connect");

        var consumer = await ConnectAsync(api, Destination + "/", " AAAA-BBBB ", kinds: ["application"]);

        var activation = Assert.Single(h.Client.Activations);
        Assert.Equal((Destination, "AAAA-BBBB"), (activation.Url, activation.Code));
        Assert.StartsWith("atlas-digest-", activation.KeyId);
        Assert.False(string.IsNullOrWhiteSpace(activation.PublicKey));
        Assert.Equal(("Active", "enroll-1", Destination), (consumer["state"]!.GetValue<string>(), consumer["enrollmentId"]!.GetValue<string>(), consumer["destinationUrl"]!.GetValue<string>()));
        var listed = await api.GetStringAsync("/api/v1/share/consumers");
        Assert.DoesNotContain("secret-credential-1", listed);
        Assert.DoesNotContain("credential", listed, StringComparison.OrdinalIgnoreCase);
        var audit = Assert.Single(h.Audit.Events, e => e.Tenant.TenantId == "t-push-connect" && e.Action == "fabric.sharing.enrollment.activate");
        Assert.Equal("admin", audit.Actor.PrincipalId);
    }

    [Theory]
    [InlineData("sharing_activation_code_invalid", "did not accept that code")]
    [InlineData("sharing_activation_code_expired", "expired")]
    [InlineData("sharing_activation_code_used", "already used")]
    public async Task A_code_the_consumer_refuses_is_explained_and_nothing_is_connected(string reason, string expected)
    {
        using var h = new Harness();
        h.Client.Activation = () => new ConsumerActivationResult(false, null, null, null, reason, null);
        var api = h.Api("t-push-code-" + reason);

        var response = await api.PostAsJsonAsync("/api/v1/share/consumers", new { destinationUrl = Destination, activationCode = "AAAA" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal("[]", (await api.GetStringAsync("/api/v1/share/consumers")).Replace(" ", ""));
        Assert.Contains(h.Audit.Events, e => e.Action == "fabric.sharing.enrollment.activate" && e.Resource.Value.Contains(reason) && e.Outcome == Vev.Fabric.Contracts.Audit.AuditOutcome.Denied);
    }

    [Theory]
    [InlineData("http://consumer.example/api")]
    [InlineData("https://user:pw@consumer.example/api")]
    [InlineData("https://consumer.example/api?x=1")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Only_plain_https_destinations_are_accepted(string url)
    {
        using var h = new Harness();

        var response = await h.Api("t-push-url").PostAsJsonAsync("/api/v1/share/consumers", new { destinationUrl = url, activationCode = "AAAA" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(h.Client.Activations);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Internal_addresses_are_recognized(string address, bool internalAddress) =>
        Assert.Equal(internalAddress, DestinationPolicy.IsInternalAddress(IPAddress.Parse(address)));

    [Fact]
    public void A_destination_can_be_limited_to_listed_hosts()
    {
        Assert.Null(DestinationPolicy.Validate("https://consumer.example/api", false, ["consumer.example"]).Problem);
        Assert.NotNull(DestinationPolicy.Validate("https://other.example/api", false, ["consumer.example"]).Problem);
        Assert.Null(DestinationPolicy.Validate("http://localhost:5000/api", true).Problem);
        Assert.Equal("https://consumer.example/api", DestinationPolicy.Validate(" https://consumer.example/api/ ", false).Url);
    }

    // ---- Pushing -------------------------------------------------------------------------------

    [Fact]
    public async Task A_push_is_a_signed_digest_with_a_rising_sequence_sent_with_the_credential()
    {
        using var h = new Harness();
        var api = h.Api("t-push-signed");
        await AddAppAsync(api, "app-1", "Checkout");
        var consumer = await ConnectAsync(api);
        var id = consumer["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/v1/share/consumers/{id}/push", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/v1/share/consumers/{id}/push", null)).StatusCode);

        Assert.Equal(2, h.Client.Pushes.Count);
        Assert.All(h.Client.Pushes, p => Assert.Equal(("secret-credential-1", Destination), (p.Credential, p.Url)));
        var first = Body(h.Client.Pushes[0].Body);
        var second = Body(h.Client.Pushes[1].Body);
        Assert.True(SignatureVerifies(first));
        Assert.True(SignatureVerifies(second));
        Assert.Equal([1, 2], new[] { first, second }.Select(d => d["digest"]!["sequence"]!.GetValue<int>()));
        Assert.Equal("Checkout", first["digest"]!["items"]![0]!["name"]!.GetValue<string>());
        var status = await GetConsumerAsync(api, id);
        Assert.Equal(2, status["lastSuccessSequence"]!.GetValue<int>());
        Assert.Null(status["lastError"]);
    }

    [Fact]
    public async Task Exactly_one_audit_record_is_written_per_push_and_the_file_audit_is_not_used()
    {
        using var h = new Harness();
        var api = h.Api("t-push-audit");
        await AddAppAsync(api, "app-1", "Secret product name");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();

        await api.PostAsync($"/api/v1/share/consumers/{id}/push", null);
        await api.PostAsync($"/api/v1/share/consumers/{id}/push", null);

        var pushes = h.Audit.Events.Where(e => e.Tenant.TenantId == "t-push-audit" && e.Action == "atlas.landscape.share.push.accept").ToList();
        Assert.Equal(2, pushes.Count);
        Assert.Contains("sequence=1", pushes[0].Resource.Value);
        Assert.Contains("sequence=2", pushes[1].Resource.Value);
        Assert.DoesNotContain(h.Audit.Events, e => e.Tenant.TenantId == "t-push-audit" && e.Action == "atlas.landscape.shared");
        Assert.DoesNotContain("Secret product name", string.Join(' ', h.Audit.Events.Where(e => e.Tenant.TenantId == "t-push-audit").Select(e => e.Resource.Value)));
    }

    [Fact]
    public async Task Changing_the_scope_takes_effect_on_the_next_push()
    {
        using var h = new Harness();
        var api = h.Api("t-push-scope");
        await AddAppAsync(api, "app-1", "Checkout");
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsJsonAsync("/api/v1/assets", new Asset("sys-1", AssetKind.System, "Payments", Lifecycle.Active), Json)).StatusCode);
        var id = (await ConnectAsync(api, kinds: ["application"]))["id"]!.GetValue<string>();
        await api.PostAsync($"/api/v1/share/consumers/{id}/push", null);

        Assert.Equal(HttpStatusCode.OK, (await api.PutAsJsonAsync($"/api/v1/share/consumers/{id}/scope", new { kinds = new[] { "system" } }, Json)).StatusCode);
        await api.PostAsync($"/api/v1/share/consumers/{id}/push", null);

        Assert.Equal(["Checkout"], Names(h.Client.Pushes[0].Body));
        Assert.Equal(["Payments"], Names(h.Client.Pushes[1].Body));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsJsonAsync($"/api/v1/share/consumers/{id}/scope", new { kinds = new[] { "dataset" } }, Json)).StatusCode);
    }

    private static string[] Names(byte[] body) => [.. Body(body)["digest"]!["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>())];

    // ---- Scheduling: off by default, on change, daily, backoff -----------------------------------

    [Fact]
    public async Task Nothing_is_pushed_while_nothing_is_connected_paused_or_revoked()
    {
        using var h = new Harness();
        var api = h.Api("t-push-off");
        await AddAppAsync(api, "app-1", "Checkout");

        await h.RunRoundAsync();
        Assert.Empty(h.Client.Pushes);

        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/v1/share/consumers/{id}/pause", null)).StatusCode);
        h.Time.Advance(TimeSpan.FromDays(2));
        await h.RunRoundAsync();
        Assert.Empty(h.Client.Pushes);

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/v1/share/consumers/{id}/resume", null)).StatusCode);
        await h.RunRoundAsync();
        Assert.Single(h.Client.Pushes);
    }

    [Fact]
    public async Task A_new_consumer_gets_a_first_push_then_changes_are_debounced_and_a_quiet_day_still_pushes()
    {
        using var h = new Harness();
        var api = h.Api("t-push-schedule");
        await AddAppAsync(api, "app-1", "Checkout");
        await ConnectAsync(api);

        await h.RunRoundAsync();                                // first push, never succeeded
        Assert.Single(h.Client.Pushes);

        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.RunRoundAsync();                                // nothing changed
        Assert.Single(h.Client.Pushes);

        await AddAppAsync(api, "app-2", "Billing");              // a change...
        await h.RunRoundAsync();                                // ...is not pushed at once (debounce)
        Assert.Single(h.Client.Pushes);
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await h.RunRoundAsync();                                // ...but shortly after
        Assert.Equal(2, h.Client.Pushes.Count);
        Assert.Equal(["Billing", "Checkout", "Example Corp"], Names(h.Client.Pushes[1].Body).Order());   // the vendor entry follows from the apps

        await h.RunRoundAsync();                                // handled: not pushed again
        Assert.Equal(2, h.Client.Pushes.Count);

        h.Time.Advance(TimeSpan.FromHours(25));
        await h.RunRoundAsync();                                // the daily push, changed or not
        Assert.Equal(3, h.Client.Pushes.Count);
    }

    [Fact]
    public async Task A_burst_of_edits_is_one_push()
    {
        using var h = new Harness();
        var api = h.Api("t-push-burst");
        await AddAppAsync(api, "app-0", "First");
        await ConnectAsync(api);
        await h.RunRoundAsync();

        for (var i = 1; i <= 4; i++)
        {
            await AddAppAsync(api, $"app-{i}", $"App {i}");
            h.Time.Advance(TimeSpan.FromSeconds(20));
            await h.RunRoundAsync();                            // each edit restarts the quiet period
        }

        Assert.Single(h.Client.Pushes);
        h.Time.Advance(TimeSpan.FromSeconds(61));
        await h.RunRoundAsync();
        Assert.Equal(2, h.Client.Pushes.Count);
        Assert.Equal(6, Names(h.Client.Pushes[1].Body).Length);   // five apps and their vendor
    }

    [Fact]
    public async Task A_failing_push_is_retried_with_growing_waits_and_recovers()
    {
        using var h = new Harness();
        var api = h.Api("t-push-retry");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();
        h.Client.Push = () => new PushResult(PushOutcome.Retry, Error: "The consumer answered HTTP 503.");

        await h.RunRoundAsync();                                                   // attempt 1 fails: wait 1 minute
        var afterFirst = await GetConsumerAsync(api, id);
        Assert.Equal((1, "The consumer answered HTTP 503."), (afterFirst["failureCount"]!.GetValue<int>(), afterFirst["lastError"]!.GetValue<string>()));
        Assert.Equal(h.Time.GetUtcNow().AddMinutes(1), afterFirst["nextAttemptAt"]!.GetValue<DateTimeOffset>());

        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.RunRoundAsync();
        Assert.Single(h.Client.Pushes);                                            // too early

        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.RunRoundAsync();                                                   // attempt 2 fails: wait 2 minutes
        Assert.Equal(2, h.Client.Pushes.Count);
        Assert.Equal(h.Time.GetUtcNow().AddMinutes(2), (await GetConsumerAsync(api, id))["nextAttemptAt"]!.GetValue<DateTimeOffset>());

        h.Client.Push = () => new PushResult(PushOutcome.Accepted);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.RunRoundAsync();                                                   // attempt 3 succeeds
        var recovered = await GetConsumerAsync(api, id);
        Assert.Equal((0, "Active"), (recovered["failureCount"]!.GetValue<int>(), recovered["state"]!.GetValue<string>()));
        Assert.Null(recovered["lastError"]);
        Assert.Null(recovered["nextAttemptAt"]);
        Assert.Equal(2, h.Audit.Events.Count(e => e.Tenant.TenantId == "t-push-retry" && e.Action == "atlas.landscape.share.push.fail"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(40, 360)]
    public void Backoff_doubles_up_to_the_ceiling(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), LandscapePushService.Backoff(failures, 360));

    // ---- Stopping: either side ---------------------------------------------------------------

    [Fact]
    public async Task Pause_and_revoke_from_this_side_stop_pushes_and_revoke_forgets_the_credential_and_tells_the_consumer()
    {
        using var h = new Harness();
        var api = h.Api("t-push-revoke");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();

        var revoked = JsonNode.Parse(await (await api.PostAsync($"/api/v1/share/consumers/{id}/revoke", null)).Content.ReadAsStringAsync())!;
        h.Time.Advance(TimeSpan.FromDays(2));
        await h.RunRoundAsync();

        Assert.Equal("Revoked", revoked["state"]!.GetValue<string>());
        Assert.Equal([(Destination, "secret-credential-1")], h.Client.Revocations);
        Assert.Empty(h.Client.Pushes);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/v1/share/consumers/{id}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/v1/share/consumers/{id}/push", null)).StatusCode);
        Assert.Empty(h.Client.Pushes);    // a revoked consumer is never pushed to, even on request
        Assert.Contains(h.Audit.Events, e => e.Action == "fabric.sharing.enrollment.revoke" && e.Tenant.TenantId == "t-push-revoke");
    }

    [Fact]
    public async Task A_revoke_succeeds_locally_even_when_the_consumer_cannot_be_told()
    {
        using var h = new Harness();
        h.Client.RevokeFails = true;
        var api = h.Api("t-push-revoke-down");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/v1/share/consumers/{id}/revoke", null)).StatusCode);
        Assert.Equal("Revoked", (await GetConsumerAsync(api, id))["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("sharing_enrollment_revoked", "revoked the sharing")]
    [InlineData("sharing_enrollment_suspended", "suspended the sharing")]
    [InlineData("sharing_binding_mismatch", "do not belong")]
    public async Task When_the_consumer_refuses_pushing_stops_and_the_reason_is_shown(string reason, string expected)
    {
        using var h = new Harness();
        var api = h.Api("t-push-denied-" + reason);
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();
        h.Client.Push = () => new PushResult(PushOutcome.Denied, reason);

        await h.RunRoundAsync();
        h.Time.Advance(TimeSpan.FromDays(2));
        await h.RunRoundAsync();

        var status = await GetConsumerAsync(api, id);
        Assert.Equal(("Stopped", reason), (status["state"]!.GetValue<string>(), status["stopReason"]!.GetValue<string>()));
        Assert.Contains(expected, status["lastError"]!.GetValue<string>());
        Assert.Single(h.Client.Pushes);                               // it stopped at the first refusal
        var denied = Assert.Single(h.Audit.Events, e => e.Tenant.TenantId == "t-push-denied-" + reason && e.Action == "atlas.landscape.share.push.deny");
        Assert.Equal(Vev.Fabric.Contracts.Audit.AuditOutcome.Denied, denied.Outcome);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/v1/share/consumers/{id}/resume", null)).StatusCode);
    }

    [Fact]
    public async Task An_expired_credential_stops_without_calling_the_consumer()
    {
        using var h = new Harness();
        h.Client.Activation = () => new ConsumerActivationResult(true, "enroll-1", "secret-credential-1", h.Time.GetUtcNow().AddDays(10), null, null);
        var api = h.Api("t-push-expired");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();

        h.Time.Advance(TimeSpan.FromDays(11));
        await h.RunRoundAsync();

        var status = await GetConsumerAsync(api, id);
        Assert.Equal(("Stopped", "sharing_credential_expired"), (status["state"]!.GetValue<string>(), status["stopReason"]!.GetValue<string>()));
        Assert.Empty(h.Client.Pushes);
    }

    [Fact]
    public async Task A_replayed_sequence_is_retried_with_a_higher_one()
    {
        using var h = new Harness();
        var api = h.Api("t-push-replay");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();
        h.Client.Push = () => new PushResult(PushOutcome.Replayed);

        await h.RunRoundAsync();
        h.Client.Push = () => new PushResult(PushOutcome.Accepted);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.RunRoundAsync();

        Assert.Equal([1, 2], h.Client.Pushes.Select(p => Body(p.Body)["digest"]!["sequence"]!.GetValue<int>()));
        Assert.Equal("Active", (await GetConsumerAsync(api, id))["state"]!.GetValue<string>());
    }

    // ---- Isolation and privacy ---------------------------------------------------------------

    [Fact]
    public async Task Each_tenant_pushes_only_its_own_landscape_and_sees_only_its_own_consumers()
    {
        using var h = new Harness();
        var a = h.Api("t-push-iso-a");
        var b = h.Api("t-push-iso-b");
        await AddAppAsync(a, "app-a", "Alpha product");
        await AddAppAsync(b, "app-b", "Beta product");
        await ConnectAsync(a, "https://a.example/api", kinds: ["application"]);
        await ConnectAsync(b, "https://b.example/api", kinds: ["application"]);

        await h.RunRoundAsync();

        Assert.Equal(2, h.Client.Pushes.Count);
        Assert.Equal(["Alpha product"], Names(h.Client.Pushes.Single(p => p.Url.StartsWith("https://a.example")).Body));
        Assert.Equal(["Beta product"], Names(h.Client.Pushes.Single(p => p.Url.StartsWith("https://b.example")).Body));
        Assert.Single(JsonNode.Parse(await a.GetStringAsync("/api/v1/share/consumers"))!.AsArray());
        var sources = h.Client.Pushes.Select(p => Body(p.Body)["digest"]!["sourceInstanceId"]!.GetValue<string>()).Distinct().ToList();
        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public async Task No_payload_content_reaches_the_logs()
    {
        using var h = new Harness();
        var api = h.Api("t-push-logs");
        await AddAppAsync(api, "app-1", "Confidential product name");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();
        h.Client.Push = () => new PushResult(PushOutcome.Retry, Error: "The consumer answered HTTP 500.");

        await h.RunRoundAsync();
        await api.PostAsync($"/api/v1/share/consumers/{id}/push", null);

        var logs = string.Join('\n', h.Logs.Messages);
        Assert.DoesNotContain("Confidential product name", logs);
        Assert.DoesNotContain("secret-credential-1", logs);
        Assert.DoesNotContain("consumer.example", logs);
    }

    [Fact]
    public async Task A_client_that_throws_is_a_retry_that_names_only_the_kind_of_failure()
    {
        using var h = new Harness();
        var api = h.Api("t-push-throws");
        var id = (await ConnectAsync(api))["id"]!.GetValue<string>();
        h.Client.Push = () => throw new HttpRequestException("secret detail https://consumer.example/api?token=abc");

        await h.RunRoundAsync();

        var status = await GetConsumerAsync(api, id);
        Assert.Equal("Active", status["state"]!.GetValue<string>());
        Assert.Equal("Could not reach the consumer (HttpRequestException).", status["lastError"]!.GetValue<string>());
        Assert.DoesNotContain("token=abc", status.ToJsonString());
    }

    [Fact]
    public async Task The_number_of_consumers_and_duplicate_destinations_are_limited()
    {
        using var h = new Harness();
        var api = h.Api("t-push-limit");

        await ConnectAsync(api, "https://one.example/api");
        var duplicate = await api.PostAsJsonAsync("/api/v1/share/consumers", new { destinationUrl = "https://ONE.example/api/", activationCode = "AAAA" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("already connected", await duplicate.Content.ReadAsStringAsync());

        for (var i = 2; i <= 5; i++) await ConnectAsync(api, $"https://c{i}.example/api");
        var tooMany = await api.PostAsJsonAsync("/api/v1/share/consumers", new { destinationUrl = "https://c6.example/api", activationCode = "AAAA" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("At most 5", await tooMany.Content.ReadAsStringAsync());
    }
}

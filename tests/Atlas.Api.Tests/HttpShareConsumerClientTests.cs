using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vev.Atlas.Api.Sharing;
using Vev.Atlas.Domain.Sharing.Push;
using Xunit;

namespace Vev.Atlas.Api.Tests;

/// <summary>The transport of the outbound push (atlas#176): what is sent, how replies are read, and where it may connect.</summary>
public sealed class HttpShareConsumerClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Authorization, string? Body, string? ContentType)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Content?.Headers.ContentType?.MediaType));
            return reply(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string? json = null) =>
        new(status) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };

    private static (HttpShareConsumerClient Client, Stub Stub) Create(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var stub = new Stub(reply);
        return (new HttpShareConsumerClient(new Factory(stub)), stub);
    }

    [Fact]
    public async Task Activation_posts_the_code_and_the_key_and_reads_the_credential()
    {
        var (client, stub) = Create(_ => Reply(HttpStatusCode.OK, """{"enrollmentId":"e-1","credential":"c-1","credentialExpiresAt":"2027-01-01T00:00:00Z"}"""));

        var result = await client.ActivateAsync("https://consumer.example/api", "AAAA-BBBB", "key-1", "spki");

        Assert.True(result.Accepted);
        Assert.Equal(("e-1", "c-1", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)), (result.EnrollmentId, result.Credential, result.CredentialExpiresAt));
        var request = Assert.Single(stub.Requests);
        Assert.Equal(("https://consumer.example/api/activate", "application/json", null), (request.Url, request.ContentType, request.Authorization));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(("AAAA-BBBB", "key-1", "spki"), (body.RootElement.GetProperty("activationCode").GetString(), body.RootElement.GetProperty("keyId").GetString(), body.RootElement.GetProperty("publicKey").GetString()));
    }

    [Fact]
    public async Task A_refused_activation_carries_the_reason_code_and_an_unreachable_consumer_is_explained()
    {
        var (refused, _) = Create(_ => Reply(HttpStatusCode.Forbidden, """{"reasonCode":"sharing_activation_code_used"}"""));
        var unreachable = new HttpShareConsumerClient(new Factory(new Stub(_ => throw new HttpRequestException("secret detail"))));

        var one = await refused.ActivateAsync("https://consumer.example/api", "x", "k", "p");
        var two = await unreachable.ActivateAsync("https://consumer.example/api", "x", "k", "p");

        Assert.Equal((false, "sharing_activation_code_used"), (one.Accepted, one.ReasonCode));
        Assert.False(two.Accepted);
        Assert.DoesNotContain("secret detail", two.Error);
    }

    [Fact]
    public async Task A_push_sends_the_digest_with_the_credential_as_a_bearer_token()
    {
        var (client, stub) = Create(_ => Reply(HttpStatusCode.Accepted));

        var result = await client.PushAsync("https://consumer.example/api", "c-1", Encoding.UTF8.GetBytes("""{"digest":{}}"""));

        Assert.Equal(PushOutcome.Accepted, result.Outcome);
        var request = Assert.Single(stub.Requests);
        Assert.Equal((HttpMethod.Post, "https://consumer.example/api/digests", "Bearer c-1", "application/json", """{"digest":{}}"""),
            (request.Method, request.Url, request.Authorization, request.ContentType, request.Body));
    }

    [Theory]
    [InlineData(200, null, PushOutcome.Accepted, null)]
    [InlineData(403, """{"reasonCode":"sharing_enrollment_revoked"}""", PushOutcome.Denied, "sharing_enrollment_revoked")]
    [InlineData(403, null, PushOutcome.Denied, "forbidden")]
    [InlineData(401, null, PushOutcome.Denied, "unauthorized")]
    [InlineData(409, """{"reasonCode":"sequence_replayed"}""", PushOutcome.Replayed, "sequence_replayed")]
    [InlineData(429, null, PushOutcome.Retry, null)]
    [InlineData(503, null, PushOutcome.Retry, null)]
    [InlineData(408, null, PushOutcome.Retry, null)]
    [InlineData(400, """{"reasonCode":"schema"}""", PushOutcome.Retry, null)]
    [InlineData(500, "not json at all", PushOutcome.Retry, null)]
    public async Task Replies_are_read_as_accepted_denied_replayed_or_retry(int status, string? json, PushOutcome expected, string? reason)
    {
        var (client, _) = Create(_ => Reply((HttpStatusCode)status, json));

        var result = await client.PushAsync("https://consumer.example/api", "c", [1]);

        Assert.Equal((expected, reason), (result.Outcome, result.ReasonCode));
    }

    [Fact]
    public async Task An_unreachable_consumer_is_a_retry_that_names_no_detail()
    {
        var client = new HttpShareConsumerClient(new Factory(new Stub(_ => throw new HttpRequestException("https://consumer.example/api?token=abc"))));

        var result = await client.PushAsync("https://consumer.example/api", "c", [1]);

        Assert.Equal(PushOutcome.Retry, result.Outcome);
        Assert.DoesNotContain("token", result.Error);
    }

    [Fact]
    public async Task Revoke_posts_to_the_destination_with_the_credential()
    {
        var (client, stub) = Create(_ => Reply(HttpStatusCode.NoContent));

        await client.RevokeAsync("https://consumer.example/api", "c-1");

        var request = Assert.Single(stub.Requests);
        Assert.Equal(("https://consumer.example/api/revoke", "Bearer c-1"), (request.Url, request.Authorization));
    }

    // ---- Where the real handler may connect ---------------------------------------------------

    private static (TcpListener Listener, int Port, Task Server) Serve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var buffer = new byte[4096];
            _ = await socket.ReceiveAsync(buffer);
            await socket.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port, server);
    }

    [Fact]
    public async Task The_real_handler_refuses_an_internal_address_unless_the_operator_allowed_it()
    {
        var (listener, port, _) = Serve();
        try
        {
            using var strict = new HttpClient(HttpShareConsumerClient.CreateHandler(new LandscapePushSettings()));
            await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync($"http://127.0.0.1:{port}/"));

            using var allowed = new HttpClient(HttpShareConsumerClient.CreateHandler(new LandscapePushSettings { AllowPrivateDestinations = true }));
            using var response = await allowed.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task The_real_handler_follows_no_redirects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            _ = await socket.ReceiveAsync(new byte[4096]);
            await socket.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        try
        {
            using var client = new HttpClient(HttpShareConsumerClient.CreateHandler(new LandscapePushSettings { AllowPrivateDestinations = true }));
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }
    }
}

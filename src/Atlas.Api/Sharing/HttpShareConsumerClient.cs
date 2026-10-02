using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Vev.Atlas.Domain.Sharing.Push;

namespace Vev.Atlas.Api.Sharing;

/// <summary>
/// The outbound call to a consuming product (atlas#176). It contacts only the destination an admin entered, sends only the
/// signed digest, follows no redirects, and reads no more than a small reply. The protocol is documented in
/// <c>docs/landscape-share-push.md</c>: <c>POST {base}/activate</c>, <c>POST {base}/digests</c> and <c>POST {base}/revoke</c>.
/// </summary>
public sealed class HttpShareConsumerClient(IHttpClientFactory factory) : IShareConsumerClient
{
    public const string HttpClientName = "atlas-share-push";
    private const int MaxReplyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ConsumerActivationResult> ActivateAsync(string destinationUrl, string activationCode, string keyId, string publicKey, CancellationToken ct = default)
    {
        try
        {
            using var response = await factory.CreateClient(HttpClientName).PostAsJsonAsync($"{destinationUrl}/activate", new { activationCode, keyId, publicKey }, Json, ct);
            var body = await ReadAsync(response, ct);
            if (response.IsSuccessStatusCode && body is { } ok)
            {
                return new ConsumerActivationResult(true, Text(ok, "enrollmentId"), Text(ok, "credential"),
                    DateTimeOffset.TryParse(Text(ok, "credentialExpiresAt"), out var expires) ? expires : null, null, null);
            }

            return new ConsumerActivationResult(false, null, null, null, body is { } denied ? Text(denied, "reasonCode") : null, $"The consumer answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new ConsumerActivationResult(false, null, null, null, null, "The consumer could not be reached. Check the address.");
        }
    }

    public async Task<PushResult> PushAsync(string destinationUrl, string credential, byte[] signedDigestJson, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{destinationUrl}/digests") { Content = new ByteArrayContent(signedDigestJson) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        HttpResponseMessage response;
        try
        {
            response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new PushResult(PushOutcome.Retry, Error: "The consumer could not be reached.");
        }

        using (response)
        {
            var body = await ReadAsync(response, ct);
            var reason = body is { } json ? Text(json, "reasonCode") : null;
            var status = (int)response.StatusCode;
            return status switch
            {
                >= 200 and < 300 => new PushResult(PushOutcome.Accepted),
                401 or 403 => new PushResult(PushOutcome.Denied, reason ?? (status == 401 ? "unauthorized" : "forbidden")),
                409 => new PushResult(PushOutcome.Replayed, reason),
                408 or 425 or 429 or >= 500 => new PushResult(PushOutcome.Retry, Error: $"The consumer answered HTTP {status}."),
                _ => new PushResult(PushOutcome.Retry, Error: $"The consumer rejected the digest (HTTP {status})."),
            };
        }
    }

    public async Task RevokeAsync(string destinationUrl, string credential, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{destinationUrl}/revoke");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);
    }

    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while (buffer.Length < MaxReplyBytes && (read = await stream.ReadAsync(chunk, ct)) > 0) buffer.Write(chunk, 0, read);
            if (buffer.Length == 0) return null;
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The handler for the push client: no redirects, and a connect step that checks every address it is about to connect to, so a
    /// destination that resolves to this installation's own network is refused unless the operator allowed it.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(LandscapePushSettings settings) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (!settings.AllowPrivateDestinations) addresses = [.. addresses.Where(a => !DestinationPolicy.IsInternalAddress(a))];
            if (addresses.Length == 0) throw new HttpRequestException("The destination does not resolve to an address this installation may push to.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

using Microsoft.Extensions.Options;

namespace Vev.Atlas.Api;

/// <summary>
/// Canonical URL/path configuration for a deployment (atlas#19). Hostnames and paths are <b>deployment
/// configuration, never a hard-coded VEV identity</b> (handbook 04, ADR 0002): the default is a flat,
/// single-host shape — public UI, login and the API on one host at the root — and nothing assumes a
/// <c>vev.software</c> hostname. Bind from the <c>Atlas:Urls</c> configuration section.
/// </summary>
public sealed class AtlasUrlOptions
{
    /// <summary>Configuration section this binds from.</summary>
    public const string SectionName = "Atlas:Urls";

    /// <summary>
    /// Absolute external base URL (scheme + host, optionally a port) for building absolute links without a
    /// request in hand, e.g. <c>https://atlas.example.com</c>. Empty/null (the default) means "derive it
    /// from the incoming request" — so a self-hoster needs to set nothing.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>The sub-path the app is hosted under behind a reverse proxy, e.g. <c>/atlas</c>. Empty
    /// (the default) means the app is served at the host root.</summary>
    public string PathBase { get; set; } = "";

    /// <summary>The path (under <see cref="PathBase"/>) where sign-in lives.</summary>
    public string LoginPath { get; set; } = "/login";

    /// <summary>The path (under <see cref="PathBase"/>) the product API is mounted at.</summary>
    public string ApiBasePath { get; set; } = "/api";

    /// <summary>Absolute base URL for product documentation links.</summary>
    public string DocsBaseUrl { get; set; } = "https://github.com/Vev-software/docs/blob/main/docs";
}

/// <summary>
/// Resolves the canonical paths and URLs a deployment uses from <see cref="AtlasUrlOptions"/> (atlas#19).
/// All generation is deterministic and normalized (single leading slash, no trailing slash, no double
/// slashes), and absolute URLs come from the configured <see cref="AtlasUrlOptions.PublicBaseUrl"/> or the
/// request itself — never a baked-in host.
/// </summary>
public sealed class AtlasUrls
{
    private readonly string? _publicBaseUrl;

    public AtlasUrls(IOptions<AtlasUrlOptions> options) : this(options.Value) { }

    public AtlasUrls(AtlasUrlOptions options)
    {
        PathBase = NormalizePath(options.PathBase, fallback: "");
        ApiBasePath = NormalizePath(options.ApiBasePath, fallback: "/api");
        LoginPath = NormalizePath(options.LoginPath, fallback: "/login");
        DocsBaseUrl = string.IsNullOrWhiteSpace(options.DocsBaseUrl)
            ? "https://github.com/Vev-software/docs/blob/main/docs"
            : options.DocsBaseUrl.Trim().TrimEnd('/');
        _publicBaseUrl = string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? null
            : options.PublicBaseUrl!.Trim().TrimEnd('/');
    }

    /// <summary>Normalized app sub-path: <c>""</c> for root, otherwise e.g. <c>/atlas</c>. Feed to UsePathBase.</summary>
    public string PathBase { get; }

    /// <summary>Normalized API mount path (relative to the app root, before any path base), e.g. <c>/api</c>.</summary>
    public string ApiBasePath { get; }

    /// <summary>Normalized login path (relative to the app root, before any path base), e.g. <c>/login</c>.</summary>
    public string LoginPath { get; }

    /// <summary>Normalized absolute docs base URL.</summary>
    public string DocsBaseUrl { get; }

    /// <summary>
    /// The from-origin API base a browser calls, including the request's path base (so it is correct behind
    /// a sub-path reverse proxy), e.g. <c>/atlas/api</c>. The SPA prepends this to <c>/v1/…</c>.
    /// </summary>
    public string ClientApiBase(HttpRequest request) => Join(request.PathBase.Value, ApiBasePath);

    /// <summary>The from-origin login path including the request's path base.</summary>
    public string ClientLoginPath(HttpRequest request) => Join(request.PathBase.Value, LoginPath);

    /// <summary>
    /// An absolute URL for an app-relative path. Uses <see cref="AtlasUrlOptions.PublicBaseUrl"/> when
    /// configured; otherwise the request's own scheme + host + path base — never a hard-coded hostname.
    /// </summary>
    public string AbsoluteUrl(HttpRequest request, string appRelativePath)
    {
        var path = EnsureLeadingSlash(appRelativePath);
        return _publicBaseUrl is not null
            ? _publicBaseUrl + path
            : $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}{path}";
    }

    /// <summary>Resolve a stable documentation path or anchor under the configured docs host.</summary>
    public string DocumentationUrl(string pathOrAnchor)
    {
        var path = pathOrAnchor.StartsWith('/') ? pathOrAnchor : "/" + pathOrAnchor;
        return DocsBaseUrl + path;
    }

    // Normalize a configured path to "" or "/seg[/seg…]": ensure a single leading slash, drop the trailing
    // one, collapse doubles. A blank value falls back to the caller's default.
    private static string NormalizePath(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim().Trim('/');
        return trimmed.Length == 0 ? fallback : "/" + trimmed;
    }

    private static string Join(string? left, string right)
    {
        var a = string.IsNullOrEmpty(left) ? "" : "/" + left.Trim('/');
        var b = string.IsNullOrEmpty(right) ? "" : "/" + right.Trim('/');
        var joined = a + b;
        return joined.Length == 0 ? "/" : joined;
    }

    private static string EnsureLeadingSlash(string path) =>
        string.IsNullOrEmpty(path) ? "/" : (path.StartsWith('/') ? path : "/" + path);
}

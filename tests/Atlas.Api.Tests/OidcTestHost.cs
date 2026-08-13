using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vev.Atlas.Api;
using Vev.Atlas.Persistence;

namespace Vev.Atlas.Api.Tests;

/// <summary>
/// Hosts the real Atlas API in the <c>fabric-oidc</c> identity mode (fabric#3) against an isolated
/// in-memory SQLite database. The host is pinned to a non-Development environment with a configured
/// Authority, and a self-signed RSA key is injected into the JWT bearer options so tokens validate fully
/// offline — no provider metadata is fetched. Use <see cref="CreateToken"/> to mint tokens the host trusts,
/// and <see cref="ForeignKey"/> to mint one signed by a different key (an untrusted issuer).
/// </summary>
public sealed class OidcTestHost : WebApplicationFactory<Program>
{
    /// <summary>The issuer the host trusts; tokens must carry this issuer to validate.</summary>
    public const string Issuer = "https://test-idp.local/realms/atlas";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _foreignRsa = RSA.Create(2048);
    private readonly JsonWebTokenHandler _handler = new();

    private RsaSecurityKey SigningKey => new(_rsa) { KeyId = "test-signing-key" };

    /// <summary>A signing key the host does <b>not</b> trust, for the wrong-signature case.</summary>
    public RsaSecurityKey ForeignKey => new(_foreignRsa) { KeyId = "foreign-key" };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        // A non-Development environment with an OIDC provider configured selects fabric-oidc (atlas#34).
        builder.UseEnvironment("Production");
        builder.UseSetting(RequestIdentityConfiguration.ModeKey, RequestIdentityConfiguration.FabricOidc);
        builder.UseSetting(RequestIdentityConfiguration.OidcAuthorityKey, Issuer);
        builder.UseSetting(RequestIdentityConfiguration.OidcRequireHttpsMetadataKey, "false");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AtlasDbContext>>();
            services.AddDbContext<AtlasDbContext>(options => options.UseSqlite(_connection));

            // Replace live provider-metadata retrieval with our self-signed key, so validation runs offline.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                jwt.RequireHttpsMetadata = false;
                jwt.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                jwt.Configuration.SigningKeys.Add(SigningKey);
                jwt.TokenValidationParameters.ValidIssuer = Issuer;
                jwt.TokenValidationParameters.IssuerSigningKey = SigningKey;
                jwt.TokenValidationParameters.ValidateIssuerSigningKey = true;
            });
        });
    }

    /// <summary>Mint a token the host trusts, signed with the in-host key.</summary>
    public string CreateToken(
        string? tenant = "t-oidc",
        string sub = "u-oidc",
        string name = "OIDC User",
        params string[] roles) =>
        CreateToken(SigningKey, tenant, sub, name, roles);

    /// <summary>Mint a token signed with an arbitrary key (use <see cref="ForeignKey"/> for an untrusted one).</summary>
    public string CreateToken(
        SecurityKey signingKey,
        string? tenant,
        string sub,
        string name,
        params string[] roles)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["name"] = name,
            ["roles"] = roles,
        };
        if (!string.IsNullOrWhiteSpace(tenant))
        {
            claims["tenant"] = tenant;
        }

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            _rsa.Dispose();
            _foreignRsa.Dispose();
        }
    }
}

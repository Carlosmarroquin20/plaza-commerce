using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Plaza.Gateway;

/// <summary>
/// How callers of the public API are identified (ADR 0018). The gateway validates JSON Web
/// Tokens; the customer is the token's <c>sub</c>. Tokens come from an OpenID Connect issuer
/// (<c>Auth:Authority</c>) or, where there is none, are signed with a shared key
/// (<c>Auth:SigningKey</c>).
/// </summary>
public static class GatewayAuth
{
    public const string LocalIssuer = "plaza-local";
    public const string LocalAudience = "plaza-api";

    private const int MinKeyBytes = 32;

    /// <summary>The signing key when tokens are local, or null when an authority issues them.</summary>
    public static byte[]? AddGatewayAuthentication(this WebApplicationBuilder builder)
    {
        var authority = builder.Configuration["Auth:Authority"];
        var audience = builder.Configuration["Auth:Audience"];
        var configuredKey = builder.Configuration["Auth:SigningKey"];

        byte[]? key = null;
        if (string.IsNullOrWhiteSpace(authority))
        {
            if (!string.IsNullOrWhiteSpace(configuredKey))
            {
                key = Convert.FromBase64String(configuredKey);
            }
            else if (builder.Environment.IsDevelopment())
            {
                // A key that exists only in this process: nothing to configure to try the API
                // locally, and nothing secret in the repository. Tokens die with the process.
                key = RandomNumberGenerator.GetBytes(MinKeyBytes);
            }
            else
            {
                throw new InvalidOperationException("Configure Auth:Authority or Auth:SigningKey. There is no default.");
            }

            if (key.Length < MinKeyBytes)
            {
                throw new InvalidOperationException($"Auth:SigningKey must be at least {MinKeyBytes} bytes, base64-encoded.");
            }
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            // Keep the token's own claim names ("sub"), not the legacy XML ones.
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = JwtRegisteredClaimNames.Sub;

            if (key is null)
            {
                options.Authority = authority;
                options.Audience = audience;
            }
            else
            {
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(key);
                options.TokenValidationParameters.ValidIssuer = LocalIssuer;
                options.TokenValidationParameters.ValidAudience = LocalAudience;
                // Pinned, so a token cannot choose a weaker algorithm or "none".
                options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
            }
        });
        builder.Services.AddAuthorization();

        return key;
    }

    /// <summary>Signs a token for a customer with the local key.</summary>
    public static string CreateLocalToken(byte[] key, Guid customerId, TimeSpan lifetime, TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalIssuer,
            Audience = LocalAudience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, customerId.ToString("D"))]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
        });
    }
}

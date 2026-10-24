using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Gamex.UserLibrary.Application;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.UserLibrary.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string DefaultIssuer = "gamex-user-library";
    public const string DefaultAudience = "gamex";
    public const string ContactAudience = "gamex-listing-contact";
    public const string ContactTokenType = "gx-contact+jwt";

    public required RsaSecurityKey SigningKey { get; init; }
    public string Issuer { get; init; } = DefaultIssuer;
    public string Audience { get; init; } = DefaultAudience;

    /// <summary>Generated at startup (Development without a configured key); tokens die with the process.</summary>
    public bool IsEphemeral { get; init; }

    public string PublicKeyPem => SigningKey.Rsa.ExportSubjectPublicKeyInfoPem();
}

/// <summary>Issues RS256 tokens. Other services validate them with the public key (<c>JWT_PUBLIC_KEY</c>).</summary>
internal sealed class TokenService(JwtOptions jwt, AuthOptions auth, TimeProvider time) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly SigningCredentials _credentials = new(jwt.SigningKey, SecurityAlgorithms.RsaSha256);

    public AccessToken CreateAccessToken(User user)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + auth.AccessTokenLifetime,
            SigningCredentials = _credentials,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("email", user.Email),
                new Claim("name", user.DisplayName),
                new Claim("role", user.Role),
                new Claim("jti", Guid.NewGuid().ToString()),
            ]),
        });
        return new AccessToken(token, (int)auth.AccessTokenLifetime.TotalSeconds);
    }

    public string CreateContactToken(ListingContact contact)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["listing_id"] = contact.ListingId.ToString(),
            ["owner_id"] = contact.OwnerId.ToString(),
            ["owner_name"] = contact.OwnerName,
            ["game_title"] = contact.GameTitle,
        };
        if (contact.OwnerAvatarUrl is not null)
        {
            claims["owner_avatar"] = contact.OwnerAvatarUrl;
        }

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            // A distinct audience and type so a contact token is never accepted as an access token, and vice versa.
            TokenType = JwtOptions.ContactTokenType,
            Issuer = jwt.Issuer,
            Audience = JwtOptions.ContactAudience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + auth.ContactTokenLifetime,
            SigningCredentials = _credentials,
            Claims = claims,
        });
    }

    public string CreateOpaqueToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string Hash(string opaqueToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(opaqueToken)));
}

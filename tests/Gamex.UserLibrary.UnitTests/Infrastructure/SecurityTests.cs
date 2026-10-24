using System.Security.Cryptography;
using Gamex.UserLibrary.Application;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Common;
using Gamex.UserLibrary.Domain.Users;
using Gamex.UserLibrary.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.UserLibrary.UnitTests.Infrastructure;

public sealed class SecurityTests
{
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private TokenService CreateService() =>
        new(new JwtOptions { SigningKey = Key }, new AuthOptions(), _time);

    private static TokenValidationParameters Validation(string audience) => new()
    {
        IssuerSigningKey = Key,
        ValidIssuer = JwtOptions.DefaultIssuer,
        ValidAudience = audience,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
    };

    [Fact]
    public async Task AccessToken_HasSpecClaimsAndAudience()
    {
        var user = User.Register("Marko P.", "marko@example.rs", null, _time.GetUtcNow().UtcDateTime);

        var token = CreateService().CreateAccessToken(user);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, Validation(JwtOptions.DefaultAudience));

        result.IsValid.ShouldBeTrue();
        result.Claims["sub"].ShouldBe(user.Id.ToString());
        result.Claims["email"].ShouldBe("marko@example.rs");
        result.Claims["name"].ShouldBe("Marko P.");
        result.Claims["role"].ShouldBe("user");
        token.ExpiresInSeconds.ShouldBe(900);
    }

    [Fact]
    public async Task ContactToken_IsNotValidAsAccessToken()
    {
        var contact = new ListingContact(Guid.NewGuid(), Guid.NewGuid(), "Ana", null, "Hades");

        var token = CreateService().CreateContactToken(contact);
        var handler = new JsonWebTokenHandler();

        (await handler.ValidateTokenAsync(token, Validation(JwtOptions.DefaultAudience))).IsValid.ShouldBeFalse();
        var asContact = await handler.ValidateTokenAsync(token, Validation(JwtOptions.ContactAudience));
        asContact.IsValid.ShouldBeTrue();
        asContact.Claims["listing_id"].ShouldBe(contact.ListingId.ToString());
        asContact.Claims["game_title"].ShouldBe("Hades");
        ((JsonWebToken)asContact.SecurityToken).Typ.ShouldBe(JwtOptions.ContactTokenType);
    }

    [Fact]
    public void OpaqueTokens_AreRandomAndHashStable()
    {
        var service = CreateService();
        var a = service.CreateOpaqueToken();

        a.ShouldNotBe(service.CreateOpaqueToken());
        service.Hash(a).Length.ShouldBe(64);
        service.Hash(a).ShouldBe(service.Hash(a));
    }

    [Fact]
    public void PasswordHasher_VerifiesOnlyTheRightPassword()
    {
        var hasher = new PasswordHasher();
        var hash = hasher.Hash("correct horse battery");

        hasher.Verify(hash, "correct horse battery").ShouldBeTrue();
        hasher.Verify(hash, "wrong").ShouldBeFalse();
        hash.ShouldNotContain("correct");
    }

    [Fact]
    public void Cursor_RoundTrips()
    {
        var at = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560);
        var id = Guid.CreateVersion7();

        Cursor.TryDecode(Cursor.Encode(at, id), out var decodedAt, out var decodedId).ShouldBeTrue();
        decodedAt.ShouldBe(at);
        decodedId.ShouldBe(id);
        Cursor.TryDecode("not-a-cursor", out _, out _).ShouldBeFalse();
        Cursor.TryDecode(null, out _, out _).ShouldBeFalse();
    }
}

namespace Gamex.UserLibrary.Domain.Users;

/// <summary>Opaque refresh token (only its SHA-256 hash is stored). Rotated on every use.</summary>
public sealed class RefreshToken
{
    private RefreshToken()
    {
        TokenHash = null!;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    /// <summary>Null = still valid.</summary>
    public DateTime? RevokedAt { get; private set; }

    /// <summary>Set when rotated; a revoked token that has a replacement and is presented again signals theft.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public static RefreshToken Issue(Guid userId, string tokenHash, DateTime now, TimeSpan lifetime)
    {
        var at = Timestamps.ToStoragePrecision(now);
        return new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAt = at,
            ExpiresAt = at + lifetime,
        };
    }

    public bool IsActive(DateTime now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTime now, Guid? replacedBy = null)
    {
        RevokedAt ??= Timestamps.ToStoragePrecision(now);
        ReplacedByTokenId ??= replacedBy;
    }
}

public static class OAuthProviders
{
    public const string Google = "google";
    public const string Facebook = "facebook";

    public static bool IsKnown(string provider) => provider is Google or Facebook;
}

/// <summary>Link between a user and an external login (table <c>oauth_providers</c>).</summary>
public sealed class OAuthLogin
{
    private OAuthLogin()
    {
        Provider = null!;
        ProviderUid = null!;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Provider { get; private set; }
    public string ProviderUid { get; private set; }

    public static OAuthLogin Link(Guid userId, string provider, string providerUid) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Provider = provider,
        ProviderUid = providerUid,
    };
}

/// <summary>Single-use password reset token (only its hash is stored).</summary>
public sealed class PasswordResetToken
{
    private PasswordResetToken()
    {
        TokenHash = null!;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }

    public static PasswordResetToken Issue(Guid userId, string tokenHash, DateTime now, TimeSpan lifetime)
    {
        var at = Timestamps.ToStoragePrecision(now);
        return new PasswordResetToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAt = at,
            ExpiresAt = at + lifetime,
        };
    }

    public bool IsUsable(DateTime now) => UsedAt is null && ExpiresAt > now;

    public void MarkUsed(DateTime now) => UsedAt = Timestamps.ToStoragePrecision(now);
}

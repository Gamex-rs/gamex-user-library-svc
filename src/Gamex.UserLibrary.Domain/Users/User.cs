namespace Gamex.UserLibrary.Domain.Users;

public static class UserRoles
{
    public const string User = "user";
    public const string Admin = "admin";
}

public sealed class User
{
    private readonly List<UserPlatform> _platforms = [];

    private User()
    {
        DisplayName = null!;
        Email = null!;
        Role = UserRoles.User;
        Country = "RS";
    }

    public Guid Id { get; private set; }
    public string DisplayName { get; private set; }

    /// <summary>Normalized: trimmed, lower-case.</summary>
    public string Email { get; private set; }

    /// <summary>Null for OAuth-only accounts.</summary>
    public string? PasswordHash { get; private set; }
    public string? AvatarUrl { get; private set; }
    public string? City { get; private set; }
    public string? Region { get; private set; }
    public string Country { get; private set; }
    public string? Bio { get; private set; }
    public string Role { get; private set; }
    public decimal RatingAvg { get; private set; }
    public int RatingCount { get; private set; }

    /// <summary>False = soft deleted; cannot sign in, listings are hidden.</summary>
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public IReadOnlyCollection<UserPlatform> Platforms => _platforms;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static User Register(string displayName, string email, string? passwordHash, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var at = Timestamps.ToStoragePrecision(now);
        return new User
        {
            Id = Guid.CreateVersion7(),
            DisplayName = displayName.Trim(),
            Email = NormalizeEmail(email),
            PasswordHash = passwordHash,
            IsActive = true,
            CreatedAt = at,
            UpdatedAt = at,
        };
    }

    public void UpdateProfile(string displayName, string? avatarUrl, string? city, string? region, string? bio, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
        AvatarUrl = Blank(avatarUrl);
        City = Blank(city);
        Region = Blank(region);
        Bio = Blank(bio);
        UpdatedAt = Timestamps.ToStoragePrecision(now);
    }

    /// <summary>Sets the avatar only if none is set (e.g. from an OAuth profile picture).</summary>
    public void UseAvatarIfMissing(string? avatarUrl, DateTime now)
    {
        if (AvatarUrl is null && !string.IsNullOrWhiteSpace(avatarUrl))
        {
            AvatarUrl = avatarUrl;
            UpdatedAt = Timestamps.ToStoragePrecision(now);
        }
    }

    public void SetPasswordHash(string passwordHash, DateTime now)
    {
        PasswordHash = passwordHash;
        UpdatedAt = Timestamps.ToStoragePrecision(now);
    }

    public void Deactivate(DateTime now)
    {
        IsActive = false;
        UpdatedAt = Timestamps.ToStoragePrecision(now);
    }

    /// <summary>Replaces platform preferences with <paramref name="platformIds"/> (duplicates ignored).</summary>
    public void ReplacePlatforms(IEnumerable<Guid> platformIds, DateTime now)
    {
        var at = Timestamps.ToStoragePrecision(now);
        var wanted = platformIds.ToHashSet();
        _platforms.RemoveAll(p => !wanted.Contains(p.PlatformId));
        foreach (var id in wanted.Where(id => _platforms.All(p => p.PlatformId != id)))
        {
            _platforms.Add(new UserPlatform(Id, id, at));
        }

        UpdatedAt = at;
    }

    /// <summary>Folds a new 1–5 score into the running average.</summary>
    public void ApplyRating(int score)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(score, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, 5);
        RatingAvg = Math.Round(((RatingAvg * RatingCount) + score) / (RatingCount + 1), 2, MidpointRounding.AwayFromZero);
        RatingCount++;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class UserPlatform
{
    private UserPlatform()
    {
    }

    internal UserPlatform(Guid userId, Guid platformId, DateTime createdAt)
    {
        UserId = userId;
        PlatformId = platformId;
        CreatedAt = createdAt;
    }

    public Guid UserId { get; private set; }
    public Guid PlatformId { get; private set; }
    public DateTime CreatedAt { get; private set; }
}

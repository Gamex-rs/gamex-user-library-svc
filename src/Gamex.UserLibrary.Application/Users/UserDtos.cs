using System.Text.Json.Serialization;

namespace Gamex.UserLibrary.Application.Users;

public sealed record PublicUserDto(
    Guid Id,
    string DisplayName,
    string? AvatarUrl,
    string? City,
    string? Region,
    string? Bio,
    DateOnly MemberSince,
    decimal RatingAvg,
    int RatingCount);

/// <summary>Own profile including private fields and platform preferences.</summary>
public sealed record MeDto(
    Guid Id,
    string Email,
    string DisplayName,
    string? AvatarUrl,
    string? City,
    string? Region,
    string Country,
    string? Bio,
    string Role,
    DateOnly MemberSince,
    decimal RatingAvg,
    int RatingCount,
    IReadOnlyList<Guid> PlatformIds,
    bool HasPassword);

public sealed record PlatformDto(Guid PlatformId, string Name, string Slug);

public sealed record AuthResult(string AccessToken, string RefreshToken, int ExpiresIn, MeDto User)
{
    /// <summary>For the HttpOnly refresh cookie; not part of the JSON body.</summary>
    [JsonIgnore]
    public DateTime RefreshTokenExpiresAt { get; init; }
}

public sealed record RatingDto(Guid Id, Guid RaterId, string RaterName, string? RaterAvatarUrl, int Score, string? Comment, DateTime CreatedAt);

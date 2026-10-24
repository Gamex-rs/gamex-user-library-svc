using Gamex.UserLibrary.Domain.Catalog;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gamex.UserLibrary.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<UserPlatform> UserPlatforms { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<OAuthLogin> OAuthLogins { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<LibraryEntry> LibraryEntries { get; }
    DbSet<ListingCompletion> ListingCompletions { get; }
    DbSet<CompletedTransaction> Transactions { get; }
    DbSet<UserRating> Ratings { get; }
    DbSet<CatalogGame> CatalogGames { get; }
    DbSet<CatalogPlatform> CatalogPlatforms { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>True if the exception is a unique-key violation (e.g. a concurrent insert of the same entry).</summary>
    bool IsUniqueViolation(DbUpdateException exception);

    /// <summary>Detaches all tracked entities, e.g. after a failed save.</summary>
    void DiscardChanges();
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

public sealed record AccessToken(string Token, int ExpiresInSeconds);

/// <summary>Data carried by a listing contact token, verified by the Messaging Service.</summary>
public sealed record ListingContact(Guid ListingId, Guid OwnerId, string OwnerName, string? OwnerAvatarUrl, string GameTitle);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);

    /// <summary>Signed, short-lived proof of a listing's owner and title for starting a conversation.</summary>
    string CreateContactToken(ListingContact contact);

    /// <summary>A cryptographically random opaque token (refresh / password reset).</summary>
    string CreateOpaqueToken();

    /// <summary>SHA-256 hex of an opaque token; only hashes are stored.</summary>
    string Hash(string opaqueToken);
}

public interface IEmailSender
{
    Task SendPasswordResetAsync(string email, string displayName, Uri resetUrl, CancellationToken cancellationToken);
}

public sealed record ExternalProfile(string ProviderUid, string? Email, bool EmailVerified, string? Name, string? AvatarUrl);

public interface IOAuthClient
{
    string Provider { get; }

    bool IsConfigured { get; }

    Uri GetAuthorizationUrl(string state, Uri redirectUri);

    /// <returns>The profile, or <c>null</c> if the code was rejected.</returns>
    Task<ExternalProfile?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken);
}

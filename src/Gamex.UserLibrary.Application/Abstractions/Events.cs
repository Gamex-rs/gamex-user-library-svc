using Gamex.UserLibrary.Domain.Library;

namespace Gamex.UserLibrary.Application.Abstractions;

/// <summary><c>library.game.status.changed</c> → <c>gamex-library-events</c> (Game Catalog stats).</summary>
public sealed record GameStatusChanged(Guid GameId, Guid UserId, StatusDelta Delta, DateTime OccurredAt);

/// <summary><c>listing.created</c>, one per wishlist match (SPEC-notification §7).</summary>
public sealed record ListingCreated(
    Guid ListingId,
    Guid GameId,
    string GameTitle,
    Guid UserId,
    string SellerName,
    ListingType ListingType,
    Guid PlatformId,
    string PlatformSlug,
    GameFormat Format,
    Guid NotifyUserId,
    DateTime OccurredAt);

public sealed record ListingExpired(Guid ListingId, string GameTitle, Guid UserId, DateTime ExpiresAt, DateTime OccurredAt);

public sealed record TransactionCompleted(
    Guid TransactionId,
    Guid ListingEntryId,
    string GameTitle,
    Guid UserIdA,
    string UserNameA,
    Guid UserIdB,
    string UserNameB,
    DateTime OccurredAt);

/// <summary>
/// Publishes after the database transaction commits. Failures are logged by the implementation; the change is
/// already saved (an outbox can be added later, as for the Game Catalog Service).
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(IReadOnlyCollection<GameStatusChanged> events, CancellationToken cancellationToken);

    Task PublishAsync(IReadOnlyCollection<ListingCreated> events, CancellationToken cancellationToken);

    Task PublishAsync(ListingExpired message, CancellationToken cancellationToken);

    Task PublishAsync(TransactionCompleted message, CancellationToken cancellationToken);
}

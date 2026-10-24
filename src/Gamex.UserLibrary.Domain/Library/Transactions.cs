namespace Gamex.UserLibrary.Domain.Library;

/// <summary>
/// One party's confirmation that the deal behind a listing happened. The owner names the partner; the partner's
/// confirmation implicitly names the owner. Scoped to a listing cycle (<see cref="ListedAt"/>) so a relisted entry
/// can complete again.
/// </summary>
public sealed class ListingCompletion
{
    private ListingCompletion()
    {
    }

    public Guid Id { get; private set; }
    public Guid LibraryEntryId { get; private set; }
    public DateTime ListedAt { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PartnerUserId { get; private set; }
    public DateTime ConfirmedAt { get; private set; }

    public static ListingCompletion Confirm(Guid entryId, DateTime listedAt, Guid userId, Guid partnerUserId, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        LibraryEntryId = entryId,
        ListedAt = listedAt,
        UserId = userId,
        PartnerUserId = partnerUserId,
        ConfirmedAt = Timestamps.ToStoragePrecision(now),
    };

    public void ChangePartner(Guid partnerUserId, DateTime now)
    {
        PartnerUserId = partnerUserId;
        ConfirmedAt = Timestamps.ToStoragePrecision(now);
    }
}

/// <summary>A completed deal between a listing owner and a partner; ratings refer to it.</summary>
public sealed class CompletedTransaction
{
    private CompletedTransaction()
    {
    }

    public Guid Id { get; private set; }
    public Guid LibraryEntryId { get; private set; }
    public Guid GameId { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid PartnerId { get; private set; }
    public DateTime CompletedAt { get; private set; }

    public static CompletedTransaction Record(LibraryEntry entry, Guid partnerId, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        LibraryEntryId = entry.Id,
        GameId = entry.GameId,
        OwnerId = entry.UserId,
        PartnerId = partnerId,
        CompletedAt = Timestamps.ToStoragePrecision(now),
    };

    public bool Involves(Guid userId) => userId == OwnerId || userId == PartnerId;

    public Guid CounterpartOf(Guid userId) => userId == OwnerId ? PartnerId : OwnerId;
}

public sealed class UserRating
{
    private UserRating()
    {
    }

    public Guid Id { get; private set; }
    public Guid RaterId { get; private set; }
    public Guid RateeId { get; private set; }
    public Guid TransactionId { get; private set; }
    public int Score { get; private set; }
    public string? Comment { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static UserRating Create(Guid raterId, Guid rateeId, Guid transactionId, int score, string? comment, DateTime now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(score, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, 5);
        return new UserRating
        {
            Id = Guid.CreateVersion7(),
            RaterId = raterId,
            RateeId = rateeId,
            TransactionId = transactionId,
            Score = score,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            CreatedAt = Timestamps.ToStoragePrecision(now),
        };
    }
}

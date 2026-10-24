namespace Gamex.UserLibrary.Domain.Library;

/// <summary>User-editable fields of an entry.</summary>
public sealed record EntryValues(
    bool IsOwned,
    bool IsWishlist,
    bool ForExchange,
    bool ForSale,
    bool WantToBuy,
    GameCondition? Condition,
    decimal? Price,
    string? PreferredGames,
    string? Notes);

/// <summary>What an update changed about the entry's listing, for event publishing.</summary>
public sealed record ListingChange(bool BecameOffered, IReadOnlyList<ListingType> OfferedTypes);

/// <summary>
/// One copy of a game in a user's library: game + platform + format. Listings are a view of entries:
/// an entry with any of ForExchange / ForSale / WantToBuy set has an active listing until it expires or completes.
/// </summary>
public sealed class LibraryEntry
{
    private LibraryEntry()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid GameId { get; private set; }
    public Guid PlatformId { get; private set; }
    public GameFormat Format { get; private set; }
    public bool IsOwned { get; private set; }
    public bool IsWishlist { get; private set; }
    public bool ForExchange { get; private set; }
    public bool ForSale { get; private set; }
    public bool WantToBuy { get; private set; }

    /// <summary>Physical copies only.</summary>
    public GameCondition? Condition { get; private set; }
    public decimal? Price { get; private set; }
    public string? PreferredGames { get; private set; }
    public string? Notes { get; private set; }
    public ListingStatus ListingStatus { get; private set; }

    /// <summary>When the current listing became active; also identifies the listing cycle for completions.</summary>
    public DateTime? ListedAt { get; private set; }
    public DateTime? ListingExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public StatusFlags Flags => new(IsOwned, IsWishlist, ForExchange, ForSale, WantToBuy);

    public bool IsListed => ForExchange || ForSale || WantToBuy;

    /// <summary>Exchange or sale, i.e. a copy is offered to others (wishlist matching applies).</summary>
    public bool IsOffered => ListingStatus == ListingStatus.Active && (ForExchange || ForSale);

    public IReadOnlyList<ListingType> ListingTypes =>
    [
        .. ForExchange ? [ListingType.Exchange] : Array.Empty<ListingType>(),
        .. ForSale ? [ListingType.Sale] : Array.Empty<ListingType>(),
        .. WantToBuy ? [ListingType.WantToBuy] : Array.Empty<ListingType>(),
    ];

    public static (LibraryEntry Entry, ListingChange Change) Create(
        Guid userId, Guid gameId, Guid platformId, GameFormat format, EntryValues values, DateTime now, TimeSpan listingLifetime)
    {
        var at = Timestamps.ToStoragePrecision(now);
        var entry = new LibraryEntry
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            GameId = gameId,
            PlatformId = platformId,
            Format = format,
            ListingStatus = ListingStatus.None,
            CreatedAt = at,
        };
        var change = entry.Apply(values, at, listingLifetime);
        return (entry, change);
    }

    public ListingChange Update(EntryValues values, DateTime now, TimeSpan listingLifetime) =>
        Apply(values, Timestamps.ToStoragePrecision(now), listingLifetime);

    public void MoveTo(Guid platformId, GameFormat format, DateTime now)
    {
        PlatformId = platformId;
        Format = format;
        if (format == GameFormat.Digital)
        {
            Condition = null;
        }

        UpdatedAt = Timestamps.ToStoragePrecision(now);
    }

    /// <summary>Background job: ends an active listing past its expiry and withdraws its offers.</summary>
    /// <returns><c>true</c> if the listing expired.</returns>
    public bool ExpireIfDue(DateTime now)
    {
        if (ListingStatus != ListingStatus.Active || ListingExpiresAt is null || ListingExpiresAt > now)
        {
            return false;
        }

        WithdrawOffers(ListingStatus.Expired, now);
        return true;
    }

    /// <summary>Both parties confirmed the transaction: the listing is done and its offers are withdrawn.</summary>
    public void Complete(DateTime now) => WithdrawOffers(ListingStatus.Completed, now);

    private void WithdrawOffers(ListingStatus status, DateTime now)
    {
        // Clearing the flags keeps catalog stats accurate and means relisting is an explicit choice.
        ForExchange = false;
        ForSale = false;
        WantToBuy = false;
        ListingStatus = status;
        ListingExpiresAt = null;
        UpdatedAt = Timestamps.ToStoragePrecision(now);
    }

    private ListingChange Apply(EntryValues values, DateTime at, TimeSpan listingLifetime)
    {
        var wasOffered = IsOffered;

        IsOwned = values.IsOwned;
        IsWishlist = values.IsWishlist;
        ForExchange = values.ForExchange;
        ForSale = values.ForSale;
        WantToBuy = values.WantToBuy;
        Condition = Format == GameFormat.Physical ? values.Condition : null;
        Price = values.Price;
        PreferredGames = Blank(values.PreferredGames);
        Notes = Blank(values.Notes);
        UpdatedAt = at;

        if (IsListed && ListingStatus != ListingStatus.Active)
        {
            ListingStatus = ListingStatus.Active;
            ListedAt = at;
            ListingExpiresAt = at + listingLifetime;
        }
        else if (!IsListed && ListingStatus == ListingStatus.Active)
        {
            ListingStatus = ListingStatus.None;
            ListingExpiresAt = null;
        }

        var becameOffered = !wasOffered && IsOffered;
        return new ListingChange(becameOffered, becameOffered ? ListingTypes.Where(t => t != ListingType.WantToBuy).ToList() : []);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

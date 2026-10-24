using Gamex.UserLibrary.Domain.Library;

namespace Gamex.UserLibrary.Application.Library;

public sealed record GameRefDto(Guid Id, string Title, string? CoverImageUrl, DateOnly? ReleaseDate, short? Metacritic);

public sealed record PlatformRefDto(Guid Id, string Name, string Slug);

/// <summary>Library entry joined with the catalog replica (SPEC-user-library §4.3 "Library entry — Response shape").</summary>
public sealed record LibraryEntryDto(
    Guid EntryId,
    GameRefDto Game,
    PlatformRefDto Platform,
    GameFormat Format,
    bool IsOwned,
    bool IsWishlist,
    bool ForExchange,
    bool ForSale,
    bool WantToBuy,
    GameCondition? Condition,
    decimal? Price,
    string? PreferredGames,
    string? Notes,
    ListingStatus ListingStatus,
    DateTime? ListedAt,
    DateTime? ListingExpiresAt);

public sealed record EntryStatusDto(
    Guid EntryId,
    Guid PlatformId,
    string PlatformName,
    string PlatformSlug,
    GameFormat Format,
    bool IsOwned,
    bool IsWishlist);

/// <summary>Per game: aggregate flags plus every copy (SPEC-user-library §4.3 statuses).</summary>
public sealed record GameLibraryStatusDto(bool IsOwned, bool IsWishlist, IReadOnlyList<EntryStatusDto> Entries);

/// <summary>Add (all required fields set) or partial update (null = unchanged; "" clears a text field).</summary>
public sealed record EntryCommand(
    Guid? GameId,
    Guid? PlatformId,
    GameFormat? Format,
    bool? IsOwned,
    bool? IsWishlist,
    bool? ForExchange,
    bool? ForSale,
    bool? WantToBuy,
    GameCondition? Condition,
    decimal? Price,
    string? PreferredGames,
    string? Notes);

public sealed record LibraryFilter(string? Status, Guid? GameId, Guid? PlatformId, GameFormat? Format);

public sealed record SellerDto(Guid Id, string DisplayName, string? AvatarUrl, string? City, decimal RatingAvg, int RatingCount);

public sealed record ListingDto(
    Guid Id,
    IReadOnlyList<ListingType> Types,
    GameRefDto Game,
    PlatformRefDto Platform,
    GameFormat Format,
    GameCondition? Condition,
    decimal? Price,
    string? PreferredGames,
    string? Notes,
    ListingStatus Status,
    DateTime? ListedAt,
    DateTime? ExpiresAt,
    SellerDto Seller);

/// <summary>Listing detail with a signed contact token for starting a conversation (Messaging Service).</summary>
public sealed record ListingDetailDto(ListingDto Listing, string? ContactToken);

public sealed record ListingFilter(
    ListingType? Type,
    Guid? GameId,
    string? Platform,
    GameFormat? Format,
    GameCondition? Condition,
    string? City,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Q,
    string? Cursor,
    int? PageSize);

public sealed record CompletionResult(bool Completed, Guid? TransactionId, Guid? WaitingForUserId);

using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Domain.Catalog;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.Domain.Users;

namespace Gamex.UserLibrary.Application.Library;

/// <summary>Entry joined with its catalog game, platform and owner.</summary>
/// <remarks>Init properties, not a positional record: EF translates member access through an object initializer,
/// but not through constructor arguments, so later filters like <c>r.Entry.Id == id</c> would fail.</remarks>
internal sealed class EntryRow
{
    public required LibraryEntry Entry { get; init; }
    public required CatalogGame Game { get; init; }
    public required CatalogPlatform Platform { get; init; }
    public required User Owner { get; init; }
}

internal static class LibraryQueries
{
    public static IQueryable<EntryRow> Rows(IAppDbContext db) =>
        from e in db.LibraryEntries
        join g in db.CatalogGames on e.GameId equals g.Id
        join p in db.CatalogPlatforms on e.PlatformId equals p.Id
        join u in db.Users on e.UserId equals u.Id
        select new EntryRow { Entry = e, Game = g, Platform = p, Owner = u };

    /// <summary>Active listings of active users.</summary>
    public static IQueryable<EntryRow> ActiveListings(IAppDbContext db) =>
        Rows(db).Where(r => r.Entry.ListingStatus == ListingStatus.Active && r.Owner.IsActive);

    public static LibraryEntryDto ToEntryDto(EntryRow r) => new(
        r.Entry.Id,
        ToGame(r.Game),
        ToPlatform(r.Platform),
        r.Entry.Format,
        r.Entry.IsOwned,
        r.Entry.IsWishlist,
        r.Entry.ForExchange,
        r.Entry.ForSale,
        r.Entry.WantToBuy,
        r.Entry.Condition,
        r.Entry.Price,
        r.Entry.PreferredGames,
        r.Entry.Notes,
        r.Entry.ListingStatus,
        r.Entry.ListedAt,
        r.Entry.ListingExpiresAt);

    public static ListingDto ToListingDto(EntryRow r) => new(
        r.Entry.Id,
        r.Entry.ListingTypes,
        ToGame(r.Game),
        ToPlatform(r.Platform),
        r.Entry.Format,
        r.Entry.Condition,
        r.Entry.Price,
        r.Entry.PreferredGames,
        r.Entry.Notes,
        r.Entry.ListingStatus,
        r.Entry.ListedAt,
        r.Entry.ListingExpiresAt,
        new SellerDto(r.Owner.Id, r.Owner.DisplayName, r.Owner.AvatarUrl, r.Owner.City, r.Owner.RatingAvg, r.Owner.RatingCount));

    private static GameRefDto ToGame(CatalogGame g) => new(g.Id, g.Title, g.CoverImageUrl, g.ReleaseDate, g.Metacritic);

    private static PlatformRefDto ToPlatform(CatalogPlatform p) => new(p.Id, p.Name, p.Slug);
}

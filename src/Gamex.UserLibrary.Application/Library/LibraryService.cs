using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Common;
using Gamex.UserLibrary.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace Gamex.UserLibrary.Application.Library;

public interface ILibraryService
{
    Task<OperationResult<IReadOnlyList<LibraryEntryDto>>> ListOwnAsync(Guid userId, LibraryFilter filter, CancellationToken ct);

    Task<OperationResult<IReadOnlyDictionary<Guid, GameLibraryStatusDto?>>> GetStatusesAsync(Guid userId, IReadOnlyList<Guid> gameIds, CancellationToken ct);

    /// <summary>Another user's public library: active listings only.</summary>
    Task<OperationResult<IReadOnlyList<ListingDto>>> GetPublicLibraryAsync(Guid userId, CancellationToken ct);

    Task<OperationResult<LibraryEntryDto>> AddAsync(Guid userId, EntryCommand command, CancellationToken ct);

    Task<OperationResult<LibraryEntryDto>> UpdateAsync(Guid userId, Guid entryId, EntryCommand command, CancellationToken ct);

    Task<OperationResult<bool>> DeleteAsync(Guid userId, Guid entryId, CancellationToken ct);
}

internal sealed class LibraryService(
    IAppDbContext db,
    IEventPublisher events,
    LibraryOptions options,
    TimeProvider time) : ILibraryService
{
    public const int MaxStatusGameIds = 50;
    public const int MaxTextLength = 2000;
    public const decimal MaxPrice = 1_000_000m;
    public const string GameNotSyncedType = "game-not-synced";

    private static readonly Dictionary<string, Func<LibraryEntry, bool>> StatusFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["owned"] = e => e.IsOwned,
        ["wishlist"] = e => e.IsWishlist,
        ["for_exchange"] = e => e.ForExchange,
        ["for_sale"] = e => e.ForSale,
        ["want_to_buy"] = e => e.WantToBuy,
    };

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<OperationResult<IReadOnlyList<LibraryEntryDto>>> ListOwnAsync(Guid userId, LibraryFilter filter, CancellationToken ct)
    {
        if (filter.Status is not null && !StatusFilters.ContainsKey(filter.Status))
        {
            return OperationResult<IReadOnlyList<LibraryEntryDto>>.Invalid("status", "Allowed values: owned, wishlist, for_exchange, for_sale, want_to_buy.");
        }

        var rows = LibraryQueries.Rows(db).AsNoTracking().Where(r => r.Entry.UserId == userId);
        rows = filter.Status?.ToLowerInvariant() switch
        {
            "owned" => rows.Where(r => r.Entry.IsOwned),
            "wishlist" => rows.Where(r => r.Entry.IsWishlist),
            "for_exchange" => rows.Where(r => r.Entry.ForExchange),
            "for_sale" => rows.Where(r => r.Entry.ForSale),
            "want_to_buy" => rows.Where(r => r.Entry.WantToBuy),
            _ => rows,
        };

        if (filter.GameId is { } gameId)
        {
            rows = rows.Where(r => r.Entry.GameId == gameId);
        }

        if (filter.PlatformId is { } platformId)
        {
            rows = rows.Where(r => r.Entry.PlatformId == platformId);
        }

        if (filter.Format is { } format)
        {
            rows = rows.Where(r => r.Entry.Format == format);
        }

        var list = await rows.OrderBy(r => r.Game.Title).ThenBy(r => r.Platform.Name).ThenBy(r => r.Entry.Format).ToListAsync(ct);
        return OperationResult<IReadOnlyList<LibraryEntryDto>>.Ok(list.Select(LibraryQueries.ToEntryDto).ToList());
    }

    public async Task<OperationResult<IReadOnlyDictionary<Guid, GameLibraryStatusDto?>>> GetStatusesAsync(Guid userId, IReadOnlyList<Guid> gameIds, CancellationToken ct)
    {
        var ids = gameIds.Distinct().ToList();
        if (ids.Count > MaxStatusGameIds)
        {
            return OperationResult<IReadOnlyDictionary<Guid, GameLibraryStatusDto?>>.Invalid("gameIds", $"At most {MaxStatusGameIds} ids.");
        }

        var entries = await (from e in db.LibraryEntries.AsNoTracking()
                             join p in db.CatalogPlatforms on e.PlatformId equals p.Id
                             where e.UserId == userId && ids.Contains(e.GameId)
                             orderby p.Name, e.Format
                             select new { e.GameId, Status = new EntryStatusDto(e.Id, p.Id, p.Name, p.Slug, e.Format, e.IsOwned, e.IsWishlist) })
            .ToListAsync(ct);

        var byGame = entries.GroupBy(x => x.GameId).ToDictionary(g => g.Key, g => g.Select(x => x.Status).ToList());
        IReadOnlyDictionary<Guid, GameLibraryStatusDto?> result = ids.ToDictionary(
            id => id,
            id => byGame.TryGetValue(id, out var copies)
                ? new GameLibraryStatusDto(copies.Any(c => c.IsOwned), copies.Any(c => c.IsWishlist), copies)
                : null);

        return OperationResult<IReadOnlyDictionary<Guid, GameLibraryStatusDto?>>.Ok(result);
    }

    public async Task<OperationResult<IReadOnlyList<ListingDto>>> GetPublicLibraryAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct))
        {
            return OperationResult<IReadOnlyList<ListingDto>>.NotFound($"No user with id {userId}.");
        }

        var rows = await LibraryQueries.ActiveListings(db).AsNoTracking()
            .Where(r => r.Entry.UserId == userId)
            .OrderByDescending(r => r.Entry.ListedAt)
            .ToListAsync(ct);
        return OperationResult<IReadOnlyList<ListingDto>>.Ok(rows.Select(LibraryQueries.ToListingDto).ToList());
    }

    public async Task<OperationResult<LibraryEntryDto>> AddAsync(Guid userId, EntryCommand command, CancellationToken ct)
    {
        var validation = new Validation()
            .Require(command.GameId is not null, "gameId", "Required.")
            .Require(command.PlatformId is not null, "platformId", "Required.")
            .Require(command.Format is not null, "format", "Required: physical or digital.");
        if (!validation.IsValid)
        {
            return OperationResult<LibraryEntryDto>.Invalid(validation.Errors);
        }

        var values = new EntryValues(
            command.IsOwned ?? false, command.IsWishlist ?? false, command.ForExchange ?? false, command.ForSale ?? false,
            command.WantToBuy ?? false, command.Condition, command.Price, command.PreferredGames, command.Notes);
        var invalid = ValidateValues(command.Format!.Value, values);
        if (invalid is not null)
        {
            return invalid;
        }

        var placement = await CheckPlacementAsync(userId, command.GameId!.Value, command.PlatformId!.Value, command.Format.Value, exceptEntryId: null, ct);
        if (placement is not null)
        {
            return placement;
        }

        await using var transaction = await db.BeginTransactionAsync(ct);
        var before = await AggregateAsync(userId, command.GameId.Value, ct);

        var (entry, change) = LibraryEntry.Create(userId, command.GameId.Value, command.PlatformId.Value, command.Format.Value, values, Now, options.ListingLifetime);
        db.LibraryEntries.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            return await DuplicateAsync(userId, command.GameId.Value, command.PlatformId.Value, command.Format.Value, ct);
        }

        var after = await AggregateAsync(userId, command.GameId.Value, ct);
        await transaction.CommitAsync(ct);

        await PublishAsync(entry, before, after, change, ct);
        return OperationResult<LibraryEntryDto>.Ok(await LoadDtoAsync(entry.Id, ct));
    }

    public async Task<OperationResult<LibraryEntryDto>> UpdateAsync(Guid userId, Guid entryId, EntryCommand command, CancellationToken ct)
    {
        if (command.GameId is { } gameId)
        {
            var current = await db.LibraryEntries.AsNoTracking().Where(e => e.Id == entryId && e.UserId == userId).Select(e => (Guid?)e.GameId).SingleOrDefaultAsync(ct);
            if (current is not null && current != gameId)
            {
                return OperationResult<LibraryEntryDto>.Invalid("gameId", "The game of an entry cannot be changed; add a new entry instead.");
            }
        }

        await using var transaction = await db.BeginTransactionAsync(ct);
        var entry = await db.LibraryEntries.SingleOrDefaultAsync(e => e.Id == entryId && e.UserId == userId, ct);
        if (entry is null)
        {
            return OperationResult<LibraryEntryDto>.NotFound($"No library entry with id {entryId}.");
        }

        var platformId = command.PlatformId ?? entry.PlatformId;
        var format = command.Format ?? entry.Format;
        var values = new EntryValues(
            command.IsOwned ?? entry.IsOwned,
            command.IsWishlist ?? entry.IsWishlist,
            command.ForExchange ?? entry.ForExchange,
            command.ForSale ?? entry.ForSale,
            command.WantToBuy ?? entry.WantToBuy,
            format == GameFormat.Digital ? null : command.Condition ?? entry.Condition,
            command.Price ?? entry.Price,
            command.PreferredGames ?? entry.PreferredGames,
            command.Notes ?? entry.Notes);

        if (format == GameFormat.Digital && command.Condition is not null)
        {
            return OperationResult<LibraryEntryDto>.Invalid("condition", "Only physical copies have a condition.");
        }

        var invalid = ValidateValues(format, values);
        if (invalid is not null)
        {
            return invalid;
        }

        if (platformId != entry.PlatformId || format != entry.Format)
        {
            var placement = await CheckPlacementAsync(userId, entry.GameId, platformId, format, entry.Id, ct);
            if (placement is not null)
            {
                return placement;
            }

            entry.MoveTo(platformId, format, Now);
        }

        // Read before saving: the database still holds the old flags.
        var before = await AggregateAsync(userId, entry.GameId, ct);
        var change = entry.Update(values, Now, options.ListingLifetime);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            return await DuplicateAsync(userId, entry.GameId, platformId, format, ct);
        }

        var after = await AggregateAsync(userId, entry.GameId, ct);
        await transaction.CommitAsync(ct);

        await PublishAsync(entry, before, after, change, ct);
        return OperationResult<LibraryEntryDto>.Ok(await LoadDtoAsync(entry.Id, ct));
    }

    public async Task<OperationResult<bool>> DeleteAsync(Guid userId, Guid entryId, CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);
        var entry = await db.LibraryEntries.SingleOrDefaultAsync(e => e.Id == entryId && e.UserId == userId, ct);
        if (entry is null)
        {
            return OperationResult<bool>.NotFound($"No library entry with id {entryId}.");
        }

        var before = await AggregateAsync(userId, entry.GameId, ct);
        db.LibraryEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        var after = await AggregateAsync(userId, entry.GameId, ct);
        await transaction.CommitAsync(ct);

        await PublishDeltaAsync(entry.GameId, userId, before, after, ct);
        return OperationResult<bool>.Ok(true);
    }

    private Task<StatusFlags> AggregateAsync(Guid userId, Guid gameId, CancellationToken ct) =>
        new LibraryAggregates(db).AggregateAsync(userId, gameId, ct);

    private static OperationResult<LibraryEntryDto>? ValidateValues(GameFormat format, EntryValues values)
    {
        var validation = new Validation()
            .Require(values.IsOwned || values.IsWishlist || values.ForExchange || values.ForSale || values.WantToBuy, "statuses", "Select at least one status.")
            .Require(format == GameFormat.Physical || values.Condition is null, "condition", "Only physical copies have a condition.")
            .Require(values.Price is null or >= 0 and <= MaxPrice && (values.Price is null || decimal.Round(values.Price.Value, 2) == values.Price), "price", $"Must be between 0 and {MaxPrice} with at most 2 decimals.")
            .Require(!values.ForSale || values.Price is not null, "price", "Required when the game is for sale.")
            .Require(values.PreferredGames is null || values.PreferredGames.Length <= MaxTextLength, "preferredGames", $"At most {MaxTextLength} characters.")
            .Require(values.Notes is null || values.Notes.Length <= MaxTextLength, "notes", $"At most {MaxTextLength} characters.");
        return validation.IsValid ? null : OperationResult<LibraryEntryDto>.Invalid(validation.Errors);
    }

    /// <summary>Game must be in the replica, platform must be one of the game's, and the copy must be new for the user.</summary>
    private async Task<OperationResult<LibraryEntryDto>?> CheckPlacementAsync(
        Guid userId, Guid gameId, Guid platformId, GameFormat format, Guid? exceptEntryId, CancellationToken ct)
    {
        var game = await db.CatalogGames.AsNoTracking().Include(g => g.Platforms).SingleOrDefaultAsync(g => g.Id == gameId, ct);
        if (game is null)
        {
            // The catalog event for a just-imported game may not have arrived yet (SPEC-user-library §4.3).
            return OperationResult<LibraryEntryDto>.Conflict(
                "The game is still being added to Gamex. Try again in a moment.",
                new Dictionary<string, object?> { ["type"] = GameNotSyncedType, ["retryAfter"] = 2 });
        }

        if (!game.IsAvailableOn(platformId))
        {
            return OperationResult<LibraryEntryDto>.Invalid("platformId", "The game is not available on this platform.");
        }

        var existing = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.GameId == gameId && e.PlatformId == platformId && e.Format == format && e.Id != exceptEntryId)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
        return existing is { } id ? DuplicateResult(id) : null;
    }

    private async Task<OperationResult<LibraryEntryDto>> DuplicateAsync(Guid userId, Guid gameId, Guid platformId, GameFormat format, CancellationToken ct)
    {
        db.DiscardChanges();
        var id = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.GameId == gameId && e.PlatformId == platformId && e.Format == format)
            .Select(e => e.Id)
            .FirstAsync(ct);
        return DuplicateResult(id);
    }

    private static OperationResult<LibraryEntryDto> DuplicateResult(Guid existingEntryId) =>
        OperationResult<LibraryEntryDto>.Conflict(
            "This game is already in your library on this platform and in this format.",
            new Dictionary<string, object?> { ["entryId"] = existingEntryId });

    private async Task<LibraryEntryDto> LoadDtoAsync(Guid entryId, CancellationToken ct)
    {
        var row = await LibraryQueries.Rows(db).AsNoTracking().SingleAsync(r => r.Entry.Id == entryId, ct);
        return LibraryQueries.ToEntryDto(row);
    }

    private async Task PublishAsync(LibraryEntry entry, StatusFlags before, StatusFlags after, ListingChange change, CancellationToken ct)
    {
        await PublishDeltaAsync(entry.GameId, entry.UserId, before, after, ct);
        if (change.BecameOffered)
        {
            await PublishWishlistMatchesAsync(entry, change, ct);
        }
    }

    private async Task PublishDeltaAsync(Guid gameId, Guid userId, StatusFlags before, StatusFlags after, CancellationToken ct)
    {
        var delta = StatusFlags.Delta(before, after);
        if (!delta.IsZero)
        {
            await events.PublishAsync([new GameStatusChanged(gameId, userId, delta, Now)], ct);
        }
    }

    /// <summary>One <c>listing.created</c> per user wishing for the same game on the same platform (any format).</summary>
    private async Task PublishWishlistMatchesAsync(LibraryEntry entry, ListingChange change, CancellationToken ct)
    {
        var listing = await LibraryQueries.Rows(db).AsNoTracking()
            .Where(r => r.Entry.Id == entry.Id)
            .Select(r => new { r.Game.Title, r.Platform.Slug, SellerName = r.Owner.DisplayName })
            .SingleAsync(ct);

        var matches = await (from e in db.LibraryEntries.AsNoTracking()
                             join u in db.Users on e.UserId equals u.Id
                             where e.GameId == entry.GameId && e.PlatformId == entry.PlatformId && e.IsWishlist
                                   && e.UserId != entry.UserId && u.IsActive
                             select e.UserId)
            .Distinct()
            .ToListAsync(ct);

        if (matches.Count == 0)
        {
            return;
        }

        // One notification per user even if the copy is offered for exchange and sale; sale is the stronger signal.
        var type = change.OfferedTypes.Contains(ListingType.Sale) ? ListingType.Sale : ListingType.Exchange;
        var created = matches.Select(userId => new ListingCreated(
            entry.Id, entry.GameId, listing.Title, entry.UserId, listing.SellerName, type,
            entry.PlatformId, listing.Slug, entry.Format, userId, Now)).ToList();
        await events.PublishAsync(created, ct);
    }
}

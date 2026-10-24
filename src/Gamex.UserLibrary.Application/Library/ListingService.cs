using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Common;
using Gamex.UserLibrary.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace Gamex.UserLibrary.Application.Library;

public interface IListingService
{
    Task<OperationResult<CursorPage<ListingDto>>> BrowseAsync(ListingFilter filter, CancellationToken ct);

    Task<OperationResult<ListingDetailDto>> GetAsync(Guid listingId, CancellationToken ct);

    /// <summary>
    /// Records the caller's confirmation that the deal happened. The owner must name the partner; the partner's
    /// confirmation names the owner. When both have confirmed each other the transaction is completed.
    /// </summary>
    Task<OperationResult<CompletionResult>> CompleteAsync(Guid listingId, Guid callerId, Guid? partnerUserId, CancellationToken ct);
}

internal sealed class ListingService(
    IAppDbContext db,
    ITokenService tokens,
    IEventPublisher events,
    TimeProvider time) : IListingService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<OperationResult<CursorPage<ListingDto>>> BrowseAsync(ListingFilter filter, CancellationToken ct)
    {
        var validation = new Validation()
            .Require(filter.PageSize is null or >= 1 and <= MaxPageSize, "pageSize", $"Must be between 1 and {MaxPageSize}.")
            .Require(filter.MinPrice is null or >= 0, "minPrice", "Must be 0 or greater.")
            .Require(filter.MaxPrice is null || filter.MinPrice is null || filter.MaxPrice >= filter.MinPrice, "maxPrice", "Must not be below minPrice.")
            .Require(filter.Cursor is null || Cursor.TryDecode(filter.Cursor, out _, out _), "cursor", "Invalid cursor.");
        if (!validation.IsValid)
        {
            return OperationResult<CursorPage<ListingDto>>.Invalid(validation.Errors);
        }

        var rows = LibraryQueries.ActiveListings(db).AsNoTracking();
        rows = filter.Type switch
        {
            ListingType.Exchange => rows.Where(r => r.Entry.ForExchange),
            ListingType.Sale => rows.Where(r => r.Entry.ForSale),
            ListingType.WantToBuy => rows.Where(r => r.Entry.WantToBuy),
            _ => rows,
        };

        if (filter.GameId is { } gameId)
        {
            rows = rows.Where(r => r.Entry.GameId == gameId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Platform))
        {
            var slug = filter.Platform.Trim();
            rows = rows.Where(r => r.Platform.Slug == slug);
        }

        if (filter.Format is { } format)
        {
            rows = rows.Where(r => r.Entry.Format == format);
        }

        if (filter.Condition is { } condition)
        {
            rows = rows.Where(r => r.Entry.Condition == condition);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            var city = filter.City.Trim();
            rows = rows.Where(r => r.Owner.City == city);
        }

        if (filter.MinPrice is { } min)
        {
            rows = rows.Where(r => r.Entry.Price >= min);
        }

        if (filter.MaxPrice is { } max)
        {
            rows = rows.Where(r => r.Entry.Price <= max);
        }

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var q = filter.Q.Trim();
            rows = rows.Where(r => r.Game.Title.Contains(q));
        }

        if (Cursor.TryDecode(filter.Cursor, out var at, out var lastId))
        {
            // Newest first; ids (time-ordered GUIDs stored as char(36)) break ties on ListedAt.
            var last = lastId.ToString();
#pragma warning disable CA1309 // Translated to SQL by EF; the StringComparison overload is not translatable.
            rows = rows.Where(r => r.Entry.ListedAt < at || (r.Entry.ListedAt == at && string.Compare(r.Entry.Id.ToString(), last) < 0));
#pragma warning restore CA1309
        }

        var size = filter.PageSize ?? DefaultPageSize;
        var page = await rows
            .OrderByDescending(r => r.Entry.ListedAt)
            .ThenByDescending(r => r.Entry.Id)
            .Take(size + 1)
            .ToListAsync(ct);

        var items = page.Take(size).Select(LibraryQueries.ToListingDto).ToList();
        var next = page.Count > size ? Cursor.Encode(items[^1].ListedAt!.Value, items[^1].Id) : null;
        return OperationResult<CursorPage<ListingDto>>.Ok(new CursorPage<ListingDto>(items, next));
    }

    public async Task<OperationResult<ListingDetailDto>> GetAsync(Guid listingId, CancellationToken ct)
    {
        var row = await LibraryQueries.ActiveListings(db).AsNoTracking().SingleOrDefaultAsync(r => r.Entry.Id == listingId, ct);
        if (row is null)
        {
            return OperationResult<ListingDetailDto>.NotFound($"No active listing with id {listingId}.");
        }

        var contactToken = tokens.CreateContactToken(new ListingContact(row.Entry.Id, row.Owner.Id, row.Owner.DisplayName, row.Owner.AvatarUrl, row.Game.Title));
        return OperationResult<ListingDetailDto>.Ok(new ListingDetailDto(LibraryQueries.ToListingDto(row), contactToken));
    }

    public async Task<OperationResult<CompletionResult>> CompleteAsync(Guid listingId, Guid callerId, Guid? partnerUserId, CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);

        var entry = await db.LibraryEntries.SingleOrDefaultAsync(e => e.Id == listingId, ct);
        if (entry is null || entry.ListedAt is null || entry.ListingStatus is not (ListingStatus.Active or ListingStatus.Expired))
        {
            return OperationResult<CompletionResult>.NotFound($"No open listing with id {listingId}.");
        }

        var ownerId = entry.UserId;
        var isOwner = callerId == ownerId;
        Guid partner;
        if (isOwner)
        {
            if (partnerUserId is not { } named || named == ownerId)
            {
                return OperationResult<CompletionResult>.Invalid("partnerUserId", "Name the user you completed the deal with.");
            }

            if (!await db.Users.AnyAsync(u => u.Id == named && u.IsActive, ct))
            {
                return OperationResult<CompletionResult>.Invalid("partnerUserId", "Unknown user.");
            }

            partner = named;
        }
        else
        {
            if (partnerUserId is { } named && named != ownerId)
            {
                return OperationResult<CompletionResult>.Invalid("partnerUserId", "Only the listing owner names a partner.");
            }

            partner = ownerId;
        }

        var listedAt = entry.ListedAt.Value;
        var mine = await db.ListingCompletions.SingleOrDefaultAsync(c => c.LibraryEntryId == entry.Id && c.ListedAt == listedAt && c.UserId == callerId, ct);
        if (mine is null)
        {
            db.ListingCompletions.Add(ListingCompletion.Confirm(entry.Id, listedAt, callerId, partner, Now));
        }
        else if (mine.PartnerUserId != partner)
        {
            mine.ChangePartner(partner, Now);
        }

        await db.SaveChangesAsync(ct);

        var completions = await db.ListingCompletions.AsNoTracking()
            .Where(c => c.LibraryEntryId == entry.Id && c.ListedAt == listedAt)
            .ToListAsync(ct);
        var ownerConfirmation = completions.SingleOrDefault(c => c.UserId == ownerId);
        var partnerConfirmed = ownerConfirmation is not null
                               && completions.Any(c => c.UserId == ownerConfirmation.PartnerUserId && c.PartnerUserId == ownerId);

        if (!partnerConfirmed)
        {
            await transaction.CommitAsync(ct);
            var waitingFor = ownerConfirmation is null ? ownerId : ownerConfirmation.PartnerUserId;
            return OperationResult<CompletionResult>.Ok(new CompletionResult(false, null, waitingFor));
        }

        var library = new LibraryAggregates(db);
        var before = await library.AggregateAsync(ownerId, entry.GameId, ct);
        var completed = CompletedTransaction.Record(entry, ownerConfirmation!.PartnerUserId, Now);
        db.Transactions.Add(completed);
        entry.Complete(Now);
        await db.SaveChangesAsync(ct);
        var after = await library.AggregateAsync(ownerId, entry.GameId, ct);
        await transaction.CommitAsync(ct);

        var delta = StatusFlags.Delta(before, after);
        if (!delta.IsZero)
        {
            await events.PublishAsync([new GameStatusChanged(entry.GameId, ownerId, delta, Now)], ct);
        }

        var names = await db.Users.AsNoTracking()
            .Where(u => u.Id == ownerId || u.Id == completed.PartnerId)
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var title = await db.CatalogGames.AsNoTracking().Where(g => g.Id == entry.GameId).Select(g => g.Title).SingleAsync(ct);
        await events.PublishAsync(
            new TransactionCompleted(completed.Id, entry.Id, title, ownerId, names[ownerId], completed.PartnerId, names[completed.PartnerId], Now),
            ct);

        return OperationResult<CompletionResult>.Ok(new CompletionResult(true, completed.Id, null));
    }
}

/// <summary>Shared aggregate-flag query (stats count users, not copies).</summary>
internal sealed class LibraryAggregates(IAppDbContext db)
{
    public async Task<StatusFlags> AggregateAsync(Guid userId, Guid gameId, CancellationToken ct)
    {
        var flags = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.GameId == gameId)
            .Select(e => new StatusFlags(e.IsOwned, e.IsWishlist, e.ForExchange, e.ForSale, e.WantToBuy))
            .ToListAsync(ct);
        return StatusFlags.Aggregate(flags);
    }
}

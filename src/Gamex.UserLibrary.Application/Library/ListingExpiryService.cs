using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Domain.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gamex.UserLibrary.Application.Library;

public interface IListingExpiryService
{
    /// <returns>Number of listings expired.</returns>
    Task<int> ExpireDueAsync(CancellationToken ct);
}

/// <summary>Hourly job (SPEC-user-library §7): expires listings past <c>listing_expires_at</c> and publishes the effects.</summary>
internal sealed partial class ListingExpiryService(
    IAppDbContext db,
    IEventPublisher events,
    TimeProvider time,
    ILogger<ListingExpiryService> logger) : IListingExpiryService
{
    public const int BatchSize = 100;

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var due = await db.LibraryEntries
                .Where(e => e.ListingStatus == ListingStatus.Active && e.ListingExpiresAt < now)
                .OrderBy(e => e.ListingExpiresAt)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (due.Count == 0)
            {
                break;
            }

            foreach (var entry in due)
            {
                await ExpireAsync(entry, now, ct);
            }

            total += due.Count;
        }

        if (total > 0)
        {
            LogExpired(logger, total);
        }

        return total;
    }

    private async Task ExpireAsync(LibraryEntry entry, DateTime now, CancellationToken ct)
    {
        var aggregates = new LibraryAggregates(db);
        var expiresAt = entry.ListingExpiresAt!.Value;

        await using var transaction = await db.BeginTransactionAsync(ct);
        var before = await aggregates.AggregateAsync(entry.UserId, entry.GameId, ct);
        entry.ExpireIfDue(now);
        await db.SaveChangesAsync(ct);
        var after = await aggregates.AggregateAsync(entry.UserId, entry.GameId, ct);
        await transaction.CommitAsync(ct);

        var delta = StatusFlags.Delta(before, after);
        if (!delta.IsZero)
        {
            await events.PublishAsync([new GameStatusChanged(entry.GameId, entry.UserId, delta, now)], ct);
        }

        var title = await db.CatalogGames.AsNoTracking().Where(g => g.Id == entry.GameId).Select(g => g.Title).SingleAsync(ct);
        await events.PublishAsync(new ListingExpired(entry.Id, title, entry.UserId, expiresAt, now), ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} listings")]
    private static partial void LogExpired(ILogger logger, int count);
}

using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Gamex.UserLibrary.Application.Catalog;

public sealed record PlatformUpserted(Guid PlatformId, string Name, string Slug, DateTime UpdatedAt);

public sealed record GameUpserted(
    Guid GameId,
    string Title,
    string? CoverImageUrl,
    DateOnly? ReleaseDate,
    short? Metacritic,
    IReadOnlyList<Guid> PlatformIds,
    DateTime UpdatedAt);

public enum ReplicaOutcome
{
    Applied,

    /// <summary>Older than or equal to the stored version; acknowledged without changes.</summary>
    Stale,

    /// <summary>The game references a platform not replicated yet; retry later so the platform event arrives first.</summary>
    MissingPlatform,
}

public interface ICatalogReplicaService
{
    Task<ReplicaOutcome> ApplyAsync(PlatformUpserted evt, CancellationToken ct);

    Task<ReplicaOutcome> ApplyAsync(GameUpserted evt, CancellationToken ct);
}

/// <summary>Maintains the read-only catalog replica from <c>gamex-catalog-events</c> (SPEC-user-library §6).</summary>
internal sealed class CatalogReplicaService(IAppDbContext db, TimeProvider time) : ICatalogReplicaService
{
    public async Task<ReplicaOutcome> ApplyAsync(PlatformUpserted evt, CancellationToken ct)
    {
        var platform = await db.CatalogPlatforms.SingleOrDefaultAsync(p => p.Id == evt.PlatformId, ct);
        if (platform is null)
        {
            db.CatalogPlatforms.Add(CatalogPlatform.Create(evt.PlatformId, evt.Name, evt.Slug, evt.UpdatedAt));
        }
        else if (!platform.TryUpdate(evt.Name, evt.Slug, evt.UpdatedAt))
        {
            return ReplicaOutcome.Stale;
        }

        await db.SaveChangesAsync(ct);
        return ReplicaOutcome.Applied;
    }

    public async Task<ReplicaOutcome> ApplyAsync(GameUpserted evt, CancellationToken ct)
    {
        var platformIds = evt.PlatformIds.Distinct().ToList();
        var known = await db.CatalogPlatforms.CountAsync(p => platformIds.Contains(p.Id), ct);
        if (known != platformIds.Count)
        {
            return ReplicaOutcome.MissingPlatform;
        }

        var game = await db.CatalogGames.Include(g => g.Platforms).SingleOrDefaultAsync(g => g.Id == evt.GameId, ct);
        var isNew = game is null;
        game ??= CatalogGame.Create(evt.GameId);

        if (!game.TryUpdate(evt.Title, evt.CoverImageUrl, evt.ReleaseDate, evt.Metacritic, platformIds, evt.UpdatedAt, time.GetUtcNow().UtcDateTime))
        {
            return ReplicaOutcome.Stale;
        }

        if (isNew)
        {
            db.CatalogGames.Add(game);
        }

        await db.SaveChangesAsync(ct);
        return ReplicaOutcome.Applied;
    }
}

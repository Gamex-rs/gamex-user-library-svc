namespace Gamex.UserLibrary.Domain.Catalog;

/// <summary>
/// Read-only replica of Game Catalog data, written only by the <c>gamex-catalog-events</c> consumer.
/// Upserts apply only when the event version (<c>updatedAt</c>) is newer than the stored one.
/// </summary>
public sealed class CatalogPlatform
{
    private CatalogPlatform()
    {
        Name = null!;
        Slug = null!;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Slug { get; private set; }
    public DateTime SourceUpdatedAt { get; private set; }

    public static CatalogPlatform Create(Guid id, string name, string slug, DateTime sourceUpdatedAt) => new()
    {
        Id = id,
        Name = name,
        Slug = slug,
        SourceUpdatedAt = sourceUpdatedAt,
    };

    /// <returns><c>false</c> if the event is not newer than the stored version.</returns>
    public bool TryUpdate(string name, string slug, DateTime sourceUpdatedAt)
    {
        if (sourceUpdatedAt <= SourceUpdatedAt)
        {
            return false;
        }

        Name = name;
        Slug = slug;
        SourceUpdatedAt = sourceUpdatedAt;
        return true;
    }
}

public sealed class CatalogGame
{
    private readonly List<CatalogGamePlatform> _platforms = [];

    private CatalogGame()
    {
        Title = null!;
    }

    public Guid Id { get; private set; }
    public string Title { get; private set; }
    public string? CoverImageUrl { get; private set; }
    public DateOnly? ReleaseDate { get; private set; }
    public short? Metacritic { get; private set; }
    public DateTime SourceUpdatedAt { get; private set; }
    public DateTime SyncedAt { get; private set; }

    public IReadOnlyCollection<CatalogGamePlatform> Platforms => _platforms;

    /// <summary>An empty row whose first <see cref="TryUpdate"/> always applies.</summary>
    public static CatalogGame Create(Guid id) => new()
    {
        Id = id,
        SourceUpdatedAt = DateTime.MinValue,
        Title = "",
    };

    /// <returns><c>false</c> if the event is not newer than the stored version.</returns>
    public bool TryUpdate(
        string title, string? coverImageUrl, DateOnly? releaseDate, short? metacritic,
        IReadOnlyCollection<Guid> platformIds, DateTime sourceUpdatedAt, DateTime now)
    {
        if (sourceUpdatedAt <= SourceUpdatedAt)
        {
            return false;
        }

        Title = title;
        CoverImageUrl = coverImageUrl;
        ReleaseDate = releaseDate;
        Metacritic = metacritic;
        SourceUpdatedAt = sourceUpdatedAt;
        SyncedAt = Timestamps.ToStoragePrecision(now);

        var wanted = platformIds.ToHashSet();
        _platforms.RemoveAll(p => !wanted.Contains(p.PlatformId));
        foreach (var id in wanted.Where(id => _platforms.All(p => p.PlatformId != id)))
        {
            _platforms.Add(new CatalogGamePlatform(Id, id));
        }

        return true;
    }

    public bool IsAvailableOn(Guid platformId) => _platforms.Any(p => p.PlatformId == platformId);
}

public sealed class CatalogGamePlatform
{
    private CatalogGamePlatform()
    {
    }

    internal CatalogGamePlatform(Guid gameId, Guid platformId)
    {
        GameId = gameId;
        PlatformId = platformId;
    }

    public Guid GameId { get; private set; }
    public Guid PlatformId { get; private set; }
}

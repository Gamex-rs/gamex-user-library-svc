using Gamex.UserLibrary.Infrastructure.Persistence;
using Gamex.UserLibrary.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gamex.UserLibrary.IntegrationTests;

[Collection(LibraryApiFixture.Name)]
public sealed class CatalogReplicaTests(LibraryApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SendAsync(string body)
    {
        using var sqs = factory.CreateSqsClient();
        await sqs.SendMessageAsync(factory.CatalogEventsUrl, body, Ct);
    }

    private async Task<T?> WaitForAsync<T>(Func<AppDbContext, Task<T?>> query)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            if (await query(scope.ServiceProvider.GetRequiredService<AppDbContext>()) is { } found)
            {
                return found;
            }

            await Task.Delay(200, Ct);
        }

        return null;
    }

    [Fact]
    public async Task GameBeforeItsPlatform_IsRetriedUntilPlatformArrives()
    {
        var platformId = Guid.CreateVersion7();
        var gameId = Guid.CreateVersion7();

        await SendAsync($$"""{"eventType":"catalog.game.upserted","gameId":"{{gameId}}","title":"Out of Order","coverImageUrl":null,"releaseDate":"2024-01-02","metacritic":80,"platformIds":["{{platformId}}"],"updatedAt":"2026-10-24T10:00:00.000001Z"}""");
        await Task.Delay(1500, Ct);
        await SendAsync($$"""{"eventType":"catalog.platform.upserted","platformId":"{{platformId}}","name":"Steam Deck","slug":"steam-deck-{{platformId:N}}","updatedAt":"2026-10-24T10:00:00.000001Z"}""");

        var game = await WaitForAsync(db => db.CatalogGames.AsNoTracking().Include(g => g.Platforms).SingleOrDefaultAsync(g => g.Id == gameId, Ct));

        game.ShouldNotBeNull();
        game.Title.ShouldBe("Out of Order");
        game.ReleaseDate.ShouldBe(new DateOnly(2024, 1, 2));
        game.IsAvailableOn(platformId).ShouldBeTrue();
    }

    [Fact]
    public async Task OlderEvent_DoesNotOverwriteNewer()
    {
        var platformId = Guid.CreateVersion7();
        var slug = $"retro-{platformId:N}";

        await SendAsync($$"""{"eventType":"catalog.platform.upserted","platformId":"{{platformId}}","name":"New Name","slug":"{{slug}}","updatedAt":"2026-10-24T12:00:00.000000Z"}""");
        await WaitForAsync(db => db.CatalogPlatforms.AsNoTracking().SingleOrDefaultAsync(p => p.Id == platformId, Ct));
        await SendAsync($$"""{"eventType":"catalog.platform.upserted","platformId":"{{platformId}}","name":"Old Name","slug":"{{slug}}","updatedAt":"2026-10-24T11:00:00.000000Z"}""");
        await Task.Delay(2000, Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var platform = await scope.ServiceProvider.GetRequiredService<AppDbContext>().CatalogPlatforms.AsNoTracking().SingleAsync(p => p.Id == platformId, Ct);
        platform.Name.ShouldBe("New Name");
    }
}

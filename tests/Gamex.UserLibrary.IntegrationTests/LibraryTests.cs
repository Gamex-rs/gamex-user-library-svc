using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gamex.UserLibrary.Application.Library;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Gamex.UserLibrary.IntegrationTests;

[Collection(LibraryApiFixture.Name)]
public sealed class LibraryTests(LibraryApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<LibraryEntryDto> AddAsync(TestUser user, object body)
    {
        var response = await user.Client.PostAsJsonAsync("/api/v1/users/me/library", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<LibraryEntryDto>(LibraryApiFactory.Json, Ct))!;
    }

    /// <summary>All deltas published so far for this user and game, in publish order.</summary>
    private Task<List<JsonElement>> DeltasAsync(Guid gameId, Guid userId, int expected, TimeSpan? timeout = null) =>
        factory.ReceiveAsync(factory.LibraryEventsUrl,
            e => e.GetProperty("gameId").GetGuid() == gameId && e.GetProperty("userId").GetGuid() == userId, expected, timeout);

    private static readonly TimeSpan NothingMoreTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Add_ReturnsEntryJoinedWithCatalog()
    {
        var game = await factory.SeedGameAsync("Hades", "playstation5", "pc");
        var user = await factory.RegisterAsync();

        var entry = await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["playstation5"], format = "physical", isOwned = true, isWishlist = true, condition = "like_new" });

        entry.Game.Title.ShouldBe("Hades");
        entry.Game.CoverImageUrl.ShouldNotBeNull();
        entry.Platform.Slug.ShouldBe("playstation5");
        entry.Format.ShouldBe(GameFormat.Physical);
        entry.Condition.ShouldBe(GameCondition.LikeNew);
        entry.IsOwned.ShouldBeTrue();
        entry.IsWishlist.ShouldBeTrue();
        entry.ListingStatus.ShouldBe(ListingStatus.None);
    }

    [Fact]
    public async Task Add_SameGameOtherPlatformOrFormat_IsAllowed_DuplicateIs409WithEntryId()
    {
        var game = await factory.SeedGameAsync("Elden Ring", "playstation5", "pc");
        var user = await factory.RegisterAsync();
        var first = await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["playstation5"], format = "physical", isOwned = true });
        await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["playstation5"], format = "digital", isWishlist = true });
        await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", isOwned = true });

        var duplicate = await user.Client.PostAsJsonAsync("/api/v1/users/me/library",
            new { gameId = game.Id, platformId = game.Platforms["playstation5"], format = "physical", isWishlist = true }, Ct);

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await duplicate.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        problem!.Extensions["entryId"]!.ToString().ShouldBe(first.EntryId.ToString());
    }

    [Fact]
    public async Task Add_UnknownGame_Returns409GameNotSyncedWithRetryAfter()
    {
        var user = await factory.RegisterAsync();
        var platform = (await factory.SeedGameAsync("Anything", "pc")).Platforms["pc"];

        var response = await user.Client.PostAsJsonAsync("/api/v1/users/me/library",
            new { gameId = Guid.NewGuid(), platformId = platform, format = "digital", isOwned = true }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(2));
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Type.ShouldBe("https://gamex.rs/errors/game-not-synced");
    }

    [Theory]
    [InlineData("platform", "platformId")]
    [InlineData("noStatus", "statuses")]
    [InlineData("digitalCondition", "condition")]
    [InlineData("saleWithoutPrice", "price")]
    public async Task Add_Invalid_Returns400(string kind, string field)
    {
        var game = await factory.SeedGameAsync("Validation", "pc", "nintendo-switch");
        var otherPlatform = (await factory.SeedGameAsync("Other", "playstation4")).Platforms["playstation4"];
        var user = await factory.RegisterAsync();
        object body = kind switch
        {
            "platform" => new { gameId = game.Id, platformId = otherPlatform, format = "physical", isOwned = true },
            "noStatus" => new { gameId = game.Id, platformId = game.Platforms["pc"], format = "physical" },
            "digitalCondition" => new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", isOwned = true, condition = "good" },
            _ => new { gameId = game.Id, platformId = game.Platforms["pc"], format = "physical", isOwned = true, forSale = true },
        };

        var response = await user.Client.PostAsJsonAsync("/api/v1/users/me/library", body, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain(field);
    }

    [Fact]
    public async Task Deltas_AreComputedOnTheUserGameAggregate()
    {
        var game = await factory.SeedGameAsync("Celeste", "pc", "nintendo-switch");
        var user = await factory.RegisterAsync();

        var pc = await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", isOwned = true, isWishlist = true });
        var first = (await DeltasAsync(game.Id, user.Id, 1)).Single().GetProperty("delta");
        first.GetProperty("owned").GetInt32().ShouldBe(1);
        first.GetProperty("wishlist").GetInt32().ShouldBe(1);

        // Second owned copy: aggregate unchanged → no new event.
        var sw = await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["nintendo-switch"], format = "physical", isOwned = true });
        (await DeltasAsync(game.Id, user.Id, 2, NothingMoreTimeout)).Count.ShouldBe(1);

        // Removing one copy keeps "owned"; removing the last one sends owned -1.
        (await user.Client.DeleteAsync(new Uri($"/api/v1/users/me/library/{sw.EntryId}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DeltasAsync(game.Id, user.Id, 2, NothingMoreTimeout)).Count.ShouldBe(1);
        (await user.Client.DeleteAsync(new Uri($"/api/v1/users/me/library/{pc.EntryId}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var all = await DeltasAsync(game.Id, user.Id, 2);
        all.Count.ShouldBe(2);
        var last = all[1].GetProperty("delta");
        last.GetProperty("owned").GetInt32().ShouldBe(-1);
        last.GetProperty("wishlist").GetInt32().ShouldBe(-1);
    }

    [Fact]
    public async Task Update_PartialAndPlatformMove()
    {
        var game = await factory.SeedGameAsync("Hollow Knight", "pc", "nintendo-switch");
        var user = await factory.RegisterAsync();
        var entry = await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "physical", isOwned = true, notes = "boxed" });

        var response = await user.Client.PutAsJsonAsync($"/api/v1/users/me/library/{entry.EntryId}",
            new { platformId = game.Platforms["nintendo-switch"], forExchange = true, preferredGames = "Celeste" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<LibraryEntryDto>(LibraryApiFactory.Json, Ct))!;
        updated.Platform.Slug.ShouldBe("nintendo-switch");
        updated.IsOwned.ShouldBeTrue("unchanged when omitted");
        updated.Notes.ShouldBe("boxed");
        updated.ForExchange.ShouldBeTrue();
        updated.ListingStatus.ShouldBe(ListingStatus.Active);

        var other = await user.Client.PutAsJsonAsync($"/api/v1/users/me/library/{Guid.NewGuid()}", new { isOwned = false }, Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Statuses_AggregatePerGameWithEntries()
    {
        var game = await factory.SeedGameAsync("Stardew Valley", "pc", "nintendo-switch");
        var unknown = Guid.NewGuid();
        var user = await factory.RegisterAsync();
        await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", isOwned = true });
        await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["nintendo-switch"], format = "physical", isWishlist = true });

        var statuses = await user.Client.GetFromJsonAsync<Dictionary<Guid, GameLibraryStatusDto?>>(
            $"/api/v1/users/me/library/statuses?gameIds={game.Id},{unknown}", LibraryApiFactory.Json, Ct);

        statuses![unknown].ShouldBeNull();
        var status = statuses[game.Id]!;
        status.IsOwned.ShouldBeTrue();
        status.IsWishlist.ShouldBeTrue();
        status.Entries.Select(e => (e.PlatformSlug, e.Format)).ShouldBe([("nintendo-switch", GameFormat.Physical), ("pc", GameFormat.Digital)], ignoreOrder: true);
    }

    [Fact]
    public async Task ListOwn_FiltersByStatus()
    {
        var game = await factory.SeedGameAsync("Inside", "pc");
        var game2 = await factory.SeedGameAsync("Limbo", "pc");
        var user = await factory.RegisterAsync();
        await AddAsync(user, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", isOwned = true });
        await AddAsync(user, new { gameId = game2.Id, platformId = game2.Platforms["pc"], format = "digital", isWishlist = true });

        var wishlist = await user.Client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/v1/users/me/library?status=wishlist", LibraryApiFactory.Json, Ct);
        var all = await user.Client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/v1/users/me/library", LibraryApiFactory.Json, Ct);

        wishlist!.Select(e => e.Game.Title).ShouldBe(["Limbo"]);
        all!.Select(e => e.Game.Title).ShouldBe(["Inside", "Limbo"]);
        (await user.Client.GetAsync(new Uri("/api/v1/users/me/library?status=bogus", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

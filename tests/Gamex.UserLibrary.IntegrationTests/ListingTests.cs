using System.Net;
using System.Net.Http.Json;
using Gamex.UserLibrary.Application.Common;
using Gamex.UserLibrary.Application.Library;
using Gamex.UserLibrary.Application.Users;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.Infrastructure.Persistence;
using Gamex.UserLibrary.Infrastructure.Security;
using Gamex.UserLibrary.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.UserLibrary.IntegrationTests;

[Collection(LibraryApiFixture.Name)]
public sealed class ListingTests(LibraryApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<LibraryEntryDto> AddAsync(TestUser user, object body)
    {
        var response = await user.Client.PostAsJsonAsync("/api/v1/users/me/library", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<LibraryEntryDto>(LibraryApiFactory.Json, Ct))!;
    }

    private async Task<CursorPage<ListingDto>> BrowseAsync(string query) =>
        (await factory.CreateHttpsClient().GetFromJsonAsync<CursorPage<ListingDto>>($"/api/v1/listings{query}", LibraryApiFactory.Json, Ct))!;

    [Fact]
    public async Task Browse_FiltersByGameTypeFormatPriceAndCity()
    {
        var game = await factory.SeedGameAsync("Baldur's Gate 3", "pc", "playstation5");
        var seller = await factory.RegisterAsync("Seller");
        await seller.Client.PutAsJsonAsync("/api/v1/users/me", new { displayName = "Seller", city = "Beograd" }, Ct);
        await AddAsync(seller, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "physical", isOwned = true, forSale = true, price = 40, condition = "good" });
        await AddAsync(seller, new { gameId = game.Id, platformId = game.Platforms["playstation5"], format = "physical", isOwned = true, forExchange = true });
        var buyer = await factory.RegisterAsync("Buyer");
        await AddAsync(buyer, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", wantToBuy = true });

        (await BrowseAsync($"?gameId={game.Id}")).Items.Count.ShouldBe(3);
        (await BrowseAsync($"?gameId={game.Id}&type=sale")).Items.ShouldHaveSingleItem().Price.ShouldBe(40m);
        (await BrowseAsync($"?gameId={game.Id}&type=want_to_buy")).Items.ShouldHaveSingleItem().Seller.Id.ShouldBe(buyer.Id);
        (await BrowseAsync($"?gameId={game.Id}&platform=playstation5")).Items.ShouldHaveSingleItem().Types.ShouldBe([ListingType.Exchange]);
        (await BrowseAsync($"?gameId={game.Id}&format=digital")).Items.ShouldHaveSingleItem();
        (await BrowseAsync($"?gameId={game.Id}&minPrice=50")).Items.ShouldBeEmpty();
        (await BrowseAsync($"?gameId={game.Id}&city=Beograd")).Items.Count.ShouldBe(2);
        var listing = (await BrowseAsync($"?gameId={game.Id}&condition=good")).Items.ShouldHaveSingleItem();
        listing.Game.Title.ShouldBe("Baldur's Gate 3");
        listing.Seller.DisplayName.ShouldBe("Seller");
        listing.Seller.City.ShouldBe("Beograd");
    }

    [Fact]
    public async Task Browse_CursorPaging_IsStableWhenListedAtTies()
    {
        var game = await factory.SeedGameAsync("Tie Breaker", "pc");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var seller = await factory.RegisterAsync($"Seller {i}");
            ids.Add((await AddAsync(seller, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", isOwned = true, forExchange = true })).EntryId);
        }

        // Force identical ListedAt so only the id tie-breaker orders the page boundary.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var at = DateTime.UtcNow.AddMinutes(-1);
            await db.LibraryEntries.Where(e => ids.Contains(e.Id)).ExecuteUpdateAsync(s => s.SetProperty(e => e.ListedAt, at), Ct);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await BrowseAsync($"?gameId={game.Id}&pageSize=2" + (cursor is null ? "" : $"&cursor={cursor}"));
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.Count.ShouldBe(5);
        seen.Distinct().Count().ShouldBe(5);
        seen.ShouldBe(ids.OrderByDescending(id => id.ToString()).ToList());
    }

    [Fact]
    public async Task Detail_IncludesContactTokenSignedForMessaging()
    {
        var game = await factory.SeedGameAsync("Ori", "pc");
        var seller = await factory.RegisterAsync("Ori Seller");
        var entry = await AddAsync(seller, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "physical", isOwned = true, forSale = true, price = 15 });

        var detail = await factory.CreateHttpsClient().GetFromJsonAsync<ListingDetailDto>($"/api/v1/listings/{entry.EntryId}", LibraryApiFactory.Json, Ct);

        detail!.Listing.Id.ShouldBe(entry.EntryId);
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(detail.ContactToken, new TokenValidationParameters
        {
            IssuerSigningKey = new RsaSecurityKey(LibraryApiFactory.SigningKey),
            ValidIssuer = JwtOptions.DefaultIssuer,
            ValidAudience = JwtOptions.ContactAudience,
        });
        validated.IsValid.ShouldBeTrue();
        validated.Claims["owner_id"].ShouldBe(seller.Id.ToString());
        validated.Claims["owner_name"].ShouldBe("Ori Seller");
        validated.Claims["game_title"].ShouldBe("Ori");

        (await factory.CreateHttpsClient().GetAsync(new Uri($"/api/v1/listings/{Guid.NewGuid()}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WishlistMatch_SamePlatformOnly_PublishesListingCreatedPerUser()
    {
        var game = await factory.SeedGameAsync("Metroid Dread", "nintendo-switch", "pc");
        var wantsSwitch = await factory.RegisterAsync("Wants Switch");
        var wantsPc = await factory.RegisterAsync("Wants PC");
        await AddAsync(wantsSwitch, new { gameId = game.Id, platformId = game.Platforms["nintendo-switch"], format = "digital", isWishlist = true });
        await AddAsync(wantsPc, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "physical", isWishlist = true });
        var seller = await factory.RegisterAsync("Switch Seller");

        var entry = await AddAsync(seller, new { gameId = game.Id, platformId = game.Platforms["nintendo-switch"], format = "physical", isOwned = true, forSale = true, price = 35 });

        var events = await factory.ReceiveAsync(factory.ActivityEventsUrl,
            e => e.GetProperty("eventType").GetString() == "listing.created" && e.GetProperty("listingId").GetGuid() == entry.EntryId, expected: 2,
            timeout: TimeSpan.FromSeconds(5));
        var created = events.ShouldHaveSingleItem();
        created.GetProperty("notifyUserId").GetGuid().ShouldBe(wantsSwitch.Id);
        created.GetProperty("sellerName").GetString().ShouldBe("Switch Seller");
        created.GetProperty("gameTitle").GetString().ShouldBe("Metroid Dread");
        created.GetProperty("listingType").GetString().ShouldBe("sale");
        created.GetProperty("platformSlug").GetString().ShouldBe("nintendo-switch");
    }

    [Fact]
    public async Task Complete_BothConfirm_CreatesTransactionPublishesEventAndAllowsRatings()
    {
        var game = await factory.SeedGameAsync("Gris", "pc");
        var owner = await factory.RegisterAsync("Owner");
        var partner = await factory.RegisterAsync("Partner");
        var stranger = await factory.RegisterAsync("Stranger");
        var entry = await AddAsync(owner, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "physical", isOwned = true, forSale = true, price = 10 });

        // Owner must name the partner.
        (await owner.Client.PostAsJsonAsync($"/api/v1/listings/{entry.EntryId}/complete", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var ownerConfirms = await owner.Client.PostAsJsonAsync($"/api/v1/listings/{entry.EntryId}/complete", new { partnerUserId = partner.Id }, Ct);
        var pending = (await ownerConfirms.Content.ReadFromJsonAsync<CompletionResult>(LibraryApiFactory.Json, Ct))!;
        pending.Completed.ShouldBeFalse();
        pending.WaitingForUserId.ShouldBe(partner.Id);

        var partnerConfirms = await partner.Client.PostAsJsonAsync($"/api/v1/listings/{entry.EntryId}/complete", new { }, Ct);
        var done = (await partnerConfirms.Content.ReadFromJsonAsync<CompletionResult>(LibraryApiFactory.Json, Ct))!;
        done.Completed.ShouldBeTrue();
        done.TransactionId.ShouldNotBeNull();

        var completed = (await factory.ReceiveAsync(factory.ActivityEventsUrl,
            e => e.GetProperty("eventType").GetString() == "transaction.completed" && e.GetProperty("listingEntryId").GetGuid() == entry.EntryId, 1)).Single();
        completed.GetProperty("userIdA").GetGuid().ShouldBe(owner.Id);
        completed.GetProperty("userNameB").GetString().ShouldBe("Partner");
        completed.GetProperty("gameTitle").GetString().ShouldBe("Gris");

        // The listing is gone and its sale flag withdrawn (forSale -1 for stats).
        (await factory.CreateHttpsClient().GetAsync(new Uri($"/api/v1/listings/{entry.EntryId}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var deltas = await factory.ReceiveAsync(factory.LibraryEventsUrl, e => e.GetProperty("gameId").GetGuid() == game.Id, 2);
        deltas.Last().GetProperty("delta").GetProperty("forSale").GetInt32().ShouldBe(-1);

        // Ratings: each party once; strangers cannot.
        var rate = await partner.Client.PostAsJsonAsync($"/api/v1/users/{owner.Id}/ratings", new { transactionId = done.TransactionId, score = 5, comment = "Fast and friendly" }, Ct);
        rate.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await partner.Client.PostAsJsonAsync($"/api/v1/users/{owner.Id}/ratings", new { transactionId = done.TransactionId, score = 4 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await stranger.Client.PostAsJsonAsync($"/api/v1/users/{owner.Id}/ratings", new { transactionId = done.TransactionId, score = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.PostAsJsonAsync($"/api/v1/users/{partner.Id}/ratings", new { transactionId = done.TransactionId, score = 4 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var profile = await factory.CreateHttpsClient().GetFromJsonAsync<PublicUserDto>($"/api/v1/users/{owner.Id}", LibraryApiFactory.Json, Ct);
        profile!.RatingCount.ShouldBe(1);
        profile.RatingAvg.ShouldBe(5m);
        var ratings = await factory.CreateHttpsClient().GetFromJsonAsync<CursorPage<RatingDto>>($"/api/v1/users/{owner.Id}/ratings", LibraryApiFactory.Json, Ct);
        ratings!.Items.ShouldHaveSingleItem().Comment.ShouldBe("Fast and friendly");
    }

    [Fact]
    public async Task Expiry_ExpiresDueListingsAndPublishesEvents()
    {
        var game = await factory.SeedGameAsync("Tunic", "pc");
        var owner = await factory.RegisterAsync();
        var entry = await AddAsync(owner, new { gameId = game.Id, platformId = game.Platforms["pc"], format = "digital", isOwned = true, forExchange = true });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.LibraryEntries.Where(e => e.Id == entry.EntryId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ListingExpiresAt, DateTime.UtcNow.AddMinutes(-1)), Ct);
            (await scope.ServiceProvider.GetRequiredService<IListingExpiryService>().ExpireDueAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
            (await db.LibraryEntries.AsNoTracking().SingleAsync(e => e.Id == entry.EntryId, Ct)).ListingStatus.ShouldBe(ListingStatus.Expired);
        }

        var expired = await factory.ReceiveAsync(factory.ActivityEventsUrl,
            e => e.GetProperty("eventType").GetString() == "listing.expired" && e.GetProperty("listingId").GetGuid() == entry.EntryId, 1);
        expired.ShouldHaveSingleItem().GetProperty("gameTitle").GetString().ShouldBe("Tunic");
    }
}

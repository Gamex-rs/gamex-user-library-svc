using Gamex.UserLibrary.Domain.Library;

namespace Gamex.UserLibrary.UnitTests.Domain;

public sealed class LibraryEntryTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private static EntryValues Values(bool owned = false, bool wishlist = false, bool exchange = false, bool sale = false, bool wantToBuy = false,
        GameCondition? condition = null, decimal? price = null) =>
        new(owned, wishlist, exchange, sale, wantToBuy, condition, price, null, null);

    private static (LibraryEntry Entry, ListingChange Change) Create(EntryValues values, GameFormat format = GameFormat.Physical) =>
        LibraryEntry.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), format, values, T0, Lifetime);

    [Fact]
    public void Create_OwnedOnly_HasNoListing()
    {
        var (entry, change) = Create(Values(owned: true));

        entry.ListingStatus.ShouldBe(ListingStatus.None);
        entry.ListedAt.ShouldBeNull();
        change.BecameOffered.ShouldBeFalse();
    }

    [Fact]
    public void Create_ForSale_ActivatesListingAndIsOffered()
    {
        var (entry, change) = Create(Values(owned: true, sale: true, price: 30));

        entry.ListingStatus.ShouldBe(ListingStatus.Active);
        entry.ListedAt.ShouldBe(T0);
        entry.ListingExpiresAt.ShouldBe(T0 + Lifetime);
        change.BecameOffered.ShouldBeTrue();
        change.OfferedTypes.ShouldBe([ListingType.Sale]);
        entry.ListingTypes.ShouldBe([ListingType.Sale]);
    }

    [Fact]
    public void Create_WantToBuy_IsListedButNotOffered()
    {
        var (entry, change) = Create(Values(wishlist: true, wantToBuy: true));

        entry.ListingStatus.ShouldBe(ListingStatus.Active);
        entry.IsOffered.ShouldBeFalse();
        change.BecameOffered.ShouldBeFalse();
    }

    [Fact]
    public void Update_AddingSaleToExistingExchange_IsNotNewlyOffered()
    {
        var (entry, _) = Create(Values(owned: true, exchange: true));

        var change = entry.Update(Values(owned: true, exchange: true, sale: true, price: 20), T0.AddDays(1), Lifetime);

        change.BecameOffered.ShouldBeFalse();
        entry.ListedAt.ShouldBe(T0, "the listing stays the same listing");
    }

    [Fact]
    public void Update_RemovingAllListingFlags_EndsListing()
    {
        var (entry, _) = Create(Values(owned: true, exchange: true));

        entry.Update(Values(owned: true), T0.AddDays(1), Lifetime);

        entry.ListingStatus.ShouldBe(ListingStatus.None);
        entry.ListingExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void Digital_DropsCondition()
    {
        var (entry, _) = Create(Values(owned: true, condition: GameCondition.LikeNew), GameFormat.Digital);

        entry.Condition.ShouldBeNull();
    }

    [Fact]
    public void ExpireIfDue_PastExpiry_ExpiresAndWithdrawsOffers()
    {
        var (entry, _) = Create(Values(owned: true, exchange: true, sale: true, price: 10));

        entry.ExpireIfDue(T0 + Lifetime - TimeSpan.FromSeconds(1)).ShouldBeFalse();
        entry.ExpireIfDue(T0 + Lifetime + TimeSpan.FromSeconds(1)).ShouldBeTrue();

        entry.ListingStatus.ShouldBe(ListingStatus.Expired);
        entry.ForExchange.ShouldBeFalse();
        entry.ForSale.ShouldBeFalse();
        entry.IsOwned.ShouldBeTrue();
    }

    [Fact]
    public void Relist_AfterExpiry_StartsNewListingCycle()
    {
        var (entry, _) = Create(Values(owned: true, sale: true, price: 10));
        entry.ExpireIfDue(T0 + Lifetime + TimeSpan.FromDays(1));

        var relistAt = T0 + Lifetime + TimeSpan.FromDays(2);
        var change = entry.Update(Values(owned: true, sale: true, price: 8), relistAt, Lifetime);

        entry.ListingStatus.ShouldBe(ListingStatus.Active);
        entry.ListedAt.ShouldBe(relistAt);
        change.BecameOffered.ShouldBeTrue();
    }

    [Fact]
    public void Complete_EndsListingAndKeepsHistory()
    {
        var (entry, _) = Create(Values(owned: true, exchange: true));

        entry.Complete(T0.AddDays(3));

        entry.ListingStatus.ShouldBe(ListingStatus.Completed);
        entry.IsListed.ShouldBeFalse();
        entry.ListedAt.ShouldBe(T0);

        // Editing a completed entry does not silently relist it.
        entry.Update(Values(owned: true), T0.AddDays(4), Lifetime);
        entry.ListingStatus.ShouldBe(ListingStatus.Completed);
    }
}

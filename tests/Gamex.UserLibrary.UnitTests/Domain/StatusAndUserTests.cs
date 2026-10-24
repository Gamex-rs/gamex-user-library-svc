using Gamex.UserLibrary.Domain.Catalog;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.Domain.Users;

namespace Gamex.UserLibrary.UnitTests.Domain;

public sealed class StatusFlagsTests
{
    [Fact]
    public void Aggregate_IsTrueIfAnyEntryHasTheFlag()
    {
        var aggregate = StatusFlags.Aggregate([
            new StatusFlags(Owned: true, Wishlist: false, ForExchange: false, ForSale: false, WantToBuy: false),
            new StatusFlags(Owned: false, Wishlist: true, ForExchange: false, ForSale: true, WantToBuy: false),
        ]);

        aggregate.ShouldBe(new StatusFlags(true, true, false, true, false));
        StatusFlags.Aggregate([]).ShouldBe(StatusFlags.None);
    }

    [Fact]
    public void Delta_ReportsOnlyChangedFlags()
    {
        var delta = StatusFlags.Delta(new StatusFlags(true, false, true, false, false), new StatusFlags(true, true, false, false, false));

        delta.ShouldBe(new StatusDelta(0, 1, -1, 0, 0));
        delta.IsZero.ShouldBeFalse();
        StatusFlags.Delta(StatusFlags.None, StatusFlags.None).IsZero.ShouldBeTrue();
    }

    [Fact]
    public void SecondCopyOfOwnedGame_ProducesNoOwnedDelta()
    {
        var ps5 = new StatusFlags(Owned: true, false, false, false, false);
        var pc = new StatusFlags(Owned: true, false, false, false, false);

        var delta = StatusFlags.Delta(StatusFlags.Aggregate([ps5]), StatusFlags.Aggregate([ps5, pc]));

        delta.IsZero.ShouldBeTrue();
    }
}

public sealed class UserTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Register_NormalizesEmail()
    {
        var user = User.Register("  Marko  ", "  Marko@Example.RS ", "hash", T0);

        user.Email.ShouldBe("marko@example.rs");
        user.DisplayName.ShouldBe("Marko");
        user.Role.ShouldBe(UserRoles.User);
        user.IsActive.ShouldBeTrue();
        user.Country.ShouldBe("RS");
    }

    [Fact]
    public void ApplyRating_KeepsRunningAverage()
    {
        var user = User.Register("Ana", "ana@example.rs", null, T0);

        user.ApplyRating(5);
        user.ApplyRating(4);
        user.ApplyRating(4);

        user.RatingCount.ShouldBe(3);
        user.RatingAvg.ShouldBe(4.33m);
    }

    [Fact]
    public void ReplacePlatforms_KeepsExistingAndDropsRemoved()
    {
        var user = User.Register("Ana", "ana@example.rs", null, T0);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        user.ReplacePlatforms([a, b], T0);

        user.ReplacePlatforms([b, c, c], T0);

        user.Platforms.Select(p => p.PlatformId).ShouldBe([b, c], ignoreOrder: true);
    }

    [Fact]
    public void RefreshToken_IsActiveUntilRevokedOrExpired()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", T0, TimeSpan.FromDays(30));

        token.IsActive(T0.AddDays(29)).ShouldBeTrue();
        token.IsActive(T0.AddDays(31)).ShouldBeFalse();

        var replacement = Guid.NewGuid();
        token.Revoke(T0.AddDays(1), replacement);
        token.IsActive(T0.AddDays(2)).ShouldBeFalse();
        token.ReplacedByTokenId.ShouldBe(replacement);
    }

    [Fact]
    public void CatalogGame_IgnoresStaleVersions()
    {
        var platform = Guid.NewGuid();
        var game = CatalogGame.Create(Guid.NewGuid());

        game.TryUpdate("Hades", null, null, 93, [platform], T0, T0).ShouldBeTrue();
        game.TryUpdate("Old title", null, null, 90, [], T0, T0).ShouldBeFalse();
        game.TryUpdate("Hades II", null, null, 95, [platform], T0.AddTicks(10), T0).ShouldBeTrue();

        game.Title.ShouldBe("Hades II");
        game.IsAvailableOn(platform).ShouldBeTrue();
    }
}

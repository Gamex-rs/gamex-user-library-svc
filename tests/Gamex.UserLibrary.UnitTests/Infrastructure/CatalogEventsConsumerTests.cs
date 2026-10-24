using Gamex.UserLibrary.Application.Catalog;
using Gamex.UserLibrary.Infrastructure.Messaging;

namespace Gamex.UserLibrary.UnitTests.Infrastructure;

public sealed class CatalogEventsConsumerTests
{
    [Fact]
    public void Parse_PlatformEvent()
    {
        var parsed = CatalogEventsConsumer.Parse("""
            {"eventType":"catalog.platform.upserted","platformId":"0192f3a1-0000-7000-8000-000000000001","name":"PlayStation 5","slug":"playstation5","updatedAt":"2026-10-24T10:00:00.123456Z"}
            """);

        var platform = parsed.ShouldBeOfType<PlatformUpserted>();
        platform.Slug.ShouldBe("playstation5");
        platform.UpdatedAt.ShouldBe(new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560));
        platform.UpdatedAt.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void Parse_GameEvent_WithNulls()
    {
        var parsed = CatalogEventsConsumer.Parse("""
            {"eventType":"catalog.game.upserted","gameId":"0192f3a1-0000-7000-8000-000000000002","title":"Homebrew","coverImageUrl":null,"releaseDate":null,"metacritic":null,"platformIds":[],"updatedAt":"2026-10-24T10:00:00.000000Z"}
            """);

        var game = parsed.ShouldBeOfType<GameUpserted>();
        game.Title.ShouldBe("Homebrew");
        game.PlatformIds.ShouldBeEmpty();
        game.ReleaseDate.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"eventType":"catalog.unknown"}""")]
    [InlineData("""{"eventType":"catalog.game.upserted","gameId":"0192f3a1-0000-7000-8000-000000000002","title":"","platformIds":[],"updatedAt":"2026-10-24T10:00:00Z"}""")]
    [InlineData("""{"eventType":"catalog.platform.upserted","platformId":"00000000-0000-0000-0000-000000000000","name":"X","slug":"x","updatedAt":"2026-10-24T10:00:00Z"}""")]
    public void Parse_Invalid_ReturnsNull(string body)
    {
        CatalogEventsConsumer.Parse(body).ShouldBeNull();
    }
}

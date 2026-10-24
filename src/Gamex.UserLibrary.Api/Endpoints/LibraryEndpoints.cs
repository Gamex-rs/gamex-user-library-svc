using System.Security.Claims;
using Gamex.UserLibrary.Api.Auth;
using Gamex.UserLibrary.Application.Library;
using Gamex.UserLibrary.Domain.Library;

namespace Gamex.UserLibrary.Api.Endpoints;

internal static class LibraryEndpoints
{
    public sealed record EntryRequest(
        Guid? GameId,
        Guid? PlatformId,
        GameFormat? Format,
        bool? IsOwned,
        bool? IsWishlist,
        bool? ForExchange,
        bool? ForSale,
        bool? WantToBuy,
        GameCondition? Condition,
        decimal? Price,
        string? PreferredGames,
        string? Notes)
    {
        public EntryCommand ToCommand() =>
            new(GameId, PlatformId, Format, IsOwned, IsWishlist, ForExchange, ForSale, WantToBuy, Condition, Price, PreferredGames, Notes);
    }

    public sealed record CompleteRequest(Guid? PartnerUserId);

    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapGroup("/api/v1/users/me/library").WithTags("Library").RequireAuthorization();

        library.MapGet("/", async (ClaimsPrincipal user, ILibraryService service, CancellationToken ct,
                string? status = null, Guid? gameId = null, Guid? platformId = null, string? format = null) =>
            QueryEnums.TryParse<GameFormat>(format, out var parsedFormat)
                ? Problems.From(await service.ListOwnAsync(user.UserId(), new LibraryFilter(status, gameId, platformId, parsedFormat), ct))
                : Problems.Validation("format", $"Allowed values: {QueryEnums.Allowed<GameFormat>()}."));

        library.MapGet("/statuses", async (ClaimsPrincipal user, ILibraryService service, CancellationToken ct, string? gameIds = null) =>
        {
            var ids = new List<Guid>();
            foreach (var part in (gameIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Guid.TryParse(part, out var id))
                {
                    return Problems.Validation("gameIds", $"'{part}' is not a valid id.");
                }

                ids.Add(id);
            }

            return Problems.From(await service.GetStatusesAsync(user.UserId(), ids, ct));
        });

        library.MapPost("/", async (EntryRequest request, ClaimsPrincipal user, ILibraryService service, CancellationToken ct) =>
            Problems.From(
                await service.AddAsync(user.UserId(), request.ToCommand(), ct),
                entry => Results.Created($"/api/v1/users/me/library/{entry.EntryId}", entry)));

        library.MapPut("/{entryId:guid}", async (Guid entryId, EntryRequest request, ClaimsPrincipal user, ILibraryService service, CancellationToken ct) =>
            Problems.From(await service.UpdateAsync(user.UserId(), entryId, request.ToCommand(), ct)));

        library.MapDelete("/{entryId:guid}", async (Guid entryId, ClaimsPrincipal user, ILibraryService service, CancellationToken ct) =>
            Problems.From(await service.DeleteAsync(user.UserId(), entryId, ct), _ => Results.NoContent()));

        var listings = app.MapGroup("/api/v1/listings").WithTags("Listings");

        listings.MapGet("/", async (IListingService service, CancellationToken ct,
                string? type = null, Guid? gameId = null, string? platform = null, string? format = null,
                string? condition = null, string? city = null, decimal? minPrice = null, decimal? maxPrice = null,
                string? q = null, string? cursor = null, int? pageSize = null) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!QueryEnums.TryParse<ListingType>(type, out var parsedType))
            {
                errors["type"] = [$"Allowed values: {QueryEnums.Allowed<ListingType>()}."];
            }

            if (!QueryEnums.TryParse<GameFormat>(format, out var parsedFormat))
            {
                errors["format"] = [$"Allowed values: {QueryEnums.Allowed<GameFormat>()}."];
            }

            if (!QueryEnums.TryParse<GameCondition>(condition, out var parsedCondition))
            {
                errors["condition"] = [$"Allowed values: {QueryEnums.Allowed<GameCondition>()}."];
            }

            if (errors.Count > 0)
            {
                return Problems.Validation(errors);
            }

            var filter = new ListingFilter(parsedType, gameId, platform, parsedFormat, parsedCondition, city, minPrice, maxPrice, q, cursor, pageSize);
            return Problems.From(await service.BrowseAsync(filter, ct));
        });

        listings.MapGet("/{id:guid}", async (Guid id, IListingService service, CancellationToken ct) =>
            Problems.From(await service.GetAsync(id, ct)));

        listings.MapPost("/{id:guid}/complete", async (Guid id, CompleteRequest? request, ClaimsPrincipal user, IListingService service, CancellationToken ct) =>
            Problems.From(await service.CompleteAsync(id, user.UserId(), request?.PartnerUserId, ct)))
            .RequireAuthorization();

        return app;
    }
}

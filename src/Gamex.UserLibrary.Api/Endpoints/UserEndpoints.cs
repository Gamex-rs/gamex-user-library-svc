using System.Security.Claims;
using Gamex.UserLibrary.Api.Auth;
using Gamex.UserLibrary.Application.Library;
using Gamex.UserLibrary.Application.Users;

namespace Gamex.UserLibrary.Api.Endpoints;

internal static class UserEndpoints
{
    public sealed record UpdateProfileRequest(string? DisplayName, string? AvatarUrl, string? City, string? Region, string? Bio);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public sealed record PlatformsRequest(Guid[]? PlatformIds);

    public sealed record RatingRequest(Guid? TransactionId, int? Score, string? Comment);

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/users").WithTags("Users");

        users.MapGet("/{id:guid}", async (Guid id, IUserService service, CancellationToken ct) =>
            Problems.From(await service.GetPublicAsync(id, ct)));

        var me = users.MapGroup("/me").RequireAuthorization();
        me.MapGet("/", async (ClaimsPrincipal user, IUserService service, CancellationToken ct) =>
            Problems.From(await service.GetMeAsync(user.UserId(), ct)));
        me.MapPut("/", async (UpdateProfileRequest r, ClaimsPrincipal user, IUserService service, CancellationToken ct) =>
            Problems.From(await service.UpdateMeAsync(user.UserId(), new UpdateProfileCommand(r.DisplayName, r.AvatarUrl, r.City, r.Region, r.Bio), ct)));
        me.MapPut("/password", async (ChangePasswordRequest r, ClaimsPrincipal user, IUserService service, CancellationToken ct) =>
            Problems.From(await service.ChangePasswordAsync(user.UserId(), r.CurrentPassword, r.NewPassword, ct), _ => Results.NoContent()))
            .RequireRateLimiting(AuthSetup.AuthRateLimit);
        me.MapDelete("/", async (ClaimsPrincipal user, IUserService service, CancellationToken ct) =>
            Problems.From(await service.DeactivateAsync(user.UserId(), ct), _ => Results.NoContent()));
        me.MapGet("/platforms", async (ClaimsPrincipal user, IUserService service, CancellationToken ct) =>
            Problems.From(await service.GetPlatformsAsync(user.UserId(), ct)));
        me.MapPut("/platforms", async (PlatformsRequest r, ClaimsPrincipal user, IUserService service, CancellationToken ct) =>
            Problems.From(await service.ReplacePlatformsAsync(user.UserId(), r.PlatformIds, ct)));

        users.MapGet("/{id:guid}/ratings", async (Guid id, IUserService service, CancellationToken ct, string? cursor = null, int? pageSize = null) =>
            Problems.From(await service.GetRatingsAsync(id, cursor, pageSize, ct)));
        users.MapPost("/{id:guid}/ratings", async (Guid id, RatingRequest r, ClaimsPrincipal user, IUserService service, CancellationToken ct) =>
            Problems.From(
                await service.SubmitRatingAsync(user.UserId(), id, new SubmitRatingCommand(r.TransactionId, r.Score, r.Comment), ct),
                rating => Results.Created($"/api/v1/users/{id}/ratings", rating)))
            .RequireAuthorization();

        // A user's public library is their active listings (SPEC-user-library §4.3); /listings is the same view.
        users.MapGet("/{id:guid}/library", async (Guid id, ILibraryService service, CancellationToken ct) =>
            Problems.From(await service.GetPublicLibraryAsync(id, ct)));
        users.MapGet("/{id:guid}/listings", async (Guid id, ILibraryService service, CancellationToken ct) =>
            Problems.From(await service.GetPublicLibraryAsync(id, ct)));

        return app;
    }
}

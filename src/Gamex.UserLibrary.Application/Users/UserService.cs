using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Common;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Gamex.UserLibrary.Application.Users;

public sealed record UpdateProfileCommand(string? DisplayName, string? AvatarUrl, string? City, string? Region, string? Bio);

public sealed record SubmitRatingCommand(Guid? TransactionId, int? Score, string? Comment);

public interface IUserService
{
    Task<OperationResult<PublicUserDto>> GetPublicAsync(Guid id, CancellationToken ct);

    Task<OperationResult<MeDto>> GetMeAsync(Guid userId, CancellationToken ct);

    Task<OperationResult<MeDto>> UpdateMeAsync(Guid userId, UpdateProfileCommand command, CancellationToken ct);

    Task<OperationResult<bool>> ChangePasswordAsync(Guid userId, string? currentPassword, string? newPassword, CancellationToken ct);

    /// <summary>Soft delete: the account can no longer sign in and its listings disappear.</summary>
    Task<OperationResult<bool>> DeactivateAsync(Guid userId, CancellationToken ct);

    Task<OperationResult<IReadOnlyList<PlatformDto>>> GetPlatformsAsync(Guid userId, CancellationToken ct);

    Task<OperationResult<IReadOnlyList<PlatformDto>>> ReplacePlatformsAsync(Guid userId, IReadOnlyList<Guid>? platformIds, CancellationToken ct);

    Task<OperationResult<CursorPage<RatingDto>>> GetRatingsAsync(Guid userId, string? cursor, int? pageSize, CancellationToken ct);

    Task<OperationResult<RatingDto>> SubmitRatingAsync(Guid raterId, Guid rateeId, SubmitRatingCommand command, CancellationToken ct);
}

internal sealed class UserService(IAppDbContext db, IPasswordHasher hasher, TimeProvider time) : IUserService
{
    private const int MaxBioLength = 2000;
    private const int MaxCommentLength = 2000;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<OperationResult<PublicUserDto>> GetPublicAsync(Guid id, CancellationToken ct)
    {
        // Materialize first: MySql.EntityFrameworkCore cannot read DATE(...) results as DateOnly.
        var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        return u is null
            ? OperationResult<PublicUserDto>.NotFound($"No user with id {id}.")
            : OperationResult<PublicUserDto>.Ok(new PublicUserDto(u.Id, u.DisplayName, u.AvatarUrl, u.City, u.Region, u.Bio, DateOnly.FromDateTime(u.CreatedAt), u.RatingAvg, u.RatingCount));
    }

    public async Task<OperationResult<MeDto>> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadActiveAsync(userId, ct);
        return user is null ? OperationResult<MeDto>.Unauthorized("Account not found.") : OperationResult<MeDto>.Ok(UserMapping.ToMe(user));
    }

    public async Task<OperationResult<MeDto>> UpdateMeAsync(Guid userId, UpdateProfileCommand command, CancellationToken ct)
    {
        var validation = new Validation()
            .Require(command.DisplayName?.Trim().Length is >= 2 and <= 100, "displayName", "Must be 2–100 characters.")
            .Require(command.AvatarUrl is null or "" || (command.AvatarUrl.Length <= 500 && Uri.TryCreate(command.AvatarUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps), "avatarUrl", "Must be an absolute https URL.")
            .Require(command.City is null || command.City.Length <= 100, "city", "At most 100 characters.")
            .Require(command.Region is null || command.Region.Length <= 100, "region", "At most 100 characters.")
            .Require(command.Bio is null || command.Bio.Length <= MaxBioLength, "bio", $"At most {MaxBioLength} characters.");
        if (!validation.IsValid)
        {
            return OperationResult<MeDto>.Invalid(validation.Errors);
        }

        var user = await LoadActiveAsync(userId, ct);
        if (user is null)
        {
            return OperationResult<MeDto>.Unauthorized("Account not found.");
        }

        user.UpdateProfile(command.DisplayName!, command.AvatarUrl, command.City, command.Region, command.Bio, Now);
        await db.SaveChangesAsync(ct);
        return OperationResult<MeDto>.Ok(UserMapping.ToMe(user));
    }

    public async Task<OperationResult<bool>> ChangePasswordAsync(Guid userId, string? currentPassword, string? newPassword, CancellationToken ct)
    {
        var validation = new Validation();
        AuthService.ValidatePassword(validation, "newPassword", newPassword);
        if (!validation.IsValid)
        {
            return OperationResult<bool>.Invalid(validation.Errors);
        }

        var user = await LoadActiveAsync(userId, ct);
        if (user is null)
        {
            return OperationResult<bool>.Unauthorized("Account not found.");
        }

        // OAuth-only accounts set their first password through the reset flow, which proves email ownership.
        if (user.PasswordHash is null || string.IsNullOrEmpty(currentPassword) || !hasher.Verify(user.PasswordHash, currentPassword))
        {
            return OperationResult<bool>.Invalid("currentPassword", "Current password is incorrect.");
        }

        user.SetPasswordHash(hasher.Hash(newPassword!), Now);
        await db.SaveChangesAsync(ct);
        return OperationResult<bool>.Ok(true);
    }

    public async Task<OperationResult<bool>> DeactivateAsync(Guid userId, CancellationToken ct)
    {
        var user = await LoadActiveAsync(userId, ct);
        if (user is null)
        {
            return OperationResult<bool>.Unauthorized("Account not found.");
        }

        user.Deactivate(Now);
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        tokens.ForEach(t => t.Revoke(Now));
        await db.SaveChangesAsync(ct);
        return OperationResult<bool>.Ok(true);
    }

    public async Task<OperationResult<IReadOnlyList<PlatformDto>>> GetPlatformsAsync(Guid userId, CancellationToken ct) =>
        OperationResult<IReadOnlyList<PlatformDto>>.Ok(await QueryPlatformsAsync(userId, ct));

    public async Task<OperationResult<IReadOnlyList<PlatformDto>>> ReplacePlatformsAsync(Guid userId, IReadOnlyList<Guid>? platformIds, CancellationToken ct)
    {
        var ids = platformIds?.Distinct().ToList() ?? [];
        var unknown = await AuthService.UnknownPlatformsAsync(db, ids, ct);
        if (unknown.Count > 0)
        {
            return OperationResult<IReadOnlyList<PlatformDto>>.Invalid("platformIds", $"Unknown platform ids: {string.Join(", ", unknown)}");
        }

        var user = await LoadActiveAsync(userId, ct);
        if (user is null)
        {
            return OperationResult<IReadOnlyList<PlatformDto>>.Unauthorized("Account not found.");
        }

        user.ReplacePlatforms(ids, Now);
        await db.SaveChangesAsync(ct);
        return OperationResult<IReadOnlyList<PlatformDto>>.Ok(await QueryPlatformsAsync(userId, ct));
    }

    public async Task<OperationResult<CursorPage<RatingDto>>> GetRatingsAsync(Guid userId, string? cursor, int? pageSize, CancellationToken ct)
    {
        var size = Math.Clamp(pageSize ?? 20, 1, 50);
        var query = from r in db.Ratings.AsNoTracking()
                    join rater in db.Users on r.RaterId equals rater.Id
                    where r.RateeId == userId
                    select new { r, rater };

        if (Cursor.TryDecode(cursor, out var at, out var id))
        {
            var lastId = id.ToString();
#pragma warning disable CA1309 // Translated to SQL by EF; the StringComparison overload is not translatable.
            query = query.Where(x => x.r.CreatedAt < at || (x.r.CreatedAt == at && string.Compare(x.r.Id.ToString(), lastId) < 0));
#pragma warning restore CA1309
        }

        var rows = await query
            .OrderByDescending(x => x.r.CreatedAt)
            .ThenByDescending(x => x.r.Id)
            .Take(size + 1)
            .Select(x => new RatingDto(x.r.Id, x.rater.Id, x.rater.DisplayName, x.rater.AvatarUrl, x.r.Score, x.r.Comment, x.r.CreatedAt))
            .ToListAsync(ct);

        var next = rows.Count > size ? Cursor.Encode(rows[size - 1].CreatedAt, rows[size - 1].Id) : null;
        return OperationResult<CursorPage<RatingDto>>.Ok(new CursorPage<RatingDto>(rows.Take(size).ToList(), next));
    }

    public async Task<OperationResult<RatingDto>> SubmitRatingAsync(Guid raterId, Guid rateeId, SubmitRatingCommand command, CancellationToken ct)
    {
        var validation = new Validation()
            .Require(command.TransactionId is not null, "transactionId", "Required.")
            .Require(command.Score is >= 1 and <= 5, "score", "Must be between 1 and 5.")
            .Require(command.Comment is null || command.Comment.Length <= MaxCommentLength, "comment", $"At most {MaxCommentLength} characters.");
        if (!validation.IsValid)
        {
            return OperationResult<RatingDto>.Invalid(validation.Errors);
        }

        var transaction = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == command.TransactionId, ct);
        if (transaction is null || !transaction.Involves(raterId) || transaction.CounterpartOf(raterId) != rateeId)
        {
            return OperationResult<RatingDto>.Forbidden("You can only rate the other party of a transaction you completed.");
        }

        if (await db.Ratings.AnyAsync(r => r.RaterId == raterId && r.TransactionId == transaction.Id, ct))
        {
            return OperationResult<RatingDto>.Conflict("You already rated this transaction.");
        }

        var rater = await db.Users.SingleAsync(u => u.Id == raterId, ct);
        var ratee = await db.Users.SingleAsync(u => u.Id == rateeId, ct);
        var rating = UserRating.Create(raterId, rateeId, transaction.Id, command.Score!.Value, command.Comment, Now);
        db.Ratings.Add(rating);
        ratee.ApplyRating(rating.Score);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            return OperationResult<RatingDto>.Conflict("You already rated this transaction.");
        }

        return OperationResult<RatingDto>.Ok(new RatingDto(rating.Id, rater.Id, rater.DisplayName, rater.AvatarUrl, rating.Score, rating.Comment, rating.CreatedAt));
    }

    private Task<User?> LoadActiveAsync(Guid userId, CancellationToken ct) =>
        db.Users.Include(u => u.Platforms).SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);

    private async Task<IReadOnlyList<PlatformDto>> QueryPlatformsAsync(Guid userId, CancellationToken ct) =>
        await (from up in db.UserPlatforms.AsNoTracking()
               join p in db.CatalogPlatforms on up.PlatformId equals p.Id
               where up.UserId == userId
               orderby p.Name
               select new PlatformDto(p.Id, p.Name, p.Slug)).ToListAsync(ct);
}

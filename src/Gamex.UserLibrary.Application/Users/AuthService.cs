using System.Net.Mail;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Common;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gamex.UserLibrary.Application.Users;

public sealed record RegisterCommand(string? DisplayName, string? Email, string? Password, IReadOnlyList<Guid>? PlatformIds);

public interface IAuthService
{
    Task<OperationResult<AuthResult>> RegisterAsync(RegisterCommand command, CancellationToken ct);

    Task<OperationResult<AuthResult>> LoginAsync(string? email, string? password, CancellationToken ct);

    /// <summary>Rotates the refresh token. Presenting an already-rotated token revokes all of the user's sessions.</summary>
    Task<OperationResult<AuthResult>> RefreshAsync(string? refreshToken, CancellationToken ct);

    Task LogoutAsync(string? refreshToken, CancellationToken ct);

    /// <summary>Always succeeds, so callers cannot probe which emails are registered.</summary>
    Task ForgotPasswordAsync(string? email, CancellationToken ct);

    Task<OperationResult<bool>> ResetPasswordAsync(string? token, string? newPassword, CancellationToken ct);

    /// <summary>Signs in with an external profile: existing link, else verified email match (linked), else a new account.</summary>
    Task<OperationResult<AuthResult>> ExternalLoginAsync(string provider, ExternalProfile profile, CancellationToken ct);
}

internal sealed partial class AuthService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    IEmailSender email,
    AuthOptions options,
    TimeProvider time,
    ILogger<AuthService> logger) : IAuthService
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    private const string InvalidCredentials = "Email or password is incorrect.";

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<OperationResult<AuthResult>> RegisterAsync(RegisterCommand command, CancellationToken ct)
    {
        var validation = new Validation()
            .Require(command.DisplayName?.Trim().Length is >= 2 and <= 100, "displayName", "Must be 2–100 characters.")
            .Require(IsValidEmail(command.Email), "email", "Must be a valid email address.");
        ValidatePassword(validation, "password", command.Password);
        if (!validation.IsValid)
        {
            return OperationResult<AuthResult>.Invalid(validation.Errors);
        }

        var platformIds = command.PlatformIds?.Distinct().ToList() ?? [];
        var unknown = await UnknownPlatformsAsync(db, platformIds, ct);
        if (unknown.Count > 0)
        {
            return OperationResult<AuthResult>.Invalid("platformIds", $"Unknown platform ids: {string.Join(", ", unknown)}");
        }

        var normalized = User.NormalizeEmail(command.Email!);
        if (await db.Users.AnyAsync(u => u.Email == normalized, ct))
        {
            return OperationResult<AuthResult>.Conflict("An account with this email already exists.");
        }

        var user = User.Register(command.DisplayName!, normalized, hasher.Hash(command.Password!), Now);
        user.ReplacePlatforms(platformIds, Now);
        db.Users.Add(user);

        try
        {
            return OperationResult<AuthResult>.Ok(await IssueTokensAsync(user, ct));
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            return OperationResult<AuthResult>.Conflict("An account with this email already exists.");
        }
    }

    public async Task<OperationResult<AuthResult>> LoginAsync(string? email, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return OperationResult<AuthResult>.Unauthorized(InvalidCredentials);
        }

        var normalized = User.NormalizeEmail(email);
        var user = await db.Users.Include(u => u.Platforms).SingleOrDefaultAsync(u => u.Email == normalized, ct);

        // Same message for unknown email, wrong password, OAuth-only and deactivated accounts.
        if (user is not { IsActive: true, PasswordHash: { } hash } || !hasher.Verify(hash, password))
        {
            return OperationResult<AuthResult>.Unauthorized(InvalidCredentials);
        }

        return OperationResult<AuthResult>.Ok(await IssueTokensAsync(user, ct));
    }

    public async Task<OperationResult<AuthResult>> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return OperationResult<AuthResult>.Unauthorized("Refresh token is missing.");
        }

        var hash = tokens.Hash(refreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null)
        {
            return OperationResult<AuthResult>.Unauthorized("Refresh token is invalid.");
        }

        if (stored.RevokedAt is not null && stored.ReplacedByTokenId is not null)
        {
            // A rotated token came back: it was likely stolen. End every session of this user.
            LogRefreshTokenReuse(logger, stored.UserId);
            await RevokeAllAsync(stored.UserId, ct);
            await db.SaveChangesAsync(ct);
            return OperationResult<AuthResult>.Unauthorized("Refresh token is invalid.");
        }

        if (!stored.IsActive(Now))
        {
            return OperationResult<AuthResult>.Unauthorized("Refresh token is invalid.");
        }

        var user = await db.Users.Include(u => u.Platforms).SingleAsync(u => u.Id == stored.UserId, ct);
        if (!user.IsActive)
        {
            stored.Revoke(Now);
            await db.SaveChangesAsync(ct);
            return OperationResult<AuthResult>.Unauthorized("Refresh token is invalid.");
        }

        return OperationResult<AuthResult>.Ok(await IssueTokensAsync(user, ct, rotated: stored));
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var hash = tokens.Hash(refreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is not null)
        {
            stored.Revoke(Now);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ForgotPasswordAsync(string? emailAddress, CancellationToken ct)
    {
        if (!IsValidEmail(emailAddress))
        {
            return;
        }

        var normalized = User.NormalizeEmail(emailAddress!);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalized && u.IsActive, ct);
        if (user is null)
        {
            return;
        }

        var raw = tokens.CreateOpaqueToken();
        db.PasswordResetTokens.Add(PasswordResetToken.Issue(user.Id, tokens.Hash(raw), Now, options.PasswordResetLifetime));
        await db.SaveChangesAsync(ct);

        var resetUrl = new Uri(options.FrontendBaseUrl, $"/reset-password?token={Uri.EscapeDataString(raw)}");
        await email.SendPasswordResetAsync(user.Email, user.DisplayName, resetUrl, ct);
    }

    public async Task<OperationResult<bool>> ResetPasswordAsync(string? token, string? newPassword, CancellationToken ct)
    {
        var validation = new Validation().Require(!string.IsNullOrWhiteSpace(token), "token", "Required.");
        ValidatePassword(validation, "password", newPassword);
        if (!validation.IsValid)
        {
            return OperationResult<bool>.Invalid(validation.Errors);
        }

        var hash = tokens.Hash(token!);
        var reset = await db.PasswordResetTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (reset is null || !reset.IsUsable(Now))
        {
            return OperationResult<bool>.Invalid("token", "The reset link is invalid or has expired.");
        }

        var user = await db.Users.SingleAsync(u => u.Id == reset.UserId, ct);
        if (!user.IsActive)
        {
            return OperationResult<bool>.Invalid("token", "The reset link is invalid or has expired.");
        }

        user.SetPasswordHash(hasher.Hash(newPassword!), Now);
        reset.MarkUsed(Now);
        await RevokeAllAsync(user.Id, ct);
        await db.SaveChangesAsync(ct);
        return OperationResult<bool>.Ok(true);
    }

    public async Task<OperationResult<AuthResult>> ExternalLoginAsync(string provider, ExternalProfile profile, CancellationToken ct)
    {
        var link = await db.OAuthLogins.SingleOrDefaultAsync(l => l.Provider == provider && l.ProviderUid == profile.ProviderUid, ct);
        User? user = null;

        if (link is not null)
        {
            user = await db.Users.Include(u => u.Platforms).SingleAsync(u => u.Id == link.UserId, ct);
        }
        else if (profile is { EmailVerified: true, Email: { } verifiedEmail } && IsValidEmail(verifiedEmail))
        {
            // Only link by email when the provider verified it; otherwise anyone could claim an existing account.
            var normalized = User.NormalizeEmail(verifiedEmail);
            user = await db.Users.Include(u => u.Platforms).SingleOrDefaultAsync(u => u.Email == normalized, ct);
            if (user is null)
            {
                var name = string.IsNullOrWhiteSpace(profile.Name) ? normalized.Split('@')[0] : profile.Name.Trim();
                user = User.Register(name.Length > 100 ? name[..100] : name, normalized, passwordHash: null, Now);
                db.Users.Add(user);
            }

            db.OAuthLogins.Add(OAuthLogin.Link(user.Id, provider, profile.ProviderUid));
        }

        if (user is null)
        {
            return OperationResult<AuthResult>.Unauthorized("The provider did not share a verified email address.");
        }

        if (!user.IsActive)
        {
            return OperationResult<AuthResult>.Unauthorized("This account is deactivated.");
        }

        user.UseAvatarIfMissing(profile.AvatarUrl, Now);
        return OperationResult<AuthResult>.Ok(await IssueTokensAsync(user, ct));
    }

    internal static async Task<List<Guid>> UnknownPlatformsAsync(IAppDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var known = await db.CatalogPlatforms.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        return ids.Except(known).ToList();
    }

    internal static void ValidatePassword(Validation validation, string field, string? password) =>
        validation.Require(
            password?.Length is >= MinPasswordLength and <= MaxPasswordLength,
            field,
            $"Must be {MinPasswordLength}–{MaxPasswordLength} characters.");

    private static bool IsValidEmail(string? value) =>
        value is { Length: > 0 and <= 255 } && MailAddress.TryCreate(value.Trim(), out var parsed) && parsed.Address == value.Trim();

    private async Task RevokeAllAsync(Guid userId, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        active.ForEach(t => t.Revoke(Now));
    }

    private async Task<AuthResult> IssueTokensAsync(User user, CancellationToken ct, RefreshToken? rotated = null)
    {
        var raw = tokens.CreateOpaqueToken();
        var refresh = RefreshToken.Issue(user.Id, tokens.Hash(raw), Now, options.RefreshTokenLifetime);
        db.RefreshTokens.Add(refresh);
        rotated?.Revoke(Now, refresh.Id);
        await db.SaveChangesAsync(ct);

        var access = tokens.CreateAccessToken(user);
        return new AuthResult(access.Token, raw, access.ExpiresInSeconds, UserMapping.ToMe(user))
        {
            RefreshTokenExpiresAt = refresh.ExpiresAt,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rotated refresh token reused for user {UserId}; all sessions revoked")]
    private static partial void LogRefreshTokenReuse(ILogger logger, Guid userId);
}

internal static class UserMapping
{
    public static MeDto ToMe(User u) => new(
        u.Id,
        u.Email,
        u.DisplayName,
        u.AvatarUrl,
        u.City,
        u.Region,
        u.Country,
        u.Bio,
        u.Role,
        DateOnly.FromDateTime(u.CreatedAt),
        u.RatingAvg,
        u.RatingCount,
        u.Platforms.Select(p => p.PlatformId).ToList(),
        u.PasswordHash is not null);
}

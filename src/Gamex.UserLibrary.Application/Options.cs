namespace Gamex.UserLibrary.Application;

public sealed class AuthOptions
{
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan ContactTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Frontend origin, for password reset links and OAuth redirects (<c>FRONTEND_BASE_URL</c>).</summary>
    public Uri FrontendBaseUrl { get; set; } = new("http://localhost:5173");
}

public sealed class LibraryOptions
{
    /// <summary>How long a listing stays active before the background job expires it (<c>LISTING_LIFETIME_DAYS</c>).</summary>
    public TimeSpan ListingLifetime { get; set; } = TimeSpan.FromDays(30);
}

using System.Security.Cryptography;
using Gamex.UserLibrary.Api.Auth;
using Gamex.UserLibrary.Application;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Users;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.UserLibrary.Api.Endpoints;

internal static class AuthEndpoints
{
    public const string RefreshCookie = "gx_refresh";
    private const string StateCookie = "gx_oauth_state";
    private const string AuthPath = "/api/v1/auth";

    public sealed record RegisterRequest(string? DisplayName, string? Email, string? Password, Guid[]? PlatformIds);

    public sealed record LoginRequest(string? Email, string? Password);

    /// <summary>The refresh token may come in the body or the HttpOnly cookie.</summary>
    public sealed record RefreshRequest(string? RefreshToken);

    public sealed record ForgotPasswordRequest(string? Email);

    public sealed record ResetPasswordRequest(string? Token, string? Password);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup(AuthPath).WithTags("Auth").RequireRateLimiting(AuthSetup.AuthRateLimit);

        auth.MapPost("/register", async (RegisterRequest request, IAuthService service, HttpContext http, CancellationToken ct) =>
            Problems.From(
                await service.RegisterAsync(new RegisterCommand(request.DisplayName, request.Email, request.Password, request.PlatformIds), ct),
                result => SignedIn(http, result, StatusCodes.Status201Created)));

        auth.MapPost("/login", async (LoginRequest request, IAuthService service, HttpContext http, CancellationToken ct) =>
            Problems.From(await service.LoginAsync(request.Email, request.Password, ct), result => SignedIn(http, result)));

        auth.MapPost("/refresh", async (HttpContext http, IAuthService service, CancellationToken ct) =>
        {
            var token = await ReadRefreshTokenAsync(http, ct);
            var result = await service.RefreshAsync(token, ct);
            if (!result.IsOk)
            {
                ClearRefreshCookie(http);
            }

            return Problems.From(result, value => SignedIn(http, value));
        });

        auth.MapPost("/logout", async (HttpContext http, IAuthService service, CancellationToken ct) =>
        {
            await service.LogoutAsync(await ReadRefreshTokenAsync(http, ct), ct);
            ClearRefreshCookie(http);
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapPost("/forgot-password", async (ForgotPasswordRequest request, IAuthService service, CancellationToken ct) =>
        {
            await service.ForgotPasswordAsync(request.Email, ct);
            return Results.Accepted();
        });

        auth.MapPost("/reset-password", async (ResetPasswordRequest request, IAuthService service, CancellationToken ct) =>
            Problems.From(await service.ResetPasswordAsync(request.Token, request.Password, ct), _ => Results.NoContent()));

        auth.MapGet("/oauth/{provider}", StartOAuth);
        auth.MapGet("/oauth/{provider}/callback", CompleteOAuth);

        return app;
    }

    /// <summary>Redirects to the provider. State (CSRF) and the post-login path are kept in a short-lived HttpOnly cookie.</summary>
    private static IResult StartOAuth(string provider, HttpContext http, IEnumerable<IOAuthClient> clients, string? redirect = null)
    {
        var client = clients.SingleOrDefault(c => c.Provider == provider);
        if (client is not { IsConfigured: true })
        {
            return Results.Problem(type: "https://gamex.rs/errors/not-found", title: "Unknown or unconfigured provider", statusCode: StatusCodes.Status404NotFound);
        }

        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        http.Response.Cookies.Append(StateCookie, $"{state}|{SafeRedirect(redirect)}", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = $"{AuthPath}/oauth",
            MaxAge = TimeSpan.FromMinutes(10),
        });

        return Results.Redirect(client.GetAuthorizationUrl(state, CallbackUri(http, provider)).ToString());
    }

    /// <summary>
    /// Provider callback. On success redirects to the frontend's /auth/callback with the tokens in the URL fragment,
    /// which browsers never send to servers (so tokens stay out of access logs and Referer headers).
    /// </summary>
    private static async Task<IResult> CompleteOAuth(
        string provider,
        HttpContext http,
        IEnumerable<IOAuthClient> clients,
        IAuthService service,
        AuthOptions options,
        CancellationToken ct,
        string? code = null,
        string? state = null,
        string? error = null)
    {
        var frontendCallback = new Uri(options.FrontendBaseUrl, "/auth/callback");
        IResult Fail(string reason) => Results.Redirect($"{frontendCallback}#error={Uri.EscapeDataString(reason)}");

        var cookie = http.Request.Cookies[StateCookie];
        http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = $"{AuthPath}/oauth", Secure = true });

        var client = clients.SingleOrDefault(c => c.Provider == provider);
        if (client is not { IsConfigured: true })
        {
            return Fail("unknown_provider");
        }

        if (error is not null || string.IsNullOrEmpty(code))
        {
            return Fail("access_denied");
        }

        var parts = cookie?.Split('|', 2);
        if (parts is not [var expectedState, var redirect] || string.IsNullOrEmpty(state)
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expectedState), System.Text.Encoding.UTF8.GetBytes(state)))
        {
            return Fail("invalid_state");
        }

        var profile = await client.ExchangeCodeAsync(code, CallbackUri(http, provider), ct);
        if (profile is null)
        {
            return Fail("exchange_failed");
        }

        var result = await service.ExternalLoginAsync(provider, profile, ct);
        if (!result.IsOk)
        {
            return Fail(result.Detail?.Contains("deactivated", StringComparison.Ordinal) == true ? "account_deactivated" : "email_required");
        }

        SetRefreshCookie(http, result.Value!);
        var fragment = $"token={Uri.EscapeDataString(result.Value!.AccessToken)}" +
                       $"&refreshToken={Uri.EscapeDataString(result.Value.RefreshToken)}" +
                       $"&expiresIn={result.Value.ExpiresIn}" +
                       $"&redirect={Uri.EscapeDataString(redirect)}";
        return Results.Redirect($"{frontendCallback}#{fragment}");
    }

    private static IResult SignedIn(HttpContext http, AuthResult result, int status = StatusCodes.Status200OK)
    {
        SetRefreshCookie(http, result);
        return status == StatusCodes.Status201Created ? Results.Json(result, statusCode: status) : Results.Ok(result);
    }

    private static void SetRefreshCookie(HttpContext http, AuthResult result) =>
        http.Response.Cookies.Append(RefreshCookie, result.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            // gamex.rs and api.gamex.rs are the same site, so Lax still sends it with credentialed XHR.
            SameSite = SameSiteMode.Lax,
            Path = AuthPath,
            Expires = result.RefreshTokenExpiresAt,
        });

    private static void ClearRefreshCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = AuthPath, Secure = true, SameSite = SameSiteMode.Lax });

    private static async Task<string?> ReadRefreshTokenAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength > 0 && http.Request.HasJsonContentType())
        {
            var body = await http.Request.ReadFromJsonAsync<RefreshRequest>(ct);
            if (!string.IsNullOrWhiteSpace(body?.RefreshToken))
            {
                return body.RefreshToken;
            }
        }

        return http.Request.Cookies[RefreshCookie];
    }

    private static Uri CallbackUri(HttpContext http, string provider)
    {
        var configured = http.RequestServices.GetRequiredService<IConfiguration>()["PUBLIC_API_BASE_URL"];
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? $"{http.Request.Scheme}://{http.Request.Host}" : configured.TrimEnd('/');
        return new Uri($"{baseUrl}{AuthPath}/oauth/{provider}/callback");
    }

    /// <summary>Only same-app relative paths, to prevent open redirects.</summary>
    private static string SafeRedirect(string? redirect) =>
        redirect is { Length: > 0 } && redirect.StartsWith('/') && !redirect.StartsWith("//", StringComparison.Ordinal) && !redirect.Contains('\\', StringComparison.Ordinal)
            ? redirect
            : "/";
}

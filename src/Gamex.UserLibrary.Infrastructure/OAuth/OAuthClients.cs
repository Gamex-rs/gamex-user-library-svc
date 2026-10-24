using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Gamex.UserLibrary.Infrastructure.OAuth;

/// <summary>Google OpenID Connect, authorization code flow.</summary>
internal sealed partial class GoogleOAuthClient(HttpClient http, GoogleOAuthOptions options, ILogger<GoogleOAuthClient> logger) : IOAuthClient
{
    public string Provider => OAuthProviders.Google;

    public bool IsConfigured => options.IsConfigured;

    public Uri GetAuthorizationUrl(string state, Uri redirectUri) => new(
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(options.ClientId!)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}" +
        "&response_type=code&scope=openid%20email%20profile&prompt=select_account" +
        $"&state={Uri.EscapeDataString(state)}");

    public async Task<ExternalProfile?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken)
    {
        using var tokenResponse = await http.PostAsync(new Uri("https://oauth2.googleapis.com/token"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = options.ClientId!,
            ["client_secret"] = options.ClientSecret!,
            ["redirect_uri"] = redirectUri.ToString(),
            ["grant_type"] = "authorization_code",
        }), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            LogExchangeFailed(logger, Provider, (int)tokenResponse.StatusCode);
            return null;
        }

        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenJson>(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://openidconnect.googleapis.com/v1/userinfo"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        using var userResponse = await http.SendAsync(request, cancellationToken);
        if (!userResponse.IsSuccessStatusCode)
        {
            LogExchangeFailed(logger, Provider, (int)userResponse.StatusCode);
            return null;
        }

        var user = await userResponse.Content.ReadFromJsonAsync<GoogleUserJson>(cancellationToken);
        return user is null ? null : new ExternalProfile(user.Sub, user.Email, user.EmailVerified, user.Name, user.Picture);
    }

    private sealed record TokenJson([property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record GoogleUserJson(
        [property: JsonPropertyName("sub")] string Sub,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] bool EmailVerified,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("picture")] string? Picture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} OAuth exchange failed with HTTP {Status}")]
    private static partial void LogExchangeFailed(ILogger logger, string provider, int status);
}

/// <summary>Facebook Login, authorization code flow (Graph API).</summary>
internal sealed partial class FacebookOAuthClient(HttpClient http, FacebookOAuthOptions options, ILogger<FacebookOAuthClient> logger) : IOAuthClient
{
    private const string GraphVersion = "v19.0";

    public string Provider => OAuthProviders.Facebook;

    public bool IsConfigured => options.IsConfigured;

    public Uri GetAuthorizationUrl(string state, Uri redirectUri) => new(
        $"https://www.facebook.com/{GraphVersion}/dialog/oauth" +
        $"?client_id={Uri.EscapeDataString(options.ClientId!)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}" +
        "&scope=email,public_profile" +
        $"&state={Uri.EscapeDataString(state)}");

    public async Task<ExternalProfile?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken)
    {
        var tokenUri = new Uri(
            $"https://graph.facebook.com/{GraphVersion}/oauth/access_token" +
            $"?client_id={Uri.EscapeDataString(options.ClientId!)}" +
            $"&client_secret={Uri.EscapeDataString(options.ClientSecret!)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}" +
            $"&code={Uri.EscapeDataString(code)}");
        using var tokenResponse = await http.GetAsync(tokenUri, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            LogExchangeFailed(logger, Provider, (int)tokenResponse.StatusCode);
            return null;
        }

        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenJson>(cancellationToken);
        var meUri = new Uri($"https://graph.facebook.com/{GraphVersion}/me?fields=id,name,email,picture.type(large)&access_token={Uri.EscapeDataString(token!.AccessToken)}");
        using var meResponse = await http.GetAsync(meUri, cancellationToken);
        if (!meResponse.IsSuccessStatusCode)
        {
            LogExchangeFailed(logger, Provider, (int)meResponse.StatusCode);
            return null;
        }

        var me = await meResponse.Content.ReadFromJsonAsync<FacebookUserJson>(cancellationToken);

        // Facebook only returns confirmed email addresses.
        return me is null ? null : new ExternalProfile(me.Id, me.Email, me.Email is not null, me.Name, me.Picture?.Data?.Url);
    }

    private sealed record TokenJson([property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record FacebookUserJson(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("picture")] PictureJson? Picture);

    private sealed record PictureJson([property: JsonPropertyName("data")] PictureDataJson? Data);

    private sealed record PictureDataJson([property: JsonPropertyName("url")] string? Url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} OAuth exchange failed with HTTP {Status}")]
    private static partial void LogExchangeFailed(ILogger logger, string provider, int status);
}

internal sealed class GoogleOAuthOptions
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

internal sealed class FacebookOAuthOptions
{
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

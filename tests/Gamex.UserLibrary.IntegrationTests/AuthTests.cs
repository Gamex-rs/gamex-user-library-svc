using System.Net;
using System.Net.Http.Json;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Users;
using Gamex.UserLibrary.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Gamex.UserLibrary.IntegrationTests;

[Collection(LibraryApiFixture.Name)]
public sealed class AuthTests(LibraryApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Password = "correct horse battery";

    private static async Task<AuthResult> ReadAuthAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<AuthResult>(LibraryApiFactory.Json, Ct)).ShouldNotBeNull();

    [Fact]
    public async Task Register_Returns201WithTokensUserAndRefreshCookie()
    {
        var platform = (await factory.SeedGameAsync("Register Game", "playstation5")).Platforms["playstation5"];
        var client = factory.CreateHttpsClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            displayName = "Marko P.",
            email = $"Marko.{Guid.NewGuid():N}@Example.RS",
            password = Password,
            platformIds = new[] { platform },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var auth = await ReadAuthAsync(response);
        auth.AccessToken.ShouldNotBeNullOrWhiteSpace();
        auth.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        auth.ExpiresIn.ShouldBe(900);
        auth.User.DisplayName.ShouldBe("Marko P.");
        auth.User.Email.ShouldBe(auth.User.Email.ToLowerInvariant());
        auth.User.PlatformIds.ShouldBe([platform]);
        auth.User.HasPassword.ShouldBeTrue();

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("gx_refresh=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);
    }

    [Fact]
    public async Task Register_DuplicateEmailCaseInsensitive_Returns409()
    {
        var user = await factory.RegisterAsync();

        var response = await factory.CreateHttpsClient().PostAsJsonAsync("/api/v1/auth/register",
            new { displayName = "Again", email = user.Email.ToUpperInvariant(), password = Password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("A", "a@example.rs", Password, "displayName")]
    [InlineData("Ana", "not-an-email", Password, "email")]
    [InlineData("Ana", "a@example.rs", "short", "password")]
    public async Task Register_Invalid_Returns400(string name, string email, string password, string field)
    {
        var response = await factory.CreateHttpsClient().PostAsJsonAsync("/api/v1/auth/register", new { displayName = name, email, password }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys.ShouldContain(field);
    }

    [Fact]
    public async Task Register_UnknownPlatform_Returns400()
    {
        var response = await factory.CreateHttpsClient().PostAsJsonAsync("/api/v1/auth/register",
            new { displayName = "Ana", email = $"a{Guid.NewGuid():N}@example.rs", password = Password, platformIds = new[] { Guid.NewGuid() } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_WrongPasswordAndUnknownEmail_SameGeneric401()
    {
        var user = await factory.RegisterAsync();
        var client = factory.CreateHttpsClient();

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "wrong password" }, Ct);
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@example.rs", password = Password }, Ct);
        var ok = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email.ToUpperInvariant(), password = Password }, Ct);

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail
            .ShouldBe((await unknown.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Detail);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_ViaCookie_RotatesToken()
    {
        var user = await factory.RegisterAsync();

        var refreshed = await user.Client.PostAsync(new Uri("/api/v1/auth/refresh", UriKind.Relative), null, Ct);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAuthAsync(refreshed)).RefreshToken.ShouldNotBe(user.Auth.RefreshToken);
    }

    [Fact]
    public async Task Refresh_ReusingRotatedToken_RevokesAllSessions()
    {
        var user = await factory.RegisterAsync();
        var client = factory.CreateHttpsClient();

        var first = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = user.Auth.RefreshToken }, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var current = (await ReadAuthAsync(first)).RefreshToken;

        // The old token comes back (e.g. stolen): rejected, and the legitimate current token dies with it.
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = user.Auth.RefreshToken }, Ct);
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var afterTheft = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = current }, Ct);
        afterTheft.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        var user = await factory.RegisterAsync();

        (await user.Client.PostAsync(new Uri("/api/v1/auth/logout", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var refresh = await factory.CreateHttpsClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = user.Auth.RefreshToken }, Ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PasswordReset_FullFlow()
    {
        var user = await factory.RegisterAsync();
        var client = factory.CreateHttpsClient();

        (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = user.Email }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "nobody@example.rs" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var link = factory.Email.ResetLinks[user.Email];
        link.ToString().ShouldStartWith($"{LibraryApiFactory.FrontendBaseUrl}/reset-password?token=");
        var token = Uri.UnescapeDataString(link.Query.Split("token=")[1]);

        (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "a brand new password" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "another password" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Password }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "a brand new password" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Resetting the password ends existing sessions.
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = user.Auth.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_RequiresTokenAndReturnsProfile()
    {
        var user = await factory.RegisterAsync("Jelena");

        (await factory.CreateHttpsClient().GetAsync(new Uri("/api/v1/users/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var me = await user.Client.GetFromJsonAsync<MeDto>("/api/v1/users/me", LibraryApiFactory.Json, Ct);
        me!.DisplayName.ShouldBe("Jelena");

        var update = await user.Client.PutAsJsonAsync("/api/v1/users/me", new { displayName = "Jelena K.", city = "Novi Sad", bio = "Retro collector" }, Ct);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        var profile = await factory.CreateHttpsClient().GetFromJsonAsync<PublicUserDto>($"/api/v1/users/{user.Id}", LibraryApiFactory.Json, Ct);
        profile!.City.ShouldBe("Novi Sad");
        profile.DisplayName.ShouldBe("Jelena K.");
    }

    [Fact]
    public async Task ChangePassword_RequiresCurrentPassword()
    {
        var user = await factory.RegisterAsync();

        (await user.Client.PutAsJsonAsync("/api/v1/users/me/password", new { currentPassword = "wrong", newPassword = "new password 1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await user.Client.PutAsJsonAsync("/api/v1/users/me/password", new { currentPassword = Password, newPassword = "new password 1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await factory.CreateHttpsClient().PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "new password 1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deactivate_BlocksLoginAndHidesProfile()
    {
        var user = await factory.RegisterAsync();

        (await user.Client.DeleteAsync(new Uri("/api/v1/users/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await factory.CreateHttpsClient().PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Password }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.CreateHttpsClient().GetAsync(new Uri($"/api/v1/users/{user.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Platforms_ReplaceAndRead()
    {
        var game = await factory.SeedGameAsync("Platform Prefs", "pc", "nintendo-switch");
        var user = await factory.RegisterAsync();

        var put = await user.Client.PutAsJsonAsync("/api/v1/users/me/platforms", new { platformIds = game.Platforms.Values }, Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var platforms = await user.Client.GetFromJsonAsync<List<PlatformDto>>("/api/v1/users/me/platforms", LibraryApiFactory.Json, Ct);
        platforms!.Select(p => p.Slug).ShouldBe(["nintendo-switch", "pc"], ignoreOrder: true);

        (await user.Client.PutAsJsonAsync("/api/v1/users/me/platforms", new { platformIds = new[] { Guid.NewGuid() } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await user.Client.PutAsJsonAsync("/api/v1/users/me/platforms", new { platformIds = Array.Empty<Guid>() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await user.Client.GetFromJsonAsync<List<PlatformDto>>("/api/v1/users/me/platforms", LibraryApiFactory.Json, Ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task OAuth_StartAndCallback_SignsInAndRedirectsWithFragment()
    {
        var client = factory.CreateHttpsClient(followRedirects: false);
        var email = $"oauth{Guid.NewGuid():N}@gmail.example";
        factory.OAuth.Profiles["code-1"] = new ExternalProfile($"google-{Guid.NewGuid():N}", email, EmailVerified: true, "Google User", "https://lh3.example/p.jpg");

        var start = await client.GetAsync(new Uri("/api/v1/auth/oauth/google?redirect=/games", UriKind.Relative), Ct);
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var state = Uri.UnescapeDataString(start.Headers.Location!.Query.Split("state=")[1].Split('&')[0]);

        var callback = await client.GetAsync(new Uri($"/api/v1/auth/oauth/google/callback?code=code-1&state={Uri.EscapeDataString(state)}", UriKind.Relative), Ct);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = callback.Headers.Location!.ToString();
        location.ShouldStartWith($"{LibraryApiFactory.FrontendBaseUrl}/auth/callback#token=");
        location.ShouldContain("&redirect=%2Fgames");
        location.ShouldNotContain("?", customMessage: "tokens must be in the fragment, not the query");

        // Same provider account again: same user, no duplicate.
        factory.OAuth.Profiles["code-2"] = factory.OAuth.Profiles["code-1"];
        var start2 = await client.GetAsync(new Uri("/api/v1/auth/oauth/google", UriKind.Relative), Ct);
        var state2 = Uri.UnescapeDataString(start2.Headers.Location!.Query.Split("state=")[1].Split('&')[0]);
        var again = await client.GetAsync(new Uri($"/api/v1/auth/oauth/google/callback?code=code-2&state={Uri.EscapeDataString(state2)}", UriKind.Relative), Ct);
        again.Headers.Location!.ToString().ShouldContain("#token=");
    }

    [Fact]
    public async Task OAuth_WrongState_RedirectsWithError()
    {
        var client = factory.CreateHttpsClient(followRedirects: false);
        await client.GetAsync(new Uri("/api/v1/auth/oauth/google", UriKind.Relative), Ct);

        var callback = await client.GetAsync(new Uri("/api/v1/auth/oauth/google/callback?code=x&state=forged", UriKind.Relative), Ct);

        callback.Headers.Location!.ToString().ShouldBe($"{LibraryApiFactory.FrontendBaseUrl}/auth/callback#error=invalid_state");
    }

    [Fact]
    public async Task OAuth_VerifiedEmailOfExistingUser_LinksAccount()
    {
        var user = await factory.RegisterAsync();
        var client = factory.CreateHttpsClient(followRedirects: false);
        factory.OAuth.Profiles["link"] = new ExternalProfile($"google-{Guid.NewGuid():N}", user.Email, EmailVerified: true, "X", null);

        var start = await client.GetAsync(new Uri("/api/v1/auth/oauth/google", UriKind.Relative), Ct);
        var state = Uri.UnescapeDataString(start.Headers.Location!.Query.Split("state=")[1].Split('&')[0]);
        var callback = await client.GetAsync(new Uri($"/api/v1/auth/oauth/google/callback?code=link&state={Uri.EscapeDataString(state)}", UriKind.Relative), Ct);

        var token = Uri.UnescapeDataString(callback.Headers.Location!.Fragment.Split("token=")[1].Split('&')[0]);
        var me = factory.CreateHttpsClient();
        me.DefaultRequestHeaders.Authorization = new("Bearer", token);
        (await me.GetFromJsonAsync<MeDto>("/api/v1/users/me", LibraryApiFactory.Json, Ct))!.Id.ShouldBe(user.Id);
    }
}

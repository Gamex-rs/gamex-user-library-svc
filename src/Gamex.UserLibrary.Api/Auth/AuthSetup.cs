using System.Security.Claims;
using System.Threading.RateLimiting;
using Gamex.UserLibrary.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.UserLibrary.Api.Auth;

internal static class AuthSetup
{
    public const string AuthRateLimit = "auth";

    public static IServiceCollection AddGamexAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Validate with this service's own signing key; options are resolved after the key is loaded.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtOptions>((options, jwt) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    IssuerSigningKey = jwt.SigningKey,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RoleClaimType = "role",
                    NameClaimType = "name",
                };
            });

        services.AddAuthorization();

        var permitPerMinute = configuration.GetValue("AUTH_RATE_LIMIT_PER_MINUTE", 20);

        // Brute-force protection for credential endpoints: per client IP.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        return services;
    }

    /// <summary>The authenticated user's id (<c>sub</c> claim).</summary>
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("sub") ?? throw new InvalidOperationException("Missing sub claim."));
}

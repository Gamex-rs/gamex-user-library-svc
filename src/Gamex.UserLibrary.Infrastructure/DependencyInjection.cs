using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Gamex.UserLibrary.Application;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Infrastructure.Email;
using Gamex.UserLibrary.Infrastructure.Jobs;
using Gamex.UserLibrary.Infrastructure.Messaging;
using Gamex.UserLibrary.Infrastructure.OAuth;
using Gamex.UserLibrary.Infrastructure.Persistence;
using Gamex.UserLibrary.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Gamex.UserLibrary.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration["DB_CONNECTION_STRING"]
            ?? throw new InvalidOperationException("DB_CONNECTION_STRING is not configured.");

        services.AddDbContext<AppDbContext>(options => options.UseMySQL(connectionString));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

        services.AddSingleton(LoadAuthOptions(configuration));
        services.AddSingleton(LoadLibraryOptions(configuration));
        services.AddSingleton(LoadJwtOptions(configuration, environment));
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddSingleton<IEmailSender, LoggingEmailSender>();

        services.AddOAuth(configuration);
        services.AddMessaging(configuration);

        services.AddSingleton(new MySqlJobLock(connectionString));
        if (configuration.GetValue("LISTING_EXPIRY_ENABLED", true))
        {
            services.AddHostedService<ListingExpiryWorker>();
        }

        return services;
    }

    private static AuthOptions LoadAuthOptions(IConfiguration configuration)
    {
        var options = new AuthOptions();
        if (configuration["FRONTEND_BASE_URL"] is { Length: > 0 } frontend)
        {
            options.FrontendBaseUrl = new Uri(frontend);
        }

        return options;
    }

    private static LibraryOptions LoadLibraryOptions(IConfiguration configuration)
    {
        var options = new LibraryOptions();
        if (int.TryParse(configuration["LISTING_LIFETIME_DAYS"], out var days) && days > 0)
        {
            options.ListingLifetime = TimeSpan.FromDays(days);
        }

        return options;
    }

    /// <summary>
    /// RS256 private key from <c>JWT_PRIVATE_KEY</c> (PEM, "\n" escapes allowed) or <c>JWT_PRIVATE_KEY_PATH</c>.
    /// Development without a key gets an ephemeral one; its public key is logged for the other local services.
    /// </summary>
    private static JwtOptions LoadJwtOptions(IConfiguration configuration, IHostEnvironment environment)
    {
        var pem = configuration["JWT_PRIVATE_KEY"];
        if (string.IsNullOrWhiteSpace(pem) && configuration["JWT_PRIVATE_KEY_PATH"] is { Length: > 0 } path)
        {
            pem = File.ReadAllText(path);
        }

        var rsa = RSA.Create();
        if (!string.IsNullOrWhiteSpace(pem))
        {
            rsa.ImportFromPem(pem.Replace("\\n", "\n", StringComparison.Ordinal));
        }
        else if (environment.IsDevelopment())
        {
            // Logged at startup (Program.cs) together with the public key for the other local services.
            rsa.KeySize = 2048;
        }
        else
        {
            throw new InvalidOperationException("JWT_PRIVATE_KEY or JWT_PRIVATE_KEY_PATH is not configured.");
        }

        return new JwtOptions
        {
            SigningKey = new RsaSecurityKey(rsa) { KeyId = KeyId(rsa) },
            IsEphemeral = string.IsNullOrWhiteSpace(pem),
            Issuer = configuration["JWT_ISSUER"] is { Length: > 0 } issuer ? issuer : JwtOptions.DefaultIssuer,
            Audience = configuration["JWT_AUDIENCE"] is { Length: > 0 } audience ? audience : JwtOptions.DefaultAudience,
        };
    }

    /// <summary>Stable key id (thumbprint of the public key) so a future key rotation can publish several keys.</summary>
    private static string KeyId(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16];

    private static void AddOAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new GoogleOAuthOptions
        {
            ClientId = configuration["GOOGLE_CLIENT_ID"],
            ClientSecret = configuration["GOOGLE_CLIENT_SECRET"],
        });
        services.AddSingleton(new FacebookOAuthOptions
        {
            ClientId = configuration["FACEBOOK_APP_ID"],
            ClientSecret = configuration["FACEBOOK_APP_SECRET"],
        });

        services.AddHttpClient<GoogleOAuthClient>().AddStandardResilienceHandler();
        services.AddHttpClient<FacebookOAuthClient>().AddStandardResilienceHandler();
        services.AddTransient<IOAuthClient>(sp => sp.GetRequiredService<GoogleOAuthClient>());
        services.AddTransient<IOAuthClient>(sp => sp.GetRequiredService<FacebookOAuthClient>());
    }

    private static void AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new MessagingOptions
        {
            LibraryEventsQueueUrl = configuration["SQS_LIBRARY_EVENTS_URL"],
            ActivityEventsQueueUrl = configuration["SQS_ACTIVITY_EVENTS_URL"],
            CatalogEventsQueueUrl = configuration["SQS_CATALOG_EVENTS_URL"],
        };
        if (int.TryParse(configuration["SQS_RETRY_DELAY_SECONDS"], out var retryDelay))
        {
            options.RetryDelaySeconds = retryDelay;
        }

        services.AddSingleton(options);
        services.AddSingleton<IAmazonSQS>(_ =>
        {
            var region = configuration["AWS_DEFAULT_REGION"] ?? "eu-central-1";
            var config = new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
            if (configuration["AWS_SERVICE_URL"] is { Length: > 0 } serviceUrl)
            {
                // LocalStack: the SDK cannot infer the region from a custom host and would sign as us-east-1.
                config.ServiceURL = serviceUrl;
                config.AuthenticationRegion = region;
                return new AmazonSQSClient(new BasicAWSCredentials("test", "test"), config);
            }

            return new AmazonSQSClient(config);
        });
        services.AddSingleton<IEventPublisher, SqsEventPublisher>();

        if (!string.IsNullOrWhiteSpace(options.CatalogEventsQueueUrl))
        {
            services.AddHostedService<CatalogEventsConsumer>();
        }
    }
}

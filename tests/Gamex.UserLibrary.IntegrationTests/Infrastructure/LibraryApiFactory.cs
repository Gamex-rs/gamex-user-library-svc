using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Application.Catalog;
using Gamex.UserLibrary.Application.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.LocalStack;
using Testcontainers.MySql;

namespace Gamex.UserLibrary.IntegrationTests.Infrastructure;

/// <summary>API against MySQL and LocalStack SQS, with a fake OAuth provider and email sender. Requires Docker.</summary>
public sealed class LibraryApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly RSA SigningKey = RSA.Create(2048);
    public const string FrontendBaseUrl = "https://gamex.test";

    private readonly MySqlContainer _mySql = new MySqlBuilder("mysql:8.0").WithDatabase("library_db").Build();
    private readonly LocalStackContainer _localStack = new LocalStackBuilder("localstack/localstack:3").Build();

    public string LibraryEventsUrl { get; private set; } = "";
    public string ActivityEventsUrl { get; private set; } = "";
    public string CatalogEventsUrl { get; private set; } = "";
    public FakeOAuthClient OAuth { get; } = new();
    public RecordingEmailSender Email { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_mySql.StartAsync(), _localStack.StartAsync());

        using var sqs = CreateSqsClient();
        LibraryEventsUrl = (await sqs.CreateQueueAsync("gamex-library-events")).QueueUrl;
        ActivityEventsUrl = (await sqs.CreateQueueAsync("gamex-activity-events")).QueueUrl;
        var dlq = (await sqs.CreateQueueAsync("gamex-catalog-events-dlq")).QueueUrl;
        var dlqArn = (await sqs.GetQueueAttributesAsync(dlq, ["QueueArn"])).QueueARN;
        CatalogEventsUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "gamex-catalog-events",
            Attributes = new Dictionary<string, string>
            {
                ["VisibilityTimeout"] = "1",
                ["RedrivePolicy"] = $$"""{"deadLetterTargetArn":"{{dlqArn}}","maxReceiveCount":"3"}""",
            },
        })).QueueUrl;

        // Building the host applies migrations.
        _ = Services;
        StartDraining(LibraryEventsUrl, ActivityEventsUrl);
    }

    public AmazonSQSClient CreateSqsClient() => new(
        new BasicAWSCredentials("test", "test"),
        new AmazonSQSConfig { ServiceURL = _localStack.GetConnectionString(), AuthenticationRegion = "eu-central-1" });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DB_CONNECTION_STRING", _mySql.GetConnectionString());
        builder.UseSetting("DB_MIGRATE_ON_STARTUP", "true");
        builder.UseSetting("JWT_PRIVATE_KEY", SigningKey.ExportPkcs8PrivateKeyPem());
        builder.UseSetting("AWS_SERVICE_URL", _localStack.GetConnectionString());
        builder.UseSetting("SQS_LIBRARY_EVENTS_URL", LibraryEventsUrl);
        builder.UseSetting("SQS_ACTIVITY_EVENTS_URL", ActivityEventsUrl);
        builder.UseSetting("SQS_CATALOG_EVENTS_URL", CatalogEventsUrl);
        builder.UseSetting("SQS_RETRY_DELAY_SECONDS", "1");
        builder.UseSetting("LISTING_EXPIRY_ENABLED", "false");
        builder.UseSetting("AUTH_RATE_LIMIT_PER_MINUTE", "100000");
        builder.UseSetting("FRONTEND_BASE_URL", FrontendBaseUrl);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOAuthClient>();
            services.AddSingleton<IOAuthClient>(OAuth);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
        });
    }

    /// <summary>HTTPS so Secure cookies (refresh token) round-trip.</summary>
    public HttpClient CreateHttpsClient(bool followRedirects = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = followRedirects,
    });

    public async Task<TestUser> RegisterAsync(string? name = null, Guid[]? platformIds = null)
    {
        var client = CreateHttpsClient();
        var email = $"user-{Guid.NewGuid():N}@example.rs";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            displayName = name ?? "Test User",
            email,
            password = "correct horse battery",
            platformIds = platformIds ?? [],
        }, Json);
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResult>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new TestUser(auth.User.Id, email, client, auth);
    }

    /// <summary>Applies catalog events straight to the replica (the consumer itself is tested separately).</summary>
    public async Task<SeededGame> SeedGameAsync(string title, params string[] platformSlugs)
    {
        await using var scope = Services.CreateAsyncScope();
        var replica = scope.ServiceProvider.GetRequiredService<ICatalogReplicaService>();
        var platforms = new List<Guid>();
        foreach (var slug in platformSlugs)
        {
            var id = PlatformIds.GetOrAdd(slug, _ => Guid.CreateVersion7());
            await replica.ApplyAsync(new PlatformUpserted(id, slug.ToUpperInvariant(), slug, DateTime.UtcNow), CancellationToken.None);
            platforms.Add(id);
        }

        var gameId = Guid.CreateVersion7();
        await replica.ApplyAsync(new GameUpserted(gameId, title, $"https://media.gamex.test/{gameId}.jpg", new DateOnly(2020, 9, 17), 93, platforms, DateTime.UtcNow), CancellationToken.None);
        return new SeededGame(gameId, title, platformSlugs.Zip(platforms).ToDictionary(x => x.First, x => x.Second));
    }

    public ConcurrentDictionary<string, Guid> PlatformIds { get; } = new();

    private readonly ConcurrentDictionary<string, ConcurrentQueue<JsonElement>> _received = new();
    private readonly CancellationTokenSource _drainStop = new();

    /// <summary>
    /// Every test publishes events that most tests never read. Draining the outgoing queues into memory in the
    /// background (instead of each test polling SQS) keeps one test's backlog from starving another's.
    /// </summary>
    private void StartDraining(params string[] queueUrls)
    {
        foreach (var url in queueUrls)
        {
            var sink = _received.GetOrAdd(url, _ => new ConcurrentQueue<JsonElement>());
            _ = Task.Run(async () =>
            {
                using var sqs = CreateSqsClient();
                while (!_drainStop.IsCancellationRequested)
                {
                    try
                    {
                        var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 }, _drainStop.Token);
                        foreach (var message in response.Messages ?? [])
                        {
                            sink.Enqueue(JsonDocument.Parse(message.Body).RootElement.Clone());
                            await sqs.DeleteMessageAsync(url, message.ReceiptHandle, _drainStop.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
        }
    }

    /// <summary>
    /// Waits until <paramref name="expected"/> matching events were published (or the timeout passes) and returns them.
    /// For "nothing published" checks pass <c>expected: 1</c> with a short timeout and assert empty.
    /// </summary>
    public async Task<List<JsonElement>> ReceiveAsync(string queueUrl, Func<JsonElement, bool> match, int expected, TimeSpan? timeout = null)
    {
        var sink = _received[queueUrl];
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        List<JsonElement> found;
        do
        {
            found = sink.Where(match).ToList();
            if (found.Count >= expected)
            {
                break;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return found;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public override async ValueTask DisposeAsync()
    {
        await _drainStop.CancelAsync();
        _drainStop.Dispose();
        await base.DisposeAsync();
        await _mySql.DisposeAsync();
        await _localStack.DisposeAsync();
    }
}

public sealed record TestUser(Guid Id, string Email, HttpClient Client, AuthResult Auth);

public sealed record SeededGame(Guid Id, string Title, IReadOnlyDictionary<string, Guid> Platforms);

public sealed class FakeOAuthClient : IOAuthClient
{
    public string Provider => "google";
    public bool IsConfigured => true;
    public ConcurrentDictionary<string, ExternalProfile> Profiles { get; } = new();

    public Uri GetAuthorizationUrl(string state, Uri redirectUri) =>
        new($"https://accounts.example/auth?state={Uri.EscapeDataString(state)}&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}");

    public Task<ExternalProfile?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.GetValueOrDefault(code));
}

public sealed class RecordingEmailSender : IEmailSender
{
    public ConcurrentDictionary<string, Uri> ResetLinks { get; } = new();

    public Task SendPasswordResetAsync(string email, string displayName, Uri resetUrl, CancellationToken cancellationToken)
    {
        ResetLinks[email] = resetUrl;
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class LibraryApiFixture : ICollectionFixture<LibraryApiFactory>
{
    public const string Name = "LibraryApi";
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SQS;
using Amazon.SQS.Model;
using Gamex.UserLibrary.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Gamex.UserLibrary.Infrastructure.Messaging;

internal sealed class MessagingOptions
{
    /// <summary><c>SQS_LIBRARY_EVENTS_URL</c>: status deltas for Game Catalog stats.</summary>
    public string? LibraryEventsQueueUrl { get; set; }

    /// <summary><c>SQS_ACTIVITY_EVENTS_URL</c>: events for the Notification Service.</summary>
    public string? ActivityEventsQueueUrl { get; set; }

    /// <summary><c>SQS_CATALOG_EVENTS_URL</c>: consumed to maintain the catalog replica.</summary>
    public string? CatalogEventsQueueUrl { get; set; }

    /// <summary>Delay before a game event whose platform is not replicated yet is redelivered (<c>SQS_RETRY_DELAY_SECONDS</c>).</summary>
    public int RetryDelaySeconds { get; set; } = 30;
}

internal static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}

/// <summary>
/// Publishes to <c>gamex-library-events</c> and <c>gamex-activity-events</c>. Failures are logged, not thrown:
/// the database change is already committed (same trade-off as the Game Catalog Service; an outbox can follow).
/// </summary>
internal sealed partial class SqsEventPublisher(IAmazonSQS sqs, MessagingOptions options, ILogger<SqsEventPublisher> logger) : IEventPublisher
{
    private const int MaxBatchSize = 10;

    public Task PublishAsync(IReadOnlyCollection<GameStatusChanged> events, CancellationToken cancellationToken) =>
        SendAsync(options.LibraryEventsQueueUrl, events.Select(e => ("library.game.status.changed", (object)new
        {
            eventType = "library.game.status.changed",
            gameId = e.GameId,
            userId = e.UserId,
            delta = new { owned = e.Delta.Owned, wishlist = e.Delta.Wishlist, forExchange = e.Delta.ForExchange, forSale = e.Delta.ForSale, wantToBuy = e.Delta.WantToBuy },
            occurredAt = e.OccurredAt,
        })), cancellationToken);

    public Task PublishAsync(IReadOnlyCollection<ListingCreated> events, CancellationToken cancellationToken) =>
        SendAsync(options.ActivityEventsQueueUrl, events.Select(e => ("listing.created", (object)new
        {
            eventType = "listing.created",
            listingId = e.ListingId,
            gameId = e.GameId,
            gameTitle = e.GameTitle,
            userId = e.UserId,
            sellerName = e.SellerName,
            listingType = e.ListingType,
            platformId = e.PlatformId,
            platformSlug = e.PlatformSlug,
            format = e.Format,
            notifyUserId = e.NotifyUserId,
            occurredAt = e.OccurredAt,
        })), cancellationToken);

    public Task PublishAsync(ListingExpired message, CancellationToken cancellationToken) =>
        SendAsync(options.ActivityEventsQueueUrl, [("listing.expired", new
        {
            eventType = "listing.expired",
            listingId = message.ListingId,
            gameTitle = message.GameTitle,
            userId = message.UserId,
            expiresAt = message.ExpiresAt,
            occurredAt = message.OccurredAt,
        })], cancellationToken);

    public Task PublishAsync(TransactionCompleted message, CancellationToken cancellationToken) =>
        SendAsync(options.ActivityEventsQueueUrl, [("transaction.completed", new
        {
            eventType = "transaction.completed",
            transactionId = message.TransactionId,
            listingEntryId = message.ListingEntryId,
            gameTitle = message.GameTitle,
            userIdA = message.UserIdA,
            userNameA = message.UserNameA,
            userIdB = message.UserIdB,
            userNameB = message.UserNameB,
            occurredAt = message.OccurredAt,
        })], cancellationToken);

    private async Task SendAsync(string? queueUrl, IEnumerable<(string EventType, object Body)> messages, CancellationToken ct)
    {
        foreach (var batch in messages.Chunk(MaxBatchSize))
        {
            if (string.IsNullOrWhiteSpace(queueUrl))
            {
                foreach (var (eventType, _) in batch)
                {
                    LogNotConfigured(logger, eventType);
                }

                continue;
            }

            var request = new SendMessageBatchRequest
            {
                QueueUrl = queueUrl,
                Entries = batch.Select((m, i) => new SendMessageBatchRequestEntry
                {
                    Id = i.ToString(CultureInfo.InvariantCulture),
                    MessageBody = JsonSerializer.Serialize(m.Body, EventJson.Options),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new() { DataType = "String", StringValue = m.EventType },
                    },
                }).ToList(),
            };

            try
            {
                var response = await sqs.SendMessageBatchAsync(request, ct);
                foreach (var failed in response.Failed ?? [])
                {
                    LogSendFailed(logger, null, batch[int.Parse(failed.Id, CultureInfo.InvariantCulture)].EventType, failed.Message);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AmazonSQSException ex)
            {
                foreach (var (eventType, _) in batch)
                {
                    LogSendFailed(logger, ex, eventType, ex.Message);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing {EventType} failed: {Reason}")]
    private static partial void LogSendFailed(ILogger logger, Exception? exception, string eventType, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Queue for {EventType} not configured; event dropped")]
    private static partial void LogNotConfigured(ILogger logger, string eventType);
}

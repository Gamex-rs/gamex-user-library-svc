using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Gamex.UserLibrary.Application.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gamex.UserLibrary.Infrastructure.Messaging;

/// <summary>
/// Consumes <c>gamex-catalog-events</c> into the catalog replica (SPEC-user-library §6). Stale events are acknowledged;
/// a game whose platform has not arrived yet is redelivered later; malformed messages reach the DLQ via the redrive policy.
/// </summary>
internal sealed partial class CatalogEventsConsumer(
    IAmazonSQS sqs,
    MessagingOptions options,
    IServiceScopeFactory scopes,
    ILogger<CatalogEventsConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = options.CatalogEventsQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 20,
                }, stoppingToken);

                foreach (var message in response.Messages ?? [])
                {
                    await HandleAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Keep polling; unhandled messages become visible again and are retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task HandleAsync(Message message, CancellationToken ct)
    {
        var parsed = Parse(message.Body);
        if (parsed is null)
        {
            LogInvalidMessage(logger, message.MessageId);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var replica = scope.ServiceProvider.GetRequiredService<ICatalogReplicaService>();
        var outcome = parsed switch
        {
            PlatformUpserted p => await replica.ApplyAsync(p, ct),
            GameUpserted g => await replica.ApplyAsync(g, ct),
            _ => throw new InvalidOperationException("Unexpected event."),
        };

        if (outcome == ReplicaOutcome.MissingPlatform)
        {
            await sqs.ChangeMessageVisibilityAsync(options.CatalogEventsQueueUrl, message.ReceiptHandle, options.RetryDelaySeconds, ct);
            return;
        }

        await sqs.DeleteMessageAsync(options.CatalogEventsQueueUrl, message.ReceiptHandle, ct);
    }

    /// <returns><see cref="PlatformUpserted"/>, <see cref="GameUpserted"/>, or <c>null</c> if invalid.</returns>
    internal static object? Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.TrimStart('﻿'));
            var root = doc.RootElement;
            return root.GetProperty("eventType").GetString() switch
            {
                "catalog.platform.upserted" => root.Deserialize<PlatformUpserted>(EventJson.Options) is { Name.Length: > 0, Slug.Length: > 0 } p && p.PlatformId != Guid.Empty ? p : null,
                "catalog.game.upserted" => root.Deserialize<GameUpserted>(EventJson.Options) is { Title.Length: > 0, PlatformIds: not null } g && g.GameId != Guid.Empty ? g : null,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling gamex-catalog-events failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invalid catalog event {MessageId}; leaving it for the DLQ")]
    private static partial void LogInvalidMessage(ILogger logger, string messageId);
}

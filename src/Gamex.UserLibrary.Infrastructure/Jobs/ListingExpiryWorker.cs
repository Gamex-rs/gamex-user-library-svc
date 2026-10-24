using Gamex.UserLibrary.Application.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gamex.UserLibrary.Infrastructure.Jobs;

/// <summary>Runs the listing expiry job every hour on one instance (MySQL <c>GET_LOCK</c>).</summary>
internal sealed partial class ListingExpiryWorker(
    MySqlJobLock jobLock,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<ListingExpiryWorker> logger) : BackgroundService
{
    public const string LockName = "gamex-library-listing-expiry";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed run must not stop the schedule; the next run retries.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var handle = await jobLock.TryAcquireAsync(LockName, ct);
        if (handle is null)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IListingExpiryService>().ExpireDueAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Listing expiry run failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

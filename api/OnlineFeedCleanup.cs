using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace api;

/// <summary>
/// Removes TV link index entries that no longer lead anywhere: their profile was deleted, their link was
/// revoked or regenerated, or a request failed halfway. Such entries grant nothing; this only keeps the
/// index small. Entries younger than two hours are left alone because they may belong to a change in progress.
/// </summary>
public sealed class OnlineFeedCleanup(OnlineFeedService feeds, ILogger<OnlineFeedCleanup> logger)
{
    private static readonly TimeSpan CleanupAge = TimeSpan.FromHours(2);
    private const int MaxChecksPerRun = 500;

    [Function("OnlineFeedCleanup")]
    public async Task Run([TimerTrigger("0 30 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (!feeds.IsConfigured)
            return;

        try
        {
            var result = await feeds.CleanupAsync(DateTimeOffset.UtcNow - CleanupAge, MaxChecksPerRun, cancellationToken);
            if (result.Deleted > 0)
                logger.LogInformation("TV link cleanup checked {Checked} index entries and deleted {Deleted}.",
                    result.Checked, result.Deleted);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (exception is RequestFailedException failed)
                logger.LogError("TV link cleanup failed: storage returned {Status} {ErrorCode}.", failed.Status, failed.ErrorCode);
            else
                logger.LogError("TV link cleanup failed ({ExceptionType}).", exception.GetType().Name);
        }
    }
}

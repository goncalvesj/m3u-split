using System.Diagnostics;
using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace api;

/// <summary>
/// Refreshes saved profiles: downloads the provider playlist into a temporary blob, indexes its
/// categories, writes a new immutable output version and promotes it with one ETag-guarded record
/// update. A failed refresh never changes the current output. Every write that matters re-checks the
/// record first, so a deleted or superseded profile is never recreated or overwritten.
/// </summary>
public sealed class OnlineProfileRefreshWorker(OnlineProfileService profiles, ILogger<OnlineProfileRefreshWorker> logger)
{
    private const int MaxUpdateAttempts = 5;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);
    // Refreshes end within the 10-minute function timeout, so anything this old is left over.
    private static readonly TimeSpan CleanupAge = TimeSpan.FromHours(2);

    [Function("OnlineProfileRefreshWorker")]
    public async Task Run(
        [QueueTrigger(OnlineProfileStore.QueueName, Connection = OnlinePlaylistStorage.ConnectionName)] string message,
        CancellationToken cancellationToken)
    {
        if (!profiles.IsConfigured)
        {
            logger.LogError("Online profile refresh message ignored because saved profiles are not configured.");
            return;
        }

        var parsed = OnlineProfileStore.ReadMessage(message);
        if (parsed is null)
        {
            logger.LogWarning("Ignoring a malformed online profile refresh message.");
            return;
        }

        var store = profiles.Store;
        // Storage failures before the refresh starts are retried by the queue and then poisoned.
        var stored = await store.GetAsync(parsed.Owner, parsed.ProfileId, cancellationToken);
        if (stored?.Record.Refresh is not { } refresh || refresh.RefreshId != parsed.RefreshId)
        {
            logger.LogInformation("Online profile refresh {RefreshId} skipped: superseded or profile deleted.", parsed.RefreshId);
            return;
        }

        var run = new RefreshRun(parsed.Owner, parsed.ProfileId, parsed.RefreshId, stored);
        if (refresh.Status == OnlineProfileRefreshStatus.Running)
        {
            // An earlier delivery stopped part-way (for example the host restarted); do not start over.
            logger.LogWarning("Online profile refresh {RefreshId} of profile {ProfileId} was interrupted.",
                run.RefreshId, run.ProfileId);
            await StopAsync(run, OnlineProfileError.Interrupted, null);
            await DeleteWorkAsync(run);
            return;
        }

        if (refresh.Status != OnlineProfileRefreshStatus.Queued)
            return;

        var startedAt = DateTimeOffset.UtcNow;
        if (!await TryUpdateRefreshAsync(run, current => current.Status == OnlineProfileRefreshStatus.Queued, (_, current) =>
            {
                current.Status = OnlineProfileRefreshStatus.Running;
                current.Stage = OnlineProfileRefreshStage.Downloading;
                current.StartedAt = startedAt;
                current.HeartbeatAt = startedAt;
                current.FinishedAt = null;
                current.BytesDownloaded = 0;
                current.TotalBytes = null;
                current.Entries = null;
                current.Error = null;
            }, cancellationToken))
            return;

        run.LastSaved = Stopwatch.GetTimestamp();
        logger.LogInformation("Online profile refresh {RefreshId} of profile {ProfileId} started from {Source}.",
            run.RefreshId, run.ProfileId, run.Stored.Record.SourceDisplay);
        try
        {
            await RefreshAsync(run, cancellationToken);
        }
        catch (RefreshAbandonedException)
        {
            logger.LogInformation("Online profile refresh {RefreshId} stopped: superseded or profile deleted.", run.RefreshId);
            await StopAsync(run, null, null);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // A refresh blob vanished, usually because the profile was deleted meanwhile.
            logger.LogInformation("Online profile refresh {RefreshId} stopped because its data was removed.", run.RefreshId);
            await StopAsync(run, OnlineProfileError.Interrupted, null);
        }
        catch (SourceFetchException exception)
        {
            await StopAsync(run, exception.Code, exception.HttpStatus);
        }
        catch (M3uFormatException exception)
        {
            await StopAsync(run, exception.Code, null);
        }
        catch (RefreshFailedException exception)
        {
            await StopAsync(run, exception.Code, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync(run, OnlineProfileError.Interrupted, null);
        }
        catch (Exception exception)
        {
            LogFailure(exception, "refresh");
            await StopAsync(run, OnlineProfileError.Internal, null);
        }
        finally
        {
            await DeleteWorkAsync(run);
        }
    }

    /// <summary>Messages that kept failing are reported as interrupted refreshes.</summary>
    [Function("OnlineProfileRefreshPoison")]
    public async Task Poison(
        [QueueTrigger(OnlineProfileStore.PoisonQueueName, Connection = OnlinePlaylistStorage.ConnectionName)] string message,
        CancellationToken cancellationToken)
    {
        if (!profiles.IsConfigured)
            return;

        var parsed = OnlineProfileStore.ReadMessage(message);
        if (parsed is null)
            return;

        try
        {
            var stored = await profiles.Store.GetAsync(parsed.Owner, parsed.ProfileId, cancellationToken);
            if (stored?.Record.Refresh is { } refresh && refresh.RefreshId == parsed.RefreshId && refresh.IsActive)
            {
                logger.LogWarning("Online profile refresh {RefreshId} of profile {ProfileId} could not be processed.",
                    parsed.RefreshId, parsed.ProfileId);
                await StopAsync(new RefreshRun(parsed.Owner, parsed.ProfileId, parsed.RefreshId, stored),
                    OnlineProfileError.Interrupted, null);
            }

            await profiles.Store.DeleteWorkAsync(parsed.Owner, parsed.ProfileId, parsed.RefreshId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Never retry poison messages; the watchdog and the cleanup timer cover anything left behind.
            LogFailure(exception, "poison handling");
        }
    }

    /// <summary>
    /// Removes left-over refresh files, output versions that are neither current nor previous, and the
    /// remains of deleted profiles. Profiles themselves never expire.
    /// </summary>
    [Function("OnlineProfileCleanup")]
    public async Task Cleanup([TimerTrigger("0 45 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (!profiles.IsConfigured)
            return;

        try
        {
            var result = await profiles.Store.CleanupAsync(DateTimeOffset.UtcNow - CleanupAge, cancellationToken);
            if (result.Total > 0)
                logger.LogInformation(
                    "Online profile cleanup deleted {WorkFiles} work files, {Outputs} old outputs and {OrphanBlobs} blobs of deleted profiles.",
                    result.WorkFiles, result.Outputs, result.OrphanBlobs);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure(exception, "cleanup");
        }
    }

    private async Task RefreshAsync(RefreshRun run, CancellationToken cancellationToken)
    {
        // The URL was validated when it was saved; check it again because the policy may have changed.
        if (!SafeUrlPolicy.TryParseSource(run.Stored.Record.SourceUrl, out var source, out var errorCode))
            throw new SourceFetchException(errorCode);

        await DownloadAsync(run, source, cancellationToken);
        var index = await IndexAsync(run, cancellationToken);
        await FilterAsync(run, index, cancellationToken);
    }

    private async Task DownloadAsync(RefreshRun run, Uri source, CancellationToken cancellationToken)
    {
        var store = profiles.Store!;
        // Each redirect hop can take up to the idle timeout, so record a heartbeat before following one.
        await using var response = await profiles.Fetcher!.FetchAsync(
            source, token => SaveProgressAsync(run, token), cancellationToken);
        run.TotalBytes = response.ContentLength;
        await store.CreateWorkAsync(run.Owner, run.ProfileId, run.RefreshId, cancellationToken);

        run.BytesDownloaded = await M3uPlaylist.CopyDownloadAsync(
            response.Content,
            (buffer, count, token) => store.AppendWorkAsync(run.Owner, run.ProfileId, run.RefreshId, buffer, count, token),
            (bytes, token) =>
            {
                run.BytesDownloaded = bytes;
                return SaveProgressIfDueAsync(run, token);
            },
            cancellationToken);
    }

    private async Task<OnlineProfileCategoryIndex> IndexAsync(RefreshRun run, CancellationToken cancellationToken)
    {
        var store = profiles.Store!;
        run.Stage = OnlineProfileRefreshStage.Indexing;
        await SaveProgressAsync(run, cancellationToken);

        M3uIndex index;
        await using (var source = await store.OpenWorkAsync(run.Owner, run.ProfileId, run.RefreshId, cancellationToken))
        {
            index = await M3uPlaylist.IndexAsync(source, async entries =>
            {
                run.Entries = entries;
                await SaveProgressIfDueAsync(run, cancellationToken);
            }, cancellationToken);
        }

        if (!index.Categories.Any(category => category.Count > 0))
            throw new M3uFormatException(M3uError.NoCategories);

        var indexedAt = DateTimeOffset.UtcNow;
        run.Entries = index.Entries;
        var categories = new OnlineProfileCategoryIndex
        {
            RefreshId = run.RefreshId,
            IndexedAt = indexedAt,
            Entries = index.Entries,
            Categories = [.. index.Categories],
        };
        var summary = new OnlineProfileIndexSummary
        {
            IndexedAt = indexedAt,
            CategoryCount = index.Categories.Count,
            Entries = index.Entries,
        };

        // Confirm the refresh is still current right before replacing the index.
        await SaveProgressAsync(run, cancellationToken);
        await store.WriteCategoriesAsync(run.Owner, run.ProfileId, categories, cancellationToken);
        run.Stage = OnlineProfileRefreshStage.Filtering;
        await SaveAsync(run, (record, refresh) =>
        {
            record.Index = summary;
            ApplyProgress(run, refresh, DateTimeOffset.UtcNow);
        }, cancellationToken);
        return categories;
    }

    private async Task FilterAsync(RefreshRun run, OnlineProfileCategoryIndex index, CancellationToken cancellationToken)
    {
        var store = profiles.Store!;
        var selection = run.Stored.Record.Categories;
        if (selection.Count == 0)
        {
            // Nothing is selected yet: the refresh only indexes and the user picks categories next.
            await PromoteAsync(run, null, cancellationToken);
            return;
        }

        var available = index.Categories
            .Where(category => category.Count > 0)
            .Select(category => category.Name)
            .ToHashSet(StringComparer.Ordinal);
        var included = selection.Where(available.Contains).ToHashSet(StringComparer.Ordinal);
        var missing = selection.Where(name => !available.Contains(name)).ToList();
        if (included.Count == 0)
            throw new RefreshFailedException(OnlineProfileError.NoMatchingChannels);

        long channels;
        long size;
        await using (var source = await store.OpenWorkAsync(run.Owner, run.ProfileId, run.RefreshId, cancellationToken))
        await using (var output = store.CreateOutputWriter(
                         run.Owner, run.ProfileId, run.RefreshId, token => SaveProgressIfDueAsync(run, token)))
        {
            var reader = new HeartbeatReadStream(source, token => SaveProgressIfDueAsync(run, token));
            channels = await M3uPlaylist.WriteFilteredAsync(reader, output, included, cancellationToken);
            if (channels == 0)
                throw new RefreshFailedException(OnlineProfileError.NoMatchingChannels);

            // Confirm the refresh is still current right before the new version becomes visible.
            await SaveProgressAsync(run, cancellationToken);
            await output.CommitAsync(cancellationToken);
            size = output.BytesWritten;
        }

        await PromoteAsync(run, new OnlineProfileOutput
        {
            OutputId = run.RefreshId,
            RefreshedAt = DateTimeOffset.UtcNow,
            SizeBytes = size,
            Channels = channels,
            IncludedCategoryCount = included.Count,
            MissingCategories = missing,
        }, cancellationToken);
    }

    /// <summary>
    /// Finishes the refresh with one ETag-guarded update that switches the current output to the new
    /// version and keeps the old one as previous. The version before that is then deleted, so exactly
    /// current + previous remain and readers that are still streaming the old version are not cut off.
    /// </summary>
    private async Task PromoteAsync(RefreshRun run, OnlineProfileOutput? output, CancellationToken cancellationToken)
    {
        Guid? replaced = null;
        Guid? kept = null;
        var finishedAt = DateTimeOffset.UtcNow;
        await SaveAsync(run, (record, refresh) =>
        {
            replaced = null;
            if (output is not null)
            {
                replaced = record.PreviousOutputId;
                if (record.Output is { } current)
                    record.PreviousOutputId = current.OutputId;
                record.Output = output;
            }

            kept = record.PreviousOutputId;
            ApplyProgress(run, refresh, finishedAt);
            refresh.Status = OnlineProfileRefreshStatus.Succeeded;
            refresh.Stage = null;
            refresh.FinishedAt = finishedAt;
            refresh.Error = null;
        }, cancellationToken);

        logger.LogInformation("Online profile refresh {RefreshId} of profile {ProfileId} succeeded.", run.RefreshId, run.ProfileId);
        if (replaced is { } old && old != output?.OutputId && old != kept)
            await TryAsync("delete replaced output", () =>
                profiles.Store!.DeleteOutputAsync(run.Owner, run.ProfileId, old, CancellationToken.None));
    }

    /// <summary>
    /// Ends a refresh that did not promote a new version: records the failure (when
    /// <paramref name="errorCode"/> is set and the refresh is still active), deletes its unused output
    /// version and finishes removing a profile that was deleted meanwhile. Never throws.
    /// </summary>
    private async Task StopAsync(RefreshRun run, string? errorCode, int? httpStatus)
    {
        var store = profiles.Store!;
        try
        {
            if (errorCode is not null)
            {
                logger.LogInformation("Online profile refresh {RefreshId} of profile {ProfileId} failed: {ErrorCode} {HttpStatus}.",
                    run.RefreshId, run.ProfileId, errorCode, httpStatus);
                var finishedAt = DateTimeOffset.UtcNow;
                await TryUpdateRefreshAsync(run, refresh => refresh.IsActive,
                    (_, refresh) => OnlineProfileService.MarkFailed(refresh, errorCode, httpStatus, finishedAt),
                    CancellationToken.None);
            }

            var latest = await store.GetAsync(run.Owner, run.ProfileId, CancellationToken.None);
            if (latest is null)
            {
                // Remove anything this refresh wrote after the profile was deleted.
                if (!await store.RecordExistsAsync(run.Owner, run.ProfileId, CancellationToken.None))
                    await store.DeleteAsync(run.Owner, run.ProfileId, CancellationToken.None);
                return;
            }

            if (latest.Record.Output?.OutputId != run.RefreshId && latest.Record.PreviousOutputId != run.RefreshId)
                await store.DeleteOutputAsync(run.Owner, run.ProfileId, run.RefreshId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogFailure(exception, "stop");
        }
    }

    private Task SaveProgressIfDueAsync(RefreshRun run, CancellationToken cancellationToken) =>
        Stopwatch.GetElapsedTime(run.LastSaved) >= ProgressInterval
            ? SaveProgressAsync(run, cancellationToken)
            : Task.CompletedTask;

    /// <summary>Records progress and a heartbeat; stops the refresh when it was superseded or the profile deleted.</summary>
    private Task SaveProgressAsync(RefreshRun run, CancellationToken cancellationToken) =>
        SaveAsync(run, (_, refresh) => ApplyProgress(run, refresh, DateTimeOffset.UtcNow), cancellationToken);

    private async Task SaveAsync(
        RefreshRun run, Action<OnlineProfileRecord, OnlineProfileRefresh> change, CancellationToken cancellationToken)
    {
        if (!await TryUpdateRefreshAsync(run, refresh => refresh.Status == OnlineProfileRefreshStatus.Running, change, cancellationToken))
            throw new RefreshAbandonedException();
        run.LastSaved = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the latest record with an ETag condition. After a concurrent
    /// change (such as a rename) the record is read again and the change re-applied.
    /// </summary>
    /// <returns>False when the profile was deleted, or this refresh was superseded or is no longer in the expected state.</returns>
    private async Task<bool> TryUpdateRefreshAsync(
        RefreshRun run,
        Func<OnlineProfileRefresh, bool> expected,
        Action<OnlineProfileRecord, OnlineProfileRefresh> change,
        CancellationToken cancellationToken)
    {
        var store = profiles.Store!;
        StoredProfile? stored = run.Stored;
        for (var attempt = 1; ; attempt++)
        {
            if (stored?.Record.Refresh is not { } refresh || refresh.RefreshId != run.RefreshId || !expected(refresh))
                return false;

            change(stored.Record, refresh);
            var saved = await store.TryUpdateAsync(stored.Record, stored.ETag, cancellationToken);
            if (saved is not null)
            {
                run.Stored = saved;
                return true;
            }

            if (attempt == MaxUpdateAttempts)
                throw new InvalidOperationException("The profile kept changing during the refresh.");

            stored = await store.GetAsync(run.Owner, run.ProfileId, cancellationToken);
            if (stored is not null)
                run.Stored = stored;
        }
    }

    private static void ApplyProgress(RefreshRun run, OnlineProfileRefresh refresh, DateTimeOffset now)
    {
        refresh.Stage = run.Stage;
        refresh.HeartbeatAt = now;
        refresh.BytesDownloaded = run.BytesDownloaded;
        refresh.TotalBytes = run.TotalBytes;
        refresh.Entries = run.Entries;
    }

    private Task DeleteWorkAsync(RefreshRun run) =>
        TryAsync("delete work file", () =>
            profiles.Store!.DeleteWorkAsync(run.Owner, run.ProfileId, run.RefreshId, CancellationToken.None));

    private async Task TryAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            LogFailure(exception, operation);
        }
    }

    // Exception messages can contain URLs or host names, so only the type and storage status are logged.
    private void LogFailure(Exception exception, string operation)
    {
        if (exception is RequestFailedException failed)
            logger.LogError("Online profile {Operation} failed: storage returned {Status} {ErrorCode}.",
                operation, failed.Status, failed.ErrorCode);
        else
            logger.LogError("Online profile {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
    }

    private sealed class RefreshRun(OnlineOwner owner, Guid profileId, Guid refreshId, StoredProfile stored)
    {
        public OnlineOwner Owner { get; } = owner;
        public Guid ProfileId { get; } = profileId;
        public Guid RefreshId { get; } = refreshId;
        /// <summary>The latest saved record and its ETag.</summary>
        public StoredProfile Stored { get; set; } = stored;
        public string Stage { get; set; } = OnlineProfileRefreshStage.Downloading;
        public long BytesDownloaded { get; set; }
        public long? TotalBytes { get; set; }
        public long? Entries { get; set; }
        public long LastSaved { get; set; } = Stopwatch.GetTimestamp();
    }

    /// <summary>The refresh was superseded or its profile deleted; stop without recording a failure.</summary>
    private sealed class RefreshAbandonedException : Exception;

    private sealed class RefreshFailedException(string code) : Exception($"The refresh failed ({code}).")
    {
        public string Code { get; } = code;
    }

    /// <summary>Read-only pass-through that records throttled heartbeats while the filter reads the source.</summary>
    private sealed class HeartbeatReadStream(Stream inner, Func<CancellationToken, Task> heartbeat) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            await heartbeat(cancellationToken);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

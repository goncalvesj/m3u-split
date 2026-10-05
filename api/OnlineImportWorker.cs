using Azure;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Diagnostics;

namespace api;

/// <summary>
/// Background side of URL imports: downloads the source into private blob storage, builds the category
/// index, and always finishes in a terminal ready or failed state once an import starts running.
/// </summary>
public sealed class OnlineImportWorker(OnlineImportService imports, ILogger<OnlineImportWorker> logger)
{
    // Append blob blocks may be at most 4 MiB on every service version, including Azurite.
    private const int ChunkSize = 4 * 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);

    [Function("OnlineImportWorker")]
    public async Task Run(
        [QueueTrigger(OnlineImportStore.QueueName, Connection = OnlinePlaylistStorage.ConnectionName)] string message,
        CancellationToken cancellationToken)
    {
        if (!imports.IsConfigured)
        {
            logger.LogError("Online import message ignored because URL import is not configured.");
            return;
        }

        var parsed = OnlineImportStore.ReadMessage(message);
        if (parsed is null)
        {
            logger.LogWarning("Ignoring a malformed online import message.");
            return;
        }

        // Storage failures before the import is marked running are thrown, so the message is retried and
        // then moved to the poison queue, where OnlineImportPoison marks the import as failed.
        var store = imports.Store;
        var stored = await store.GetAsync(parsed.Owner, parsed.ImportId, cancellationToken);
        if (stored is null)
            return;

        var record = stored.Record;
        var now = DateTimeOffset.UtcNow;
        if (record.ExpiresAt <= now)
        {
            await store.DeleteImportAsync(record.Owner, record.Id, cancellationToken);
            return;
        }

        if (record.IsTerminal)
            return;

        if (record.Status == OnlineImportStatus.Running)
        {
            // A previous attempt stopped part-way (for example the host restarted). Do not download again.
            logger.LogWarning("Online import {ImportId} was interrupted.", record.Id);
            record.MarkFailed(OnlineImportError.Interrupted, null, now);
            if (await store.TryUpdateAsync(record, stored.ETag, cancellationToken) is not null)
                await store.DeleteDataAsync(record.Owner, record.Id, cancellationToken);
            return;
        }

        record.Status = OnlineImportStatus.Running;
        record.Stage = OnlineImportStage.Downloading;
        record.UpdatedAt = now;
        var etag = await store.TryUpdateAsync(record, stored.ETag, cancellationToken);
        if (etag is null)
            return;

        var run = new ImportRun(record, etag.Value);
        try
        {
            await DownloadAsync(run, cancellationToken);
            await IndexAsync(run, cancellationToken);
            logger.LogInformation("Online import {ImportId} is ready.", record.Id);
        }
        catch (ImportAbandonedException)
        {
            // Discarded, or ended by the status watchdog, while it was running.
            await TryAsync("clean up abandoned import", () =>
                store.DeleteDataAsync(record.Owner, record.Id, CancellationToken.None));
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // The import's blobs were deleted underneath it (discarded or replaced by a newer import).
            logger.LogInformation("Online import {ImportId} stopped because it was discarded.", record.Id);
            await FailAsync(run, OnlineImportError.Interrupted, null);
        }
        catch (SourceFetchException exception)
        {
            await FailAsync(run, exception.Code, exception.HttpStatus);
        }
        catch (M3uFormatException exception)
        {
            await FailAsync(run, exception.Code, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailAsync(run, OnlineImportError.Interrupted, null);
        }
        catch (Exception exception)
        {
            LogFailure(exception, "import");
            await FailAsync(run, OnlineImportError.Internal, null);
        }
    }

    /// <summary>Messages that kept failing before their import started are reported as interrupted.</summary>
    [Function("OnlineImportPoison")]
    public async Task Poison(
        [QueueTrigger(OnlineImportStore.PoisonQueueName, Connection = OnlinePlaylistStorage.ConnectionName)] string message,
        CancellationToken cancellationToken)
    {
        if (!imports.IsConfigured)
            return;

        var parsed = OnlineImportStore.ReadMessage(message);
        if (parsed is null)
            return;

        try
        {
            var store = imports.Store;
            var stored = await store.GetAsync(parsed.Owner, parsed.ImportId, cancellationToken);
            if (stored is null || stored.Record.IsTerminal)
                return;

            var record = stored.Record;
            logger.LogWarning("Online import {ImportId} could not be processed.", record.Id);
            record.MarkFailed(OnlineImportError.Interrupted, null, DateTimeOffset.UtcNow);
            if (await store.TryUpdateAsync(record, stored.ETag, cancellationToken) is not null)
                await store.DeleteDataAsync(record.Owner, record.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            // Never retry poison messages; the status watchdog and expiry cover anything left behind.
            LogFailure(exception, "poison handling");
        }
    }

    /// <summary>Deletes expired imports for every user. Lifecycle management can be added as a backstop.</summary>
    [Function("OnlineImportCleanup")]
    public async Task Cleanup([TimerTrigger("0 15 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (!imports.IsConfigured)
            return;

        try
        {
            var deleted = await imports.Store.DeleteExpiredAsync(DateTimeOffset.UtcNow, cancellationToken);
            if (deleted > 0)
                logger.LogInformation("Deleted {Count} expired online import blobs.", deleted);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure(exception, "cleanup");
        }
    }

    private async Task DownloadAsync(ImportRun run, CancellationToken cancellationToken)
    {
        var store = imports.Store!;
        var record = run.Record;
        // Each redirect hop can take up to the idle timeout, so record a heartbeat before following one.
        await using var response = await imports.Fetcher!.FetchAsync(
            new Uri(record.SourceUrl), token => SaveProgressAsync(run, token), cancellationToken);
        record.TotalBytes = response.ContentLength;
        await store.CreateSourceAsync(record, cancellationToken);
        var blob = store.GetSourceBlob(record.Owner, record.Id);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            var checkedHeader = false;
            long stored = 0;
            var lastProgress = Stopwatch.GetTimestamp();
            var endOfStream = false;
            while (!endOfStream)
            {
                var filled = 0;
                while (filled < ChunkSize)
                {
                    var read = await response.Content.ReadAsync(buffer.AsMemory(filled, ChunkSize - filled), cancellationToken);
                    if (read == 0)
                    {
                        endOfStream = true;
                        break;
                    }

                    filled += read;
                    if (!checkedHeader)
                    {
                        var looksLikeM3u = M3uPlaylist.LooksLikeM3u(buffer.AsSpan(0, filled), endOfStream: false);
                        if (looksLikeM3u == false)
                            throw new M3uFormatException(M3uError.NotM3u);
                        checkedHeader = looksLikeM3u == true;
                    }

                    if (Stopwatch.GetElapsedTime(lastProgress) >= ProgressInterval)
                    {
                        record.BytesDownloaded = stored + filled;
                        await SaveProgressAsync(run, cancellationToken);
                        lastProgress = Stopwatch.GetTimestamp();
                    }
                }

                // Whatever is still undecided after the first chunk (or a short body) is not a playlist.
                if (!checkedHeader)
                {
                    if (M3uPlaylist.LooksLikeM3u(buffer.AsSpan(0, filled), endOfStream: true) != true)
                        throw new M3uFormatException(M3uError.NotM3u);
                    checkedHeader = true;
                }

                if (filled > 0)
                {
                    using var chunk = new MemoryStream(buffer, 0, filled, writable: false);
                    await blob.AppendBlockAsync(chunk, new Azure.Storage.Blobs.Models.AppendBlobAppendBlockOptions(), cancellationToken);
                    stored += filled;
                }
            }

            record.BytesDownloaded = stored;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task IndexAsync(ImportRun run, CancellationToken cancellationToken)
    {
        var store = imports.Store!;
        var record = run.Record;
        record.Stage = OnlineImportStage.Indexing;
        // A started import is never downloaded again, so it no longer needs to keep provider credentials.
        record.SourceUrl = "";
        await SaveProgressAsync(run, cancellationToken);

        M3uIndex index;
        await using (var source = await store.OpenSourceAsync(record.Owner, record.Id, cancellationToken))
        {
            if (source is null)
                throw new ImportAbandonedException();

            var lastProgress = Stopwatch.GetTimestamp();
            index = await M3uPlaylist.IndexAsync(source, async entries =>
            {
                if (Stopwatch.GetElapsedTime(lastProgress) < ProgressInterval)
                    return;
                record.Entries = entries;
                await SaveProgressAsync(run, cancellationToken);
                lastProgress = Stopwatch.GetTimestamp();
            }, cancellationToken);
        }

        if (!index.Categories.Any(category => category.Count > 0))
            throw new M3uFormatException(M3uError.NoCategories);

        await store.WriteIndexAsync(record, index, cancellationToken);
        record.Status = OnlineImportStatus.Ready;
        record.Stage = null;
        record.Entries = index.Entries;
        record.CategoryCount = index.Categories.Count;
        await SaveProgressAsync(run, cancellationToken);
    }

    /// <summary>Saves the record; stops the import when it was discarded or changed by someone else.</summary>
    private async Task SaveProgressAsync(ImportRun run, CancellationToken cancellationToken)
    {
        run.Record.UpdatedAt = DateTimeOffset.UtcNow;
        run.ETag = await imports.Store!.TryUpdateAsync(run.Record, run.ETag, cancellationToken)
                   ?? throw new ImportAbandonedException();
    }

    private async Task FailAsync(ImportRun run, string errorCode, int? httpStatus)
    {
        var store = imports.Store!;
        var record = run.Record;
        logger.LogInformation("Online import {ImportId} failed: {ErrorCode} {HttpStatus}.", record.Id, errorCode, httpStatus);
        record.MarkFailed(errorCode, httpStatus, DateTimeOffset.UtcNow);
        // The download may already be cancelled, so finish with a fresh token.
        await TryAsync("record failure", () => store.TryUpdateAsync(record, run.ETag, CancellationToken.None));
        await TryAsync("delete failed import data", () =>
            store.DeleteDataAsync(record.Owner, record.Id, CancellationToken.None));
    }

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
            logger.LogError("Online import {Operation} failed: storage returned {Status} {ErrorCode}.",
                operation, failed.Status, failed.ErrorCode);
        else
            logger.LogError("Online import {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
    }

    private sealed class ImportRun(OnlineImportRecord record, ETag etag)
    {
        public OnlineImportRecord Record { get; } = record;
        public ETag ETag { get; set; } = etag;
    }

    private sealed class ImportAbandonedException : Exception;
}

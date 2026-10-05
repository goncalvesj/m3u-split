using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace api;

/// <summary>
/// Shared saved-profile services. Profiles reuse the URL import download limits
/// (<c>OnlineImport__*</c>) and the <c>OnlinePlaylistStorage</c> account. Invalid or missing
/// configuration is logged once and reported by the endpoints as 503 profiles_not_configured instead
/// of stopping the whole Function App.
/// </summary>
public sealed class OnlineProfileService : IDisposable
{
    private const int MaxWatchdogAttempts = 3;
    private readonly ILogger logger;

    public OnlineProfileService(IConfiguration configuration, AzureComponentFactory components, ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<OnlineProfileService>();
        var importSettings = OnlineImportSettings.Read(configuration, out var importError);
        if (importSettings is null)
        {
            logger.LogError("Saved online profiles are disabled: {ConfigurationError}", importError);
            return;
        }

        var settings = OnlineProfileSettings.Read(configuration, out var settingsError);
        if (settings is null)
        {
            logger.LogError("Saved online profiles are disabled: {ConfigurationError}", settingsError);
            return;
        }

        var storage = OnlinePlaylistStorage.Create(configuration, components, out var storageError);
        if (storage is null)
        {
            logger.LogError("Saved online profiles are disabled: {ConfigurationError}", storageError);
            return;
        }

        ImportSettings = importSettings;
        Settings = settings;
        Store = new OnlineProfileStore(storage.Blobs, storage.Queues, loggerFactory.CreateLogger<OnlineProfileStore>());
        Fetcher = new SafeHttpFetcher(importSettings.FetchOptions);
    }

    /// <summary>The shared download limits and timeouts.</summary>
    public OnlineImportSettings? ImportSettings { get; }
    public OnlineProfileSettings? Settings { get; }
    public OnlineProfileStore? Store { get; }
    public SafeHttpFetcher? Fetcher { get; }

    [MemberNotNullWhen(true, nameof(ImportSettings), nameof(Settings), nameof(Store), nameof(Fetcher))]
    public bool IsConfigured => ImportSettings is not null && Settings is not null && Store is not null && Fetcher is not null;

    /// <summary>
    /// True when a refresh has waited in the queue longer than the queue allowance, or has been running
    /// without a heartbeat longer than the idle allowance, so its worker is presumed lost.
    /// </summary>
    public bool IsStale(OnlineProfileRefresh refresh, DateTimeOffset now)
    {
        EnsureConfigured();
        return refresh.Status switch
        {
            OnlineProfileRefreshStatus.Queued => now - refresh.QueuedAt > ImportSettings.StaleQueuedAfter,
            OnlineProfileRefreshStatus.Running =>
                now - (refresh.HeartbeatAt ?? refresh.StartedAt ?? refresh.QueuedAt) > ImportSettings.StaleRunningAfter,
            _ => false,
        };
    }

    /// <summary>Marks a queued or running refresh as failed without touching the current output.</summary>
    public static void MarkFailed(OnlineProfileRefresh refresh, string errorCode, int? httpStatus, DateTimeOffset now)
    {
        refresh.Status = OnlineProfileRefreshStatus.Failed;
        refresh.FinishedAt = now;
        refresh.Error = new OnlineProfileRefreshError { Code = errorCode, HttpStatus = httpStatus };
    }

    /// <summary>Starts a new queued refresh on the record; the caller saves it and then enqueues it.</summary>
    public static OnlineProfileRefresh StartRefresh(OnlineProfileRecord record, DateTimeOffset now)
    {
        var refresh = new OnlineProfileRefresh
        {
            RefreshId = Guid.NewGuid(),
            Status = OnlineProfileRefreshStatus.Queued,
            QueuedAt = now,
        };
        record.Refresh = refresh;
        return refresh;
    }

    public Task EnqueueRefreshAsync(OnlineProfileRecord record, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var refresh = record.Refresh ?? throw new InvalidOperationException("The profile has no refresh to queue.");
        return Store.EnqueueRefreshAsync(
            new OnlineProfileRefreshMessage(
                OnlineProfileRefreshMessage.CurrentVersion, record.TenantId, record.ObjectId, record.Id, refresh.RefreshId),
            cancellationToken);
    }

    /// <summary>
    /// Reads a profile. A refresh that the watchdog considers stale (<see cref="IsStale"/>) is first saved
    /// as failed (refresh_interrupted) with an ETag condition, so neither the list nor the detail ever
    /// shows a lost refresh as in progress.
    /// </summary>
    public async Task<StoredProfile?> GetCurrentAsync(OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        for (var attempt = 1; ; attempt++)
        {
            var stored = await Store.GetAsync(owner, profileId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (stored?.Record.Refresh is not { } refresh || !IsStale(refresh, now))
                return stored;

            MarkFailed(refresh, OnlineProfileError.Interrupted, null, now);
            var saved = await Store.TryUpdateAsync(stored.Record, stored.ETag, cancellationToken);
            if (saved is not null)
            {
                logger.LogWarning("Online profile refresh {RefreshId} of profile {ProfileId} was interrupted.",
                    refresh.RefreshId, profileId);
                return saved;
            }

            // The record changed or was deleted meanwhile. After a few attempts report the in-memory
            // state; the next read saves it.
            if (attempt == MaxWatchdogAttempts)
                return stored;
        }
    }

    /// <summary>Reads all of the owner's profiles (oldest first), applying the watchdog to each.</summary>
    public async Task<List<StoredProfile>> ListAsync(OnlineImportOwner owner, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var ids = await Store.ListIdsAsync(owner, cancellationToken);
        var profiles = await Task.WhenAll(ids.Select(id => GetCurrentAsync(owner, id, cancellationToken)));
        return profiles
            .OfType<StoredProfile>()
            .OrderBy(profile => profile.Record.CreatedAt)
            .ThenBy(profile => profile.Record.Id)
            .ToList();
    }

    /// <summary>
    /// Opens the current output version of a profile for streaming, or returns null when the profile does
    /// not exist or has no output yet. Output versions are immutable, so the content is never partial or
    /// mixed; if the version was replaced and removed between reading the record and opening it, the
    /// record is read once more. The caller must dispose the result.
    /// </summary>
    public async Task<OnlineProfileOutputContent?> OpenCurrentOutputAsync(
        OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stored = await Store.GetAsync(owner, profileId, cancellationToken);
            if (stored?.Record.Output is not { } output)
                return null;

            var download = await Store.OpenOutputAsync(owner, profileId, output.OutputId, cancellationToken);
            if (download is not null)
                return new OnlineProfileOutputContent(download.Content, output, stored.Record.Name, download.Details);
        }

        return null;
    }

    public void Dispose() => Fetcher?.Dispose();

    [MemberNotNull(nameof(ImportSettings), nameof(Settings), nameof(Store), nameof(Fetcher))]
    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Saved online profiles are not configured.");
    }
}

/// <summary>An open current output version. Disposing it closes the content stream.</summary>
public sealed class OnlineProfileOutputContent : IAsyncDisposable
{
    internal OnlineProfileOutputContent(Stream content, OnlineProfileOutput output, string profileName, BlobDownloadDetails details)
    {
        Content = content;
        OutputId = output.OutputId;
        RefreshedAt = output.RefreshedAt;
        Channels = output.Channels;
        SizeBytes = details.ContentLength;
        ETag = details.ETag;
        ProfileName = profileName;
    }

    /// <summary>The playlist (UTF-8 without a BOM, \n line endings).</summary>
    public Stream Content { get; }
    public Guid OutputId { get; }
    public DateTimeOffset RefreshedAt { get; }
    public long SizeBytes { get; }
    public long Channels { get; }
    public ETag ETag { get; }
    public string ProfileName { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

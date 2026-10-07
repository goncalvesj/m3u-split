using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace api;

public static class OnlineProfileRefreshStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static bool IsKnown(string? status) => status is Queued or Running or Succeeded or Failed;
}

public static class OnlineProfileRefreshStage
{
    public const string Downloading = "downloading";
    public const string Indexing = "indexing";
    public const string Filtering = "filtering";

    public static bool IsKnown(string? stage) => stage is null or Downloading or Indexing or Filtering;
}

/// <summary>Refresh failure codes that are not download (<see cref="SourceFetchError"/>) or playlist (<see cref="M3uError"/>) errors.</summary>
public static class OnlineProfileError
{
    public const string Interrupted = "refresh_interrupted";
    public const string NoMatchingChannels = "no_matching_channels";
    public const string Internal = "internal_error";
}

/// <summary>
/// A saved profile (<c>profile.json</c>). <see cref="SourceUrl"/> usually contains provider credentials:
/// it is never returned, logged, queued or written to blob metadata. This is a class rather than a
/// record so that no generated <c>ToString</c> can print it.
/// </summary>
public sealed class OnlineProfileRecord
{
    public const int CurrentVersion = 1;

    public int V { get; set; } = CurrentVersion;
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ObjectId { get; set; }
    public string Name { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string SourceDisplay { get; set; } = "";
    /// <summary>Selected category names: ordered, de-duplicated and compared ordinally.</summary>
    public List<string> Categories { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>When the name, URL or selection last changed. Refresh progress does not change it.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    public OnlineProfileRefresh? Refresh { get; set; }
    public OnlineProfileIndexSummary? Index { get; set; }
    /// <summary>The current output version. A failed refresh never changes it.</summary>
    public OnlineProfileOutput? Output { get; set; }
    /// <summary>The version that was current before <see cref="Output"/>, kept for readers that are still streaming it.</summary>
    public Guid? PreviousOutputId { get; set; }

    [JsonIgnore]
    public OnlineOwner Owner => new(TenantId, ObjectId);
}

/// <summary>The latest refresh of a profile.</summary>
public sealed class OnlineProfileRefresh
{
    public Guid RefreshId { get; set; }
    public string Status { get; set; } = OnlineProfileRefreshStatus.Queued;
    public string? Stage { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public long? Entries { get; set; }
    public OnlineProfileRefreshError? Error { get; set; }

    [JsonIgnore]
    public bool IsActive => Status is OnlineProfileRefreshStatus.Queued or OnlineProfileRefreshStatus.Running;
}

public sealed class OnlineProfileRefreshError
{
    public string Code { get; set; } = "";
    public int? HttpStatus { get; set; }
}

public sealed class OnlineProfileIndexSummary
{
    public DateTimeOffset IndexedAt { get; set; }
    public int CategoryCount { get; set; }
    public long Entries { get; set; }
}

public sealed class OnlineProfileOutput
{
    public Guid OutputId { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
    public long SizeBytes { get; set; }
    public long Channels { get; set; }
    public int IncludedCategoryCount { get; set; }
    /// <summary>Selected categories that were not in the playlist when this version was written.</summary>
    public List<string> MissingCategories { get; set; } = [];
}

/// <summary>The latest successful playlist index of a profile (<c>categories.json</c>).</summary>
public sealed class OnlineProfileCategoryIndex
{
    public const int CurrentVersion = 1;

    public int V { get; set; } = CurrentVersion;
    public Guid RefreshId { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public long Entries { get; set; }
    public List<M3uCategory> Categories { get; set; } = [];
}

/// <summary>Queue message for one refresh. It never contains the URL; the worker reads it from the record.</summary>
public sealed record OnlineProfileRefreshMessage(int V, Guid TenantId, Guid ObjectId, Guid ProfileId, Guid RefreshId)
{
    public const int CurrentVersion = 1;

    [JsonIgnore]
    public OnlineOwner Owner => new(TenantId, ObjectId);

    [JsonIgnore]
    public bool IsValid =>
        V == CurrentVersion && TenantId != Guid.Empty && ObjectId != Guid.Empty &&
        ProfileId != Guid.Empty && RefreshId != Guid.Empty;
}

public sealed record StoredProfile(OnlineProfileRecord Record, ETag ETag);

public sealed class OnlineProfileCleanupResult
{
    public int WorkFiles { get; set; }
    public int Outputs { get; set; }
    public int OrphanBlobs { get; set; }
    public int Total => WorkFiles + Outputs + OrphanBlobs;
}

/// <summary>
/// Blob and queue access for saved profiles. Profiles live in their own private container, under
/// <c>{tenantId}/{objectId}/{profileId}/</c>:
/// <c>profile.json</c> (the record, updated only with ETag conditions), <c>categories.json</c> (the
/// latest index), <c>outputs/{outputId}.m3u</c> (immutable output versions) and
/// <c>work/{refreshId}.m3u</c> (the temporary download of one refresh).
/// </summary>
public sealed class OnlineProfileStore
{
    public const string ContainerName = "online-profiles";
    public const string QueueName = "online-profile-refreshes";
    public const string PoisonQueueName = QueueName + "-poison";
    public const string PlaylistContentType = "audio/x-mpegurl";

    private const string RecordFile = "profile.json";
    private const string CategoriesFile = "categories.json";
    private const string OutputsFolder = "outputs/";
    private const string WorkFolder = "work/";
    private const string PlaylistExtension = ".m3u";
    private const string JsonContentType = "application/json";
    private static readonly TimeSpan MessageTimeToLive = TimeSpan.FromHours(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient container;
    private readonly QueueClient queue;
    private readonly QueueClient poisonQueue;
    private readonly ILogger logger;
    private readonly SemaphoreSlim ensureLock = new(1, 1);
    private volatile bool ensured;

    public OnlineProfileStore(BlobServiceClient blobs, QueueServiceClient queues, ILogger logger)
    {
        container = blobs.GetBlobContainerClient(ContainerName);
        queue = queues.GetQueueClient(QueueName);
        poisonQueue = queues.GetQueueClient(PoisonQueueName);
        this.logger = logger;
    }

    public static string ProfilePrefix(OnlineOwner owner, Guid profileId) => $"{owner.BlobPrefix}{profileId:D}/";

    /// <summary>Creates the private container and both queues once per process.</summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (ensured)
            return;

        await ensureLock.WaitAsync(cancellationToken);
        try
        {
            if (ensured)
                return;

            await CreateIfMissingAsync($"container {ContainerName}",
                () => container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken));
            await CreateIfMissingAsync($"queue {QueueName}",
                () => queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken));
            await CreateIfMissingAsync($"queue {PoisonQueueName}",
                () => poisonQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken));
            ensured = true;
        }
        finally
        {
            ensureLock.Release();
        }
    }

    public async Task<StoredProfile> CreateAsync(OnlineProfileRecord record, CancellationToken cancellationToken)
    {
        var response = await RecordBlob(record.Owner, record.Id).UploadAsync(
            BinaryData.FromObjectAsJson(record, Json),
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = JsonContentType },
            },
            cancellationToken);
        return new StoredProfile(record, response.Value.ETag);
    }

    public async Task<StoredProfile?> GetAsync(OnlineOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        BlobDownloadResult result;
        try
        {
            result = (await RecordBlob(owner, profileId).DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }

        OnlineProfileRecord? record;
        try
        {
            record = result.Content.ToObjectFromJson<OnlineProfileRecord>(Json);
        }
        catch (JsonException)
        {
            record = null;
        }

        if (!IsValid(record, owner, profileId))
        {
            logger.LogWarning("Ignoring an unreadable online profile record {ProfileId}.", profileId);
            return null;
        }

        return new StoredProfile(record, result.Details.ETag);
    }

    /// <summary>Saves the record only if it has not changed since <paramref name="etag"/>; never recreates a deleted profile.</summary>
    /// <returns>The saved record, or null when it was changed or deleted meanwhile.</returns>
    public async Task<StoredProfile?> TryUpdateAsync(OnlineProfileRecord record, ETag etag, CancellationToken cancellationToken)
    {
        try
        {
            var response = await RecordBlob(record.Owner, record.Id).UploadAsync(
                BinaryData.FromObjectAsJson(record, Json),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfMatch = etag },
                    HttpHeaders = new BlobHttpHeaders { ContentType = JsonContentType },
                },
                cancellationToken);
            return new StoredProfile(record, response.Value.ETag);
        }
        catch (RequestFailedException exception) when (exception.Status is 404 or 412)
        {
            return null;
        }
    }

    public async Task<bool> RecordExistsAsync(OnlineOwner owner, Guid profileId, CancellationToken cancellationToken) =>
        (await RecordBlob(owner, profileId).ExistsAsync(cancellationToken)).Value;

    /// <summary>Lists the IDs of the owner's profiles (every <c>{profileId}/profile.json</c> under the owner prefix).</summary>
    public async Task<List<Guid>> ListIdsAsync(OnlineOwner owner, CancellationToken cancellationToken)
    {
        var prefix = owner.BlobPrefix;
        var ids = new List<Guid>();
        await foreach (var item in ListBlobsAsync(prefix, cancellationToken))
        {
            var relative = item.Name[prefix.Length..];
            var slash = relative.IndexOf('/');
            if (slash > 0 && relative[(slash + 1)..] == RecordFile &&
                OnlineIds.TryParse(relative[..slash], out var id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// Deletes the record first, so a refresh in flight can no longer update it, then every blob under
    /// the profile prefix, including blobs that later features add there.
    /// </summary>
    /// <returns>True when the record existed.</returns>
    public async Task<bool> DeleteAsync(OnlineOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        var prefix = ProfilePrefix(owner, profileId);
        var existed = await DeleteBlobAsync(prefix + RecordFile, cancellationToken);
        await foreach (var item in ListBlobsAsync(prefix, cancellationToken))
            await DeleteBlobAsync(item.Name, cancellationToken);
        return existed;
    }

    public async Task WriteCategoriesAsync(
        OnlineOwner owner, Guid profileId, OnlineProfileCategoryIndex index, CancellationToken cancellationToken) =>
        await CategoriesBlob(owner, profileId).UploadAsync(
            BinaryData.FromObjectAsJson(index, Json),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = JsonContentType } },
            cancellationToken);

    public async Task<OnlineProfileCategoryIndex?> GetCategoriesAsync(
        OnlineOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        BlobDownloadResult result;
        try
        {
            result = (await CategoriesBlob(owner, profileId).DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }

        OnlineProfileCategoryIndex? index;
        try
        {
            index = result.Content.ToObjectFromJson<OnlineProfileCategoryIndex>(Json);
        }
        catch (JsonException)
        {
            index = null;
        }

        if (index is not { V: OnlineProfileCategoryIndex.CurrentVersion, Categories: not null } ||
            index.Categories.Any(category => category is null || string.IsNullOrEmpty(category.Name) || category.Count < 0))
        {
            logger.LogWarning("Ignoring an unreadable online profile category index {ProfileId}.", profileId);
            return null;
        }

        return index;
    }

    public async Task CreateWorkAsync(OnlineOwner owner, Guid profileId, Guid refreshId, CancellationToken cancellationToken) =>
        await WorkBlob(owner, profileId, refreshId).CreateAsync(
            new AppendBlobCreateOptions { HttpHeaders = new BlobHttpHeaders { ContentType = PlaylistContentType } },
            cancellationToken);

    /// <summary>
    /// Appends one chunk (at most 4 MiB). Appending to a work file that a profile delete removed fails
    /// instead of silently recreating it.
    /// </summary>
    public async Task AppendWorkAsync(
        OnlineOwner owner, Guid profileId, Guid refreshId, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(buffer, 0, count, writable: false);
        await WorkBlob(owner, profileId, refreshId).AppendBlockAsync(content, new AppendBlobAppendBlockOptions(), cancellationToken);
    }

    public Task<Stream> OpenWorkAsync(OnlineOwner owner, Guid profileId, Guid refreshId, CancellationToken cancellationToken) =>
        WorkBlob(owner, profileId, refreshId).OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), cancellationToken);

    public Task<bool> DeleteWorkAsync(OnlineOwner owner, Guid profileId, Guid refreshId, CancellationToken cancellationToken) =>
        DeleteBlobAsync(WorkName(owner, profileId, refreshId), cancellationToken);

    /// <summary>Starts writing a new output version; nothing is visible until <see cref="OnlineProfileOutputWriter.CommitAsync"/>.</summary>
    public OnlineProfileOutputWriter CreateOutputWriter(
        OnlineOwner owner, Guid profileId, Guid outputId, Func<CancellationToken, Task>? onBlockStaged = null) =>
        new(container.GetBlockBlobClient(OutputName(owner, profileId, outputId)), onBlockStaged);

    /// <summary>Opens an output version for streaming, or returns null when it does not exist.</summary>
    public async Task<BlobDownloadStreamingResult?> OpenOutputAsync(
        OnlineOwner owner, Guid profileId, Guid outputId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.GetBlobClient(OutputName(owner, profileId, outputId))
                .DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken);
            return response.Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public Task<bool> DeleteOutputAsync(OnlineOwner owner, Guid profileId, Guid outputId, CancellationToken cancellationToken) =>
        DeleteBlobAsync(OutputName(owner, profileId, outputId), cancellationToken);

    public async Task EnqueueRefreshAsync(OnlineProfileRefreshMessage message, CancellationToken cancellationToken) =>
        await queue.SendMessageAsync(
            BinaryData.FromObjectAsJson(message, Json), visibilityTimeout: null, timeToLive: MessageTimeToLive, cancellationToken);

    public static OnlineProfileRefreshMessage? ReadMessage(string message)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OnlineProfileRefreshMessage>(message, Json);
            return parsed is { IsValid: true } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes, once older than <paramref name="cutoff"/>: work files, output versions that are neither
    /// the current nor the previous output, and everything under a prefix whose record no longer exists.
    /// Records, category indexes, current and previous outputs and unknown blobs of existing profiles are kept.
    /// </summary>
    public async Task<OnlineProfileCleanupResult> CleanupAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var result = new OnlineProfileCleanupResult();
        ProfileBlobs? group = null;
        // A flat listing is ordered by name, so the blobs of one profile arrive together.
        await foreach (var item in ListBlobsAsync(null, cancellationToken))
        {
            if (!TryParseLocation(item.Name, out var owner, out var profileId, out var prefix))
                continue;

            if (group is null || group.Prefix != prefix)
            {
                if (group is not null)
                    await CleanupProfileAsync(group, cutoff, result, cancellationToken);
                group = new ProfileBlobs(owner, profileId, prefix);
            }

            group.Items.Add(item);
        }

        if (group is not null)
            await CleanupProfileAsync(group, cutoff, result, cancellationToken);
        return result;
    }

    private async Task CleanupProfileAsync(
        ProfileBlobs group, DateTimeOffset cutoff, OnlineProfileCleanupResult result, CancellationToken cancellationToken)
    {
        var old = group.Items
            .Where(item => item.Properties.LastModified is { } modified && modified < cutoff)
            .ToList();
        if (old.Count == 0)
            return;

        var recordName = group.Prefix + RecordFile;
        if (!group.Items.Any(item => item.Name == recordName))
        {
            // The profile was deleted; remove what was left behind unless the record exists after all.
            if ((await container.GetBlobClient(recordName).ExistsAsync(cancellationToken)).Value)
                return;
            foreach (var item in old)
            {
                if (await DeleteBlobAsync(item.Name, cancellationToken))
                    result.OrphanBlobs++;
            }

            return;
        }

        var outputs = new List<BlobItem>();
        foreach (var item in old)
        {
            var relative = item.Name[group.Prefix.Length..];
            if (relative.StartsWith(WorkFolder, StringComparison.Ordinal))
            {
                if (await DeleteBlobAsync(item.Name, cancellationToken))
                    result.WorkFiles++;
            }
            else if (relative.StartsWith(OutputsFolder, StringComparison.Ordinal))
            {
                outputs.Add(item);
            }
        }

        if (outputs.Count == 0)
            return;

        // Unreadable or just-deleted records keep their outputs; a later run handles a deleted profile.
        if (await GetAsync(group.Owner, group.ProfileId, cancellationToken) is not { } stored)
            return;

        var current = stored.Record.Output is { } output ? OutputName(group.Owner, group.ProfileId, output.OutputId) : null;
        var previous = stored.Record.PreviousOutputId is { } previousId
            ? OutputName(group.Owner, group.ProfileId, previousId)
            : null;
        foreach (var item in outputs)
        {
            if (item.Name != current && item.Name != previous && await DeleteBlobAsync(item.Name, cancellationToken))
                result.Outputs++;
        }
    }

    private static bool TryParseLocation(string name, out OnlineOwner owner, out Guid profileId, out string prefix)
    {
        owner = default;
        profileId = Guid.Empty;
        prefix = "";
        var parts = name.Split('/', 4);
        if (parts.Length != 4 || parts[3].Length == 0 ||
            !OnlineIds.TryParse(parts[0], out var tenantId) ||
            !OnlineIds.TryParse(parts[1], out var objectId) ||
            !OnlineIds.TryParse(parts[2], out profileId))
            return false;

        owner = new OnlineOwner(tenantId, objectId);
        prefix = ProfilePrefix(owner, profileId);
        return true;
    }

    private static bool IsValid([NotNullWhen(true)] OnlineProfileRecord? record, OnlineOwner owner, Guid profileId)
    {
        if (record is not
            {
                V: OnlineProfileRecord.CurrentVersion,
                Name.Length: > 0,
                SourceUrl.Length: > 0,
                SourceDisplay: not null,
                Categories: not null,
            } ||
            record.Id != profileId || record.TenantId != owner.TenantId || record.ObjectId != owner.ObjectId ||
            record.Categories.Any(string.IsNullOrEmpty))
            return false;

        if (record.Refresh is { } refresh &&
            (refresh.RefreshId == Guid.Empty ||
             !OnlineProfileRefreshStatus.IsKnown(refresh.Status) ||
             !OnlineProfileRefreshStage.IsKnown(refresh.Stage) ||
             refresh.Error is { Code: null or "" }))
            return false;

        return record.Output is null ||
               (record.Output.OutputId != Guid.Empty && record.Output.MissingCategories is not null);
    }

    private async Task CreateIfMissingAsync(string resource, Func<Task> create)
    {
        try
        {
            await create();
        }
        catch (RequestFailedException exception) when (exception.Status == 403)
        {
            // The identity may only have data access; the resource must then be pre-created (docs/online-setup.md).
            logger.LogWarning("Online profile storage {Resource} could not be created ({ErrorCode}); assuming it exists.",
                resource, exception.ErrorCode);
        }
    }

    private async IAsyncEnumerable<BlobItem> ListBlobsAsync(
        string? prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var pages = container
            .GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }, cancellationToken)
            .AsPages()
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool more;
            try
            {
                more = await pages.MoveNextAsync();
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                // The container has not been created yet.
                more = false;
            }

            if (!more)
                yield break;
            foreach (var item in pages.Current.Values)
                yield return item;
        }
    }

    private async Task<bool> DeleteBlobAsync(string name, CancellationToken cancellationToken)
    {
        var response = await container.GetBlobClient(name).DeleteIfExistsAsync(
            DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        return response.Value;
    }

    private BlobClient RecordBlob(OnlineOwner owner, Guid profileId) =>
        container.GetBlobClient(ProfilePrefix(owner, profileId) + RecordFile);

    private BlobClient CategoriesBlob(OnlineOwner owner, Guid profileId) =>
        container.GetBlobClient(ProfilePrefix(owner, profileId) + CategoriesFile);

    private AppendBlobClient WorkBlob(OnlineOwner owner, Guid profileId, Guid refreshId) =>
        container.GetAppendBlobClient(WorkName(owner, profileId, refreshId));

    private static string WorkName(OnlineOwner owner, Guid profileId, Guid refreshId) =>
        $"{ProfilePrefix(owner, profileId)}{WorkFolder}{refreshId:D}{PlaylistExtension}";

    private static string OutputName(OnlineOwner owner, Guid profileId, Guid outputId) =>
        $"{ProfilePrefix(owner, profileId)}{OutputsFolder}{outputId:D}{PlaylistExtension}";

    private sealed class ProfileBlobs(OnlineOwner owner, Guid profileId, string prefix)
    {
        public OnlineOwner Owner { get; } = owner;
        public Guid ProfileId { get; } = profileId;
        public string Prefix { get; } = prefix;
        public List<BlobItem> Items { get; } = [];
    }
}

/// <summary>
/// Writes a new output version as staged blocks and commits the block list once, with IfNoneMatch=*,
/// so the blob appears complete in one step and is never overwritten. If writing fails nothing becomes
/// visible (uncommitted blocks expire on their own). Only asynchronous writes are supported.
/// </summary>
public sealed class OnlineProfileOutputWriter : Stream
{
    private const int BlockSize = 4 * 1024 * 1024;

    private readonly BlockBlobClient blob;
    private readonly Func<CancellationToken, Task>? onBlockStaged;
    private readonly List<string> blockIds = [];
    private byte[]? buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
    private int filled;

    internal OnlineProfileOutputWriter(BlockBlobClient blob, Func<CancellationToken, Task>? onBlockStaged)
    {
        this.blob = blob;
        this.onBlockStaged = onBlockStaged;
    }

    /// <summary>The number of bytes written so far, which is the size of the committed version.</summary>
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        var target = buffer ?? throw new ObjectDisposedException(nameof(OnlineProfileOutputWriter));
        while (!source.IsEmpty)
        {
            var count = Math.Min(source.Length, BlockSize - filled);
            source.Span[..count].CopyTo(target.AsSpan(filled));
            filled += count;
            BytesWritten += count;
            source = source[count..];
            if (filled == BlockSize)
                await StageAsync(cancellationToken);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Stages the remaining bytes and commits the version. Fails if a blob with this name already exists.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await StageAsync(cancellationToken);
        await blob.CommitBlockListAsync(
            blockIds,
            new CommitBlockListOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = OnlineProfileStore.PlaylistContentType },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            },
            cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null;
        }

        base.Dispose(disposing);
    }

    private async Task StageAsync(CancellationToken cancellationToken)
    {
        if (filled == 0)
            return;

        var target = buffer ?? throw new ObjectDisposedException(nameof(OnlineProfileOutputWriter));
        // Block IDs must all have the same length.
        var blockId = Convert.ToBase64String(
            Encoding.ASCII.GetBytes(blockIds.Count.ToString("D8", CultureInfo.InvariantCulture)));
        using (var content = new MemoryStream(target, 0, filled, writable: false))
            await blob.StageBlockAsync(blockId, content, new BlockBlobStageBlockOptions(), cancellationToken);

        blockIds.Add(blockId);
        filled = 0;
        if (onBlockStaged is not null)
            await onBlockStaged(cancellationToken);
    }
}

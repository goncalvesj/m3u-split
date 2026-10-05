using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace api;

public static class OnlineImportStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class OnlineImportStage
{
    public const string Downloading = "downloading";
    public const string Indexing = "indexing";
}

public static class OnlineImportError
{
    public const string Interrupted = "import_interrupted";
    public const string Internal = "internal_error";
}

/// <summary>The validated signed-in user that owns import data. Only ever built from the validated token.</summary>
public readonly record struct OnlineImportOwner(Guid TenantId, Guid ObjectId)
{
    public static OnlineImportOwner From(OnlineIdentity identity) =>
        new(Guid.ParseExact(identity.TenantId, "D"), Guid.ParseExact(identity.ObjectId, "D"));

    public string BlobPrefix => $"{TenantId:D}/{ObjectId:D}/";
}

public static class OnlineImportIds
{
    /// <summary>Accepts only the canonical lowercase form, so every import has exactly one storage path.</summary>
    public static bool TryParse(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty &&
        string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
}

/// <summary>Queue message for the import worker. It carries identifiers only, never the source URL.</summary>
public sealed record OnlineImportMessage(int V, Guid TenantId, Guid ObjectId, Guid ImportId)
{
    public const int CurrentVersion = 1;

    [JsonIgnore]
    public OnlineImportOwner Owner => new(TenantId, ObjectId);

    [JsonIgnore]
    public bool IsValid =>
        V == CurrentVersion && TenantId != Guid.Empty && ObjectId != Guid.Empty && ImportId != Guid.Empty;
}

/// <summary>
/// The private import record. <see cref="SourceUrl"/> usually contains provider credentials, so it is only
/// stored here, only until the download finishes or fails, and is never logged, returned, queued or included
/// in <see cref="ToString"/>.
/// </summary>
public sealed class OnlineImportRecord
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ObjectId { get; set; }
    public string SourceUrl { get; set; } = "";
    public string SourceDisplay { get; set; } = "";
    public string Status { get; set; } = OnlineImportStatus.Queued;
    public string? Stage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public long Entries { get; set; }
    public int CategoryCount { get; set; }
    public string? ErrorCode { get; set; }
    public int? HttpStatus { get; set; }

    [JsonIgnore]
    public OnlineImportOwner Owner => new(TenantId, ObjectId);

    [JsonIgnore]
    public bool IsTerminal => Status is OnlineImportStatus.Ready or OnlineImportStatus.Failed;

    public void MarkFailed(string errorCode, int? httpStatus, DateTimeOffset now)
    {
        Status = OnlineImportStatus.Failed;
        Stage = null;
        ErrorCode = errorCode;
        HttpStatus = httpStatus;
        UpdatedAt = now;
        // A failed import is never downloaded again, so it does not need to keep provider credentials.
        SourceUrl = "";
    }

    public override string ToString() => $"Online import {Id:D} ({Status})";
}

public sealed record StoredImport(OnlineImportRecord Record, ETag ETag);

/// <summary>
/// Private blob and queue storage for URL imports. Every user's data lives under
/// {tenantId}/{objectId}/{importId}/ in a private container; each import has import.json (the record),
/// source.m3u (the downloaded playlist) and categories.json (the category index). Every blob carries an
/// expiresat metadata value that the cleanup timer, and optionally a lifecycle rule, rely on.
/// </summary>
public sealed class OnlineImportStore
{
    public const string ContainerName = "online-imports";
    public const string QueueName = "online-imports";
    public const string PoisonQueueName = QueueName + "-poison";
    private const string RecordFile = "import.json";
    private const string SourceFile = "source.m3u";
    private const string IndexFile = "categories.json";
    private const string ExpiresAtKey = "expiresat";
    private const string CreatedAtKey = "createdat";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient container;
    private readonly QueueClient queue;
    private readonly QueueClient poisonQueue;
    private readonly ILogger logger;
    private readonly SemaphoreSlim ensureLock = new(1, 1);
    private volatile bool ensured;

    public OnlineImportStore(OnlinePlaylistStorage storage, TimeSpan retention, ILogger logger)
    {
        container = storage.Blobs.GetBlobContainerClient(ContainerName);
        queue = storage.Queues.GetQueueClient(QueueName);
        poisonQueue = storage.Queues.GetQueueClient(PoisonQueueName);
        Retention = retention;
        this.logger = logger;
    }

    public TimeSpan Retention { get; }

    /// <summary>Creates the private container and queues once per process when they do not exist yet.</summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (ensured)
            return;

        await ensureLock.WaitAsync(cancellationToken);
        try
        {
            if (ensured)
                return;

            await CreateIfMissingAsync("container", () =>
                container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken));
            await CreateIfMissingAsync("queue", () =>
                queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken));
            await CreateIfMissingAsync("poison queue", () =>
                poisonQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken));
            ensured = true;
        }
        finally
        {
            ensureLock.Release();
        }
    }

    public async Task<ETag> CreateAsync(OnlineImportRecord record, CancellationToken cancellationToken)
    {
        var response = await RecordBlob(record.Owner, record.Id).UploadAsync(
            BinaryData.FromObjectAsJson(record, Json),
            new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                Metadata = RecordMetadata(record),
                HttpHeaders = JsonHeaders(),
            },
            cancellationToken);
        return response.Value.ETag;
    }

    /// <summary>Returns the import only when it exists and belongs to <paramref name="owner"/>.</summary>
    public async Task<StoredImport?> GetAsync(OnlineImportOwner owner, Guid importId, CancellationToken cancellationToken)
    {
        BlobDownloadResult result;
        try
        {
            result = (await RecordBlob(owner, importId).DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }

        var record = result.Content.ToObjectFromJson<OnlineImportRecord>(Json);
        if (record is null || record.Version != OnlineImportRecord.CurrentVersion || record.Id != importId ||
            record.TenantId != owner.TenantId || record.ObjectId != owner.ObjectId)
        {
            logger.LogWarning("Online import record {ImportId} is not valid for its storage path.", importId);
            return null;
        }

        return new StoredImport(record, result.Details.ETag);
    }

    /// <summary>Saves the record if nobody changed it since <paramref name="etag"/>; null when it was changed or deleted.</summary>
    public async Task<ETag?> TryUpdateAsync(OnlineImportRecord record, ETag etag, CancellationToken cancellationToken)
    {
        try
        {
            var response = await RecordBlob(record.Owner, record.Id).UploadAsync(
                BinaryData.FromObjectAsJson(record, Json),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfMatch = etag },
                    Metadata = RecordMetadata(record),
                    HttpHeaders = JsonHeaders(),
                },
                cancellationToken);
            return response.Value.ETag;
        }
        catch (RequestFailedException exception) when (exception.Status is 404 or 412)
        {
            return null;
        }
    }

    /// <summary>Deletes the record first, so the import disappears before its data does.</summary>
    public async Task DeleteImportAsync(OnlineImportOwner owner, Guid importId, CancellationToken cancellationToken)
    {
        await DeleteBlobAsync(ImportPrefix(owner, importId) + RecordFile, cancellationToken);
        await DeleteDataAsync(owner, importId, cancellationToken);
    }

    public async Task DeleteDataAsync(OnlineImportOwner owner, Guid importId, CancellationToken cancellationToken)
    {
        await DeleteBlobAsync(ImportPrefix(owner, importId) + SourceFile, cancellationToken);
        await DeleteBlobAsync(ImportPrefix(owner, importId) + IndexFile, cancellationToken);
    }

    /// <summary>
    /// Deletes the owner's other imports, including leftovers without a record. Imports created after
    /// <paramref name="current"/> are kept, so two imports started at the same time cannot delete each other.
    /// </summary>
    public async Task<int> DeleteOlderImportsAsync(
        OnlineImportRecord current, CancellationToken cancellationToken)
    {
        var prefix = current.Owner.BlobPrefix;
        var groups = new Dictionary<Guid, (List<string> Names, DateTimeOffset? CreatedAt)>();
        await foreach (var item in ListAsync(prefix, cancellationToken))
        {
            var relative = item.Name[prefix.Length..];
            var slash = relative.IndexOf('/');
            if (slash < 0 || !OnlineImportIds.TryParse(relative[..slash], out var importId) || importId == current.Id)
                continue;

            if (!groups.TryGetValue(importId, out var group))
                group = ([], null);
            group.Names.Add(item.Name);
            if (relative[(slash + 1)..] == RecordFile && TryReadTime(item.Metadata, CreatedAtKey, out var createdAt))
                group.CreatedAt = createdAt;
            groups[importId] = group;
        }

        var deleted = 0;
        foreach (var (importId, group) in groups)
        {
            if (group.CreatedAt is { } createdAt &&
                (createdAt > current.CreatedAt || createdAt == current.CreatedAt &&
                 string.CompareOrdinal(importId.ToString("D"), current.Id.ToString("D")) > 0))
                continue;

            foreach (var name in group.Names.OrderBy(name => name.EndsWith("/" + RecordFile, StringComparison.Ordinal) ? 0 : 1))
                await DeleteBlobAsync(name, cancellationToken);
            deleted++;
        }

        return deleted;
    }

    /// <summary>Deletes every blob whose expiry has passed, across all users.</summary>
    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = 0;
        try
        {
            await foreach (var item in ListAsync(null, cancellationToken))
            {
                var expired = TryReadTime(item.Metadata, ExpiresAtKey, out var expiresAt)
                    ? expiresAt <= now
                    : item.Properties.LastModified is { } modified && modified + Retention + TimeSpan.FromHours(1) < now;
                if (expired && await DeleteBlobAsync(item.Name, cancellationToken))
                    deleted++;
            }
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // Nothing has been imported yet, so the container does not exist.
        }

        return deleted;
    }

    public async Task EnqueueAsync(OnlineImportRecord record, CancellationToken cancellationToken)
    {
        var message = new OnlineImportMessage(OnlineImportMessage.CurrentVersion, record.TenantId, record.ObjectId, record.Id);
        await queue.SendMessageAsync(
            BinaryData.FromObjectAsJson(message, Json), visibilityTimeout: null, timeToLive: Retention, cancellationToken);
    }

    public static OnlineImportMessage? ReadMessage(string message)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<OnlineImportMessage>(message, Json);
            return parsed is { IsValid: true } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task WriteIndexAsync(OnlineImportRecord record, M3uIndex index, CancellationToken cancellationToken)
    {
        await IndexBlob(record.Owner, record.Id).UploadAsync(
            BinaryData.FromObjectAsJson(index, Json),
            new BlobUploadOptions { Metadata = DataMetadata(record), HttpHeaders = JsonHeaders() },
            cancellationToken);
    }

    public async Task<M3uIndex?> ReadIndexAsync(OnlineImportOwner owner, Guid importId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await IndexBlob(owner, importId).DownloadContentAsync(cancellationToken);
            return result.Value.Content.ToObjectFromJson<M3uIndex>(Json);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// The downloaded playlist. An append blob keeps every committed chunk, and appending to a blob that a
    /// discard has deleted fails instead of silently recreating it.
    /// </summary>
    public AppendBlobClient GetSourceBlob(OnlineImportOwner owner, Guid importId) =>
        container.GetAppendBlobClient(ImportPrefix(owner, importId) + SourceFile);

    public async Task CreateSourceAsync(OnlineImportRecord record, CancellationToken cancellationToken)
    {
        await GetSourceBlob(record.Owner, record.Id).CreateAsync(
            new AppendBlobCreateOptions
            {
                Metadata = DataMetadata(record),
                HttpHeaders = new BlobHttpHeaders { ContentType = "audio/x-mpegurl" },
            },
            cancellationToken);
    }

    public async Task<Stream?> OpenSourceAsync(OnlineImportOwner owner, Guid importId, CancellationToken cancellationToken)
    {
        try
        {
            return await GetSourceBlob(owner, importId).OpenReadAsync(
                new BlobOpenReadOptions(allowModifications: false), cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
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
            logger.LogWarning("Online import storage {Resource} could not be created ({ErrorCode}); assuming it exists.",
                resource, exception.ErrorCode);
        }
    }

    private IAsyncEnumerable<BlobItem> ListAsync(string? prefix, CancellationToken cancellationToken) =>
        container.GetBlobsAsync(
            new GetBlobsOptions { Traits = BlobTraits.Metadata, Prefix = prefix }, cancellationToken);

    private async Task<bool> DeleteBlobAsync(string name, CancellationToken cancellationToken)
    {
        var response = await container.GetBlobClient(name).DeleteIfExistsAsync(
            DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        return response.Value;
    }

    private BlobClient RecordBlob(OnlineImportOwner owner, Guid importId) =>
        container.GetBlobClient(ImportPrefix(owner, importId) + RecordFile);

    private BlobClient IndexBlob(OnlineImportOwner owner, Guid importId) =>
        container.GetBlobClient(ImportPrefix(owner, importId) + IndexFile);

    private static string ImportPrefix(OnlineImportOwner owner, Guid importId) => $"{owner.BlobPrefix}{importId:D}/";

    private static BlobHttpHeaders JsonHeaders() => new() { ContentType = "application/json" };

    private static Dictionary<string, string> RecordMetadata(OnlineImportRecord record) => new()
    {
        [ExpiresAtKey] = FormatTime(record.ExpiresAt),
        [CreatedAtKey] = FormatTime(record.CreatedAt),
    };

    private static Dictionary<string, string> DataMetadata(OnlineImportRecord record) => new()
    {
        [ExpiresAtKey] = FormatTime(record.ExpiresAt),
    };

    private static string FormatTime(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryReadTime(IDictionary<string, string>? metadata, string key, out DateTimeOffset time)
    {
        time = default;
        return metadata is not null && metadata.TryGetValue(key, out var value) &&
               DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}

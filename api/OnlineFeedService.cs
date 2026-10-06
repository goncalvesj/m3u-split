using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace api;

/// <summary>
/// The TV link of one profile, stored as <c>{tenantId}/{objectId}/{profileId}/feed.json</c> next to the
/// profile record. It is the source of truth: a link works only while this blob holds its token. The
/// token is kept so the signed-in owner can copy the link again; the same private storage already holds
/// the provider URL, which is at least as sensitive. Deleting the profile deletes this blob with it.
/// </summary>
public sealed class OnlineFeedState
{
    public const int CurrentVersion = 1;

    public int V { get; set; } = CurrentVersion;
    /// <summary>Identifies one token; a regenerated link gets a new ID.</summary>
    public Guid FeedId { get; set; }
    public string Token { get; set; } = "";
    /// <summary>Lower-case hex SHA-256 of the UTF-8 token, the name of its index entry.</summary>
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The index entry <c>{tokenHash}.json</c> in the private <c>online-feeds</c> container that leads from a
/// token to its profile. It never contains the token and grants nothing by itself.
/// </summary>
public sealed class OnlineFeedLookup
{
    public const int CurrentVersion = 1;

    public int V { get; set; } = CurrentVersion;
    public Guid TenantId { get; set; }
    public Guid ObjectId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid FeedId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [JsonIgnore]
    public OnlineImportOwner Owner => new(TenantId, ObjectId);
}

/// <summary>A <c>feed.json</c> blob; <see cref="State"/> is null when the blob exists but is not a valid link.</summary>
public sealed record StoredFeed(OnlineFeedState? State, ETag ETag);

public enum OnlineFeedOutcome
{
    Created,
    Existing,
    Replaced,
    FeedMissing,
    ProfileMissing,
    Busy,
}

public readonly record struct OnlineFeedChange(OnlineFeedOutcome Outcome, OnlineFeedState? State = null);

/// <summary>The profile behind a working TV link.</summary>
public readonly record struct OnlineFeedTarget(OnlineImportOwner Owner, Guid ProfileId);

public sealed class OnlineFeedCleanupResult
{
    public int Checked { get; set; }
    public int Deleted { get; set; }
}

/// <summary>
/// Secret TV links: one anonymous, read-only link per saved profile that always serves its latest
/// successful output. A token is 256 random bits; the index in the private <c>online-feeds</c> container is
/// keyed by the token's SHA-256 hash and only says which profile to look at, while the profile's
/// <c>feed.json</c> decides whether the link still works. Uses the saved-profile storage account and
/// settings, plus the online access allowlist, which is checked again on every anonymous request.
/// </summary>
public sealed class OnlineFeedService
{
    public const string ContainerName = "online-feeds";
    public const int TokenLength = 43;

    private const string FeedFile = "feed.json";
    private const string LookupExtension = ".json";
    private const string JsonContentType = "application/json";
    private const string PlaylistSuffix = ".m3u";
    private const int HashLength = 64;
    private const int MaxWriteAttempts = 5;
    // 32 bytes leave two unused bits in the last of 43 characters; the canonical encoding keeps them zero.
    private const string LastTokenCharacters = "AEIMQUYcgkosw048";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly OnlineProfileService profiles;
    private readonly OnlineProfileStore? store;
    private readonly OnlineAuthSettings? authSettings;
    private readonly BlobContainerClient? lookups;
    private readonly BlobContainerClient? profileBlobs;
    private readonly ILogger logger;
    private readonly SemaphoreSlim ensureLock = new(1, 1);
    private volatile bool ensured;

    public OnlineFeedService(
        IConfiguration configuration, AzureComponentFactory components, ILoggerFactory loggerFactory, OnlineProfileService profiles)
    {
        this.profiles = profiles;
        logger = loggerFactory.CreateLogger<OnlineFeedService>();
        if (!profiles.IsConfigured)
        {
            logger.LogError("TV feed links are disabled because saved profiles are not configured.");
            return;
        }

        var settings = OnlineAuthSettings.Read(configuration, out var settingsError);
        if (settings is null)
        {
            logger.LogError("TV feed links are disabled: {ConfigurationError}", settingsError);
            return;
        }

        var storage = OnlinePlaylistStorage.Create(configuration, components, out var storageError);
        if (storage is null)
        {
            logger.LogError("TV feed links are disabled: {ConfigurationError}", storageError);
            return;
        }

        store = profiles.Store;
        authSettings = settings;
        lookups = storage.Blobs.GetBlobContainerClient(ContainerName);
        profileBlobs = storage.Blobs.GetBlobContainerClient(OnlineProfileStore.ContainerName);
    }

    [MemberNotNullWhen(true, nameof(store), nameof(authSettings), nameof(lookups), nameof(profileBlobs))]
    public bool IsConfigured =>
        store is not null && authSettings is not null && lookups is not null && profileBlobs is not null;

    /// <summary>A new token: 32 random bytes as unpadded base64url, 43 characters of <c>[A-Za-z0-9_-]</c>.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Reads a token from a link, ignoring an optional ".m3u" suffix. Only the exact form produced by
    /// <see cref="NewToken"/> is accepted, so nothing else ever reaches storage.
    /// </summary>
    public static bool TryReadToken(string? value, [NotNullWhen(true)] out string? token)
    {
        token = null;
        if (value is null)
            return false;
        if (value.EndsWith(PlaylistSuffix, StringComparison.OrdinalIgnoreCase))
            value = value[..^PlaylistSuffix.Length];
        if (!IsToken(value))
            return false;

        token = value;
        return true;
    }

    /// <summary>The lower-case hex SHA-256 of the UTF-8 token.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(HashBytes(token));

    /// <summary>True when the owner of a link may still use online playlists (same tenant and allowlist as sign-in).</summary>
    public bool IsAllowed(OnlineImportOwner owner)
    {
        EnsureConfigured();
        return owner.TenantId == authSettings.TenantId && authSettings.AllowedObjectIds.Contains(owner.ObjectId);
    }

    public Task<bool> ProfileExistsAsync(OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        return store.RecordExistsAsync(owner, profileId, cancellationToken);
    }

    /// <summary>Opens the latest successful output of a profile, or returns null when there is none.</summary>
    public Task<OnlineProfileOutputContent?> OpenOutputAsync(
        OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        return profiles.OpenCurrentOutputAsync(owner, profileId, cancellationToken);
    }

    /// <summary>Reads the link of a profile, or returns null when it has none.</summary>
    public async Task<StoredFeed?> GetFeedAsync(OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        BlobDownloadResult result;
        try
        {
            result = (await FeedBlob(owner, profileId).DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }

        OnlineFeedState? state;
        try
        {
            state = result.Content.ToObjectFromJson<OnlineFeedState>(Json);
        }
        catch (JsonException)
        {
            state = null;
        }

        if (!IsValid(state))
        {
            logger.LogWarning("Ignoring an unreadable TV link of online profile {ProfileId}.", profileId);
            state = null;
        }

        return new StoredFeed(state, result.Details.ETag);
    }

    /// <summary>
    /// Finds the profile behind a token. Unknown, revoked and replaced tokens, deleted profiles and owners
    /// who are no longer allowed all return null, so callers cannot tell them apart.
    /// </summary>
    public async Task<OnlineFeedTarget?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var hash = HashBytes(token);
        var lookup = (await GetLookupAsync(Convert.ToHexStringLower(hash), cancellationToken))?.Lookup;
        if (lookup is null || !IsAllowed(lookup.Owner))
            return null;

        var state = (await GetFeedAsync(lookup.Owner, lookup.ProfileId, cancellationToken))?.State;
        if (state is null || state.FeedId != lookup.FeedId ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(state.TokenHash), hash))
            return null;

        return new OnlineFeedTarget(lookup.Owner, lookup.ProfileId);
    }

    /// <summary>
    /// Returns the link of a profile, creating it when there is none. The index entry is written before
    /// feed.json, so a working link can always be found. When a concurrent request writes feed.json first,
    /// its link is returned and the unused index entry is removed.
    /// </summary>
    public async Task<OnlineFeedChange> CreateAsync(OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await EnsureCreatedAsync(cancellationToken);
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var current = await GetFeedAsync(owner, profileId, cancellationToken);
            if (current?.State is { } existing)
                return new OnlineFeedChange(OnlineFeedOutcome.Existing, existing);

            var state = NewState();
            if (!await TryCreateLookupAsync(owner, profileId, state, cancellationToken))
                continue;

            // An unreadable feed.json is replaced in exactly the version that was read.
            if (await TryWriteFeedAsync(owner, profileId, state, current?.ETag, cancellationToken) is not { } etag)
            {
                await DeleteLookupQuietlyAsync(state.TokenHash);
                continue;
            }

            if (!await store.RecordExistsAsync(owner, profileId, cancellationToken))
                return await AbandonAsync(owner, profileId, state, etag);

            logger.LogInformation("TV link {FeedId} created for online profile {ProfileId}.", state.FeedId, profileId);
            return new OnlineFeedChange(OnlineFeedOutcome.Created, state);
        }

        return new OnlineFeedChange(OnlineFeedOutcome.Busy);
    }

    /// <summary>
    /// Gives an existing link a new token. The new index entry is written first; the old token stops
    /// working the moment feed.json is replaced, after which its index entry is only cleanup.
    /// </summary>
    public async Task<OnlineFeedChange> RegenerateAsync(OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await EnsureCreatedAsync(cancellationToken);
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var current = await GetFeedAsync(owner, profileId, cancellationToken);
            if (current?.State is not { } previous)
                return new OnlineFeedChange(OnlineFeedOutcome.FeedMissing);

            var state = NewState();
            if (!await TryCreateLookupAsync(owner, profileId, state, cancellationToken))
                continue;

            if (await TryWriteFeedAsync(owner, profileId, state, current.ETag, cancellationToken) is not { } etag)
            {
                await DeleteLookupQuietlyAsync(state.TokenHash);
                continue;
            }

            await DeleteLookupQuietlyAsync(previous.TokenHash);
            if (!await store.RecordExistsAsync(owner, profileId, cancellationToken))
                return await AbandonAsync(owner, profileId, state, etag);

            logger.LogInformation("TV link {PreviousFeedId} of online profile {ProfileId} replaced by {FeedId}.",
                previous.FeedId, profileId, state.FeedId);
            return new OnlineFeedChange(OnlineFeedOutcome.Replaced, state);
        }

        return new OnlineFeedChange(OnlineFeedOutcome.Busy);
    }

    /// <summary>Revokes the link of a profile. Deleting feed.json ends it at once; its index entry is only cleanup.</summary>
    /// <returns>True when the profile had a link.</returns>
    public async Task<bool> RevokeAsync(OnlineImportOwner owner, Guid profileId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var current = await GetFeedAsync(owner, profileId, cancellationToken);
        if (current is null)
            return false;

        await FeedBlob(owner, profileId).DeleteIfExistsAsync(
            DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        if (current.State is { } state)
        {
            await DeleteLookupQuietlyAsync(state.TokenHash);
            logger.LogInformation("TV link {FeedId} of online profile {ProfileId} revoked.", state.FeedId, profileId);
        }

        return true;
    }

    /// <summary>
    /// Deletes index entries older than <paramref name="cutoff"/> whose profile, feed.json or token is gone,
    /// checking at most <paramref name="maxChecks"/> entries. Newer entries may belong to a change in progress.
    /// </summary>
    public async Task<OnlineFeedCleanupResult> CleanupAsync(DateTimeOffset cutoff, int maxChecks, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var result = new OnlineFeedCleanupResult();
        await foreach (var item in ListLookupsAsync(cancellationToken))
        {
            if (result.Checked >= maxChecks)
                break;
            if (!TryParseLookupName(item.Name, out var hash) ||
                item.Properties.LastModified is not { } modified || modified >= cutoff)
                continue;

            result.Checked++;
            var stored = await GetLookupAsync(hash, cancellationToken);
            if (stored is null || (stored.Lookup is { } lookup && await IsCurrentAsync(lookup, hash, cancellationToken)))
                continue;

            if (await DeleteLookupAsync(hash, stored.ETag, cancellationToken))
                result.Deleted++;
        }

        return result;
    }

    private async Task<bool> IsCurrentAsync(OnlineFeedLookup lookup, string hash, CancellationToken cancellationToken)
    {
        if (!await store!.RecordExistsAsync(lookup.Owner, lookup.ProfileId, cancellationToken))
            return false;

        var state = (await GetFeedAsync(lookup.Owner, lookup.ProfileId, cancellationToken))?.State;
        return state is not null && state.FeedId == lookup.FeedId && state.TokenHash == hash;
    }

    /// <summary>Undoes a link written while its profile was being deleted, so it cannot outlive the profile.</summary>
    private async Task<OnlineFeedChange> AbandonAsync(OnlineImportOwner owner, Guid profileId, OnlineFeedState state, ETag etag)
    {
        try
        {
            await FeedBlob(owner, profileId).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots, new BlobRequestConditions { IfMatch = etag }, CancellationToken.None);
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            // Replaced meanwhile; whatever is left under a deleted profile is removed by the profile cleanup.
        }
        catch (Exception exception)
        {
            LogQuietFailure(exception, "link removal");
        }

        await DeleteLookupQuietlyAsync(state.TokenHash);
        return new OnlineFeedChange(OnlineFeedOutcome.ProfileMissing);
    }

    private static OnlineFeedState NewState()
    {
        var token = NewToken();
        return new OnlineFeedState
        {
            FeedId = Guid.NewGuid(),
            Token = token,
            TokenHash = Hash(token),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <returns>False when an entry with this hash already exists, which 256 random bits make practically impossible.</returns>
    private async Task<bool> TryCreateLookupAsync(
        OnlineImportOwner owner, Guid profileId, OnlineFeedState state, CancellationToken cancellationToken)
    {
        var lookup = new OnlineFeedLookup
        {
            TenantId = owner.TenantId,
            ObjectId = owner.ObjectId,
            ProfileId = profileId,
            FeedId = state.FeedId,
            CreatedAt = state.CreatedAt,
        };
        try
        {
            await LookupBlob(state.TokenHash).UploadAsync(
                BinaryData.FromObjectAsJson(lookup, Json),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = JsonContentType },
                },
                cancellationToken);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            return false;
        }
    }

    /// <summary>Creates feed.json when <paramref name="ifMatch"/> is null, otherwise replaces exactly that version.</summary>
    /// <returns>The new ETag, or null when feed.json changed meanwhile.</returns>
    private async Task<ETag?> TryWriteFeedAsync(
        OnlineImportOwner owner, Guid profileId, OnlineFeedState state, ETag? ifMatch, CancellationToken cancellationToken)
    {
        try
        {
            var response = await FeedBlob(owner, profileId).UploadAsync(
                BinaryData.FromObjectAsJson(state, Json),
                new BlobUploadOptions
                {
                    Conditions = ifMatch is { } etag
                        ? new BlobRequestConditions { IfMatch = etag }
                        : new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = JsonContentType },
                },
                cancellationToken);
            return response.Value.ETag;
        }
        catch (RequestFailedException exception) when (exception.Status is 404 or 409 or 412)
        {
            return null;
        }
    }

    private async Task<StoredLookup?> GetLookupAsync(string hash, CancellationToken cancellationToken)
    {
        BlobDownloadResult result;
        try
        {
            result = (await LookupBlob(hash).DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }

        OnlineFeedLookup? lookup;
        try
        {
            lookup = result.Content.ToObjectFromJson<OnlineFeedLookup>(Json);
        }
        catch (JsonException)
        {
            lookup = null;
        }

        return new StoredLookup(IsValid(lookup) ? lookup : null, result.Details.ETag);
    }

    private async Task<bool> DeleteLookupAsync(string hash, ETag etag, CancellationToken cancellationToken)
    {
        try
        {
            var response = await LookupBlob(hash).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots, new BlobRequestConditions { IfMatch = etag }, cancellationToken);
            return response.Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 412)
        {
            return false;
        }
    }

    // Index entries of links that no longer work grant nothing; the cleanup timer removes any left behind.
    private async Task DeleteLookupQuietlyAsync(string hash)
    {
        try
        {
            await LookupBlob(hash).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogQuietFailure(exception, "index cleanup");
        }
    }

    /// <summary>Creates the private index container once per process.</summary>
    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        if (ensured)
            return;

        await ensureLock.WaitAsync(cancellationToken);
        try
        {
            if (ensured)
                return;

            try
            {
                await lookups!.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status == 403)
            {
                // The identity may only have data access; the container must then be pre-created (docs/online-setup.md).
                logger.LogWarning("TV link storage container {Container} could not be created ({ErrorCode}); assuming it exists.",
                    ContainerName, exception.ErrorCode);
            }

            ensured = true;
        }
        finally
        {
            ensureLock.Release();
        }
    }

    private async IAsyncEnumerable<BlobItem> ListLookupsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var pages = lookups!
            .GetBlobsAsync(new GetBlobsOptions(), cancellationToken)
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

    private static bool IsToken(string value) =>
        value.Length == TokenLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') &&
        LastTokenCharacters.Contains(value[^1]);

    private static byte[] HashBytes(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static bool TryParseLookupName(string name, [NotNullWhen(true)] out string? hash)
    {
        hash = null;
        if (name.Length != HashLength + LookupExtension.Length || !name.EndsWith(LookupExtension, StringComparison.Ordinal))
            return false;

        var value = name[..HashLength];
        if (!value.All(char.IsAsciiHexDigitLower))
            return false;

        hash = value;
        return true;
    }

    private static bool IsValid([NotNullWhen(true)] OnlineFeedState? state) =>
        state is { V: OnlineFeedState.CurrentVersion, Token: not null, TokenHash: not null } &&
        state.FeedId != Guid.Empty && state.CreatedAt != default &&
        IsToken(state.Token) && state.TokenHash == Hash(state.Token);

    private static bool IsValid([NotNullWhen(true)] OnlineFeedLookup? lookup) =>
        lookup is { V: OnlineFeedLookup.CurrentVersion } &&
        lookup.TenantId != Guid.Empty && lookup.ObjectId != Guid.Empty &&
        lookup.ProfileId != Guid.Empty && lookup.FeedId != Guid.Empty && lookup.CreatedAt != default;

    private BlobClient FeedBlob(OnlineImportOwner owner, Guid profileId) =>
        profileBlobs!.GetBlobClient(OnlineProfileStore.ProfilePrefix(owner, profileId) + FeedFile);

    private BlobClient LookupBlob(string hash) => lookups!.GetBlobClient(hash + LookupExtension);

    // Exception messages can contain blob names, which include token hashes, so only the type and status are logged.
    private void LogQuietFailure(Exception exception, string operation)
    {
        if (exception is RequestFailedException failed)
            logger.LogWarning("TV link {Operation} failed: storage returned {Status} {ErrorCode}.",
                operation, failed.Status, failed.ErrorCode);
        else
            logger.LogWarning("TV link {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
    }

    [MemberNotNull(nameof(store), nameof(authSettings), nameof(lookups), nameof(profileBlobs))]
    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("TV feed links are not configured.");
    }

    private sealed record StoredLookup(OnlineFeedLookup? Lookup, ETag ETag);
}

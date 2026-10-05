using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace api;

public sealed record OnlineProfileCreateRequest(string? Name, string? Url, List<string?>? Categories);

public sealed record OnlineProfileRenameRequest(string? Name);

public sealed record OnlineProfileSourceRequest(string? Url);

public sealed record OnlineProfileSelectionRequest(List<string?>? Categories);

public sealed record OnlineProfileErrorResponse(string Code, int? HttpStatus);

public sealed record OnlineProfileRefreshResponse(
    Guid RefreshId,
    string Status,
    string? Stage,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? FinishedAt,
    long BytesDownloaded,
    long? TotalBytes,
    long? Entries,
    OnlineProfileErrorResponse? Error);

public sealed record OnlineProfileIndexResponse(DateTimeOffset IndexedAt, int CategoryCount, long Entries);

public sealed record OnlineProfileOutputResponse(
    Guid OutputId,
    DateTimeOffset RefreshedAt,
    long SizeBytes,
    long Channels,
    int IncludedCategoryCount,
    int MissingCategoryCount,
    IReadOnlyList<string> MissingCategories);

/// <summary>A saved profile as returned to its owner. The provider URL itself is never returned.</summary>
public sealed record OnlineProfileResponse(
    Guid Id,
    string Name,
    string SourceDisplay,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int SelectedCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Categories,
    OnlineProfileRefreshResponse? Refresh,
    OnlineProfileIndexResponse? Index,
    OnlineProfileOutputResponse? Output);

public sealed record OnlineProfileListResponse(
    IReadOnlyList<OnlineProfileResponse> Profiles, int MaxProfiles, long MaxDownloadBytes);

public sealed record OnlineProfileCategoryResponse(string Name, int Count);

public sealed record OnlineProfileCategoriesResponse(
    IReadOnlyList<OnlineProfileCategoryResponse> Categories,
    long Entries,
    DateTimeOffset IndexedAt,
    IReadOnlyList<string> Selected);

/// <summary>
/// Saved profiles: a named provider URL plus selected categories, refreshed on the server with one click.
/// Every endpoint runs the online access guard first and only ever touches the caller's own profiles.
/// </summary>
public sealed class OnlineProfileFunctions(
    OnlineAccess access, OnlineProfileService profiles, ILogger<OnlineProfileFunctions> logger)
{
    private const int MaxSmallBodyBytes = 16 * 1024;
    private const int MaxCategoriesBodyBytes = 4 * 1024 * 1024;
    private const int MaxNameLength = 80;
    private const int MaxUpdateAttempts = 5;
    private const int MaxListedMissingCategories = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function("OnlineProfileList")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/profiles")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();

        try
        {
            var stored = await profiles.ListAsync(auth.Owner, cancellationToken);
            return new OkObjectResult(new OnlineProfileListResponse(
                stored.Select(profile => ToResponse(profile.Record, detail: false)).ToList(),
                profiles.Settings.MaxProfilesPerUser,
                profiles.ImportSettings.MaxDownloadBytes));
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "list");
            return Unavailable();
        }
    }

    [Function("OnlineProfileCreate")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/profiles")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();

        var body = await ReadJsonAsync<OnlineProfileCreateRequest>(request, MaxCategoriesBodyBytes, cancellationToken);
        if (body is null)
            return OnlineHttp.Error(400, "invalid_request", "Send a JSON body with the profile name and playlist URL.");
        if (!TryReadName(body.Name, out var name))
            return InvalidName();
        if (!SafeUrlPolicy.TryParseSource(body.Url, out var source, out var errorCode))
            return InvalidUrl(errorCode);

        List<string> categories = [];
        if (body.Categories is not null && !TryReadSelection(body.Categories, allowEmpty: true, out categories))
            return InvalidSelection($"Send up to {M3uPlaylist.MaxCategories} non-empty category names.");

        var store = profiles.Store;
        var maxProfiles = profiles.Settings.MaxProfilesPerUser;
        var now = DateTimeOffset.UtcNow;
        var record = new OnlineProfileRecord
        {
            Id = Guid.NewGuid(),
            TenantId = auth.Owner.TenantId,
            ObjectId = auth.Owner.ObjectId,
            Name = name,
            SourceUrl = source.AbsoluteUri,
            SourceDisplay = SafeUrlPolicy.Display(source),
            Categories = categories,
            CreatedAt = now,
            UpdatedAt = now,
        };
        OnlineProfileService.StartRefresh(record, now);

        try
        {
            await store.EnsureCreatedAsync(cancellationToken);
            // Best effort: two simultaneous requests can both pass this check.
            var existing = await store.ListIdsAsync(auth.Owner, cancellationToken);
            if (existing.Count >= maxProfiles)
                return OnlineHttp.Error(409, "profile_limit_reached",
                    $"You can save up to {maxProfiles} profiles. Delete one to add another.");

            await store.CreateAsync(record, cancellationToken);
            try
            {
                await profiles.EnqueueRefreshAsync(record, cancellationToken);
            }
            catch
            {
                await TryAsync("discard unqueued profile", () => store.DeleteAsync(record.Owner, record.Id, CancellationToken.None));
                throw;
            }
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "create");
            return Unavailable();
        }

        logger.LogInformation("Online profile {ProfileId} created for {Source}.", record.Id, record.SourceDisplay);
        return new ObjectResult(ToResponse(record, detail: true)) { StatusCode = StatusCodes.Status201Created };
    }

    [Function("OnlineProfileGet")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/profiles/{profileId}")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        try
        {
            var stored = await profiles.GetCurrentAsync(auth.Owner, id, cancellationToken);
            return stored is null ? NotFound() : new OkObjectResult(ToResponse(stored.Record, detail: true));
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "get");
            return Unavailable();
        }
    }

    [Function("OnlineProfileCategories")]
    public async Task<IActionResult> Categories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/profiles/{profileId}/categories")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        var store = profiles.Store;
        try
        {
            var stored = await store.GetAsync(auth.Owner, id, cancellationToken);
            if (stored is null)
                return NotFound();

            var index = await store.GetCategoriesAsync(auth.Owner, id, cancellationToken);
            if (index is null)
                return NotIndexed();

            return new OkObjectResult(new OnlineProfileCategoriesResponse(
                index.Categories.Select(category => new OnlineProfileCategoryResponse(category.Name, category.Count)).ToList(),
                index.Entries,
                index.IndexedAt,
                stored.Record.Categories));
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "categories");
            return Unavailable();
        }
    }

    [Function("OnlineProfileSelect")]
    public async Task<IActionResult> Select(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/profiles/{profileId}/categories")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        var body = await ReadJsonAsync<OnlineProfileSelectionRequest>(request, MaxCategoriesBodyBytes, cancellationToken);
        if (body is null)
            return OnlineHttp.Error(400, "invalid_request", "Send a JSON body with the selected categories.");
        if (body.Categories is null || !TryReadSelection(body.Categories, allowEmpty: false, out var selection))
            return InvalidSelection("Select at least one category.");

        var store = profiles.Store;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var stored = await profiles.GetCurrentAsync(auth.Owner, id, cancellationToken);
                if (stored is null)
                    return NotFound();

                var record = stored.Record;
                if (record.Refresh is { IsActive: true })
                    return RefreshInProgress();

                var index = await store.GetCategoriesAsync(auth.Owner, id, cancellationToken);
                if (index is null)
                    return NotIndexed();

                // Names are matched exactly (ordinal, untrimmed). Categories that are already selected may be
                // kept even when the provider has temporarily dropped them.
                var known = index.Categories.Select(category => category.Name).ToHashSet(StringComparer.Ordinal);
                known.UnionWith(record.Categories);
                if (!selection.All(known.Contains))
                    return InvalidSelection("Some selected categories are not in this playlist. Reload the categories and try again.");

                var now = DateTimeOffset.UtcNow;
                record.Categories = selection;
                record.UpdatedAt = now;
                OnlineProfileService.StartRefresh(record, now);
                var saved = await store.TryUpdateAsync(record, stored.ETag, cancellationToken);
                if (saved is not null)
                    return await QueueAsync(saved, "select");
                if (attempt == MaxUpdateAttempts)
                    return Unavailable();
            }
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "select");
            return Unavailable();
        }
    }

    [Function("OnlineProfileRename")]
    public async Task<IActionResult> Rename(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/profiles/{profileId}/rename")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        var body = await ReadJsonAsync<OnlineProfileRenameRequest>(request, MaxSmallBodyBytes, cancellationToken);
        if (body is null)
            return OnlineHttp.Error(400, "invalid_request", "Send a JSON body with the new profile name.");
        if (!TryReadName(body.Name, out var name))
            return InvalidName();

        var store = profiles.Store;
        try
        {
            // Renaming is allowed during a refresh; the worker re-applies its changes to the latest record.
            for (var attempt = 1; ; attempt++)
            {
                var stored = await profiles.GetCurrentAsync(auth.Owner, id, cancellationToken);
                if (stored is null)
                    return NotFound();

                stored.Record.Name = name;
                stored.Record.UpdatedAt = DateTimeOffset.UtcNow;
                var saved = await store.TryUpdateAsync(stored.Record, stored.ETag, cancellationToken);
                if (saved is not null)
                    return new OkObjectResult(ToResponse(saved.Record, detail: true));
                if (attempt == MaxUpdateAttempts)
                    return Unavailable();
            }
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "rename");
            return Unavailable();
        }
    }

    [Function("OnlineProfileSource")]
    public async Task<IActionResult> Source(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/profiles/{profileId}/source")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        var body = await ReadJsonAsync<OnlineProfileSourceRequest>(request, MaxSmallBodyBytes, cancellationToken);
        if (body is null)
            return OnlineHttp.Error(400, "invalid_request", "Send a JSON body with the playlist URL.");
        if (!SafeUrlPolicy.TryParseSource(body.Url, out var source, out var errorCode))
            return InvalidUrl(errorCode);

        var store = profiles.Store;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var stored = await profiles.GetCurrentAsync(auth.Owner, id, cancellationToken);
                if (stored is null)
                    return NotFound();

                var record = stored.Record;
                if (record.Refresh is { IsActive: true })
                    return RefreshInProgress();

                // The selection, latest index and current output are kept until the new source is refreshed.
                var now = DateTimeOffset.UtcNow;
                record.SourceUrl = source.AbsoluteUri;
                record.SourceDisplay = SafeUrlPolicy.Display(source);
                record.UpdatedAt = now;
                OnlineProfileService.StartRefresh(record, now);
                var saved = await store.TryUpdateAsync(record, stored.ETag, cancellationToken);
                if (saved is not null)
                {
                    logger.LogInformation("Online profile {ProfileId} source changed to {Source}.", id, record.SourceDisplay);
                    return await QueueAsync(saved, "source");
                }

                if (attempt == MaxUpdateAttempts)
                    return Unavailable();
            }
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "source");
            return Unavailable();
        }
    }

    [Function("OnlineProfileRefresh")]
    public async Task<IActionResult> Refresh(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/profiles/{profileId}/refresh")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        var store = profiles.Store;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var stored = await profiles.GetCurrentAsync(auth.Owner, id, cancellationToken);
                if (stored is null)
                    return NotFound();

                // Idempotent: an active refresh is returned as-is and no extra message is queued.
                if (stored.Record.Refresh is { IsActive: true })
                    return Accepted(stored.Record);

                OnlineProfileService.StartRefresh(stored.Record, DateTimeOffset.UtcNow);
                var saved = await store.TryUpdateAsync(stored.Record, stored.ETag, cancellationToken);
                if (saved is not null)
                    return await QueueAsync(saved, "refresh");
                if (attempt == MaxUpdateAttempts)
                    return Unavailable();
            }
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "refresh");
            return Unavailable();
        }
    }

    [Function("OnlineProfileOutput")]
    public async Task<IActionResult> Output(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/profiles/{profileId}/output")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        OnlineProfileOutputContent? output = null;
        try
        {
            output = await profiles.OpenCurrentOutputAsync(auth.Owner, id, cancellationToken);
            if (output is null)
            {
                return await profiles.Store.RecordExistsAsync(auth.Owner, id, cancellationToken)
                    ? OnlineHttp.Error(404, "output_not_found",
                        "This profile has no playlist yet. Choose categories and refresh it.")
                    : NotFound();
            }

            var result = new OnlineProfileOutputResult(output, logger);
            output = null;
            return result;
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "output");
            return Unavailable();
        }
        finally
        {
            if (output is not null)
                await output.DisposeAsync();
        }
    }

    [Function("OnlineProfileDelete")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "online/profiles/{profileId}")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!profiles.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(profileId, out var id))
            return NotFound();

        try
        {
            // The record goes first, so a refresh in flight stops and removes anything it writes afterwards.
            if (!await profiles.Store.DeleteAsync(auth.Owner, id, cancellationToken))
                return NotFound();

            logger.LogInformation("Online profile {ProfileId} deleted.", id);
            return new NoContentResult();
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "delete");
            return Unavailable();
        }
    }

    /// <summary>Queues the refresh that was just saved. If that fails, the refresh is ended so it never looks stuck.</summary>
    private async Task<IActionResult> QueueAsync(StoredProfile saved, string operation)
    {
        try
        {
            await profiles.EnqueueRefreshAsync(saved.Record, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogStorageFailure(exception, operation);
            if (saved.Record.Refresh is { } refresh)
            {
                OnlineProfileService.MarkFailed(refresh, OnlineProfileError.Interrupted, null, DateTimeOffset.UtcNow);
                await TryAsync("end unqueued refresh",
                    () => profiles.Store!.TryUpdateAsync(saved.Record, saved.ETag, CancellationToken.None));
            }

            return Unavailable();
        }

        logger.LogInformation("Online profile refresh {RefreshId} of profile {ProfileId} queued.",
            saved.Record.Refresh?.RefreshId, saved.Record.Id);
        return Accepted(saved.Record);
    }

    private static ObjectResult Accepted(OnlineProfileRecord record) =>
        new(ToResponse(record, detail: true)) { StatusCode = StatusCodes.Status202Accepted };

    private static OnlineProfileResponse ToResponse(OnlineProfileRecord record, bool detail) => new(
        record.Id,
        record.Name,
        record.SourceDisplay,
        record.CreatedAt,
        record.UpdatedAt,
        record.Categories.Count,
        detail ? record.Categories : null,
        record.Refresh is { } refresh
            ? new OnlineProfileRefreshResponse(
                refresh.RefreshId,
                refresh.Status,
                refresh.Stage,
                refresh.QueuedAt,
                refresh.StartedAt,
                refresh.HeartbeatAt,
                refresh.FinishedAt,
                refresh.BytesDownloaded,
                refresh.TotalBytes,
                refresh.Entries,
                refresh.Error is { } error ? new OnlineProfileErrorResponse(error.Code, error.HttpStatus) : null)
            : null,
        record.Index is { } index ? new OnlineProfileIndexResponse(index.IndexedAt, index.CategoryCount, index.Entries) : null,
        record.Output is { } output
            ? new OnlineProfileOutputResponse(
                output.OutputId,
                output.RefreshedAt,
                output.SizeBytes,
                output.Channels,
                output.IncludedCategoryCount,
                output.MissingCategories.Count,
                output.MissingCategories.Take(MaxListedMissingCategories).ToList())
            : null);

    /// <summary>Accepts a trimmed name of 1 to 80 printable characters (no control, formatting or unassigned characters).</summary>
    private static bool TryReadName(string? value, out string name)
    {
        name = value?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength)
            return false;

        for (var position = 0; position < name.Length;)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(position), out var rune, out var consumed) != OperationStatus.Done)
                return false;

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                // Joiners and emoji tag characters are allowed; bidirectional and other invisible controls are not.
                case UnicodeCategory.Format when rune.Value is not (0x200C or 0x200D or (>= 0xE0020 and <= 0xE007F)):
                    return false;
            }

            position += consumed;
        }

        return true;
    }

    /// <summary>Accepts up to MaxCategories non-empty names, keeping the first occurrence of each.</summary>
    private static bool TryReadSelection(List<string?> requested, bool allowEmpty, out List<string> selection)
    {
        selection = [];
        if (requested.Count > M3uPlaylist.MaxCategories || (requested.Count == 0 && !allowEmpty))
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requested)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (seen.Add(name))
                selection.Add(name);
        }

        return true;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
        where T : class
    {
        if (!request.HasJsonContentType() || request.ContentLength > maxBytes)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task TryAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            LogStorageFailure(exception, operation);
        }
    }

    private static bool IsAborted(HttpRequest request, CancellationToken cancellationToken) =>
        request.HttpContext.RequestAborted.IsCancellationRequested || cancellationToken.IsCancellationRequested;

    // Exception messages can contain storage paths or other details, so only the type and status are logged.
    private void LogStorageFailure(Exception exception, string operation)
    {
        if (exception is RequestFailedException failed)
            logger.LogError("Online profile {Operation} failed: storage returned {Status} {ErrorCode}.",
                operation, failed.Status, failed.ErrorCode);
        else
            logger.LogError("Online profile {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
    }

    private static ObjectResult NotFound() =>
        OnlineHttp.Error(404, "profile_not_found", "This profile does not exist or was deleted.");

    private static ObjectResult NotIndexed() =>
        OnlineHttp.Error(409, "profile_not_indexed",
            "The playlist of this profile has not been read yet. Wait for the refresh to finish and try again.");

    private static ObjectResult RefreshInProgress() =>
        OnlineHttp.Error(409, "refresh_in_progress", "A refresh is in progress. Wait for it to finish and try again.");

    private static ObjectResult InvalidName() =>
        OnlineHttp.Error(400, "invalid_name", $"Enter a name of 1 to {MaxNameLength} printable characters.");

    private static ObjectResult InvalidUrl(string errorCode) =>
        errorCode == SourceFetchError.BlockedDestination
            ? OnlineHttp.Error(400, errorCode, "This address is not allowed. Use the provider's public playlist URL.")
            : OnlineHttp.Error(400, SourceFetchError.InvalidUrl,
                $"Enter a complete http:// or https:// playlist URL (up to {SafeUrlPolicy.MaxUrlLength} characters).");

    private static ObjectResult InvalidSelection(string message) =>
        OnlineHttp.Error(400, "invalid_selection", message);

    private static ObjectResult NotConfigured() =>
        OnlineHttp.Error(503, "profiles_not_configured", "Saved profiles are not configured on the server.");

    private static ObjectResult Unavailable() =>
        OnlineHttp.Error(503, "profiles_unavailable", "Saved profile storage is temporarily unavailable. Try again.");
}

/// <summary>Streams the current output version of a profile from blob storage to the response.</summary>
public sealed class OnlineProfileOutputResult(OnlineProfileOutputContent output, ILogger logger) : IActionResult
{
    private const int MaxFileNameLength = 80;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var response = http.Response;
        var aborted = http.RequestAborted;
        await using (output)
        {
            try
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "audio/x-mpegurl; charset=utf-8";
                response.ContentLength = output.SizeBytes;
                response.Headers.CacheControl = "no-store";
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.ContentDisposition = $"attachment; filename=\"{FileName(output.ProfileName)}\"";
                await output.Content.CopyToAsync(response.Body, aborted);
            }
            catch (Exception) when (aborted.IsCancellationRequested)
            {
                // The client went away; there is nobody left to answer.
            }
            catch (Exception exception) when (!aborted.IsCancellationRequested)
            {
                logger.LogError("Streaming a profile playlist failed ({ExceptionType}).", exception.GetType().Name);
                if (response.HasStarted)
                {
                    // Abort instead of ending normally so the client cannot mistake a partial file for a complete one.
                    http.Abort();
                    return;
                }

                response.Clear();
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                response.Headers.CacheControl = "no-store";
                await response.WriteAsJsonAsync(
                    new { error = "profiles_unavailable", message = "The playlist could not be downloaded. Try again." },
                    CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// A conservative ASCII file name from the profile name: runs of other characters become "-",
    /// at most 80 characters, "profile" when nothing is left, always ending in ".m3u".
    /// </summary>
    public static string FileName(string profileName)
    {
        var builder = new StringBuilder(profileName.Length);
        var replaced = false;
        foreach (var character in profileName)
        {
            if (character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or ' ' or '.' or '_' or '-')
            {
                if (replaced)
                    builder.Append('-');
                builder.Append(character);
                replaced = false;
            }
            else
            {
                replaced = true;
            }
        }

        var name = builder.ToString().Trim(' ', '.', '-', '_');
        if (name.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
            name = name[..^4].TrimEnd(' ', '.', '-', '_');
        if (name.Length > MaxFileNameLength)
            name = name[..MaxFileNameLength].TrimEnd(' ', '.', '-', '_');
        return (name.Length == 0 ? "profile" : name) + ".m3u";
    }
}

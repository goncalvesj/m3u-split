using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace api;

public sealed record StartOnlineImportRequest(string? Url);

public sealed record OnlineImportPlaylistRequest(List<string?>? Categories);

public sealed record OnlineImportErrorResponse(string Code, int? HttpStatus);

public sealed record OnlineImportStatusResponse(
    Guid Id,
    string Status,
    string? Stage,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    long BytesDownloaded,
    long? TotalBytes,
    long MaxBytes,
    long Entries,
    int CategoryCount,
    OnlineImportErrorResponse? Error);

public sealed record OnlineImportCategoryResponse(string Name, int Count);

public sealed record OnlineImportCategoriesResponse(IReadOnlyList<OnlineImportCategoryResponse> Categories, long Entries);

/// <summary>
/// Authenticated URL import endpoints. Every endpoint runs the online access guard first and only ever
/// touches imports under the validated caller's tenant and object ID; other users' imports are reported
/// as not found. Responses never include the source URL, only its scheme://host[:port] display form.
/// </summary>
public sealed class OnlineImportFunctions(
    OnlineAccess access, OnlineImportService imports, ILogger<OnlineImportFunctions> logger)
{
    private const int MaxStartBodyBytes = 16 * 1024;
    private const int MaxPlaylistBodyBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function("OnlineImportStart")]
    public async Task<IActionResult> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/imports")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!imports.IsConfigured)
            return NotConfigured();

        var body = await ReadJsonAsync<StartOnlineImportRequest>(request, MaxStartBodyBytes, cancellationToken);
        if (body is null)
            return OnlineHttp.Error(400, "invalid_request", "Send a JSON body with the playlist URL.");

        if (!SafeUrlPolicy.TryParseSource(body.Url, out var source, out var errorCode))
        {
            return errorCode == SourceFetchError.BlockedDestination
                ? OnlineHttp.Error(400, errorCode, "This address is not allowed. Use the provider's public playlist URL.")
                : OnlineHttp.Error(400, SourceFetchError.InvalidUrl,
                    $"Enter a complete http:// or https:// playlist URL (up to {SafeUrlPolicy.MaxUrlLength} characters).");
        }

        var store = imports.Store;
        var now = DateTimeOffset.UtcNow;
        var record = new OnlineImportRecord
        {
            Id = Guid.NewGuid(),
            TenantId = auth.Owner.TenantId,
            ObjectId = auth.Owner.ObjectId,
            SourceUrl = source.AbsoluteUri,
            SourceDisplay = SafeUrlPolicy.Display(source),
            Status = OnlineImportStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + imports.Settings.Retention,
        };

        try
        {
            await store.EnsureCreatedAsync(cancellationToken);
            await store.CreateAsync(record, cancellationToken);
            try
            {
                await store.EnqueueAsync(record, cancellationToken);
            }
            catch
            {
                await TryAsync("discard unqueued import", () => store.DeleteImportAsync(record.Owner, record.Id, CancellationToken.None));
                throw;
            }
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "start");
            return Unavailable();
        }

        // One import per user: older imports are discarded. They also expire on their own if this fails.
        await TryAsync("discard older imports", () => store.DeleteOlderImportsAsync(record, cancellationToken));

        logger.LogInformation("Online import {ImportId} queued.", record.Id);
        return new ObjectResult(ToResponse(record)) { StatusCode = StatusCodes.Status202Accepted };
    }

    [Function("OnlineImportStatus")]
    public async Task<IActionResult> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/imports/{importId}")] HttpRequest request,
        string importId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!imports.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(importId, out var id))
            return NotFound();

        var store = imports.Store;
        try
        {
            var stored = await store.GetAsync(auth.Owner, id, cancellationToken);
            if (stored is null)
                return NotFound();

            var record = stored.Record;
            var now = DateTimeOffset.UtcNow;
            if (record.ExpiresAt <= now)
            {
                await store.DeleteImportAsync(auth.Owner, id, cancellationToken);
                return Expired();
            }

            if (IsStale(record, now))
            {
                // The worker stopped reporting progress (for example the host restarted), so end the import.
                record.MarkFailed(OnlineImportError.Interrupted, null, now);
                if (await store.TryUpdateAsync(record, stored.ETag, cancellationToken) is not null)
                {
                    await store.DeleteDataAsync(auth.Owner, id, cancellationToken);
                }
                else
                {
                    stored = await store.GetAsync(auth.Owner, id, cancellationToken);
                    if (stored is null)
                        return NotFound();
                    record = stored.Record;
                }
            }

            return new OkObjectResult(ToResponse(record));
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "status");
            return Unavailable();
        }
    }

    [Function("OnlineImportCategories")]
    public async Task<IActionResult> Categories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/imports/{importId}/categories")] HttpRequest request,
        string importId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!imports.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(importId, out var id))
            return NotFound();

        var store = imports.Store;
        try
        {
            var (record, failure) = await GetReadyAsync(auth.Owner, id, cancellationToken);
            if (failure is not null)
                return failure;

            var index = await store.ReadIndexAsync(auth.Owner, id, cancellationToken);
            if (index is null)
            {
                await store.DeleteImportAsync(auth.Owner, id, cancellationToken);
                return Expired();
            }

            return new OkObjectResult(new OnlineImportCategoriesResponse(
                index.Categories.Select(category => new OnlineImportCategoryResponse(category.Name, category.Count)).ToList(),
                index.Entries));
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "categories");
            return Unavailable();
        }
    }

    [Function("OnlineImportPlaylist")]
    public async Task<IActionResult> Playlist(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/imports/{importId}/playlist")] HttpRequest request,
        string importId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!imports.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(importId, out var id))
            return NotFound();

        var body = await ReadJsonAsync<OnlineImportPlaylistRequest>(request, MaxPlaylistBodyBytes, cancellationToken);
        if (body?.Categories is not { Count: > 0 and <= M3uPlaylist.MaxCategories } requested ||
            requested.Any(string.IsNullOrEmpty))
            return InvalidSelection("Select at least one category.");

        var store = imports.Store;
        Stream? source = null;
        try
        {
            var (_, failure) = await GetReadyAsync(auth.Owner, id, cancellationToken);
            if (failure is not null)
                return failure;

            var index = await store.ReadIndexAsync(auth.Owner, id, cancellationToken);
            if (index is null)
            {
                await store.DeleteImportAsync(auth.Owner, id, cancellationToken);
                return Expired();
            }

            // Category names are matched exactly (ordinal, untrimmed), like the browser parser.
            var available = index.Categories
                .Where(category => category.Count > 0)
                .Select(category => category.Name)
                .ToHashSet(StringComparer.Ordinal);
            var selected = requested.Where(name => available.Contains(name!)).Select(name => name!)
                .ToHashSet(StringComparer.Ordinal);
            if (selected.Count == 0)
                return InvalidSelection("None of the selected categories has channels in this import.");

            source = await store.OpenSourceAsync(auth.Owner, id, cancellationToken);
            if (source is null)
            {
                await store.DeleteImportAsync(auth.Owner, id, cancellationToken);
                return Expired();
            }

            var result = new FilteredPlaylistResult(source, selected, logger);
            source = null;
            return result;
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "playlist");
            return Unavailable();
        }
        finally
        {
            if (source is not null)
                await source.DisposeAsync();
        }
    }

    [Function("OnlineImportDiscard")]
    public async Task<IActionResult> Discard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "online/imports/{importId}")] HttpRequest request,
        string importId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!imports.IsConfigured)
            return NotConfigured();
        if (!OnlineImportIds.TryParse(importId, out var id))
            return NotFound();

        var store = imports.Store;
        try
        {
            if (await store.GetAsync(auth.Owner, id, cancellationToken) is null)
                return NotFound();

            await store.DeleteImportAsync(auth.Owner, id, cancellationToken);
            logger.LogInformation("Online import {ImportId} discarded.", id);
            return new NoContentResult();
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "discard");
            return Unavailable();
        }
    }

    private async Task<(OnlineImportRecord? Record, IActionResult? Failure)> GetReadyAsync(
        OnlineImportOwner owner, Guid id, CancellationToken cancellationToken)
    {
        var store = imports.Store!;
        var stored = await store.GetAsync(owner, id, cancellationToken);
        if (stored is null)
            return (null, NotFound());

        if (stored.Record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await store.DeleteImportAsync(owner, id, cancellationToken);
            return (null, Expired());
        }

        if (stored.Record.Status != OnlineImportStatus.Ready)
            return (null, OnlineHttp.Error(409, "import_not_ready", "This import is not ready yet."));

        return (stored.Record, null);
    }

    private bool IsStale(OnlineImportRecord record, DateTimeOffset now) => record.Status switch
    {
        OnlineImportStatus.Queued => now - record.UpdatedAt > imports.Settings!.StaleQueuedAfter,
        OnlineImportStatus.Running => now - record.UpdatedAt > imports.Settings!.StaleRunningAfter,
        _ => false,
    };

    private OnlineImportStatusResponse ToResponse(OnlineImportRecord record) => new(
        record.Id,
        record.Status,
        record.Stage,
        record.SourceDisplay,
        record.CreatedAt,
        record.UpdatedAt,
        record.ExpiresAt,
        record.BytesDownloaded,
        record.TotalBytes,
        imports.Settings!.MaxDownloadBytes,
        record.Entries,
        record.CategoryCount,
        record.ErrorCode is null ? null : new OnlineImportErrorResponse(record.ErrorCode, record.HttpStatus));

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
            logger.LogError("Online import {Operation} failed: storage returned {Status} {ErrorCode}.",
                operation, failed.Status, failed.ErrorCode);
        else
            logger.LogError("Online import {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
    }

    private static ObjectResult NotFound() =>
        OnlineHttp.Error(404, "import_not_found", "This import does not exist or was discarded.");

    private static ObjectResult Expired() =>
        OnlineHttp.Error(410, "import_expired", "This import has expired. Import the playlist again.");

    private static ObjectResult InvalidSelection(string message) =>
        OnlineHttp.Error(400, "invalid_selection", message);

    private static ObjectResult NotConfigured() =>
        OnlineHttp.Error(503, "import_not_configured", "Playlist import is not configured on the server.");

    private static ObjectResult Unavailable() =>
        OnlineHttp.Error(503, "import_unavailable", "Playlist import storage is temporarily unavailable. Try again.");
}

/// <summary>Streams the filtered playlist straight from blob storage to the response.</summary>
public sealed class FilteredPlaylistResult(Stream source, IReadOnlySet<string> categories, ILogger logger) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var response = http.Response;
        var aborted = http.RequestAborted;
        await using (source)
        {
            try
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = "audio/x-mpegurl; charset=utf-8";
                response.Headers.CacheControl = "no-store";
                response.Headers.XContentTypeOptions = "nosniff";
                response.Headers.ContentDisposition = "attachment; filename=\"filtered.m3u\"";
                await M3uPlaylist.WriteFilteredAsync(source, response.Body, categories, aborted);
            }
            catch (Exception) when (aborted.IsCancellationRequested)
            {
                // The client went away; there is nobody left to answer.
            }
            catch (Exception exception) when (!aborted.IsCancellationRequested)
            {
                logger.LogError("Writing a filtered playlist failed ({ExceptionType}).", exception.GetType().Name);
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
                    new { error = "import_unavailable", message = "The playlist could not be generated. Try again." },
                    CancellationToken.None);
            }
        }
    }
}

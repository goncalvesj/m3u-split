using Azure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace api;

/// <summary>The TV link of a profile as returned to its signed-in owner.</summary>
public sealed class OnlineFeedResponse(string token, DateTimeOffset createdAt)
{
    public string Token { get; } = token;
    public DateTimeOffset CreatedAt { get; } = createdAt;
}

/// <summary>
/// Secret TV links. The owner endpoints run the online access guard first and manage the one link of
/// one of the caller's profiles; <c>feed/{token}</c> serves that profile's latest successful output to
/// anyone holding the link, without sign-in.
/// </summary>
public sealed class OnlineFeedFunctions(
    OnlineAccess access, OnlineFeedService feeds, OnlineRateLimiter limiter, ILogger<OnlineFeedFunctions> logger)
{
    [Function("OnlineFeedGet")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/profiles/{profileId}/feed")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!feeds.IsConfigured)
            return NotConfigured();
        if (!OnlineIds.TryParse(profileId, out var id))
            return ProfileNotFound();

        try
        {
            if (!await feeds.ProfileExistsAsync(auth.Owner, id, cancellationToken))
                return ProfileNotFound();

            return (await feeds.GetFeedAsync(auth.Owner, id, cancellationToken))?.State is { } state
                ? new OkObjectResult(ToResponse(state))
                : FeedNotFound();
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "read");
            return Unavailable();
        }
    }

    /// <summary>Returns the existing link (200) or creates one (201); repeating the request never creates a second link.</summary>
    [Function("OnlineFeedCreate")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/profiles/{profileId}/feed")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!feeds.IsConfigured)
            return NotConfigured();
        if (!OnlineIds.TryParse(profileId, out var id))
            return ProfileNotFound();

        try
        {
            if (!await feeds.ProfileExistsAsync(auth.Owner, id, cancellationToken))
                return ProfileNotFound();

            var change = await feeds.CreateAsync(auth.Owner, id, cancellationToken);
            return change switch
            {
                { Outcome: OnlineFeedOutcome.Created, State: { } state } =>
                    new ObjectResult(ToResponse(state)) { StatusCode = StatusCodes.Status201Created },
                { Outcome: OnlineFeedOutcome.Existing, State: { } state } => new OkObjectResult(ToResponse(state)),
                { Outcome: OnlineFeedOutcome.ProfileMissing } => ProfileNotFound(),
                _ => Unavailable(),
            };
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "create");
            return Unavailable();
        }
    }

    /// <summary>
    /// Replaces the token of the existing link; the old link stops working immediately. A profile without
    /// a link gets 404 feed_not_found rather than a new link, so a stale page never creates one by accident.
    /// </summary>
    [Function("OnlineFeedRegenerate")]
    public async Task<IActionResult> Regenerate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "online/profiles/{profileId}/feed/regenerate")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!feeds.IsConfigured)
            return NotConfigured();
        if (!OnlineIds.TryParse(profileId, out var id))
            return ProfileNotFound();

        try
        {
            if (!await feeds.ProfileExistsAsync(auth.Owner, id, cancellationToken))
                return ProfileNotFound();

            var change = await feeds.RegenerateAsync(auth.Owner, id, cancellationToken);
            return change switch
            {
                { Outcome: OnlineFeedOutcome.Replaced, State: { } state } => new OkObjectResult(ToResponse(state)),
                { Outcome: OnlineFeedOutcome.FeedMissing } => FeedNotFound(),
                { Outcome: OnlineFeedOutcome.ProfileMissing } => ProfileNotFound(),
                _ => Unavailable(),
            };
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "regenerate");
            return Unavailable();
        }
    }

    /// <summary>Revokes the link; 204 also when the profile has none.</summary>
    [Function("OnlineFeedRevoke")]
    public async Task<IActionResult> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "online/profiles/{profileId}/feed")] HttpRequest request,
        string profileId,
        CancellationToken cancellationToken)
    {
        var auth = await OnlineHttp.AuthorizeAsync(access, request, cancellationToken);
        if (auth.Denied is not null)
            return auth.Denied;
        if (!feeds.IsConfigured)
            return NotConfigured();
        if (!OnlineIds.TryParse(profileId, out var id))
            return ProfileNotFound();

        try
        {
            if (!await feeds.ProfileExistsAsync(auth.Owner, id, cancellationToken))
                return ProfileNotFound();

            await feeds.RevokeAsync(auth.Owner, id, cancellationToken);
            return new NoContentResult();
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "revoke");
            return Unavailable();
        }
    }

    /// <summary>
    /// The TV link itself: no sign-in, read-only, GET or HEAD, with an optional ".m3u" suffix. Every
    /// invalid link (malformed, unknown, revoked, replaced, deleted profile, owner no longer allowed) gets
    /// the same 404. A valid link of a profile that has never been refreshed successfully gets 404
    /// feed_not_ready. GET requests of a valid link also count against its hourly download limit, and
    /// <see cref="OnlineRateLimitMiddleware"/> applies the client limit of TV links before this function
    /// runs; both answer 429 rate_limited. Neither the token nor its hash is ever logged.
    /// </summary>
    [Function("OnlineFeed")]
    public async Task<IActionResult> Feed(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "head", Route = "feed/{token}")] HttpRequest request,
        string token,
        CancellationToken cancellationToken)
    {
        OnlineFeedResult.ApplyHeaders(request.HttpContext.Response);
        if (!feeds.IsConfigured)
            return NotConfigured();
        if (!OnlineFeedService.TryReadToken(token, out var value))
            return LinkNotFound();

        OnlineProfileOutputContent? output = null;
        try
        {
            if (await feeds.ResolveAsync(value, cancellationToken) is not { } target)
                return LinkNotFound();
            // Only working links reach this limit, so it cannot show whether an unknown token exists.
            if (!HttpMethods.IsHead(request.Method) &&
                limiter.CheckFeedDownload(request.HttpContext.Response, value) is { } limited)
                return limited;

            output = await feeds.OpenOutputAsync(target.Owner, target.ProfileId, cancellationToken);
            if (output is null)
            {
                return await feeds.ProfileExistsAsync(target.Owner, target.ProfileId, cancellationToken)
                    ? OnlineHttp.Error(404, "feed_not_ready",
                        "This TV link has no playlist yet. It starts working after the first successful refresh.")
                    : LinkNotFound();
            }

            var result = new OnlineFeedResult(output, HttpMethods.IsHead(request.Method), logger);
            output = null;
            return result;
        }
        catch (Exception exception) when (!IsAborted(request, cancellationToken))
        {
            LogStorageFailure(exception, "download");
            return Unavailable();
        }
        finally
        {
            if (output is not null)
                await output.DisposeAsync();
        }
    }

    private static OnlineFeedResponse ToResponse(OnlineFeedState state) => new(state.Token, state.CreatedAt);

    private static bool IsAborted(HttpRequest request, CancellationToken cancellationToken) =>
        request.HttpContext.RequestAborted.IsCancellationRequested || cancellationToken.IsCancellationRequested;

    // Exception messages can contain blob names, which include token hashes, so only the type and status are logged.
    private void LogStorageFailure(Exception exception, string operation)
    {
        if (exception is RequestFailedException failed)
            logger.LogError("TV link {Operation} failed: storage returned {Status} {ErrorCode}.",
                operation, failed.Status, failed.ErrorCode);
        else
            logger.LogError("TV link {Operation} failed ({ExceptionType}).", operation, exception.GetType().Name);
    }

    private static ObjectResult ProfileNotFound() =>
        OnlineHttp.Error(404, "profile_not_found", "This profile does not exist or was deleted.");

    private static ObjectResult FeedNotFound() =>
        OnlineHttp.Error(404, "feed_not_found", "This profile has no TV link.");

    private static ObjectResult LinkNotFound() =>
        OnlineHttp.Error(404, "feed_not_found", "This TV link does not exist or was revoked.");

    private static ObjectResult NotConfigured() =>
        OnlineHttp.Error(503, "feeds_not_configured", "TV links are not configured on the server.");

    private static ObjectResult Unavailable() =>
        OnlineHttp.Error(503, "feeds_unavailable", "TV links are temporarily unavailable. Try again.");
}

/// <summary>Sends profile output through a TV link. HEAD responses omit the playlist body.</summary>
public sealed class OnlineFeedResult(OnlineProfileOutputContent output, bool headOnly, ILogger logger) : IActionResult
{
    /// <summary>Sends the playlist with TV link headers and error codes.</summary>
    public Task ExecuteResultAsync(ActionContext context) =>
        OnlineProfileOutputResult.SendAsync(output, context, logger, isFeed: true, headOnly: headOnly);

    /// <summary>Sets TV link headers that prevent caches, search indexes, and referrer disclosure.</summary>
    public static void ApplyHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}

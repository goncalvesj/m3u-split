using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace api;

public enum OnlineRateLimitPolicy
{
    Instance,
    Api,
    Feed,
    FeedDownload,
}

/// <summary>
/// In-memory fixed-window rate limits of every HTTP endpoint. <see cref="OnlineRateLimitMiddleware"/> checks
/// them before the function runs, so a rejected request costs no sign-in check, storage read or download.
/// The counters belong to one Function App instance and start again when the instance restarts. The client
/// key is the connection address that the Functions host reports, never a request header; when the host
/// reports one shared address, all clients of an instance share one budget. Addresses and TV link tokens
/// are never logged.
/// </summary>
public sealed class OnlineRateLimiter : IDisposable
{
    /// <summary>The function name of the anonymous TV link endpoint, which has its own client limit and headers.</summary>
    public const string FeedFunctionName = "OnlineFeed";

    private const long WarningIntervalMilliseconds = 60_000;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly ILogger logger;
    private readonly FixedWindowRateLimiter? instance;
    private readonly PartitionedRateLimiter<Key>? clients;
    private readonly long[] rejected = new long[Enum.GetValues<OnlineRateLimitPolicy>().Length];
    private readonly long[] nextWarning = new long[Enum.GetValues<OnlineRateLimitPolicy>().Length];

    public OnlineRateLimiter(IConfiguration configuration, ILogger<OnlineRateLimiter> logger)
    {
        this.logger = logger;
        var settings = OnlineRateLimitSettings.Read(configuration, out var error);
        if (settings is null)
        {
            logger.LogError("All HTTP endpoints are disabled: {ConfigurationError}", error);
            return;
        }

        instance = new FixedWindowRateLimiter(Window(settings.InstanceRequestsPerMinute, Minute, autoReplenishment: true));
        clients = CreateClientLimiter(settings);
    }

    [MemberNotNullWhen(true, nameof(instance), nameof(clients))]
    public bool IsConfigured => instance is not null && clients is not null;

    /// <summary>
    /// Checks the instance limit and the client limit of one HTTP request. Returns null when the function
    /// may run, otherwise the 429 or 503 result to send instead.
    /// </summary>
    public IActionResult? Check(HttpContext context, string functionName)
    {
        var response = context.Response;
        var feed = functionName == FeedFunctionName;
        if (feed)
            OnlineFeedResult.ApplyHeaders(response);
        if (!IsConfigured)
            return NotConfigured(response);

        // A full instance rejects before a client partition exists, so a flood from new addresses stays cheap.
        using (var probe = instance.AttemptAcquire(0))
        {
            if (!probe.IsAcquired)
                return Reject(response, OnlineRateLimitPolicy.Instance, probe);
        }

        var policy = feed ? OnlineRateLimitPolicy.Feed : OnlineRateLimitPolicy.Api;
        using (var client = clients.AttemptAcquire(new Key(policy, ClientKey(context))))
        {
            if (!client.IsAcquired)
                return Reject(response, policy, client);
        }

        using var lease = instance.AttemptAcquire();
        return lease.IsAcquired ? null : Reject(response, OnlineRateLimitPolicy.Instance, lease);
    }

    /// <summary>
    /// Checks the download limit of one working TV link before its playlist is opened. A regenerated link
    /// has a new token and therefore a new limit.
    /// </summary>
    public IActionResult? CheckFeedDownload(HttpResponse response, string token)
    {
        if (!IsConfigured)
            return NotConfigured(response);

        using var lease = clients.AttemptAcquire(new Key(OnlineRateLimitPolicy.FeedDownload, OnlineFeedService.Hash(token)));
        return lease.IsAcquired ? null : Reject(response, OnlineRateLimitPolicy.FeedDownload, lease);
    }

    /// <summary>
    /// The client part of a limit key: the connection address from the Functions host, an IPv4-mapped
    /// address as IPv4 and an IPv6 address as its /64 prefix. Request headers are not read.
    /// </summary>
    public static string ClientKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
            return address.ToString();

        bytes[8..].Clear();
        return new IPAddress(bytes) + "/64";
    }

    public void Dispose()
    {
        instance?.Dispose();
        clients?.Dispose();
    }

    private static PartitionedRateLimiter<Key> CreateClientLimiter(OnlineRateLimitSettings settings) =>
        PartitionedRateLimiter.Create<Key, Key>(key => RateLimitPartition.GetFixedWindowLimiter(key, partition =>
            partition.Policy switch
            {
                OnlineRateLimitPolicy.Api => Window(settings.ApiRequestsPerMinute, Minute),
                OnlineRateLimitPolicy.Feed => Window(settings.FeedRequestsPerMinute, Minute),
                OnlineRateLimitPolicy.FeedDownload => Window(settings.FeedDownloadsPerHour, Hour),
                _ => throw new ArgumentOutOfRangeException(nameof(key), partition.Policy, "No client limit has this policy."),
            }));

    // No queue: a request over a limit is rejected at once instead of holding an execution open.
    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window, bool autoReplenishment = false) => new()
    {
        PermitLimit = permits,
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = autoReplenishment,
    };

    private ObjectResult Reject(HttpResponse response, OnlineRateLimitPolicy policy, RateLimitLease lease)
    {
        var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60;
        response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        response.Headers.CacheControl = "no-store";
        Warn(policy);
        return OnlineHttp.Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests. Try again later.");
    }

    private static ObjectResult NotConfigured(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        return OnlineHttp.Error(StatusCodes.Status503ServiceUnavailable, "rate_limits_not_configured",
            "The rate limits of the online API are not configured correctly on the server.");
    }

    // At most one warning per policy and minute, with the number of rejections since the previous warning.
    private void Warn(OnlineRateLimitPolicy policy)
    {
        var index = (int)policy;
        Interlocked.Increment(ref rejected[index]);
        var now = Environment.TickCount64;
        var due = Interlocked.Read(ref nextWarning[index]);
        if (now < due ||
            Interlocked.CompareExchange(ref nextWarning[index], now + WarningIntervalMilliseconds, due) != due)
            return;

        logger.LogWarning(
            "Rate limit {RateLimitPolicy} rejected {RejectedRequests} requests on this instance since its previous warning.",
            policy, Interlocked.Exchange(ref rejected[index], 0));
    }

    private readonly record struct Key(OnlineRateLimitPolicy Policy, string Partition);
}

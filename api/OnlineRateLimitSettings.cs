using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace api;

/// <summary>
/// Validated in-memory rate limits of the HTTP endpoints (see <see cref="OnlineRateLimiter"/>). Each limit
/// applies to one Function App instance: the API and TV link request limits to each client address, the
/// download limit to each TV link and the instance limit to all HTTP requests of the instance together.
/// </summary>
public sealed record OnlineRateLimitSettings(
    int ApiRequestsPerMinute,
    int FeedRequestsPerMinute,
    int FeedDownloadsPerHour,
    int InstanceRequestsPerMinute)
{
    public const string SectionName = "OnlineRateLimits";
    public const int DefaultApiRequestsPerMinute = 300;
    public const int DefaultFeedRequestsPerMinute = 30;
    public const int DefaultFeedDownloadsPerHour = 20;
    public const int DefaultInstanceRequestsPerMinute = 1200;

    public static OnlineRateLimitSettings? Read(IConfiguration configuration, out string error)
    {
        var section = configuration.GetSection(SectionName);
        if (!ReadInt(section, nameof(ApiRequestsPerMinute), DefaultApiRequestsPerMinute, 10, 6000, out var api, out error) ||
            !ReadInt(section, nameof(FeedRequestsPerMinute), DefaultFeedRequestsPerMinute, 1, 600, out var feed, out error) ||
            !ReadInt(section, nameof(FeedDownloadsPerHour), DefaultFeedDownloadsPerHour, 1, 1000, out var downloads, out error) ||
            !ReadInt(section, nameof(InstanceRequestsPerMinute), DefaultInstanceRequestsPerMinute, 60, 60000, out var instance, out error))
            return null;

        return new OnlineRateLimitSettings(api, feed, downloads, instance);
    }

    private static bool ReadInt(
        IConfiguration section, string key, int fallback, int minimum, int maximum, out int value, out string error)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            error = "";
            return true;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= minimum && value <= maximum)
        {
            error = "";
            return true;
        }

        error = $"{SectionName}:{key} must be a whole number from {minimum} to {maximum}.";
        return false;
    }
}

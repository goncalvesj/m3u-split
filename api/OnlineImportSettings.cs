using Microsoft.Extensions.Configuration;
using System.Globalization;

namespace api;

public sealed record OnlineImportSettings(
    long MaxDownloadBytes,
    TimeSpan DownloadTimeout,
    TimeSpan IdleTimeout,
    TimeSpan Retention,
    string UserAgent)
{
    public const string SectionName = "OnlineImport";
    public const string DefaultUserAgent = "m3u-split/1.0";

    // A queued import that never started, or a running import without a heartbeat, is reported as interrupted.
    public TimeSpan StaleQueuedAfter => DownloadTimeout + TimeSpan.FromMinutes(10);
    public TimeSpan StaleRunningAfter => IdleTimeout + TimeSpan.FromMinutes(3);

    public SafeFetchOptions FetchOptions => new(MaxDownloadBytes, DownloadTimeout, IdleTimeout, UserAgent);

    public static OnlineImportSettings? Read(IConfiguration configuration, out string error)
    {
        var section = configuration.GetSection(SectionName);
        if (!ReadInt(section, "MaxDownloadMiB", 300, 1, 2048, out var maxMiB, out error) ||
            !ReadInt(section, "DownloadTimeoutSeconds", 300, 10, 3600, out var downloadSeconds, out error) ||
            !ReadInt(section, "IdleTimeoutSeconds", 60, 5, 300, out var idleSeconds, out error) ||
            !ReadInt(section, "RetentionHours", 24, 1, 168, out var retentionHours, out error))
            return null;

        if (idleSeconds > downloadSeconds)
        {
            error = "OnlineImport:IdleTimeoutSeconds must not be greater than OnlineImport:DownloadTimeoutSeconds.";
            return null;
        }

        var userAgent = section["UserAgent"];
        if (string.IsNullOrEmpty(userAgent))
        {
            userAgent = DefaultUserAgent;
        }
        else if (userAgent.Length > 256 || userAgent != userAgent.Trim() ||
                 userAgent.Any(character => character is < ' ' or > '~'))
        {
            error = "OnlineImport:UserAgent must be 1-256 printable ASCII characters without leading or trailing spaces.";
            return null;
        }

        error = "";
        return new OnlineImportSettings(
            maxMiB * 1024L * 1024L,
            TimeSpan.FromSeconds(downloadSeconds),
            TimeSpan.FromSeconds(idleSeconds),
            TimeSpan.FromHours(retentionHours),
            userAgent);
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

        error = $"OnlineImport:{key} must be a whole number from {minimum} to {maximum}.";
        return false;
    }
}

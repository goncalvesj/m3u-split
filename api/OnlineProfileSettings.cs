using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace api;

/// <summary>
/// Validated settings for saved online profiles. Download limits (size, timeouts, user agent)
/// come from <see cref="OnlineImportSettings"/>.
/// </summary>
public sealed record OnlineProfileSettings(int MaxProfilesPerUser)
{
    public const string SectionName = "OnlineProfiles";
    public const int DefaultMaxProfilesPerUser = 10;
    public const int MinProfilesPerUser = 1;
    public const int MaxProfilesPerUserLimit = 50;

    public static OnlineProfileSettings? Read(IConfiguration configuration, out string error)
    {
        var raw = configuration.GetSection(SectionName)["MaxProfilesPerUser"];
        var value = DefaultMaxProfilesPerUser;
        if (!string.IsNullOrWhiteSpace(raw) &&
            (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
             value < MinProfilesPerUser || value > MaxProfilesPerUserLimit))
        {
            error = $"{SectionName}:MaxProfilesPerUser must be a whole number from {MinProfilesPerUser} to {MaxProfilesPerUserLimit}.";
            return null;
        }

        error = "";
        return new OnlineProfileSettings(value);
    }
}

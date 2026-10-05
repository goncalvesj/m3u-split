using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace api;

public sealed record OnlineAuthSettings(
    Guid TenantId,
    Guid ApiClientId,
    Guid SpaClientId,
    string RequiredScope,
    IReadOnlySet<Guid> AllowedObjectIds)
{
    public string Issuer => $"https://login.microsoftonline.com/{TenantId:D}/v2.0";

    public static OnlineAuthSettings? Read(IConfiguration configuration, out string error)
    {
        var section = configuration.GetSection("OnlineAuth");
        if (!ReadId(section["TenantId"], out var tenantId) ||
            !ReadId(section["ApiClientId"], out var apiClientId) ||
            !ReadId(section["SpaClientId"], out var spaClientId) ||
            apiClientId == spaClientId)
        {
            error = "OnlineAuth requires a tenant ID and separate API and SPA application IDs.";
            return null;
        }

        var scope = section["RequiredScope"];
        if (scope is null || !Regex.IsMatch(scope, @"\A[a-zA-Z][a-zA-Z0-9_.-]{0,127}\z"))
        {
            error = "OnlineAuth:RequiredScope must be one delegated scope name, not a scope URI.";
            return null;
        }

        var allowedIds = new HashSet<Guid>();
        foreach (var value in (section["AllowedObjectIds"] ?? "").Split(','))
        {
            if (!ReadId(value.Trim(), out var objectId))
            {
                error = "OnlineAuth:AllowedObjectIds must contain only explicitly allowed user object IDs, separated by commas.";
                return null;
            }
            allowedIds.Add(objectId);
        }

        error = "";
        return new OnlineAuthSettings(tenantId, apiClientId, spaClientId, scope, allowedIds);
    }

    private static bool ReadId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;
}

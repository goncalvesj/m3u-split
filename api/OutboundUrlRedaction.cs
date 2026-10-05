using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace api;

/// <summary>
/// Reduces every outgoing HTTP dependency to scheme://host[:port] before it is sent to Application
/// Insights. Playlist URLs usually carry provider credentials in the path or query string, and
/// automatic dependency tracking would otherwise record the full URL.
/// </summary>
public sealed class OutboundUrlRedaction(ITelemetryProcessor next) : ITelemetryProcessor
{
    public void Process(ITelemetry item)
    {
        if (item is DependencyTelemetry dependency)
            Redact(dependency);

        next.Process(item);
    }

    public static void Redact(DependencyTelemetry dependency)
    {
        var method = dependency.Name?.Split(' ', 2)[0];
        if (string.IsNullOrEmpty(method) || method.Length > 16 || !method.All(char.IsAsciiLetterUpper))
            method = "HTTP";

        if (Uri.TryCreate(dependency.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            dependency.Data = $"{uri.Scheme}://{uri.Authority}";
            dependency.Name = $"{method} {uri.Authority}";
        }
        else if (dependency.Type?.StartsWith("Http", StringComparison.OrdinalIgnoreCase) == true)
        {
            // An HTTP dependency whose URL cannot be parsed is dropped rather than risk keeping credentials.
            dependency.Data = "";
            dependency.Name = method;
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace api;

/// <summary>
/// Removes TV link tokens from everything the worker sends to Application Insights. A link
/// <c>/api/feed/{token}</c> works like a password, so request names and URLs, log and exception messages,
/// custom properties (such as a logged request path) and operation names keep only <c>/feed/redacted</c>.
/// </summary>
public sealed partial class FeedPathRedaction(ITelemetryProcessor next) : ITelemetryProcessor
{
    public void Process(ITelemetry item)
    {
        Redact(item);
        next.Process(item);
    }

    public static void Redact(ITelemetry item)
    {
        switch (item)
        {
            case RequestTelemetry request:
                if (TryRedact(request.Name, out var name))
                    request.Name = name;
                if (request.Url is { } url && TryRedact(url.OriginalString, out var redactedUrl))
                    request.Url = Uri.TryCreate(redactedUrl, UriKind.RelativeOrAbsolute, out var safeUrl) ? safeUrl : null;
                break;
            case TraceTelemetry trace:
                if (TryRedact(trace.Message, out var message))
                    trace.Message = message;
                break;
            case ExceptionTelemetry exception:
                if (TryRedact(exception.Message, out var exceptionMessage))
                    exception.Message = exceptionMessage;
                foreach (var details in exception.ExceptionDetailsInfoList)
                {
                    if (TryRedact(details.Message, out var detailsMessage))
                        details.Message = detailsMessage;
                }
                break;
        }

        if (item is ISupportProperties { Properties: var properties })
        {
            foreach (var (key, value) in properties.ToArray())
            {
                if (TryRedact(value, out var redacted))
                    properties[key] = redacted;
            }
        }

        var operation = item.Context.Operation;
        if (TryRedact(operation.Name, out var operationName))
            operation.Name = operationName;
    }

    /// <summary>Replaces every path segment that follows <c>/feed/</c>; returns false when there is none.</summary>
    public static bool TryRedact(string? value, [NotNullWhen(true)] out string? redacted)
    {
        redacted = null;
        if (string.IsNullOrEmpty(value) || !value.Contains("/feed/", StringComparison.OrdinalIgnoreCase))
            return false;

        var result = FeedPath().Replace(value, "$1redacted");
        if (result == value)
            return false;

        redacted = result;
        return true;
    }

    [GeneratedRegex(@"(/feed/)[^/?#\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeedPath();
}

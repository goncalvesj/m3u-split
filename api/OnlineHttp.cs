using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace api;

public readonly record struct OnlineHttpAuthorization(OnlineImportOwner Owner, IActionResult? Denied);

/// <summary>Response conventions shared by the online HTTP endpoints, matching OnlineSession.</summary>
public static class OnlineHttp
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the online access guard before anything else. On success the data owner comes only from the
    /// validated token; on failure the denial is mapped exactly like OnlineSession does.
    /// </summary>
    public static async Task<OnlineHttpAuthorization> AuthorizeAsync(
        OnlineAccess access, HttpRequest request, CancellationToken cancellationToken)
    {
        NoStore(request);
        var result = await access.AuthorizeAsync(request, cancellationToken);
        if (result is AuthorizedOnlineUser allowed)
            return new OnlineHttpAuthorization(OnlineImportOwner.From(allowed.Identity), null);

        var denied = (OnlineAccessDenied)result;
        if (denied.StatusCode == StatusCodes.Status401Unauthorized)
            request.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";

        return new OnlineHttpAuthorization(default, Error(denied.StatusCode, denied.Code, denied.Message));
    }

    public static void NoStore(HttpRequest request) =>
        request.HttpContext.Response.Headers.CacheControl = "no-store";

    public static ObjectResult Error(int statusCode, string code, string message) =>
        new(new { error = code, message }) { StatusCode = statusCode };

    /// <summary>Reads a JSON body within the byte limit. Returns null for an invalid body.</summary>
    public static async Task<T?> ReadJsonAsync<T>(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
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
}

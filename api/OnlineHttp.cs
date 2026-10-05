using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace api;

public readonly record struct OnlineHttpAuthorization(OnlineImportOwner Owner, IActionResult? Denied);

/// <summary>Response conventions shared by the online HTTP endpoints, matching OnlineSession.</summary>
public static class OnlineHttp
{
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
}

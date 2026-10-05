using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace api;

public sealed class OnlineSession(OnlineAccess access)
{
    [Function("OnlineSession")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "online/session")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await access.AuthorizeAsync(request, cancellationToken);
        if (result is AuthorizedOnlineUser allowed)
            return new OkObjectResult(allowed.Identity);

        var denied = (OnlineAccessDenied)result;
        if (denied.StatusCode == StatusCodes.Status401Unauthorized)
            request.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";

        return new ObjectResult(new { error = denied.Code, message = denied.Message })
        {
            StatusCode = denied.StatusCode,
        };
    }
}

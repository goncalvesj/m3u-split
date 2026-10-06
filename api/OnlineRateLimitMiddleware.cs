using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace api;

/// <summary>
/// Applies <see cref="OnlineRateLimiter"/> to every HTTP-triggered function before the function runs. A
/// rejected request gets its 429 or 503 result and the function does not run. Other triggers pass through.
/// It must be registered after <c>ConfigureFunctionsWebApplication</c>, which provides the HTTP context and
/// sends the result.
/// </summary>
public sealed class OnlineRateLimitMiddleware(OnlineRateLimiter limiter) : IFunctionsWorkerMiddleware
{
    public Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext is null || limiter.Check(httpContext, context.FunctionDefinition.Name) is not { } rejected)
            return next(context);

        context.GetInvocationResult().Value = rejected;
        return Task.CompletedTask;
    }
}

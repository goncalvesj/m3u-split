using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;

namespace api;

public sealed class OnlineProfilesFlagMiddleware(IConfiguration configuration) : IFunctionsWorkerMiddleware
{
    private readonly bool enabled = string.Equals(
        configuration["OnlineProfiles:Enabled"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    public Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (enabled)
            return next(context);

        if (context.GetHttpContext() is not null)
            context.GetInvocationResult().Value = new NotFoundResult();

        return Task.CompletedTask;
    }
}

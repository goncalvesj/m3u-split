using api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton<OnlineAccess>();

// URL import: storage credentials follow the Functions connection conventions (see docs/online-setup.md).
builder.Services.AddAzureClientsCore();
builder.Services.AddSingleton<OnlineImportService>();

// Saved profiles: same storage account and download limits as URL import, separate container and queues.
builder.Services.AddSingleton<OnlineProfileService>();

// TV links: secret read-only links to saved profiles, in the same storage account (see docs/online-setup.md).
builder.Services.AddSingleton<OnlineFeedService>();

// TV link paths carry their token; ASP.NET Core request logs ("Request starting ...") must not record them.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Outgoing playlist downloads must never reach telemetry with their full URL.
builder.Services.AddApplicationInsightsTelemetryProcessor<OutboundUrlRedaction>();

// TV link tokens must never reach telemetry either.
builder.Services.AddApplicationInsightsTelemetryProcessor<FeedPathRedaction>();

builder.Build().Run();

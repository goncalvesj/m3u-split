using api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton<OnlineAccess>();

// URL import: storage credentials follow the Functions connection conventions (see docs/online-setup.md).
builder.Services.AddAzureClientsCore();
builder.Services.AddSingleton<OnlineImportService>();

// Saved profiles: same storage account and download limits as URL import, separate container and queues.
builder.Services.AddSingleton<OnlineProfileService>();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Outgoing playlist downloads must never reach telemetry with their full URL.
builder.Services.AddApplicationInsightsTelemetryProcessor<OutboundUrlRedaction>();

builder.Build().Run();

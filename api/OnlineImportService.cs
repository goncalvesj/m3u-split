using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace api;

/// <summary>
/// Shared URL import services. Invalid or missing configuration is logged once and reported by the
/// endpoints as 503 import_not_configured instead of stopping the whole Function App.
/// </summary>
public sealed class OnlineImportService : IDisposable
{
    public OnlineImportService(IConfiguration configuration, AzureComponentFactory components, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<OnlineImportService>();
        var settings = OnlineImportSettings.Read(configuration, out var settingsError);
        if (settings is null)
        {
            logger.LogError("Online URL import is disabled: {ConfigurationError}", settingsError);
            return;
        }

        var storage = OnlinePlaylistStorage.Create(configuration, components, out var storageError);
        if (storage is null)
        {
            logger.LogError("Online URL import is disabled: {ConfigurationError}", storageError);
            return;
        }

        Settings = settings;
        Store = new OnlineImportStore(storage, settings.Retention, loggerFactory.CreateLogger<OnlineImportStore>());
        Fetcher = new SafeHttpFetcher(settings.FetchOptions);
    }

    public OnlineImportSettings? Settings { get; }
    public OnlineImportStore? Store { get; }
    public SafeHttpFetcher? Fetcher { get; }

    [MemberNotNullWhen(true, nameof(Settings), nameof(Store), nameof(Fetcher))]
    public bool IsConfigured => Settings is not null && Store is not null && Fetcher is not null;

    public void Dispose() => Fetcher?.Dispose();
}

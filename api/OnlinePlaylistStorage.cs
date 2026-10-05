using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;

namespace api;

/// <summary>
/// Clients for the dedicated app-data storage account. This is deliberately separate from the
/// Functions host storage (AzureWebJobsStorage) and uses the same connection conventions as the
/// queue trigger: a connection string, or identity-based blobServiceUri/queueServiceUri settings.
/// </summary>
public sealed class OnlinePlaylistStorage
{
    public const string ConnectionName = "OnlinePlaylistStorage";

    private OnlinePlaylistStorage(BlobServiceClient blobs, QueueServiceClient queues)
    {
        Blobs = blobs;
        Queues = queues;
    }

    public BlobServiceClient Blobs { get; }
    public QueueServiceClient Queues { get; }

    public static OnlinePlaylistStorage? Create(
        IConfiguration configuration, AzureComponentFactory components, out string error)
    {
        var section = configuration.GetSection(ConnectionName);
        var blobOptions = new BlobClientOptions();
        // The Functions queue trigger expects Base64 message bodies by default.
        var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };

        try
        {
            if (!string.IsNullOrWhiteSpace(section.Value))
            {
                error = "";
                return new OnlinePlaylistStorage(
                    new BlobServiceClient(section.Value, blobOptions),
                    new QueueServiceClient(section.Value, queueOptions));
            }

            if (!TryReadServiceUri(section["blobServiceUri"], out var blobServiceUri) ||
                !TryReadServiceUri(section["queueServiceUri"], out var queueServiceUri))
            {
                error = $"{ConnectionName} requires a connection string, or HTTPS {ConnectionName}__blobServiceUri " +
                        $"and {ConnectionName}__queueServiceUri settings for an identity-based connection.";
                return null;
            }

            // Honours <connection>__credential, __clientId and related settings the same way as the Functions host.
            var credential = components.CreateTokenCredential(section);
            error = "";
            return new OnlinePlaylistStorage(
                new BlobServiceClient(blobServiceUri, credential, blobOptions),
                new QueueServiceClient(queueServiceUri, credential, queueOptions));
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or InvalidOperationException)
        {
            // Never include the exception message: connection strings can contain account keys.
            error = $"{ConnectionName} is not a valid storage connection ({exception.GetType().Name}).";
            return null;
        }
    }

    private static bool TryReadServiceUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}

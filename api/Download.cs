using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace api;

public partial class Download
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    // Upload makes this name: a lowercase GUID and an optional short extension.
    [GeneratedRegex(@"^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}(?:\.[A-Za-z0-9]{1,16})?\z")]
    private static partial Regex StoredFileNameRegex();

    [Function("Download")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        var data = await ReadBodyAsync(req);
        if (data is null) return new BadRequestObjectResult("Invalid request body.");
        if (data.FileName is not { } storedName || !StoredFileNameRegex().IsMatch(storedName))
            return new BadRequestObjectResult("Invalid file name.");
        if (data.Categories is not { Count: > 0 and <= M3uPlaylist.MaxCategories } categories ||
            categories.Any(category => string.IsNullOrEmpty(category?.Name)) ||
            categories.Sum(category => category.Name.Length) > M3uPlaylist.MaxCategoryNameChars)
            return new BadRequestObjectResult("Invalid categories.");

        var fileContent = Environment.GetEnvironmentVariable("UseBlobStorage") is "true"
            ? (await new BlobContainerClient(
                    Environment.GetEnvironmentVariable("StorageConnectionString"), "uploads")
                .GetBlobClient(storedName)
                .DownloadContentAsync()).Value.Content.ToString()
            : await File.ReadAllTextAsync(
                Path.Combine(Environment.GetEnvironmentVariable("FilePath")!, storedName));

        var result = new StringBuilder();
        foreach (var cat in categories)
        {
            var pattern = $@"#EXTINF:-?\d+.*group-title=""{Regex.Escape(cat.Name)}"".*\n.*\n";
            foreach (Match m in Regex.Matches(fileContent, pattern))
                result.Append(m.Value);
        }

        return new OkObjectResult(result.ToString());
    }

    private static async Task<CategoriesDto?> ReadBodyAsync(HttpRequest req)
    {
        if (req.ContentLength > MaxBodyBytes)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await req.Body.ReadAsync(chunk, req.HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize(
                buffer.GetBuffer().AsSpan(0, (int)buffer.Length), AppJsonContext.Default.CategoriesDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

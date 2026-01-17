using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace api;

public class Download
{
    [Function("Download")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        var data = await JsonSerializer.DeserializeAsync(req.Body, AppJsonContext.Default.CategoriesDto);
        if (data is null) return new BadRequestObjectResult("Invalid request body.");

        var fileContent = Environment.GetEnvironmentVariable("UseBlobStorage") is "true"
            ? (await new BlobContainerClient(
                    Environment.GetEnvironmentVariable("StorageConnectionString"), "uploads")
                .GetBlobClient(data.FileName)
                .DownloadContentAsync()).Value.Content.ToString()
            : await File.ReadAllTextAsync(
                Path.Combine(Environment.GetEnvironmentVariable("FilePath")!, data.FileName));

        var result = new StringBuilder();
        foreach (var cat in data.Categories)
        {
            var pattern = $@"#EXTINF:-?\d+.*group-title=""{Regex.Escape(cat.Name)}"".*\n.*\n";
            foreach (Match m in Regex.Matches(fileContent, pattern))
                result.Append(m.Value);
        }

        return new OkObjectResult(result.ToString());
    }
}

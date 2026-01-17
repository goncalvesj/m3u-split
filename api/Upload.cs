using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.RegularExpressions;

namespace api;

public partial class Upload
{
    [GeneratedRegex(@"group-title=""([^""]*)""")] 
    private static partial Regex CategoryRegex();

    [Function("Upload")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        var file = req.Form.Files.GetFile("file");
        if (file is not { Length: > 0 })
            return new BadRequestObjectResult("No file was uploaded.");

        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        
        string content;
        await using (var stream = file.OpenReadStream())
        {
            if (Environment.GetEnvironmentVariable("UseBlobStorage") is "true")
            {
                var container = new BlobContainerClient(
                    Environment.GetEnvironmentVariable("StorageConnectionString"), "uploads");
                await container.CreateIfNotExistsAsync();
                await container.GetBlobClient(fileName).UploadAsync(stream);
                stream.Position = 0;
            }
            else
            {
                var filePath = Path.Combine(Environment.GetEnvironmentVariable("FilePath")!, fileName);
                await using var fs = File.Create(filePath);
                await stream.CopyToAsync(fs);
                stream.Position = 0;
            }
            
            using var reader = new StreamReader(stream);
            content = await reader.ReadToEndAsync();
        }

        var categories = CategoryRegex().Matches(content)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(name => new Category(name))
            .ToArray();

        return new OkObjectResult(new UploadResponse(fileName, categories));
    }
}

public record UploadResponse(string FileName, Category[] Categories);

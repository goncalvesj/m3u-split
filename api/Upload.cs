using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.RegularExpressions;

namespace api;

public partial class Upload
{
    // The Functions host also limits a request body to 100 MB by default.
    private const long MaxUploadBytes = 100 * 1024 * 1024;

    [GeneratedRegex(@"group-title=""([^""]*)""")] 
    private static partial Regex CategoryRegex();

    [GeneratedRegex(@"^\.[A-Za-z0-9]{1,16}\z")]
    private static partial Regex SafeExtensionRegex();

    [Function("Upload")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req)
    {
        var file = req.Form.Files.GetFile("file");
        if (file is not { Length: > 0 })
            return new BadRequestObjectResult("No file was uploaded.");
        if (file.Length > MaxUploadBytes)
            return new BadRequestObjectResult("The file is too large.");

        // Keep only a short extension of letters and digits. Download accepts only this name format.
        var extension = Path.GetExtension(file.FileName);
        var fileName = SafeExtensionRegex().IsMatch(extension)
            ? $"{Guid.NewGuid()}{extension}"
            : Guid.NewGuid().ToString();
        
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

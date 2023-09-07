using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;

namespace M3U.Split
{

    public class Upload
    {
        [FunctionName("Upload")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            // Get the file from the request body
            var file = req.Form.Files.GetFile("file");
            // Check if a file was uploaded
            if (file == null || file.Length == 0)
            {
                return new BadRequestObjectResult("No file was uploaded.");
            }
            // Generate a unique file name
            var fileName = Guid.NewGuid().ToString() + Path.GetExtension(file.FileName);

            // Read the file content
            var fileContent = await ReadFileContentAsync(file);

            // Extract the categories
            var categories = ExtractCategories(fileContent);

            if (Environment.GetEnvironmentVariable("UseBlobStorage") == "true")
            {
                // Save the file to Azure Blob Storage
                var containerName = "uploads";
                var container = new BlobContainerClient(Environment.GetEnvironmentVariable("StorageConnectionString"), containerName);
                container.CreateIfNotExists();
                var blobClient = container.GetBlobClient(fileName);
                using var stream = file.OpenReadStream();
                await blobClient.UploadAsync(stream);
            }
            else
            {
                // Save the file to disk
                var filePath = Path.Combine(Environment.GetEnvironmentVariable("FilePath"), fileName);
                using var stream = new FileStream(filePath, FileMode.Create);
                await file.CopyToAsync(stream);
            }

            // Return the file name and URL
            var baseUrl = $"{req.Scheme}://{req.Host}";
            var fileUrl = $"{baseUrl}/api/files/{fileName}";
            var response = new
            {
                FileName = fileName,
                // FileUrl = fileUrl,
                Categories = categories
            };
            return new OkObjectResult(response);
        }

        private static async Task<string> ReadFileContentAsync(IFormFile file)
        {
            using var reader = new StreamReader(file.OpenReadStream());
            return await reader.ReadToEndAsync();
        }

        private static Dictionary<string, Category> ExtractCategories(string fileContent)
        {
            var categories = new Dictionary<string, Category>();
            var regex = new Regex(@"group-title=""([^""]*)""");
            var matches = regex.Matches(fileContent);
            foreach (Match match in matches)
            {
                var categoryName = match.Groups[1].Value;
                var category = new Category
                {
                    // Start = match.Index,
                    // End = match.Index + match.Length,
                    Name = categoryName
                };
                categories[categoryName] = category;
            }
            return categories;
        }
    }
}

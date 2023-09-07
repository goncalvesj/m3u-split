using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Linq;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;
using Azure.Storage.Sas;
using Azure.Storage;

namespace M3U.Split
{
    public class Download
    {
        [FunctionName("Download")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonConvert.DeserializeObject<CategoriesDto>(requestBody);

            var fileContent = "";
            if (Environment.GetEnvironmentVariable("UseBlobStorage") == "true")
            {
                // Download the file from Azure Blob Storage
                var containerName = "uploads";
                var container = new BlobContainerClient(Environment.GetEnvironmentVariable("StorageConnectionString"), containerName);
                var blobClient = container.GetBlobClient(data.FileName);
                BlobDownloadResult downloadResult = await blobClient.DownloadContentAsync();
                fileContent = downloadResult.Content.ToString();
            }
            else
            {
                var filePath = Path.Combine(Environment.GetEnvironmentVariable("FilePath"), data.FileName);
                fileContent = await File.ReadAllTextAsync(filePath);
            }

            var selectedCategories = "";
            foreach (var option in data.Categories)
            {
                var category = option.Name.Replace(@"\", @"\\").Replace("|", @"\|");
                var pattern = $"(#EXTINF:-\\d+.*group-title=\"{category}\".*\\n.*\\n)";
                var matches = Regex.Matches(fileContent, pattern);

                if (matches.Count > 0)
                {
                    selectedCategories += string.Join("", matches.Select(m => m.Value));
                }
            }

            if (data.GenerateLink)
            {
                // Save new version of the file on storage
                var _containerName = "downloads";
                var _container = new BlobContainerClient(Environment.GetEnvironmentVariable("StorageConnectionString"), _containerName);
                var _blobClient = _container.GetBlobClient(data.FileName);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(selectedCategories));
                await _blobClient.UploadAsync(stream, true);

                // Generate SAS Link
                var sasBuilder = new BlobSasBuilder()
                {
                    BlobContainerName = _containerName,
                    BlobName = data.FileName,
                    Resource = "b",
                    ExpiresOn = DateTimeOffset.UtcNow.AddDays(10)
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read);
                var sasQuery = sasBuilder.ToSasQueryParameters(new StorageSharedKeyCredential(Environment.GetEnvironmentVariable("StorageAccountName"), Environment.GetEnvironmentVariable("StorageAccountKey")));
                var sasUri = new UriBuilder(_blobClient.Uri)
                {
                    Query = sasQuery.ToString()
                }
                .Uri;
            }

            return new OkObjectResult(selectedCategories);
        }
    }
}

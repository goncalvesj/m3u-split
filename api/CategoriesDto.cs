using System.Text.Json.Serialization;

namespace api;

public record CategoriesDto(string FileName, bool GenerateLink, List<Category> Categories);

[JsonSerializable(typeof(CategoriesDto))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class AppJsonContext : JsonSerializerContext;

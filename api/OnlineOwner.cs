namespace api;

/// <summary>The validated signed-in user that owns saved profiles and TV links. Only built from the validated token.</summary>
public readonly record struct OnlineOwner(Guid TenantId, Guid ObjectId)
{
    public static OnlineOwner From(OnlineIdentity identity) =>
        new(Guid.ParseExact(identity.TenantId, "D"), Guid.ParseExact(identity.ObjectId, "D"));

    public string BlobPrefix => $"{TenantId:D}/{ObjectId:D}/";
}

public static class OnlineIds
{
    /// <summary>Accepts only the canonical lowercase form, so every ID has exactly one storage path.</summary>
    public static bool TryParse(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty &&
        string.Equals(value, id.ToString("D"), StringComparison.Ordinal);
}

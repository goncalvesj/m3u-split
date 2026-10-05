using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Net.Http.Headers;
using System.Security.Claims;

namespace api;

public sealed class OnlineAccess
{
    private readonly OnlineAuthSettings? settings;
    private readonly BaseConfigurationManager? metadata;
    private readonly ILogger<OnlineAccess> logger;
    private readonly JsonWebTokenHandler tokens = new() { MapInboundClaims = false };

    public OnlineAccess(
        IConfiguration configuration,
        ILogger<OnlineAccess> logger,
        BaseConfigurationManager? metadata = null)
    {
        this.logger = logger;
        settings = OnlineAuthSettings.Read(configuration, out var error);
        if (settings is null)
        {
            logger.LogError("Online access is disabled: {ConfigurationError}", error);
            return;
        }

        this.metadata = metadata ?? new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{settings.Issuer}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public async Task<OnlineAccessResult> AuthorizeAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (settings is null || metadata is null)
            return new OnlineAccessDenied(503, "online_not_configured",
                "Online access is not configured on the server.");

        if (request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
            return new OnlineAccessDenied(401, "authentication_required",
                "A Microsoft API access token is required.");

        if (!tokens.CanReadToken(header.Parameter))
            return InvalidToken();

        try
        {
            // Surface initial discovery outages as unavailable, not as a successful or anonymous session.
            await metadata.GetBaseConfigurationAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Microsoft signing-key discovery is unavailable.");
            return new OnlineAccessDenied(503, "identity_unavailable",
                "Microsoft token validation is temporarily unavailable.");
        }

        var validation = await tokens.ValidateTokenAsync(header.Parameter, new TokenValidationParameters
        {
            ConfigurationManager = metadata,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            // Metadata refresh must never broaden the configured single-tenant issuer.
            IssuerValidator = (issuer, _, _) => issuer == settings.Issuer
                ? issuer
                : throw new SecurityTokenInvalidIssuerException("Unexpected token issuer."),
            ValidateAudience = true,
            ValidAudience = settings.ApiClientId.ToString("D"),
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            IncludeTokenOnFailedValidation = false,
        });

        if (!validation.IsValid)
        {
            logger.LogInformation("Online API token validation failed ({FailureType}).",
                validation.Exception?.GetType().Name);
            return InvalidToken();
        }

        var claims = validation.ClaimsIdentity;
        if (SingleClaim(claims, "ver") != "2.0" ||
            !ReadId(claims, "tid", out var tenantId) || tenantId != settings.TenantId ||
            !ReadId(claims, "oid", out var objectId))
            return InvalidToken();

        var scopes = SingleClaim(claims, "scp")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (!scopes.Contains(settings.RequiredScope, StringComparer.Ordinal) ||
            SingleClaim(claims, "idtyp") == "app")
            return new OnlineAccessDenied(403, "insufficient_scope",
                "The required delegated API permission was not granted.");

        if (!ReadId(claims, "azp", out var clientId) || clientId != settings.SpaClientId)
            return new OnlineAccessDenied(403, "client_not_allowed",
                "This application is not allowed to access the online API.");

        if (!settings.AllowedObjectIds.Contains(objectId))
            return new OnlineAccessDenied(403, "account_not_allowed",
                "This Microsoft account is signed in but is not allowed to use online playlists.");

        return new AuthorizedOnlineUser(new OnlineIdentity(
            tenantId.ToString("D"), objectId.ToString("D"),
            new DateTimeOffset(validation.SecurityToken.ValidTo, TimeSpan.Zero)));
    }

    private static OnlineAccessDenied InvalidToken() =>
        new(401, "invalid_token", "The API access token is invalid or expired. Sign in again.");

    private static string? SingleClaim(ClaimsIdentity identity, string name)
    {
        var matches = identity.FindAll(name).ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }

    private static bool ReadId(ClaimsIdentity identity, string name, out Guid id) =>
        Guid.TryParseExact(SingleClaim(identity, name), "D", out id) && id != Guid.Empty;
}

public sealed record OnlineIdentity(string TenantId, string ObjectId, DateTimeOffset ExpiresAt);
public abstract record OnlineAccessResult;
public sealed record AuthorizedOnlineUser(OnlineIdentity Identity) : OnlineAccessResult;
public sealed record OnlineAccessDenied(int StatusCode, string Code, string Message) : OnlineAccessResult;

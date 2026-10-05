using api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Api.Tests;

public sealed class OnlineAccessTests : IDisposable
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string ApiId = "22222222-2222-4222-8222-222222222222";
    private const string SpaId = "33333333-3333-4333-8333-333333333333";
    private const string AllowedId = "44444444-4444-4444-8444-44444444abcd";
    private const string OtherId = "55555555-5555-4555-8555-555555555555";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RSA otherRsa = RSA.Create(2048);

    private Dictionary<string, string?> Settings() => new()
    {
        ["OnlineAuth:TenantId"] = Tenant,
        ["OnlineAuth:ApiClientId"] = ApiId,
        ["OnlineAuth:SpaClientId"] = SpaId,
        ["OnlineAuth:RequiredScope"] = "access_as_user",
        ["OnlineAuth:AllowedObjectIds"] = AllowedId,
    };

    private RsaSecurityKey Key(RSA source) => new(source) { KeyId = "local-test-key" };

    private OnlineAccess Access(
        Dictionary<string, string?>? settings = null,
        BaseConfigurationManager? metadata = null)
    {
        var discovery = new OpenIdConnectConfiguration { Issuer = Issuer };
        discovery.SigningKeys.Add(Key(rsa));
        return new OnlineAccess(
            new ConfigurationBuilder().AddInMemoryCollection(settings ?? Settings()).Build(),
            NullLogger<OnlineAccess>.Instance,
            metadata ?? new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery));
    }

    private string Token(string variant = "valid")
    {
        var claims = new Dictionary<string, object>
        {
            ["tid"] = Tenant,
            ["oid"] = AllowedId,
            ["ver"] = "2.0",
            ["azp"] = SpaId,
            ["scp"] = "other_scope access_as_user",
            ["preferred_username"] = "display-name@example.invalid",
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ApiId,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(Key(rsa), SecurityAlgorithms.RsaSha256),
        };
        switch (variant)
        {
            case "malformed": return "not-a-token";
            case "signature": descriptor.SigningCredentials = new SigningCredentials(Key(otherRsa), SecurityAlgorithms.RsaSha256); break;
            case "unsigned": descriptor.SigningCredentials = null; break;
            case "algorithm": descriptor.SigningCredentials = new SigningCredentials(Key(rsa), SecurityAlgorithms.RsaSha512); break;
            case "issuer": descriptor.Issuer = $"https://login.microsoftonline.com/{OtherId}/v2.0"; break;
            case "id-token": descriptor.Audience = SpaId; claims.Remove("scp"); break;
            case "audience": descriptor.Audience = "https://graph.microsoft.com"; break;
            case "audience-slash": descriptor.Audience = $"{ApiId}/"; break;
            case "expired": descriptor.Expires = DateTime.UtcNow.AddMinutes(-1); break;
            case "future": descriptor.NotBefore = DateTime.UtcNow.AddMinutes(10); break;
            case "no-expiry": descriptor.Expires = null; break;
            case "wrong-tenant": claims["tid"] = OtherId; break;
            case "no-tenant": claims.Remove("tid"); break;
            case "duplicate-tenant": claims["tid"] = new[] { Tenant, OtherId }; break;
            case "no-object-id": claims.Remove("oid"); break;
            case "invalid-object-id": claims["oid"] = "display-name@example.invalid"; break;
            case "version": claims["ver"] = "1.0"; break;
            case "disallowed": claims["oid"] = OtherId; break;
            case "email-is-not-identity": claims["oid"] = OtherId; claims["email"] = AllowedId; claims["sub"] = AllowedId; break;
            case "no-scope": claims.Remove("scp"); break;
            case "scope-prefix": claims["scp"] = "access_as_user_extra"; break;
            case "scope-case": claims["scp"] = "ACCESS_AS_USER"; break;
            case "application-token": claims.Remove("scp"); claims["roles"] = new[] { "access_as_user" }; break;
            case "application-type": claims["idtyp"] = "app"; break;
            case "wrong-client": claims["azp"] = OtherId; break;
            case "no-client": claims.Remove("azp"); break;
            case "allowed-case": claims["oid"] = AllowedId.ToUpperInvariant(); break;
        }
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static HttpRequest Request(string? token)
    {
        var request = new DefaultHttpContext().Request;
        if (token is not null) request.Headers.Authorization = $"Bearer {token}";
        return request;
    }

    private static JsonElement Body(ObjectResult result) =>
        JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    public async Task Session_rejects_requests_without_a_bearer_token(string? authorization)
    {
        var request = Request(null);
        request.Headers.Authorization = authorization;
        request.Headers["x-ms-client-principal"] = "{\"userId\":\"pretend-user\"}";
        request.Headers.Origin = "https://goncalvesj.github.io";
        request.QueryString = new QueryString("?code=not-an-access-token");

        var response = Assert.IsType<ObjectResult>(await new OnlineSession(Access()).Run(request, default));

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("authentication_required", Body(response).GetProperty("error").GetString());
        Assert.Equal("Bearer", request.HttpContext.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("no-store", request.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Multiple_authorization_headers_are_rejected()
    {
        var request = Request(null);
        request.Headers.Authorization = new StringValues([$"Bearer {Token()}", $"Bearer {Token()}"]);
        var response = Assert.IsType<OnlineAccessDenied>(await Access().AuthorizeAsync(request, default));
        Assert.Equal(401, response.StatusCode);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("signature")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("audience-slash")]
    [InlineData("id-token")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("no-expiry")]
    [InlineData("wrong-tenant")]
    [InlineData("no-tenant")]
    [InlineData("duplicate-tenant")]
    [InlineData("no-object-id")]
    [InlineData("invalid-object-id")]
    [InlineData("version")]
    public async Task Invalid_API_tokens_are_rejected_by_the_session_endpoint(string variant)
    {
        var response = Assert.IsType<ObjectResult>(
            await new OnlineSession(Access()).Run(Request(Token(variant)), default));

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("invalid_token", Body(response).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("no-scope", "insufficient_scope")]
    [InlineData("scope-prefix", "insufficient_scope")]
    [InlineData("scope-case", "insufficient_scope")]
    [InlineData("application-token", "insufficient_scope")]
    [InlineData("application-type", "insufficient_scope")]
    [InlineData("wrong-client", "client_not_allowed")]
    [InlineData("no-client", "client_not_allowed")]
    [InlineData("disallowed", "account_not_allowed")]
    [InlineData("email-is-not-identity", "account_not_allowed")]
    public async Task Valid_but_unauthorized_tokens_get_a_clear_forbidden_response(string variant, string code)
    {
        var response = Assert.IsType<ObjectResult>(
            await new OnlineSession(Access()).Run(Request(Token(variant)), default));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(code, Body(response).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("allowed-case")]
    public async Task Allowed_users_receive_only_identity_and_token_expiry(string variant)
    {
        var request = Request(Token(variant));
        var response = Assert.IsType<OkObjectResult>(await new OnlineSession(Access()).Run(request, default));
        var body = Body(response);

        Assert.Equal(Tenant, body.GetProperty("tenantId").GetString());
        Assert.Equal(AllowedId, body.GetProperty("objectId").GetString());
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.Equal(3, body.EnumerateObject().Count());
        Assert.Equal("no-store", request.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("TenantId", null)]
    [InlineData("TenantId", "common")]
    [InlineData("TenantId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("ApiClientId", "")]
    [InlineData("ApiClientId", SpaId)]
    [InlineData("SpaClientId", "")]
    [InlineData("RequiredScope", "")]
    [InlineData("RequiredScope", "api://application/access_as_user")]
    [InlineData("RequiredScope", "access_as_user another_scope")]
    [InlineData("AllowedObjectIds", null)]
    [InlineData("AllowedObjectIds", "")]
    [InlineData("AllowedObjectIds", "someone@example.invalid")]
    [InlineData("AllowedObjectIds", AllowedId + ",invalid")]
    [InlineData("AllowedObjectIds", AllowedId + ",")]
    [InlineData("AllowedObjectIds", "00000000-0000-0000-0000-000000000000")]
    public async Task Missing_or_invalid_configuration_fails_closed(string setting, string? value)
    {
        var settings = Settings();
        settings[$"OnlineAuth:{setting}"] = value;
        var response = Assert.IsType<ObjectResult>(
            await new OnlineSession(Access(settings)).Run(Request(Token()), default));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("online_not_configured", Body(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Completely_absent_configuration_does_not_enable_guest_access()
    {
        var response = Assert.IsType<OnlineAccessDenied>(
            await Access([]).AuthorizeAsync(Request(null), default));
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task Multiple_explicitly_allowed_object_ids_are_supported()
    {
        var settings = Settings();
        settings["OnlineAuth:AllowedObjectIds"] = $"{OtherId}, {AllowedId}";
        Assert.IsType<AuthorizedOnlineUser>(
            await Access(settings).AuthorizeAsync(Request(Token("disallowed")), default));
    }

    [Fact]
    public async Task Discovery_cannot_broaden_the_expected_issuer()
    {
        var metadata = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{OtherId}/v2.0" };
        metadata.SigningKeys.Add(Key(rsa));
        var access = Access(metadata: new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));

        var denied = Assert.IsType<OnlineAccessDenied>(await access.AuthorizeAsync(Request(Token("issuer")), default));
        Assert.Equal(401, denied.StatusCode);
    }

    [Fact]
    public async Task Signing_key_discovery_failure_never_creates_a_session()
    {
        var response = Assert.IsType<OnlineAccessDenied>(
            await Access(metadata: new UnavailableMetadata()).AuthorizeAsync(Request(Token()), default));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("identity_unavailable", response.Code);
    }

    [Fact]
    public void Only_the_guarded_online_endpoint_is_anonymous_at_the_Function_host()
    {
        var onlineTrigger = typeof(OnlineSession).GetMethod("Run")!.GetParameters()[0]
            .GetCustomAttribute<HttpTriggerAttribute>()!;
        Assert.Equal(AuthorizationLevel.Anonymous, onlineTrigger.AuthLevel);
        Assert.Equal("online/session", onlineTrigger.Route);
        Assert.NotNull(onlineTrigger.Methods);
        Assert.Equal(["get"], onlineTrigger.Methods);

        foreach (var legacy in new[] { typeof(Upload), typeof(Download) })
        {
            var trigger = legacy.GetMethod("Run")!.GetParameters()[0].GetCustomAttribute<HttpTriggerAttribute>()!;
            Assert.Equal(AuthorizationLevel.Function, trigger.AuthLevel);
        }
    }

    private sealed class UnavailableMetadata : BaseConfigurationManager
    {
        public override Task<BaseConfiguration> GetBaseConfigurationAsync(CancellationToken cancel) =>
            throw new IOException("Simulated metadata outage.");

        public override void RequestRefresh() { }
    }

    public void Dispose()
    {
        rsa.Dispose();
        otherRsa.Dispose();
    }
}

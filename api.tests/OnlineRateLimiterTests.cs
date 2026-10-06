using System.Net;
using System.Reflection;
using System.Text.Json;
using api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Api.Tests;

public sealed class OnlineRateLimiterTests
{
    private const string Client = "203.0.113.10";

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static OnlineRateLimiter Limiter(Dictionary<string, string?>? values = null) =>
        new(Configuration(values), NullLogger<OnlineRateLimiter>.Instance);

    private static HttpContext Context(string? address = Client)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        return context;
    }

    private static string? ErrorCode(ObjectResult result) =>
        JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("error").GetString();

    [Fact]
    public void Settings_use_the_defaults_when_they_are_empty()
    {
        var settings = OnlineRateLimitSettings.Read(
            Configuration(new() { ["OnlineRateLimits:ApiRequestsPerMinute"] = " " }), out var error);

        Assert.Equal("", error);
        Assert.Equal(new OnlineRateLimitSettings(300, 30, 20, 1200), settings);
    }

    [Fact]
    public void Settings_read_values_in_range()
    {
        var settings = OnlineRateLimitSettings.Read(Configuration(new()
        {
            ["OnlineRateLimits:ApiRequestsPerMinute"] = "10",
            ["OnlineRateLimits:FeedRequestsPerMinute"] = " 600 ",
            ["OnlineRateLimits:FeedDownloadsPerHour"] = "1",
            ["OnlineRateLimits:InstanceRequestsPerMinute"] = "60000",
        }), out var error);

        Assert.Equal("", error);
        Assert.Equal(new OnlineRateLimitSettings(10, 600, 1, 60000), settings);
    }

    [Theory]
    [InlineData("ApiRequestsPerMinute", "9")]
    [InlineData("ApiRequestsPerMinute", "6001")]
    [InlineData("ApiRequestsPerMinute", "-5")]
    [InlineData("FeedRequestsPerMinute", "0")]
    [InlineData("FeedRequestsPerMinute", "601")]
    [InlineData("FeedRequestsPerMinute", "ten")]
    [InlineData("FeedDownloadsPerHour", "0")]
    [InlineData("FeedDownloadsPerHour", "1001")]
    [InlineData("FeedDownloadsPerHour", "1e3")]
    [InlineData("InstanceRequestsPerMinute", "59")]
    [InlineData("InstanceRequestsPerMinute", "60001")]
    public void Settings_reject_values_that_are_not_valid(string name, string value)
    {
        var settings = OnlineRateLimitSettings.Read(
            Configuration(new() { [$"OnlineRateLimits:{name}"] = value }), out var error);

        Assert.Null(settings);
        Assert.StartsWith($"OnlineRateLimits:{name} must be a whole number from ", error);
    }

    [Fact]
    public void Settings_that_are_not_valid_close_every_http_endpoint()
    {
        using var limiter = Limiter(new() { ["OnlineRateLimits:FeedDownloadsPerHour"] = "0" });
        var context = Context();

        Assert.False(limiter.IsConfigured);
        var result = Assert.IsType<ObjectResult>(limiter.Check(context, "OnlineSession"));
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("rate_limits_not_configured", ErrorCode(result));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());

        var feed = Context();
        Assert.Equal(503, Assert.IsType<ObjectResult>(limiter.Check(feed, OnlineRateLimiter.FeedFunctionName)).StatusCode);
        Assert.Equal("noindex, nofollow", feed.Response.Headers["X-Robots-Tag"].ToString());
        Assert.Equal(503, Assert.IsType<ObjectResult>(
            limiter.CheckFeedDownload(new DefaultHttpContext().Response, OnlineFeedService.NewToken())).StatusCode);
    }

    [Fact]
    public void Each_client_has_its_own_api_limit()
    {
        using var limiter = Limiter(new() { ["OnlineRateLimits:ApiRequestsPerMinute"] = "10" });
        for (var i = 0; i < 10; i++)
            Assert.Null(limiter.Check(Context(), "OnlineProfileList"));

        var context = Context();
        var result = Assert.IsType<ObjectResult>(limiter.Check(context, "OnlineSession"));
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("rate_limited", ErrorCode(result));
        Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());

        Assert.Null(limiter.Check(Context("203.0.113.11"), "OnlineProfileList"));
        Assert.Null(limiter.Check(Context(), OnlineRateLimiter.FeedFunctionName));
    }

    [Fact]
    public void Tv_links_have_their_own_client_limit_and_keep_their_headers()
    {
        using var limiter = Limiter(new() { ["OnlineRateLimits:FeedRequestsPerMinute"] = "2" });
        Assert.Null(limiter.Check(Context(), OnlineRateLimiter.FeedFunctionName));
        Assert.Null(limiter.Check(Context(), OnlineRateLimiter.FeedFunctionName));

        var context = Context();
        var result = Assert.IsType<ObjectResult>(limiter.Check(context, OnlineRateLimiter.FeedFunctionName));
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("rate_limited", ErrorCode(result));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("noindex, nofollow", context.Response.Headers["X-Robots-Tag"].ToString());

        Assert.Null(limiter.Check(Context(), "OnlineSession"));
    }

    [Fact]
    public void The_instance_limit_applies_to_all_clients_together()
    {
        using var limiter = Limiter(new() { ["OnlineRateLimits:InstanceRequestsPerMinute"] = "60" });
        for (var i = 0; i < 60; i++)
            Assert.Null(limiter.Check(Context($"198.51.100.{i}"), "OnlineSession"));

        var context = Context("192.0.2.1");
        var result = Assert.IsType<ObjectResult>(limiter.Check(context, OnlineRateLimiter.FeedFunctionName));
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("rate_limited", ErrorCode(result));
        Assert.Equal("60", context.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void A_client_over_its_limit_does_not_use_the_instance_limit()
    {
        using var limiter = Limiter(new()
        {
            ["OnlineRateLimits:ApiRequestsPerMinute"] = "10",
            ["OnlineRateLimits:InstanceRequestsPerMinute"] = "60",
        });
        for (var i = 0; i < 100; i++)
            limiter.Check(Context(), "OnlineSession");

        for (var i = 0; i < 50; i++)
            Assert.Null(limiter.Check(Context($"198.51.100.{i}"), "OnlineSession"));
        Assert.NotNull(limiter.Check(Context("192.0.2.1"), "OnlineSession"));
    }

    [Theory]
    [InlineData(Client, Client)]
    [InlineData("::ffff:203.0.113.10", Client)]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData(null, "unknown")]
    public void Client_keys_use_the_connection_address(string? address, string expected)
    {
        Assert.Equal(expected, OnlineRateLimiter.ClientKey(Context(address)));
    }

    [Fact]
    public void Client_keys_ignore_forwarded_headers()
    {
        var context = Context();
        context.Request.Headers["X-Forwarded-For"] = "192.0.2.99";
        context.Request.Headers["X-Real-IP"] = "192.0.2.99";

        Assert.Equal(Client, OnlineRateLimiter.ClientKey(context));
    }

    [Fact]
    public void Addresses_in_one_ipv6_64_share_a_limit()
    {
        using var limiter = Limiter(new() { ["OnlineRateLimits:FeedRequestsPerMinute"] = "1" });

        Assert.Null(limiter.Check(Context("2001:db8:1:2::1"), OnlineRateLimiter.FeedFunctionName));
        Assert.NotNull(limiter.Check(Context("2001:db8:1:2::2"), OnlineRateLimiter.FeedFunctionName));
        Assert.Null(limiter.Check(Context("2001:db8:1:3::1"), OnlineRateLimiter.FeedFunctionName));
    }

    [Fact]
    public void Each_tv_link_has_its_own_download_limit()
    {
        using var limiter = Limiter(new() { ["OnlineRateLimits:FeedDownloadsPerHour"] = "2" });
        var token = OnlineFeedService.NewToken();
        Assert.Null(limiter.CheckFeedDownload(new DefaultHttpContext().Response, token));
        Assert.Null(limiter.CheckFeedDownload(new DefaultHttpContext().Response, token));

        var response = new DefaultHttpContext().Response;
        var result = Assert.IsType<ObjectResult>(limiter.CheckFeedDownload(response, token));
        Assert.Equal(429, result.StatusCode);
        Assert.Equal("rate_limited", ErrorCode(result));
        Assert.Equal("3600", response.Headers.RetryAfter.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());

        Assert.Null(limiter.CheckFeedDownload(new DefaultHttpContext().Response, OnlineFeedService.NewToken()));
    }

    [Fact]
    public void The_limiter_knows_the_name_of_the_tv_link_function()
    {
        var function = typeof(OnlineFeedFunctions).GetMethod(nameof(OnlineFeedFunctions.Feed))!
            .GetCustomAttribute<FunctionAttribute>()!;

        Assert.Equal(OnlineRateLimiter.FeedFunctionName, function.Name);
    }
}

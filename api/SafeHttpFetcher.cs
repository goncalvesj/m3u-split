using System.Net;
using System.Net.Sockets;

namespace api;

/// <summary>Safe, non-sensitive failure codes for source downloads.</summary>
public static class SourceFetchError
{
    public const string InvalidUrl = "invalid_url";
    public const string BlockedDestination = "blocked_destination";
    public const string InvalidRedirect = "invalid_redirect";
    public const string TooManyRedirects = "too_many_redirects";
    public const string SourceUnreachable = "source_unreachable";
    public const string SourceHttpError = "source_http_error";
    public const string SourceTimeout = "source_timeout";
    public const string SourceTooLarge = "source_too_large";
}

/// <summary>A download failure that is safe to persist and return: it never carries the URL.</summary>
public sealed class SourceFetchException(string code, int? httpStatus = null)
    : Exception($"The source download failed ({code}).")
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

public sealed record SafeFetchOptions(long MaxBytes, TimeSpan OverallTimeout, TimeSpan IdleTimeout, string UserAgent);

/// <summary>Syntax rules for user-supplied source URLs and every redirect hop.</summary>
public static class SafeUrlPolicy
{
    public const int MaxUrlLength = 2048;
    public const int MaxRedirectUrlLength = 8192;

    public static bool TryParseSource(string? value, out Uri uri, out string errorCode)
    {
        uri = null!;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxUrlLength ||
            !Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            errorCode = SourceFetchError.InvalidUrl;
            return false;
        }

        var error = Validate(parsed, MaxUrlLength);
        if (error is not null)
        {
            errorCode = error;
            return false;
        }

        uri = parsed;
        errorCode = "";
        return true;
    }

    /// <summary>Returns null when the URL may be requested, otherwise a <see cref="SourceFetchError"/> code.</summary>
    public static string? Validate(Uri uri, int maxLength)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.AbsoluteUri.Length > maxLength ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            string.IsNullOrEmpty(uri.IdnHost))
            return SourceFetchError.InvalidUrl;

        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.');
        if (host.Length == 0)
            return SourceFetchError.InvalidUrl;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return SourceFetchError.BlockedDestination;

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 &&
            (!IPAddress.TryParse(host, out var literal) || !PublicAddressPolicy.IsPublic(literal)))
            return SourceFetchError.BlockedDestination;

        return null;
    }

    /// <summary>The only form of a source URL that may be shown or logged: scheme://host[:port].</summary>
    public static string Display(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }
}

/// <summary>
/// Downloads untrusted URLs without exposing internal networks. Every connection is pinned to an
/// address that was resolved and validated in the same step, redirects are followed manually with
/// the same checks, and size and time limits are enforced while the body is read.
/// </summary>
public sealed class SafeHttpFetcher : IDisposable
{
    private const int MaxRedirects = 5;
    private readonly SafeFetchOptions options;
    private readonly HttpClient client;

    public SafeHttpFetcher(SafeFetchOptions options)
    {
        this.options = options;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = options.IdleTimeout < TimeSpan.FromSeconds(15) ? options.IdleTimeout : TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ResponseDrainTimeout = TimeSpan.FromSeconds(2),
            MaxResponseDrainSize = 64 * 1024,
            MaxConnectionsPerServer = 4,
            // Do not send trace context headers to third-party servers.
            ActivityHeadersPropagator = null,
            ConnectCallback = ConnectToPublicAddressAsync,
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<SafeFetchResponse> FetchAsync(Uri source, CancellationToken cancellationToken) =>
        FetchAsync(source, null, cancellationToken);

    /// <param name="source">The URL to fetch. It is never logged or included in exceptions.</param>
    /// <param name="onRedirect">Called before each redirect is followed, for example to record a heartbeat.</param>
    /// <param name="cancellationToken">Cancels the whole fetch.</param>
    public async Task<SafeFetchResponse> FetchAsync(
        Uri source, Func<CancellationToken, Task>? onRedirect, CancellationToken cancellationToken)
    {
        var timeouts = new FetchTimeouts(options.OverallTimeout, options.IdleTimeout, cancellationToken);
        try
        {
            var current = source;
            for (var redirects = 0; ; redirects++)
            {
                var error = SafeUrlPolicy.Validate(
                    current, redirects == 0 ? SafeUrlPolicy.MaxUrlLength : SafeUrlPolicy.MaxRedirectUrlLength);
                if (error is not null)
                    throw new SourceFetchException(
                        redirects == 0 || error == SourceFetchError.BlockedDestination ? error : SourceFetchError.InvalidRedirect);

                var request = new HttpRequestMessage(HttpMethod.Get, current)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");

                var response = await timeouts.RunAsync(
                    token => new ValueTask<HttpResponseMessage>(
                        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)),
                    CancellationToken.None);

                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (redirects >= MaxRedirects)
                        throw new SourceFetchException(SourceFetchError.TooManyRedirects);
                    if (location is null || !Uri.TryCreate(current, location, out var next))
                        throw new SourceFetchException(SourceFetchError.InvalidRedirect);

                    // Fragments are never sent; drop them so they do not affect validation.
                    current = string.IsNullOrEmpty(next.Fragment) ? next : new Uri(next.GetLeftPart(UriPartial.Query));
                    if (onRedirect is not null)
                        await onRedirect(cancellationToken);
                    continue;
                }

                if (status is < 200 or > 299)
                {
                    response.Dispose();
                    throw new SourceFetchException(SourceFetchError.SourceHttpError, status);
                }

                // With automatic decompression this is only present for uncompressed bodies; the counted limit
                // below applies to the decompressed bytes in every case.
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength > options.MaxBytes)
                {
                    response.Dispose();
                    throw new SourceFetchException(SourceFetchError.SourceTooLarge);
                }

                Stream body;
                try
                {
                    body = await timeouts.RunAsync(
                        token => new ValueTask<Stream>(response.Content.ReadAsStreamAsync(token)),
                        CancellationToken.None);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }

                return new SafeFetchResponse(
                    response, new LimitedReadStream(body, timeouts, options.MaxBytes), contentLength, timeouts);
            }
        }
        catch
        {
            timeouts.Dispose();
            throw;
        }
    }

    public void Dispose() => client.Dispose();

    /// <summary>
    /// Resolves the host, rejects it if any address is not public, then connects only to those validated
    /// addresses. Resolution and connection happen together, so DNS rebinding cannot swap the destination.
    /// TLS (SNI and certificate host validation) is layered on top by the handler using the URL host name.
    /// </summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new SourceFetchException(SourceFetchError.SourceUnreachable);
    }

    private static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken cancellationToken)
    {
        host = host.Trim('[', ']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                throw new SourceFetchException(SourceFetchError.SourceUnreachable);
            }
            catch (ArgumentException)
            {
                throw new SourceFetchException(SourceFetchError.InvalidUrl);
            }
        }

        if (addresses.Length == 0)
            throw new SourceFetchException(SourceFetchError.SourceUnreachable);

        // Reject the host if ANY resolved address is not public, not just the one that would be used.
        if (addresses.Any(address => !PublicAddressPolicy.IsPublic(address)))
            throw new SourceFetchException(SourceFetchError.BlockedDestination);

        var normalized = addresses
            .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Distinct()
            .ToArray();

        // Prefer IPv4: outbound IPv6 is often unavailable on Functions workers.
        return
        [
            .. normalized.Where(address => address.AddressFamily == AddressFamily.InterNetwork),
            .. normalized.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6),
        ];
    }

    /// <summary>Overall and idle (time between bytes) limits that turn into source_timeout failures.</summary>
    private sealed class FetchTimeouts : IDisposable
    {
        private readonly CancellationToken caller;
        private readonly TimeSpan idleTimeout;
        private readonly CancellationTokenSource overall;
        private readonly CancellationTokenSource idle = new();
        private readonly CancellationTokenSource linked;

        public FetchTimeouts(TimeSpan overallTimeout, TimeSpan idleTimeout, CancellationToken caller)
        {
            this.caller = caller;
            this.idleTimeout = idleTimeout;
            overall = new CancellationTokenSource(overallTimeout);
            linked = CancellationTokenSource.CreateLinkedTokenSource(caller, overall.Token, idle.Token);
        }

        public async ValueTask<T> RunAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken extra)
        {
            // The caller's token is already linked; only link a different per-read token.
            using var combined = extra.CanBeCanceled && extra != caller
                ? CancellationTokenSource.CreateLinkedTokenSource(linked.Token, extra)
                : null;
            idle.CancelAfter(idleTimeout);
            try
            {
                return await operation(combined?.Token ?? linked.Token);
            }
            catch (Exception exception) when (exception is not SourceFetchException)
            {
                if (caller.IsCancellationRequested || extra.IsCancellationRequested)
                    throw;
                throw Translate(exception, linked.IsCancellationRequested);
            }
            finally
            {
                // Time spent by the consumer between reads (for example writing to storage) is not idle time.
                if (!idle.IsCancellationRequested)
                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        private static SourceFetchException Translate(Exception exception, bool timedOut)
        {
            for (var inner = exception; inner is not null; inner = inner.InnerException)
            {
                if (inner is SourceFetchException fetch)
                    return fetch;
            }

            if (timedOut || exception is OperationCanceledException or TimeoutException)
                return new SourceFetchException(SourceFetchError.SourceTimeout);

            return new SourceFetchException(SourceFetchError.SourceUnreachable);
        }

        public void Dispose()
        {
            linked.Dispose();
            idle.Dispose();
            overall.Dispose();
        }
    }

    /// <summary>Counts decompressed bytes, enforces the size limit and applies the idle and overall timeouts.</summary>
    private sealed class LimitedReadStream(Stream inner, FetchTimeouts timeouts, long maxBytes) : Stream
    {
        private long total;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => total;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await timeouts.RunAsync(token => inner.ReadAsync(buffer, token), cancellationToken);
            total += read;
            if (total > maxBytes)
                throw new SourceFetchException(SourceFetchError.SourceTooLarge);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // Synchronous reads would block a worker thread on network I/O; consumers must read asynchronously.
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

/// <summary>A successful (2xx) response whose body enforces the fetch limits while it is read.</summary>
public sealed class SafeFetchResponse : IAsyncDisposable
{
    private readonly HttpResponseMessage response;
    private readonly IDisposable timeouts;

    internal SafeFetchResponse(HttpResponseMessage response, Stream content, long? contentLength, IDisposable timeouts)
    {
        this.response = response;
        this.timeouts = timeouts;
        Content = content;
        ContentLength = contentLength;
    }

    /// <summary>The response body. Read it asynchronously; reads throw <see cref="SourceFetchException"/> on limits.</summary>
    public Stream Content { get; }

    /// <summary>The declared length of an uncompressed body, when the server sent one.</summary>
    public long? ContentLength { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        response.Dispose();
        timeouts.Dispose();
    }
}

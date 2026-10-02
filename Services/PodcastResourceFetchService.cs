using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Authenticated podcast-resource acquisition with an SSRF-resistant transport.
/// It intentionally supports only bounded GETs for known podcast resource kinds.
/// No caller-provided target headers, credentials, cookies, or proxy settings cross
/// this boundary.
/// </summary>
public static class PodcastResourceFetchService
{
    public const int MaxRedirects = 5;
    public const int MaxUrlLength = 4096;
    public const int MaxFeedBytes = 4 * 1024 * 1024;
    public const int MaxChaptersBytes = 1024 * 1024;
    public const int MaxTranscriptBytes = 2 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "feed",
        "chapters",
        "transcript",
    };

    private static readonly SocketsHttpHandler Handler = new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
        MaxResponseHeadersLength = 32,
        UseCookies = false,
        UseProxy = false,
        ActivityHeadersPropagator = null,
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    private static readonly HttpClient Http = new(Handler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public sealed class FetchException : Exception
    {
        public FetchException(string code, HttpStatusCode responseStatus, string message, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            ResponseStatus = responseStatus;
        }

        public string Code { get; }

        public HttpStatusCode ResponseStatus { get; }
    }

    /// <summary>Returns the raw-byte ceiling for a supported resource kind.</summary>
    public static int MaxBytesForKind(string kind)
        => NormalizeKind(kind) switch
        {
            "feed" => MaxFeedBytes,
            "chapters" => MaxChaptersBytes,
            "transcript" => MaxTranscriptBytes,
            _ => 0,
        };

    /// <summary>
    /// Validates URI syntax before transport. Address classification is repeated at
    /// connect time so DNS rebinding cannot turn an approved host into a private hop.
    /// </summary>
    public static bool TryValidateTargetUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUrlLength)
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (candidate.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(candidate.DnsSafeHost) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        var host = candidate.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var literal) && !IsPublicAddress(literal))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    /// <summary>Resolves and validates one redirect target without following it.</summary>
    public static bool TryResolveRedirect(Uri current, string? location, out Uri? next)
    {
        next = null;
        if (string.IsNullOrWhiteSpace(location) || location.Length > MaxUrlLength)
        {
            return false;
        }

        if (!Uri.TryCreate(current, location, out var candidate) ||
            !TryValidateTargetUri(candidate.AbsoluteUri, out var validated) ||
            validated is null)
        {
            return false;
        }

        // Do not silently downgrade confidentiality when a feed/resource begins on TLS.
        if (current.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            validated.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        next = validated;
        return true;
    }

    /// <summary>
    /// Public means globally routable for this outbound-fetch boundary. Private,
    /// loopback, link-local, carrier-NAT, documentation, benchmark, multicast,
    /// unspecified, and other non-global special ranges are rejected.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsPublicIpv4(address);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicIpv4(address.MapToIPv4());
        }

        if (address.ScopeId != 0 ||
            IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.All(static value => value == 0) ||
            (bytes[0] & 0xFE) == 0xFC || // fc00::/7 ULA
            (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) || // documentation
            (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) || // Teredo
            (bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xF0) is 0x10 or 0x20)) // ORCHID/ORCHIDv2
        {
            return false;
        }

        // Deprecated IPv4-compatible ::/96 addresses are not accepted.
        if (bytes.Take(12).All(static value => value == 0))
        {
            return false;
        }

        // 64:ff9b::/96 well-known NAT64: classify by the embedded IPv4 address.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B &&
            bytes.Skip(4).Take(8).All(static value => value == 0))
        {
            return IsPublicIpv4(new IPAddress(bytes.AsSpan(12, 4)));
        }

        // 2002::/16 6to4: classify by the embedded IPv4 gateway.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return IsPublicIpv4(new IPAddress(bytes.AsSpan(2, 4)));
        }

        return true;
    }

    /// <summary>Fetches a supported podcast resource through the hardened transport.</summary>
    public static async Task<PodcastResourceFetchResponse> FetchAsync(
        PodcastResourceFetchRequest request,
        CancellationToken cancellationToken)
    {
        var kind = NormalizeKind(request.Kind);
        var maxBytes = MaxBytesForKind(kind);
        if (maxBytes <= 0 || !AllowedKinds.Contains(kind))
        {
            throw new FetchException("unsupported-kind", HttpStatusCode.BadRequest, "Unsupported podcast resource kind.");
        }

        if (!TryValidateTargetUri(request.Url, out var current) || current is null)
        {
            throw new FetchException("unsafe-target", HttpStatusCode.BadRequest, "Podcast resource URL is not allowed.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        var token = timeoutCts.Token;

        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            using var message = BuildRequest(current, kind, request, includeValidators: redirectCount == 0);
            HttpResponseMessage response;
            try
            {
                response = await SendSafeRequestAsync(message, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FetchException("timeout", HttpStatusCode.GatewayTimeout, "Podcast resource fetch timed out.", ex);
            }
            catch (HttpRequestException ex) when (FindPolicyException(ex) is { } policy)
            {
                throw policy;
            }
            catch (HttpRequestException ex)
            {
                throw new FetchException("upstream-unreachable", HttpStatusCode.BadGateway, "Podcast resource host could not be reached.", ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return new PodcastResourceFetchResponse(
                        Status: "not-modified",
                        FinalUrl: current.AbsoluteUri,
                        ContentType: response.Content.Headers.ContentType?.ToString(),
                        BodyBase64: null,
                        LengthBytes: 0,
                        ETag: response.Headers.ETag?.ToString(),
                        LastModified: FormatLastModified(response.Content.Headers.LastModified));
                }

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= MaxRedirects)
                    {
                        throw new FetchException("redirect-limit", HttpStatusCode.BadGateway, "Podcast resource exceeded the redirect limit.");
                    }

                    var location = response.Headers.Location?.ToString();
                    if (!TryResolveRedirect(current, location, out var next) || next is null)
                    {
                        throw new FetchException("unsafe-redirect", HttpStatusCode.BadGateway, "Podcast resource redirect target is not allowed.");
                    }

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new FetchException(
                        "upstream-http",
                        HttpStatusCode.BadGateway,
                        $"Podcast resource upstream returned HTTP {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
                {
                    throw new FetchException("response-too-large", HttpStatusCode.RequestEntityTooLarge, "Podcast resource exceeds its size limit.");
                }

                var body = await ReadBoundedBodyAsync(response.Content, maxBytes, token).ConfigureAwait(false);
                return new PodcastResourceFetchResponse(
                    Status: "updated",
                    FinalUrl: current.AbsoluteUri,
                    ContentType: response.Content.Headers.ContentType?.ToString(),
                    BodyBase64: Convert.ToBase64String(body),
                    LengthBytes: body.LongLength,
                    ETag: response.Headers.ETag?.ToString(),
                    LastModified: FormatLastModified(response.Content.Headers.LastModified));
            }
        }

        throw new FetchException("redirect-limit", HttpStatusCode.BadGateway, "Podcast resource exceeded the redirect limit.");
    }

    /// <summary>
    /// Sends one already-validated outbound request through the shared connection-time
    /// public-address guard. Callers remain responsible for redirect policy.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendSafeRequestAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (FindPolicyException(ex) is { } policy)
        {
            throw policy;
        }
    }

    private static HttpRequestMessage BuildRequest(
        Uri uri,
        string kind,
        PodcastResourceFetchRequest request,
        bool includeValidators)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.UserAgent.ParseAdd("Slipmat-AudioGateway/1.0");
        message.Headers.TryAddWithoutValidation("Accept", AcceptHeaderForKind(kind));

        if (includeValidators && !string.IsNullOrWhiteSpace(request.ETag))
        {
            if (!EntityTagHeaderValue.TryParse(request.ETag, out var etag))
            {
                message.Dispose();
                throw new FetchException("invalid-validator", HttpStatusCode.BadRequest, "Invalid ETag validator.");
            }

            message.Headers.IfNoneMatch.Add(etag);
        }

        if (includeValidators && !string.IsNullOrWhiteSpace(request.LastModified))
        {
            if (!DateTimeOffset.TryParse(
                    request.LastModified,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var modified))
            {
                message.Dispose();
                throw new FetchException("invalid-validator", HttpStatusCode.BadRequest, "Invalid Last-Modified validator.");
            }

            message.Headers.IfModifiedSince = modified;
        }

        return message;
    }

    private static string AcceptHeaderForKind(string kind)
        => kind switch
        {
            "feed" => "application/rss+xml, application/xml;q=0.9, text/xml;q=0.9, application/atom+xml;q=0.8, text/plain;q=0.5, */*;q=0.1",
            "chapters" => "application/json, application/json+chapters;q=0.9, text/json;q=0.8, text/plain;q=0.5, */*;q=0.1",
            "transcript" => "text/vtt, application/x-subrip;q=0.9, application/srt;q=0.9, text/srt;q=0.9, text/plain;q=0.8, text/html;q=0.5, application/json;q=0.4, */*;q=0.1",
            _ => "*/*;q=0.1",
        };

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new FetchException("response-too-large", HttpStatusCode.RequestEntityTooLarge, "Podcast resource exceeds its size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new FetchException("dns-failure", HttpStatusCode.BadGateway, "Podcast resource DNS resolution failed.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(static address => !IsPublicAddress(address)))
        {
            throw new FetchException("non-public-address", HttpStatusCode.BadRequest, "Podcast resource host must resolve exclusively to public addresses.");
        }

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                lastFailure = ex;
                socket.Dispose();
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("Unable to connect to podcast resource host.", lastFailure);
    }

    private static FetchException? FindPolicyException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is FetchException policy)
            {
                return policy;
            }
        }

        return null;
    }

    private static bool IsPublicIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return !InIpv4Range(value, 0x00000000, 8) && // 0.0.0.0/8
            !InIpv4Range(value, 0x0A000000, 8) && // 10.0.0.0/8
            !InIpv4Range(value, 0x64400000, 10) && // 100.64.0.0/10 CGNAT
            !InIpv4Range(value, 0x7F000000, 8) && // 127.0.0.0/8
            !InIpv4Range(value, 0xA9FE0000, 16) && // 169.254.0.0/16
            !InIpv4Range(value, 0xAC100000, 12) && // 172.16.0.0/12
            !InIpv4Range(value, 0xC0000000, 24) && // 192.0.0.0/24 protocol assignments
            !InIpv4Range(value, 0xC0000200, 24) && // 192.0.2.0/24 documentation
            !InIpv4Range(value, 0xC0A80000, 16) && // 192.168.0.0/16
            !InIpv4Range(value, 0xC6120000, 15) && // 198.18.0.0/15 benchmark
            !InIpv4Range(value, 0xC6336400, 24) && // 198.51.100.0/24 documentation
            !InIpv4Range(value, 0xCB007100, 24) && // 203.0.113.0/24 documentation
            !InIpv4Range(value, 0xE0000000, 4) && // multicast
            !InIpv4Range(value, 0xF0000000, 4); // reserved/broadcast
    }

    private static bool InIpv4Range(uint address, uint network, int prefixLength)
    {
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (address & mask) == (network & mask);
    }

    private static string NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? FormatLastModified(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
}

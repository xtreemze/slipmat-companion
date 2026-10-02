using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Bounded raw-JSON fetch adapter for public media directories that the Slipmat
/// client already understands. The allowlist is deliberately provider-specific:
/// callers cannot turn this endpoint into a generic authenticated proxy.
/// </summary>
public static class DirectoryResourceFetchService
{
    public const int ContractVersion = 1;
    public const string GpodderProviderId = "gpodder.net";
    public const string RadioBrowserProviderId = "radio-browser";
    public const string IptvOrgProviderId = "iptv-org";

    private const int MaxGpodderBytes = 2 * 1024 * 1024;
    private const int MaxRadioBrowserBytes = 4 * 1024 * 1024;
    private const int MaxIptvOrgBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> IptvOrgPaths = new(StringComparer.Ordinal)
    {
        "/api/channels.json",
        "/api/feeds.json",
        "/api/logos.json",
        "/api/streams.json",
        "/api/guides.json",
    };

    private static readonly HashSet<string> RadioSearchQueryKeys = new(StringComparer.Ordinal)
    {
        "name",
        "hidebroken",
        "limit",
        "order",
        "reverse",
        "countrycode",
        "language",
        "tag",
    };

    public sealed class FetchException : Exception
    {
        public FetchException(
            string code,
            HttpStatusCode responseStatus,
            string message,
            Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            ResponseStatus = responseStatus;
        }

        public string Code { get; }

        public HttpStatusCode ResponseStatus { get; }
    }

    /// <summary>
    /// Validates one requested provider URL and returns its response byte ceiling.
    /// This is public so the allowlist contract can be pinned without live network I/O.
    /// </summary>
    public static bool TryValidateDirectoryTarget(
        string? providerId,
        string? url,
        out Uri? target,
        out int maxBytes)
    {
        target = null;
        maxBytes = 0;

        var normalizedProviderId = providerId?.Trim();
        if (!PodcastResourceFetchService.TryValidateTargetUri(url, out var candidate)
            || candidate is null
            || !candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var allowed = normalizedProviderId switch
        {
            GpodderProviderId => IsAllowedGpodder(candidate),
            RadioBrowserProviderId => IsAllowedRadioBrowser(candidate),
            IptvOrgProviderId => IsAllowedIptvOrg(candidate),
            _ => false,
        };

        if (!allowed)
        {
            return false;
        }

        maxBytes = normalizedProviderId switch
        {
            GpodderProviderId => MaxGpodderBytes,
            RadioBrowserProviderId => MaxRadioBrowserBytes,
            IptvOrgProviderId => MaxIptvOrgBytes,
            _ => 0,
        };
        target = candidate;
        return maxBytes > 0;
    }

    /// <summary>Fetch one allowlisted JSON resource through the shared SSRF-hardened transport.</summary>
    public static async Task<DirectoryResourceFetchResponse> FetchAsync(
        DirectoryResourceFetchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Version != ContractVersion
            || !TryValidateDirectoryTarget(request.ProviderId, request.Url, out var current, out var maxBytes)
            || current is null)
        {
            throw new FetchException(
                "invalid-directory-target",
                HttpStatusCode.BadRequest,
                "Directory provider or URL is not allowed.");
        }

        var providerId = request.ProviderId.Trim();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        var token = timeoutCts.Token;

        for (var redirectCount = 0; redirectCount <= PodcastResourceFetchService.MaxRedirects; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.UserAgent.ParseAdd("Slipmat-AudioGateway/1.0");
            message.Headers.TryAddWithoutValidation("Accept", "application/json, text/json;q=0.9, */*;q=0.1");

            HttpResponseMessage response;
            try
            {
                response = await PodcastResourceFetchService.SendSafeRequestAsync(message, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FetchException(
                    "timeout",
                    HttpStatusCode.GatewayTimeout,
                    "Directory resource fetch timed out.",
                    ex);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PodcastResourceFetchService.FetchException ex)
            {
                throw new FetchException(
                    ex.Code,
                    ex.ResponseStatus,
                    "Directory resource transport policy rejected the request.",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                throw new FetchException(
                    "upstream-unreachable",
                    HttpStatusCode.BadGateway,
                    "Directory resource host could not be reached.",
                    ex);
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= PodcastResourceFetchService.MaxRedirects)
                    {
                        throw new FetchException(
                            "redirect-limit",
                            HttpStatusCode.BadGateway,
                            "Directory resource exceeded the redirect limit.");
                    }

                    var location = response.Headers.Location?.ToString();
                    if (!PodcastResourceFetchService.TryResolveRedirect(current, location, out var next)
                        || next is null
                        || !TryValidateDirectoryTarget(providerId, next.AbsoluteUri, out var validatedNext, out var nextMaxBytes)
                        || validatedNext is null)
                    {
                        throw new FetchException(
                            "unsafe-redirect",
                            HttpStatusCode.BadGateway,
                            "Directory resource redirect target is not allowed.");
                    }

                    current = validatedNext;
                    maxBytes = nextMaxBytes;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new FetchException(
                        "upstream-http",
                        HttpStatusCode.BadGateway,
                        $"Directory resource upstream returned HTTP {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength is long declaredLength
                    && declaredLength > maxBytes)
                {
                    throw new FetchException(
                        "response-too-large",
                        HttpStatusCode.RequestEntityTooLarge,
                        "Directory resource exceeds its size limit.");
                }

                var body = await ReadBoundedBodyAsync(response.Content, maxBytes, token)
                    .ConfigureAwait(false);
                return new DirectoryResourceFetchResponse(
                    Version: ContractVersion,
                    ProviderId: providerId,
                    FinalUrl: current.AbsoluteUri,
                    ContentType: response.Content.Headers.ContentType?.ToString(),
                    BodyBase64: Convert.ToBase64String(body),
                    LengthBytes: body.LongLength);
            }
        }

        throw new FetchException(
            "redirect-limit",
            HttpStatusCode.BadGateway,
            "Directory resource exceeded the redirect limit.");
    }

    private static bool IsAllowedGpodder(Uri target)
    {
        if (!target.DnsSafeHost.Equals("gpodder.net", StringComparison.OrdinalIgnoreCase)
            || !target.AbsolutePath.Equals("/search.json", StringComparison.Ordinal))
        {
            return false;
        }

        var query = ParseQuery(target.Query);
        if (query is null || !HasOnlyKeys(query, "q", "scale_logo"))
        {
            return false;
        }

        if (!query.TryGetValue("q", out var search)
            || search.Length is < 2 or > 160)
        {
            return false;
        }

        return query.TryGetValue("scale_logo", out var scale)
            && scale.Equals("256", StringComparison.Ordinal);
    }

    private static bool IsAllowedRadioBrowser(Uri target)
    {
        var host = target.DnsSafeHost;
        if (!host.Equals("all.api.radio-browser.info", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".api.radio-browser.info", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(target.Query);
        if (query is null)
        {
            return false;
        }

        if (target.AbsolutePath.Equals("/json/servers", StringComparison.Ordinal))
        {
            return query.Count == 0;
        }

        if (target.AbsolutePath.Equals("/json/stations/search", StringComparison.Ordinal))
        {
            if (!HasOnlyKeys(query, RadioSearchQueryKeys))
            {
                return false;
            }

            if (!query.TryGetValue("name", out var name)
                || name.Length is < 2 or > 160)
            {
                return false;
            }

            if (!query.TryGetValue("limit", out var limitValue)
                || !int.TryParse(limitValue, out var limit)
                || limit is < 1 or > 50
                || !HasExactValue(query, "hidebroken", "true")
                || !HasExactValue(query, "order", "votes")
                || !HasExactValue(query, "reverse", "true"))
            {
                return false;
            }

            return BoundedOptional(query, "countrycode", 8)
                && BoundedOptional(query, "language", 64)
                && BoundedOptional(query, "tag", 64);
        }

        const string topVotePrefix = "/json/stations/topvote/";
        if (!target.AbsolutePath.StartsWith(topVotePrefix, StringComparison.Ordinal)
            || !int.TryParse(target.AbsolutePath[topVotePrefix.Length..], out var topVoteLimit)
            || topVoteLimit is < 1 or > 50)
        {
            return false;
        }

        return HasOnlyKeys(query, "hidebroken")
            && HasExactValue(query, "hidebroken", "true");
    }

    private static bool IsAllowedIptvOrg(Uri target)
        => target.DnsSafeHost.Equals("iptv-org.github.io", StringComparison.OrdinalIgnoreCase)
            && IptvOrgPaths.Contains(target.AbsolutePath)
            && string.IsNullOrEmpty(target.Query);

    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = query.TrimStart('?');
        if (normalized.Length == 0)
        {
            return result;
        }

        foreach (var part in normalized.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(pieces[0].Replace("+", "%20", StringComparison.Ordinal));
                value = pieces.Length == 2
                    ? Uri.UnescapeDataString(pieces[1].Replace("+", "%20", StringComparison.Ordinal))
                    : string.Empty;
            }
            catch (UriFormatException)
            {
                return null;
            }

            if (key.Length == 0 || result.ContainsKey(key))
            {
                return null;
            }

            result[key] = value;
        }

        return result;
    }

    private static bool HasOnlyKeys(
        IReadOnlyDictionary<string, string> query,
        params string[] allowedKeys)
        => HasOnlyKeys(query, new HashSet<string>(allowedKeys, StringComparer.Ordinal));

    private static bool HasOnlyKeys(
        IReadOnlyDictionary<string, string> query,
        IReadOnlySet<string> allowedKeys)
    {
        foreach (var key in query.Keys)
        {
            if (!allowedKeys.Contains(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExactValue(
        IReadOnlyDictionary<string, string> query,
        string key,
        string expected)
        => query.TryGetValue(key, out var value)
            && value.Equals(expected, StringComparison.Ordinal);

    private static bool BoundedOptional(
        IReadOnlyDictionary<string, string> query,
        string key,
        int maxLength)
        => !query.TryGetValue(key, out var value)
            || value.Length <= maxLength;

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new MemoryStream(capacity: Math.Min(maxBytes, 1024 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > maxBytes)
            {
                throw new FetchException(
                    "response-too-large",
                    HttpStatusCode.RequestEntityTooLarge,
                    "Directory resource exceeds its size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}

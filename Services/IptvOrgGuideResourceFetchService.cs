using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Bounded XMLTV acquisition for guide URLs that are explicitly published by
/// the current iptv-org catalog. The caller cannot choose an arbitrary proxy
/// destination: the initial URL must exactly match normalized provider evidence.
/// </summary>
public static class IptvOrgGuideResourceFetchService
{
    public const int ContractVersion = 1;
    public const int MaxXmlTvBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

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

    public static async Task<IptvOrgGuideResourceFetchResponse> FetchAsync(
        IptvOrgCatalogService catalog,
        IptvOrgGuideResourceFetchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Version != ContractVersion ||
            !PodcastResourceFetchService.TryValidateTargetUri(request.Url, out var initial) ||
            initial is null)
        {
            throw new FetchException(
                "invalid-guide-target",
                HttpStatusCode.BadRequest,
                "Guide request is not valid.");
        }

        var normalizedInitialUrl = initial.AbsoluteUri;
        var snapshot = await catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!IsListedGuideTarget(snapshot, normalizedInitialUrl))
        {
            var refreshed = await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
            snapshot = refreshed.Snapshot;
            if (!IsListedGuideTarget(snapshot, normalizedInitialUrl))
            {
                throw new FetchException(
                    "unlisted-guide-target",
                    HttpStatusCode.BadRequest,
                    "Guide URL is not published by the current iptv-org catalog.");
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        var token = timeoutCts.Token;
        var current = initial;

        for (var redirectCount = 0; redirectCount <= PodcastResourceFetchService.MaxRedirects; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.UserAgent.ParseAdd("Slipmat-AudioGateway/1.0");
            message.Headers.TryAddWithoutValidation(
                "Accept",
                "application/xml,text/xml;q=0.9,application/gzip;q=0.7,*/*;q=0.1");

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
                    "Guide resource fetch timed out.",
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
                    "Guide resource transport policy rejected the request.",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                throw new FetchException(
                    "upstream-unreachable",
                    HttpStatusCode.BadGateway,
                    "Guide resource host could not be reached.",
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
                            "Guide resource exceeded the redirect limit.");
                    }

                    var location = response.Headers.Location?.ToString();
                    if (!PodcastResourceFetchService.TryResolveRedirect(current, location, out var next) ||
                        next is null)
                    {
                        throw new FetchException(
                            "unsafe-redirect",
                            HttpStatusCode.BadGateway,
                            "Guide resource redirect target is not allowed.");
                    }

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new FetchException(
                        "upstream-http",
                        HttpStatusCode.BadGateway,
                        $"Guide resource upstream returned HTTP {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength is long declaredLength &&
                    declaredLength > MaxXmlTvBytes)
                {
                    throw new FetchException(
                        "response-too-large",
                        HttpStatusCode.RequestEntityTooLarge,
                        "Guide resource exceeds its size limit.");
                }

                var body = await ReadBoundedBodyAsync(response.Content, token).ConfigureAwait(false);
                return new IptvOrgGuideResourceFetchResponse(
                    Version: ContractVersion,
                    FinalUrl: current.AbsoluteUri,
                    ContentType: response.Content.Headers.ContentType?.ToString(),
                    BodyBase64: Convert.ToBase64String(body),
                    LengthBytes: body.LongLength);
            }
        }

        throw new FetchException(
            "redirect-limit",
            HttpStatusCode.BadGateway,
            "Guide resource exceeded the redirect limit.");
    }

    /// <summary>Returns whether a validated URL is explicitly present in provider guide evidence.</summary>
    public static bool IsListedGuideTarget(IptvOrgCatalogSnapshot snapshot, string? url)
    {
        if (!PodcastResourceFetchService.TryValidateTargetUri(url, out var parsed) || parsed is null)
        {
            return false;
        }

        return snapshot.GuideSourceUrls.Contains(parsed.AbsoluteUri);
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new MemoryStream(capacity: 1024 * 1024);
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaxXmlTvBytes)
            {
                throw new FetchException(
                    "response-too-large",
                    HttpStatusCode.RequestEntityTooLarge,
                    "Guide resource exceeds its size limit.");
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

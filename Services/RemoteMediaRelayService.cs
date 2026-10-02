using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Bounded remote-media relay ticket authority.
///
/// This is an execution adapter, not playback or media identity. Upstream locators
/// never leave the authenticated prepare boundary. Tickets are opaque, short-lived,
/// memory-only capabilities; losing the companion simply returns playback to the
/// client's direct-source fallback path.
/// </summary>
public static class RemoteMediaRelayService
{
    public const string PodcastEnclosureKind = "podcast-enclosure";
    public const string RadioStreamKind = "radio-stream";
    public const int MaxTickets = 1024;
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan ResponseHeaderTimeout = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<string, RelayTicket> Tickets =
        new(StringComparer.Ordinal);
    private static readonly object PrepareGate = new();

    public sealed record RelayTicket(
        string RelayId,
        Uri Target,
        string Kind,
        DateTimeOffset ExpiresAt);

    public sealed class RelayException : Exception
    {
        public RelayException(string code, HttpStatusCode responseStatus, string message, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            ResponseStatus = responseStatus;
        }

        public string Code { get; }

        public HttpStatusCode ResponseStatus { get; }
    }

    /// <summary>Prepare one opaque relay ticket after applying the hardened public-URL policy.</summary>
    public static RemoteMediaRelayPrepareResponse Prepare(
        RemoteMediaRelayPrepareRequest request,
        DateTimeOffset? now = null)
    {
        var kind = NormalizeKind(request.Kind);
        if (!IsSupportedKind(kind))
        {
            throw new RelayException("unsupported-kind", HttpStatusCode.BadRequest, "Unsupported remote media relay kind.");
        }

        if (!PodcastResourceFetchService.TryValidateTargetUri(request.Url, out var target) || target is null)
        {
            throw new RelayException("unsafe-target", HttpStatusCode.BadRequest, "Remote media URL is not allowed.");
        }

        var observedNow = now ?? DateTimeOffset.UtcNow;
        lock (PrepareGate)
        {
            CleanupExpired(observedNow);
            TrimToCapacity();

            var relayId = CreateRelayId();
            var expiresAt = observedNow.Add(TicketLifetime);
            Tickets[relayId] = new RelayTicket(relayId, target, kind, expiresAt);

            return new RemoteMediaRelayPrepareResponse(
                RelayId: relayId,
                MediaPath: $"/Plugins/AudioGateway/media-relay/{relayId}",
                ExpiresAtMs: expiresAt.ToUnixTimeMilliseconds(),
                SampleAccess: "cors-readable");
        }
    }

    /// <summary>
    /// Open the current upstream response for a prepared ticket. Redirects are
    /// manually revalidated and every connection is checked by the same DNS/IP
    /// policy as bounded podcast-resource acquisition.
    /// </summary>
    public static async Task<HttpResponseMessage> OpenAsync(
        string relayId,
        RangeHeaderValue? range,
        DateTimeOffset? now,
        CancellationToken cancellationToken)
    {
        var observedNow = now ?? DateTimeOffset.UtcNow;
        CleanupExpired(observedNow);

        if (!IsValidRelayId(relayId) ||
            !Tickets.TryGetValue(relayId, out var ticket) ||
            ticket.ExpiresAt <= observedNow)
        {
            Tickets.TryRemove(relayId, out _);
            throw new RelayException("relay-expired", HttpStatusCode.NotFound, "Remote media relay is unavailable.");
        }

        var current = ticket.Target;
        for (var redirectCount = 0; redirectCount <= PodcastResourceFetchService.MaxRedirects; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.UserAgent.ParseAdd("Slipmat-AudioGateway/1.0");
            message.Headers.TryAddWithoutValidation("Accept", "audio/*, application/ogg;q=0.9, */*;q=0.1");
            // Preserve byte-range meaning for finite media even though the shared
            // hardened transport can decode compressed HTTP entities.
            message.Headers.AcceptEncoding.ParseAdd("identity");
            if (ticket.Kind == PodcastEnclosureKind && range is not null)
            {
                message.Headers.Range = range;
            }

            HttpResponseMessage response;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ResponseHeaderTimeout);
            try
            {
                response = await PodcastResourceFetchService.SendSafeRequestAsync(message, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RelayException("timeout", HttpStatusCode.GatewayTimeout, "Remote media response headers timed out.", ex);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (PodcastResourceFetchService.FetchException ex)
            {
                throw new RelayException(ex.Code, ex.ResponseStatus, "Remote media relay policy rejected the upstream request.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new RelayException("upstream-unreachable", HttpStatusCode.BadGateway, "Remote media host could not be reached.", ex);
            }

            if (!IsRedirect(response.StatusCode))
            {
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    return response;
                }

                var status = response.StatusCode;
                response.Dispose();
                throw new RelayException(
                    "upstream-http",
                    HttpStatusCode.BadGateway,
                    $"Remote media upstream returned HTTP {(int)status}.");
            }

            using (response)
            {
                if (redirectCount >= PodcastResourceFetchService.MaxRedirects)
                {
                    throw new RelayException("redirect-limit", HttpStatusCode.BadGateway, "Remote media exceeded the redirect limit.");
                }

                var location = response.Headers.Location?.ToString();
                if (!PodcastResourceFetchService.TryResolveRedirect(current, location, out var next) || next is null)
                {
                    throw new RelayException("unsafe-redirect", HttpStatusCode.BadGateway, "Remote media redirect target is not allowed.");
                }

                current = next;
            }
        }

        throw new RelayException("redirect-limit", HttpStatusCode.BadGateway, "Remote media exceeded the redirect limit.");
    }

    internal static bool IsSupportedKind(string? kind)
        => NormalizeKind(kind) is PodcastEnclosureKind or RadioStreamKind;

    internal static bool IsValidRelayId(string? relayId)
    {
        if (string.IsNullOrWhiteSpace(relayId) || relayId.Length != 43)
        {
            return false;
        }

        foreach (var character in relayId)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    internal static void ClearForTesting() => Tickets.Clear();

    internal static int TicketCountForTesting => Tickets.Count;

    private static string CreateRelayId()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void CleanupExpired(DateTimeOffset now)
    {
        foreach (var entry in Tickets)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                Tickets.TryRemove(entry.Key, out _);
            }
        }
    }

    private static void TrimToCapacity()
    {
        while (Tickets.Count >= MaxTickets)
        {
            string? oldestKey = null;
            DateTimeOffset oldestExpiry = DateTimeOffset.MaxValue;
            foreach (var entry in Tickets)
            {
                if (entry.Value.ExpiresAt < oldestExpiry)
                {
                    oldestExpiry = entry.Value.ExpiresAt;
                    oldestKey = entry.Key;
                }
            }

            if (oldestKey is null || !Tickets.TryRemove(oldestKey, out _))
            {
                break;
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static string NormalizeKind(string? kind)
        => kind?.Trim().ToLowerInvariant() ?? string.Empty;
}

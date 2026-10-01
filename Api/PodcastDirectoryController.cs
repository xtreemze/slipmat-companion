using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Api;

/// <summary>
/// Executes bounded Podcast Index directory search with credentials that remain
/// exclusively in the Jellyfin server process.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/AudioGateway/podcasts/directory")]
public sealed class PodcastDirectoryController : ControllerBase
{
    public const string ApiKeyEnvironmentVariable = "SLIPMAT_PODCAST_INDEX_API_KEY";
    public const string ApiSecretEnvironmentVariable = "SLIPMAT_PODCAST_INDEX_API_SECRET";
    public const string UserAgentEnvironmentVariable = "SLIPMAT_PODCAST_INDEX_USER_AGENT";
    public const string ProviderId = "podcastindex.org";

    private const int ContractVersion = 1;
    private const int MinQueryLength = 2;
    private const int MaxQueryLength = 160;
    private const int MaxResults = 50;
    private const long MaxResponseBytes = 2 * 1024 * 1024;
    private const string SearchEndpoint = "https://api.podcastindex.org/api/1.0/search/byterm";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private readonly ILogger<PodcastDirectoryController> _logger;

    public PodcastDirectoryController(ILogger<PodcastDirectoryController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns true only when both Podcast Index credential values are present.
    /// Values themselves are never returned by the capability contract.
    /// </summary>
    public static bool IsPodcastIndexConfigured()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiSecretEnvironmentVariable));
    }

    /// <summary>
    /// POST /Plugins/AudioGateway/podcasts/directory/search
    /// </summary>
    [HttpPost("search")]
    [ProducesResponseType(typeof(PodcastDirectorySearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PodcastDirectorySearchResponse>> Search(
        [FromBody] PodcastDirectorySearchRequest request)
    {
        var query = request.Query?.Trim() ?? string.Empty;
        if (request.Version != ContractVersion
            || query.Length < MinQueryLength
            || query.Length > MaxQueryLength
            || request.RequestGeneration < 0
            || request.Limit < 1
            || request.Limit > MaxResults)
        {
            return BadRequest();
        }

        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        var apiSecret = Environment.GetEnvironmentVariable(ApiSecretEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var authorization = CreateAuthorization(apiKey, apiSecret, timestamp);
        var userAgent = NormalizeUserAgent(
            Environment.GetEnvironmentVariable(UserAgentEnvironmentVariable));

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&max={request.Limit}");

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, url);
        upstreamRequest.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        upstreamRequest.Headers.TryAddWithoutValidation("X-Auth-Date", timestamp);
        upstreamRequest.Headers.TryAddWithoutValidation("X-Auth-Key", apiKey);
        upstreamRequest.Headers.TryAddWithoutValidation("Authorization", authorization);
        upstreamRequest.Headers.TryAddWithoutValidation("Accept", "application/json");

        try
        {
            using var response = await Http.SendAsync(
                    upstreamRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "Podcast Index search rejected with HTTP {StatusCode}",
                    (int)response.StatusCode);
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Podcast Index search failed with HTTP {StatusCode}",
                    (int)response.StatusCode);
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                _logger.LogWarning("Podcast Index search response exceeded the byte ceiling");
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            await response.Content.LoadIntoBufferAsync(MaxResponseBytes).ConfigureAwait(false);
            var payload = await response.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (payload.LongLength > MaxResponseBytes)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var results = NormalizeSearchResponse(payload, request.Limit);
            return Ok(new PodcastDirectorySearchResponse(
                ContractVersion,
                ProviderId,
                request.RequestGeneration,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                results));
        }
        catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogWarning("Podcast Index search timed out");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Podcast Index search transport failed: {ExceptionType}",
                ex.GetType().Name);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Podcast Index search returned invalid JSON");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    public static string CreateAuthorization(string apiKey, string apiSecret, string timestamp)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey + apiSecret + timestamp);
        return Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    }

    public static IReadOnlyList<PodcastDirectorySearchResult> NormalizeSearchResponse(
        byte[] payload,
        int limit)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("feeds", out var feeds)
            || feeds.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Podcast Index response is missing feeds");
        }

        var results = new List<PodcastDirectorySearchResult>();
        var seenFeeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feed in feeds.EnumerateArray())
        {
            if (results.Count >= limit)
            {
                break;
            }

            if (feed.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var feedLocator = SafeHttpUrl(ReadString(feed, "url"));
            if (feedLocator is null || !seenFeeds.Add(feedLocator))
            {
                continue;
            }

            var title = ReadString(feed, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = feedLocator;
            }

            var id = ReadScalarString(feed, "id");
            var resourceId = !string.IsNullOrWhiteSpace(id)
                ? $"podcastindex:{id}"
                : feedLocator;
            var artwork = SafeHttpUrl(ReadString(feed, "image"))
                ?? SafeHttpUrl(ReadString(feed, "artwork"));
            var directoryUrl = SafeHttpUrl(ReadString(feed, "link"));
            var publisher = ReadString(feed, "author") ?? ReadString(feed, "ownerName");

            results.Add(new PodcastDirectorySearchResult(
                ContractVersion,
                ProviderId,
                resourceId,
                title,
                publisher,
                ReadString(feed, "description"),
                artwork,
                directoryUrl,
                feedLocator));
        }

        return results;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? NormalizeText(property.GetString())
            : null;
    }

    private static string? ReadScalarString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => NormalizeText(property.GetString()),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static string NormalizeUserAgent(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 256
            || normalized.Contains('\r')
            || normalized.Contains('\n'))
        {
            return "Slipmat-AudioGateway/1.0";
        }

        return normalized;
    }

    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? SafeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services.Packaging;

/// <summary>
/// Offers captures of an album's physical packaging (back cover, disc face,
/// booklet, tray and liner scans) from the Cover Art Archive, TheAudioDB and,
/// with a personal token, Discogs, in the image slots Slipmat's Package
/// Inspector reads. Jellyfin's own AudioDB provider supplies only the front.
/// Each source is independent: one failing or missing never hides the others.
/// </summary>
public sealed class AlbumPackagingImageProvider : IRemoteImageProvider, IHasOrder
{
    private const string UserAgent =
        "Slipmat-AudioGateway/1.0 ( https://github.com/xtreemze/slipmat-companion )";

    private static readonly ImageType[] Slots =
    {
        ImageType.Primary,
        ImageType.BoxRear,
        ImageType.Disc,
        ImageType.Box,
        ImageType.Menu,
        ImageType.Art,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AlbumPackagingImageProvider> _logger;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly PackagingSourceKeys _keys;

    public AlbumPackagingImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<AlbumPackagingImageProvider> logger)
        : this(
            httpClientFactory,
            logger,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            PackagingSourceKeys.FromEnvironment())
    {
    }

    internal AlbumPackagingImageProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<AlbumPackagingImageProvider> logger,
        Func<PluginConfiguration> configuration,
        PackagingSourceKeys keys)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
        _keys = keys;
    }

    /// <inheritdoc />
    public string Name => "Slipmat Packaging";

    /// <inheritdoc />
    // After Jellyfin's built-in fetchers, so a library keeps its chosen front.
    public int Order => 10;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
        => item is MusicAlbum && _configuration().PackagingImagesEnabled;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => Slots;

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken)
    {
        if (item is not MusicAlbum album)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var sources = new List<Task<IReadOnlyList<PackagingImageCandidate>>>
        {
            Guard("Cover Art Archive", () => FromCoverArtArchive(album, cancellationToken)),
            Guard("TheAudioDB", () => FromAudioDb(album, cancellationToken)),
        };
        if (_keys.DiscogsToken is not null)
        {
            sources.Add(Guard("Discogs", () => FromDiscogs(album, _keys.DiscogsToken, cancellationToken)));
        }

        var results = await Task.WhenAll(sources).ConfigureAwait(false);
        return results
            .SelectMany(candidates => candidates)
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.Rank)
            .Select(candidate => new RemoteImageInfo
            {
                ProviderName = $"{Name} ({candidate.Source})",
                Url = candidate.Url,
                ThumbnailUrl = candidate.ThumbnailUrl,
                Type = candidate.Type,
                Width = candidate.Width,
                Height = candidate.Height,
                CommunityRating = candidate.Rank,
            })
            .ToArray();
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return Client().SendAsync(request, cancellationToken);
    }

    private async Task<IReadOnlyList<PackagingImageCandidate>> Guard(
        string source,
        Func<Task<IReadOnlyList<PackagingImageCandidate>>> fetch)
    {
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            _logger.LogDebug(exception, "{Source} packaging images unavailable", source);
            return Array.Empty<PackagingImageCandidate>();
        }
    }

    private async Task<IReadOnlyList<PackagingImageCandidate>> FromCoverArtArchive(
        MusicAlbum album,
        CancellationToken cancellationToken)
    {
        var release = album.GetProviderId(MetadataProvider.MusicBrainzAlbum);
        var group = album.GetProviderId(MetadataProvider.MusicBrainzReleaseGroup);
        // Only a release carries its back, disc and booklet scans; the release
        // group endpoint answers with the front alone, as a last resort.
        foreach (var path in new[]
                 {
                     string.IsNullOrWhiteSpace(release) ? null : $"release/{release}",
                     string.IsNullOrWhiteSpace(group) ? null : $"release-group/{group}",
                 })
        {
            if (path is null)
            {
                continue;
            }

            var json = await GetJson($"https://coverartarchive.org/{path}", null, cancellationToken)
                .ConfigureAwait(false);
            if (json is null)
            {
                continue;
            }

            var candidates = PackagingImageSources.ParseCoverArtArchive(json);
            if (candidates.Count > 0)
            {
                return candidates;
            }
        }

        return Array.Empty<PackagingImageCandidate>();
    }

    private async Task<IReadOnlyList<PackagingImageCandidate>> FromAudioDb(
        MusicAlbum album,
        CancellationToken cancellationToken)
    {
        var key = _keys.AudioDbApiKey;
        var id = album.GetProviderId(MetadataProvider.AudioDbAlbum);
        var artist = album.AlbumArtists.FirstOrDefault();
        string? url = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            url = $"https://www.theaudiodb.com/api/v1/json/{key}/album.php?m={Uri.EscapeDataString(id)}";
        }
        else if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(album.Name))
        {
            url = $"https://www.theaudiodb.com/api/v1/json/{key}/searchalbum.php?s={Uri.EscapeDataString(artist)}&a={Uri.EscapeDataString(album.Name)}";
        }

        if (url is null)
        {
            return Array.Empty<PackagingImageCandidate>();
        }

        var json = await GetJson(url, null, cancellationToken).ConfigureAwait(false);
        return json is null ? Array.Empty<PackagingImageCandidate>() : PackagingImageSources.ParseAudioDb(json);
    }

    private async Task<IReadOnlyList<PackagingImageCandidate>> FromDiscogs(
        MusicAlbum album,
        string token,
        CancellationToken cancellationToken)
    {
        var artist = album.AlbumArtists.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album.Name))
        {
            return Array.Empty<PackagingImageCandidate>();
        }

        var authorization = new AuthenticationHeaderValue("Discogs", $"token={token}");
        var search = await GetJson(
                $"https://api.discogs.com/database/search?type=release&per_page=5&artist={Uri.EscapeDataString(artist)}&release_title={Uri.EscapeDataString(album.Name)}",
                authorization,
                cancellationToken)
            .ConfigureAwait(false);
        var releaseId = search is null ? null : PackagingImageSources.ParseDiscogsSearchReleaseId(search);
        if (releaseId is null)
        {
            return Array.Empty<PackagingImageCandidate>();
        }

        var release = await GetJson($"https://api.discogs.com/releases/{releaseId}", authorization, cancellationToken)
            .ConfigureAwait(false);
        return release is null ? Array.Empty<PackagingImageCandidate>() : PackagingImageSources.ParseDiscogsRelease(release);
    }

    /// <summary>The body of a successful JSON response; null for 404 or another failure status.</summary>
    private async Task<string?> GetJson(
        string url,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authorization is not null)
        {
            request.Headers.Authorization = authorization;
        }

        using var response = await Client().SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(NamedClient.Default);
}

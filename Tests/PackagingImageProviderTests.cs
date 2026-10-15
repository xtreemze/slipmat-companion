using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Services.Packaging;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class PackagingImageProviderTests
{
    private const string ReleaseId = "76df3287-6cda-33eb-8e9a-044b5e15ffdd";
    private const string ReleaseGroupId = "f6c334ab-42cd-4bbb-b33d-2c2ae7670c20";

    private const string CaaRelease = """
        {"images":[
          {"types":["Front"],"approved":true,"image":"http://coverartarchive.org/release/r/1.jpg","thumbnails":{"500":"http://coverartarchive.org/release/r/1-500.jpg"}},
          {"types":["Back"],"approved":true,"image":"http://coverartarchive.org/release/r/2.jpg","thumbnails":{"large":"http://coverartarchive.org/release/r/2-500.jpg"}},
          {"types":["Medium"],"approved":true,"image":"http://coverartarchive.org/release/r/3.jpg"},
          {"types":["Booklet"],"approved":true,"image":"http://coverartarchive.org/release/r/4.jpg"},
          {"types":["Tray"],"approved":true,"image":"http://coverartarchive.org/release/r/5.jpg"},
          {"types":["Spine","Back"],"approved":true,"image":"http://coverartarchive.org/release/r/6.jpg"},
          {"types":["Matrix/Runout"],"approved":true,"image":"http://coverartarchive.org/release/r/7.jpg"},
          {"types":["Back"],"approved":false,"image":"http://coverartarchive.org/release/r/8.jpg"}
        ]}
        """;

    private const string AudioDbAlbum = """
        {"album":[{"idAlbum":"2115888","strAlbum":"X","strArtist":"A","strAlbumThumb":"https://r2.theaudiodb.com/images/front.jpg","strAlbumThumbHQ":null,"strAlbumBack":"https://r2.theaudiodb.com/images/back.jpg","strAlbumCDart":"https://r2.theaudiodb.com/images/cd.png","strAlbumSpine":"https://r2.theaudiodb.com/images/spine.jpg"}]}
        """;

    private const string FanartTvAlbum = """
        {"albums":[
          {"release_group_id":"f6c334ab-42cd-4bbb-b33d-2c2ae7670c20",
            "albumcover":[
              {"id":"front-1","url":"https://assets.fanart.tv/fanart/music/a/albumcover/front.jpg"},
              {"id":"front-2","url":"https://other.example/images/not-fanart.jpg"}],
            "cdart":[{"id":"disc-1","url":"https://assets.fanart.tv/fanart/music/a/cdart/disc.png"}]},
          {"release_group_id":"11111111-1111-1111-1111-111111111111",
            "albumcover":[{"id":"wrong","url":"https://assets.fanart.tv/fanart/music/b/albumcover/wrong.jpg"}]}
        ]}
        """;

    private const string DiscogsSearch = """{"results":[{"id":12345,"title":"A - X"}]}""";

    private const string DiscogsRelease = """
        {"images":[
          {"type":"primary","uri":"https://i.discogs.com/front.jpg","uri150":"https://i.discogs.com/front-150.jpg","width":600,"height":600},
          {"type":"secondary","uri":"https://i.discogs.com/back.jpg","width":600,"height":600},
          {"type":"secondary","uri":"https://i.discogs.com/inner.jpg","width":600,"height":600}
        ]}
        """;

    [Theory]
    [InlineData("http://127.0.0.1/private", PackagingImageSources.CoverArtArchive)]
    [InlineData("http://169.254.169.254/latest/meta-data", PackagingImageSources.CoverArtArchive)]
    [InlineData("https://assets.fanart.tv.evil.example/art.jpg", PackagingImageSources.FanartTv)]
    [InlineData("https://user:password@assets.fanart.tv/art.jpg", PackagingImageSources.FanartTv)]
    [InlineData("https://assets.fanart.tv:444/art.jpg", PackagingImageSources.FanartTv)]
    [InlineData("http://assets.fanart.tv/art.jpg", PackagingImageSources.FanartTv)]
    [InlineData("file:///etc/passwd", PackagingImageSources.Discogs)]
    [InlineData("https://i.discogs.com/art.jpg#secret", PackagingImageSources.Discogs)]
    [InlineData("https://r2.theaudiodb.com@localhost/back.jpg", PackagingImageSources.AudioDb)]
    [InlineData("https://evil.example/back.jpg", PackagingImageSources.AudioDb)]
    public void Image_candidates_reject_untrusted_origins_and_schemes(string url, string source)
    {
        Assert.Null(PackagingImageSources.NormalizeRemoteImageUrl(url, source));
    }

    [Theory]
    [InlineData("http://coverartarchive.org/release/r/2.jpg", PackagingImageSources.CoverArtArchive,
        "https://coverartarchive.org/release/r/2.jpg")]
    [InlineData("https://r2.theaudiodb.com/images/back.jpg", PackagingImageSources.AudioDb,
        "https://r2.theaudiodb.com/images/back.jpg")]
    [InlineData("https://assets.fanart.tv/fanart/music/a/albumcover/front.jpg", PackagingImageSources.FanartTv,
        "https://assets.fanart.tv/fanart/music/a/albumcover/front.jpg")]
    [InlineData("https://i.discogs.com/front.jpg", PackagingImageSources.Discogs,
        "https://i.discogs.com/front.jpg")]
    public void Trusted_provider_image_origins_remain_usable(string url, string source, string expected)
    {
        Assert.Equal(expected, PackagingImageSources.NormalizeRemoteImageUrl(url, source));
    }

    [Fact]
    public void Provider_parsers_discard_untrusted_artwork_before_offering_it_to_Jellyfin()
    {
        Assert.Empty(PackagingImageSources.ParseCoverArtArchive(
            CaaRelease.Replace("coverartarchive.org", "127.0.0.1", StringComparison.Ordinal)));
        Assert.Empty(PackagingImageSources.ParseAudioDb(
            AudioDbAlbum.Replace("r2.theaudiodb.com", "localhost", StringComparison.Ordinal)));
        Assert.Empty(PackagingImageSources.ParseDiscogsRelease(
            DiscogsRelease.Replace("i.discogs.com", "localhost", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("http://127.0.0.1/secret")]
    [InlineData("https://i.discogs.com.evil.example/secret")]
    [InlineData("file:///etc/passwd")]
    public async Task Download_boundary_rejects_untrusted_urls_without_network_request(string url)
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, string.Empty));
        var provider = Create(handler, new PluginConfiguration());

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetImageResponse(url, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_boundary_accepts_known_provider_image_hosts()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, string.Empty));
        var provider = Create(handler, new PluginConfiguration());

        using var response = await provider.GetImageResponse(
            "https://coverartarchive.org/release/r/2.jpg", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "https://coverartarchive.org/release/r/2.jpg" }, handler.Requests);
    }

    [Fact]
    public void Cover_Art_Archive_types_map_to_the_slots_the_inspector_reads()
    {
        var candidates = PackagingImageSources.ParseCoverArtArchive(CaaRelease);

        Assert.Equal(
            new[] { ImageType.Primary, ImageType.BoxRear, ImageType.Disc, ImageType.Box, ImageType.Menu, ImageType.BoxRear },
            candidates.Select(candidate => candidate.Type).ToArray());
        // Unapproved and unmapped captures are not offered.
        Assert.DoesNotContain(candidates, candidate => candidate.Url.EndsWith("7.jpg", StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, candidate => candidate.Url.EndsWith("8.jpg", StringComparison.Ordinal));
        Assert.All(candidates, candidate => Assert.StartsWith("https://", candidate.Url, StringComparison.Ordinal));
        Assert.Equal("https://coverartarchive.org/release/r/2-500.jpg", candidates[1].ThumbnailUrl);
    }

    [Fact]
    public void TheAudioDB_supplies_the_back_cover_and_disc_its_Jellyfin_plugin_leaves_out()
    {
        var candidates = PackagingImageSources.ParseAudioDb(AudioDbAlbum);

        Assert.Contains(candidates, c => c.Type == ImageType.BoxRear && c.Url.EndsWith("back.jpg", StringComparison.Ordinal));
        Assert.Contains(candidates, c => c.Type == ImageType.Disc && c.Url.EndsWith("cd.png", StringComparison.Ordinal));
        Assert.Contains(candidates, c => c.Type == ImageType.Art && c.Url.EndsWith("spine.jpg", StringComparison.Ordinal));
        Assert.Contains(candidates, c => c.Type == ImageType.Primary && c.Url.EndsWith("front.jpg", StringComparison.Ordinal));
        Assert.Empty(PackagingImageSources.ParseAudioDb("""{"album":null}"""));
    }

    [Fact]
    public void TheAudioDB_album_identity_selects_the_matching_record_and_rejects_wrong_scans()
    {
        const string payload = """
            {"album":[
              {"idAlbum":"100","strAlbum":"X","strArtist":"Other","strAlbumBack":"https://r2.theaudiodb.com/images/wrong.jpg"},
              {"idAlbum":"200","strAlbum":"X","strArtist":"A","strAlbumBack":"https://r2.theaudiodb.com/images/correct.jpg"}
            ]}
            """;

        var byName = PackagingImageSources.ParseAudioDb(
            payload, expectedAlbumTitle: " x ", expectedArtist: "a");
        Assert.Equal(
            "https://r2.theaudiodb.com/images/correct.jpg",
            Assert.Single(byName).Url);
        Assert.Empty(PackagingImageSources.ParseAudioDb(
            payload, expectedAlbumTitle: "X", expectedArtist: "Missing"));
        Assert.Empty(PackagingImageSources.ParseAudioDb(
            payload, expectedAlbumTitle: "X"));
        Assert.Equal(
            "https://r2.theaudiodb.com/images/wrong.jpg",
            Assert.Single(PackagingImageSources.ParseAudioDb(
                payload, expectedAlbumId: "100")).Url);
        Assert.Empty(PackagingImageSources.ParseAudioDb(
            payload, expectedAlbumId: "999"));
    }

    [Fact]
    public async Task TheAudioDB_search_cannot_attribute_an_unrelated_back_scan_to_the_album()
    {
        var album = new MusicAlbum { Name = "X", AlbumArtists = new[] { "A" } };
        var handler = new RecordingHandler(url =>
            url.Contains("searchalbum.php", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """
                    {"album":[{"strAlbum":"X","strArtist":"Other","strAlbumBack":"https://r2.theaudiodb.com/images/wrong.jpg"}]}
                    """)
                : (HttpStatusCode.NotFound, string.Empty));

        var images = (await Create(handler, new PluginConfiguration()).GetImages(
            album, CancellationToken.None)).ToArray();

        Assert.DoesNotContain(images, image => image.Type == ImageType.BoxRear);
    }

    [Fact]
    public void Discogs_unlabelled_secondaries_are_inserts_not_claimed_back_covers()
    {
        Assert.Equal(12345, PackagingImageSources.ParseDiscogsSearchReleaseId(DiscogsSearch));
        var candidates = PackagingImageSources.ParseDiscogsRelease(DiscogsRelease);

        Assert.Equal(
            new[] { ImageType.Primary, ImageType.Box, ImageType.Menu },
            candidates.Select(candidate => candidate.Type).ToArray());
        Assert.Equal(600, candidates[0].Width);
    }

    [Fact]
    public void Fanart_release_group_art_never_claims_a_rear_cover_or_exact_disc()
    {
        var candidates = PackagingImageSources.ParseFanartTvAlbum(FanartTvAlbum, ReleaseGroupId);

        var front = Assert.Single(candidates);
        Assert.Equal(ImageType.Primary, front.Type);
        Assert.Equal(PackagingImageSources.FanartTv, front.Source);
        Assert.Contains("front.jpg", front.Url, StringComparison.Ordinal);
        Assert.Empty(PackagingImageSources.ParseFanartTvAlbum(FanartTvAlbum, "11111111-1111-1111-1111-111111111112"));
    }

    [Fact]
    public async Task Fanart_credentials_stay_in_request_headers_and_only_with_a_release_group()
    {
        var album = new MusicAlbum { Name = "X", AlbumArtists = new[] { "A" } };
        album.SetProviderId(MetadataProvider.MusicBrainzReleaseGroup, ReleaseGroupId);
        var handler = new RecordingHandler(url =>
            url.Contains("fanart.tv/v3.2/music/albums/", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, FanartTvAlbum)
                : (HttpStatusCode.NotFound, string.Empty));
        var provider = Create(handler, new PluginConfiguration(), PackagingSourceKeys.From(null, null, "secret-fanart-key"));

        var images = (await provider.GetImages(album, CancellationToken.None)).ToArray();

        Assert.Contains(images, image => image.Type == ImageType.Primary
            && image.ProviderName!.Contains("fanart.tv", StringComparison.Ordinal));
        Assert.DoesNotContain(images, image => image.Type is ImageType.BoxRear or ImageType.Disc
            && image.ProviderName!.Contains("fanart.tv", StringComparison.Ordinal));
        Assert.Contains("secret-fanart-key", handler.FanartApiKeys);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("secret-fanart-key", StringComparison.Ordinal));

        var unknownAlbum = new MusicAlbum { Name = "Unknown" };
        await provider.GetImages(unknownAlbum, CancellationToken.None);
        Assert.Single(handler.Requests, url => url.Contains("fanart.tv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cover_art_archive_group_candidates_cannot_supply_an_exact_back_scan()
    {
        var album = new MusicAlbum { Name = "X" };
        album.SetProviderId(MetadataProvider.MusicBrainzReleaseGroup, ReleaseGroupId);
        var handler = new RecordingHandler(url =>
            url.Contains("coverartarchive.org/release-group/", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, CaaRelease)
                : (HttpStatusCode.NotFound, string.Empty));
        var images = (await Create(handler, new PluginConfiguration()).GetImages(album, CancellationToken.None)).ToArray();

        Assert.Contains(images, image => image.Type == ImageType.Primary
            && image.ProviderName!.Contains("Cover Art Archive", StringComparison.Ordinal));
        Assert.DoesNotContain(images, image => image.Type == ImageType.BoxRear
            && image.ProviderName!.Contains("Cover Art Archive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provider_merges_sources_labelled_scans_first_and_survives_a_failing_source()
    {
        var album = new MusicAlbum { Name = "X", AlbumArtists = new[] { "A" } };
        album.SetProviderId(MetadataProvider.MusicBrainzAlbum, ReleaseId);
        var handler = new RecordingHandler(url => url switch
        {
            _ when url.StartsWith($"https://coverartarchive.org/release/{ReleaseId}", StringComparison.Ordinal) => (HttpStatusCode.OK, CaaRelease),
            _ when url.Contains("theaudiodb.com", StringComparison.Ordinal) => (HttpStatusCode.OK, AudioDbAlbum),
            _ when url.Contains("database/search", StringComparison.Ordinal) => (HttpStatusCode.InternalServerError, string.Empty),
            _ => (HttpStatusCode.NotFound, string.Empty),
        });
        var provider = Create(handler, new PluginConfiguration(), PackagingSourceKeys.From(null, "token"));

        var images = (await provider.GetImages(album, CancellationToken.None)).ToArray();

        var backs = images.Where(image => image.Type == ImageType.BoxRear).ToArray();
        Assert.True(backs.Length >= 2);
        Assert.Contains("Cover Art Archive", backs[0].ProviderName, StringComparison.Ordinal);
        Assert.Contains(images, image => image.Type == ImageType.Disc && image.ProviderName!.Contains("TheAudioDB", StringComparison.Ordinal));
        // The AudioDB search fell back to artist + title because there is no AudioDB id.
        Assert.Contains(handler.Requests, url => url.Contains("/json/123/searchalbum.php?s=A&a=X", StringComparison.Ordinal));
        // Discogs failed; the others still answered.
        Assert.Contains(handler.Requests, url => url.Contains("api.discogs.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discogs_is_not_asked_without_a_token()
    {
        var album = new MusicAlbum { Name = "X", AlbumArtists = new[] { "A" } };
        var handler = new RecordingHandler(_ => (HttpStatusCode.NotFound, string.Empty));
        var provider = Create(handler, new PluginConfiguration());

        var images = await provider.GetImages(album, CancellationToken.None);

        Assert.Empty(images);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("discogs", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("fanart.tv", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_only_serves_albums_and_can_be_switched_off()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.NotFound, string.Empty));
        Assert.True(Create(handler, new PluginConfiguration()).Supports(new MusicAlbum()));
        Assert.False(Create(handler, new PluginConfiguration()).Supports(new Audio()));
        Assert.False(Create(handler, new PluginConfiguration { PackagingImagesEnabled = false }).Supports(new MusicAlbum()));
    }

    private static AlbumPackagingImageProvider Create(
        RecordingHandler handler,
        PluginConfiguration configuration,
        PackagingSourceKeys? keys = null)
        => new(
            new SingleClientFactory(handler),
            NullLogger<AlbumPackagingImageProvider>.Instance,
            () => configuration,
            keys ?? PackagingSourceKeys.From(null, null));

    [Fact]
    public void Source_keys_default_to_the_public_AudioDB_key_and_no_Discogs()
    {
        var keys = PackagingSourceKeys.From("  ", " ");
        Assert.Equal(PackagingSourceKeys.PublicAudioDbKey, keys.AudioDbApiKey);
        Assert.Null(keys.DiscogsToken);
        Assert.Null(keys.FanartTvApiKey);
        Assert.Equal("fanart-key", PackagingSourceKeys.From(null, null, " fanart-key ").FanartTvApiKey);
        Assert.Equal("abc", PackagingSourceKeys.From(" abc ", null).AudioDbApiKey);
    }

    private sealed class RecordingHandler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public List<string> FanartApiKeys { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
                if (request.Headers.TryGetValues("api-key", out var apiKeys))
                {
                    FanartApiKeys.AddRange(apiKeys);
                }
            }

            var (status, body) = respond(url);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

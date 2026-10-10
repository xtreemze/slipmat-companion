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
        {"album":[{"strAlbum":"X","strAlbumThumb":"https://r2.theaudiodb.com/images/front.jpg","strAlbumThumbHQ":null,"strAlbumBack":"https://r2.theaudiodb.com/images/back.jpg","strAlbumCDart":"https://r2.theaudiodb.com/images/cd.png","strAlbumSpine":"https://r2.theaudiodb.com/images/spine.jpg"}]}
        """;

    private const string DiscogsSearch = """{"results":[{"id":12345,"title":"A - X"}]}""";

    private const string DiscogsRelease = """
        {"images":[
          {"type":"primary","uri":"https://i.discogs.com/front.jpg","uri150":"https://i.discogs.com/front-150.jpg","width":600,"height":600},
          {"type":"secondary","uri":"https://i.discogs.com/back.jpg","width":600,"height":600},
          {"type":"secondary","uri":"https://i.discogs.com/inner.jpg","width":600,"height":600}
        ]}
        """;

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
    public void Discogs_secondaries_follow_the_conventional_upload_order()
    {
        Assert.Equal(12345, PackagingImageSources.ParseDiscogsSearchReleaseId(DiscogsSearch));
        var candidates = PackagingImageSources.ParseDiscogsRelease(DiscogsRelease);

        Assert.Equal(
            new[] { ImageType.Primary, ImageType.BoxRear, ImageType.Box },
            candidates.Select(candidate => candidate.Type).ToArray());
        Assert.Equal(600, candidates[0].Width);
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
        Assert.Equal("abc", PackagingSourceKeys.From(" abc ", null).AudioDbApiKey);
    }

    private sealed class RecordingHandler(Func<string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
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

using System.Linq;
using Jellyfin.Plugin.AudioGateway.Services;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudLibraryAudioImagePolicyTests
{
    [Fact]
    public void Disable_adds_an_empty_Audio_fetcher_list_when_none_exists()
    {
        var options = new LibraryOptions();

        JellyfinCloudLibraryProjection.DisableEmbeddedAudioImageExtraction(options);

        var audio = Assert.Single(options.TypeOptions);
        Assert.Equal("Audio", audio.Type);
        Assert.Empty(audio.ImageFetchers);
    }

    [Fact]
    public void Disable_preserves_other_types_and_empties_an_existing_Audio_entry()
    {
        var options = new LibraryOptions
        {
            TypeOptions =
            [
                new TypeOptions { Type = "MusicAlbum", ImageFetchers = ["TheAudioDB"] },
                new TypeOptions { Type = "Audio", ImageFetchers = ["Image Extractor"] },
            ],
        };

        JellyfinCloudLibraryProjection.DisableEmbeddedAudioImageExtraction(options);

        Assert.Equal(2, options.TypeOptions.Length);
        Assert.Equal(
            ["TheAudioDB"],
            options.TypeOptions.Single(t => t.Type == "MusicAlbum").ImageFetchers);
        Assert.Empty(options.TypeOptions.Single(t => t.Type == "Audio").ImageFetchers);
    }

    [Fact]
    public void Restore_removes_the_Audio_entry_when_none_was_captured()
    {
        var options = new LibraryOptions();
        JellyfinCloudLibraryProjection.DisableEmbeddedAudioImageExtraction(options);

        JellyfinCloudLibraryProjection.RestoreAudioImageFetchers(options, null);

        Assert.Empty(options.TypeOptions);
    }

    [Fact]
    public void Restore_reinstates_the_captured_Audio_fetchers()
    {
        var options = new LibraryOptions
        {
            TypeOptions = [new TypeOptions { Type = "Audio", ImageFetchers = ["Image Extractor"] }],
        };
        var captured = JellyfinCloudLibraryProjection.FindAudioTypeOptions(options)!.ImageFetchers;
        JellyfinCloudLibraryProjection.DisableEmbeddedAudioImageExtraction(options);

        JellyfinCloudLibraryProjection.RestoreAudioImageFetchers(options, captured);

        Assert.Equal(
            ["Image Extractor"],
            options.TypeOptions.Single(t => t.Type == "Audio").ImageFetchers);
    }
}

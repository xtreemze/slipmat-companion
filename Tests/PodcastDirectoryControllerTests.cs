using System.Text;
using Jellyfin.Plugin.AudioGateway.Api;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class PodcastDirectoryControllerTests
{
    [Fact]
    public void CreateAuthorization_IsDeterministicSha1Hex()
    {
        var first = PodcastDirectoryController.CreateAuthorization(
            "fixture-a",
            "fixture-b",
            "1700000000");
        var second = PodcastDirectoryController.CreateAuthorization(
            "fixture-a",
            "fixture-b",
            "1700000000");

        Assert.Equal(first, second);
        Assert.Equal(40, first.Length);
        Assert.Matches("^[a-f0-9]{40}$", first);
    }

    [Fact]
    public void NormalizeSearchResponse_ProjectsPublisherFeedEvidenceAndDropsUnsafeEntries()
    {
        var json = """
            {
              "feeds": [
                {
                  "id": 123,
                  "url": "https://example.test/feed.xml",
                  "title": "Example Show",
                  "author": "Example Publisher",
                  "description": "Example description",
                  "image": "https://example.test/image.jpg",
                  "link": "https://example.test/"
                },
                {
                  "id": 124,
                  "url": "https://user:secret@example.test/private.xml",
                  "title": "Unsafe"
                },
                {
                  "id": 125,
                  "url": "https://example.test/feed.xml",
                  "title": "Duplicate"
                }
              ]
            }
            """;

        var result = PodcastDirectoryController.NormalizeSearchResponse(
            Encoding.UTF8.GetBytes(json),
            10);

        var item = Assert.Single(result);
        Assert.Equal("podcastindex:123", item.ResourceId);
        Assert.Equal("Example Show", item.Title);
        Assert.Equal("Example Publisher", item.Publisher);
        Assert.Equal("https://example.test/feed.xml", item.FeedLocator);
        Assert.Equal("https://example.test/image.jpg", item.ArtworkUrl);
    }

    [Fact]
    public void NormalizeSearchResponse_RespectsRequestedLimit()
    {
        var json = """
            {
              "feeds": [
                { "id": 1, "url": "https://example.test/one.xml", "title": "One" },
                { "id": 2, "url": "https://example.test/two.xml", "title": "Two" }
              ]
            }
            """;

        var result = PodcastDirectoryController.NormalizeSearchResponse(
            Encoding.UTF8.GetBytes(json),
            1);

        Assert.Single(result);
        Assert.Equal("podcastindex:1", result[0].ResourceId);
    }
}

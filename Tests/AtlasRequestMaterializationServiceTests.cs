using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class AtlasRequestMaterializationServiceTests
{
    [Fact]
    public async Task MaterializeRequestsAsync_UsesMusicBrainzReleaseGroupWhenAvailable()
    {
        var httpClient = new HttpClient(new StubHandler(
            releaseGroupsBody: """
            {
              "release-groups": [
                {
                  "id": "interstellar-space",
                  "title": "Interstellar Space",
                  "score": 98,
                  "first-release-date": "1967-01-01",
                  "artist-credit": [
                    { "name": "John Coltrane" }
                  ]
                }
              ]
            }
            """,
            releasesBody: """
            {
              "releases": [
                {
                  "id": "interstellar-space-impulse",
                  "title": "Interstellar Space",
                  "date": "1974-01-01",
                  "country": "US",
                  "status": "Official",
                  "packaging": "Jewel Case",
                  "label-info": [
                    { "label": { "name": "Impulse!" } }
                  ],
                  "media": [
                    { "format": "CD", "track-count": 5 }
                  ]
                },
                {
                  "id": "interstellar-space-digital",
                  "title": "Interstellar Space (Digital)",
                  "date": "2020-01-01",
                  "country": "XW",
                  "status": "Official",
                  "packaging": "None",
                  "media": [
                    { "format": "Digital Media", "track-count": 5 }
                  ]
                }
              ]
            }
            """));

        var items = await AtlasRequestMaterializationService.MaterializeRequestsAsync(
            [
                new AtlasAcquisitionRequestRecord(
                    AlbumId: "streamrip:qobuz:interstellar-space",
                    ConceptId: "streamrip:qobuz:interstellar-space",
                    ReleaseId: null,
                    Title: "Interstellar Space",
                    ArtistName: "John Coltrane",
                    RequestedAt: "2026-03-12T12:00:00.000Z",
                    Provider: "streamrip")
            ],
            httpClient);

        Assert.Single(items);
        Assert.Equal("mb-release-group:interstellar-space", items[0].Concept.Id);
        Assert.Equal("requested", items[0].Concept.Availability);
        Assert.Equal("1967-01-01", items[0].Concept.CanonicalDate.IsoDate);
        Assert.Equal("interstellar-space", items[0].Concept.ReleaseGroupId);
        Assert.Equal("streamrip:qobuz:interstellar-space", items[0].RequestLink.RequestAlbumId);
        Assert.Equal("mb-release-group:interstellar-space", items[0].RequestLink.ConceptId);
        Assert.Equal(2, items[0].Releases.Count);
        Assert.Equal("release:interstellar-space-impulse", items[0].Releases[0].Id);
        Assert.Equal("Impulse!", items[0].Releases[0].Label);
        Assert.False(items[0].Releases[0].IsDigitalOnly);
        Assert.True(items[0].Releases[1].IsDigitalOnly);
        Assert.Single(items[0].ProviderCandidates);
        Assert.Equal("qobuz", items[0].ProviderCandidates[0].ProviderSource);
        Assert.Equal("interstellar-space", items[0].ProviderCandidates[0].ProviderAlbumId);
        Assert.Equal("release:interstellar-space-digital", items[0].ProviderCandidates[0].ReleaseId);
        Assert.Equal("release_linked", items[0].ProviderCandidates[0].DecisionState);
        Assert.NotEmpty(items[0].SourceObservations);
        Assert.NotEmpty(items[0].ResolutionRecords);
    }

    [Fact]
    public async Task MaterializeRequestsAsync_FallsBackToProvisionalRecordWhenNoMatchExists()
    {
        var httpClient = new HttpClient(new StubHandler("""
        { "release-groups": [] }
        """));

        var items = await AtlasRequestMaterializationService.MaterializeRequestsAsync(
            [
                new AtlasAcquisitionRequestRecord(
                    AlbumId: "streamrip:qobuz:unknown",
                    ConceptId: "streamrip:qobuz:unknown",
                    ReleaseId: null,
                    Title: "Unknown Album",
                    ArtistName: "Unknown Artist",
                    RequestedAt: "2026-03-12T12:00:00.000Z",
                    Provider: "streamrip")
            ],
            httpClient);

        Assert.Single(items);
        Assert.Equal("streamrip:qobuz:unknown", items[0].Concept.Id);
        Assert.Equal("conflict_or_review_needed", items[0].Concept.CatalogState);
        Assert.Null(items[0].Concept.ReleaseGroupId);
        Assert.Single(items[0].ProviderCandidates);
    }

    [Fact]
    public async Task MaterializeRequestsAsync_UsesReviewBandWhenMultipleDigitalReleaseTargetsExist()
    {
        var httpClient = new HttpClient(new StubHandler(
            releaseGroupsBody: """
            {
              "release-groups": [
                {
                  "id": "giant-steps",
                  "title": "Giant Steps",
                  "score": 97,
                  "first-release-date": "1960-01-27",
                  "artist-credit": [
                    { "name": "John Coltrane" }
                  ]
                }
              ]
            }
            """,
            releasesBody: """
            {
              "releases": [
                {
                  "id": "giant-steps-digital-a",
                  "title": "Giant Steps (Digital A)",
                  "date": "2019-01-01",
                  "country": "XW",
                  "status": "Official",
                  "packaging": "None",
                  "media": [
                    { "format": "Digital Media", "track-count": 7 }
                  ]
                },
                {
                  "id": "giant-steps-digital-b",
                  "title": "Giant Steps (Digital B)",
                  "date": "2020-01-01",
                  "country": "XW",
                  "status": "Official",
                  "packaging": "None",
                  "media": [
                    { "format": "Digital Media", "track-count": 7 }
                  ]
                }
              ]
            }
            """));

        var items = await AtlasRequestMaterializationService.MaterializeRequestsAsync(
            [
                new AtlasAcquisitionRequestRecord(
                    AlbumId: "streamrip:tidal:giant-steps",
                    ConceptId: "streamrip:tidal:giant-steps",
                    ReleaseId: null,
                    Title: "Giant Steps",
                    ArtistName: "John Coltrane",
                    RequestedAt: "2026-03-12T12:00:00.000Z",
                    Provider: "streamrip")
            ],
            httpClient);

        Assert.Single(items);
        Assert.Single(items[0].ProviderCandidates);
        Assert.Null(items[0].ProviderCandidates[0].ReleaseId);
        Assert.Equal("review_needed", items[0].ProviderCandidates[0].DecisionState);
        Assert.Equal(
            [
                "Multiple official digital releases matched this provider candidate",
                "Exact edition mapping remains ambiguous",
            ],
            items[0].ProviderCandidates[0].DecisiveFactors);
    }

    private sealed class StubHandler(string releaseGroupsBody, string? releasesBody = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        request.RequestUri?.AbsoluteUri.Contains("/ws/2/release?", System.StringComparison.Ordinal) == true
                            ? releasesBody ?? """{ "releases": [] }"""
                            : releaseGroupsBody,
                        Encoding.UTF8,
                        "application/json"),
                });
    }
}

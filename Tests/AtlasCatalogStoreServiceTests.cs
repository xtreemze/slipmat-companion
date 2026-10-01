using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class AtlasCatalogStoreServiceTests
{
    [Fact]
    public void LoadCatalog_MigratesLegacyMaterializedRequestList()
    {
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var path = AtlasCatalogStoreService.ResolveCatalogPath(tempDir.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                """
                [
                  {
                    "id": "streamrip:qobuz:interstellar-space",
                    "conceptId": "mb-release-group:interstellar-space",
                    "title": "Interstellar Space",
                    "artistName": "John Coltrane",
                    "artistKey": "john-coltrane",
                    "year": 1967,
                    "imageUrl": null,
                    "canonicalDate": {
                      "isoDate": "1967-01-01",
                      "timestamp": -94694400000,
                      "confidence": "high",
                      "granularity": "day"
                    },
                    "availability": "requested",
                    "catalogState": "catalog_only",
                    "sourceKind": "musicbrainz",
                    "externalBinding": {
                      "provider": "musicbrainz",
                      "artistName": "John Coltrane",
                      "releaseGroupId": "interstellar-space",
                      "score": 98,
                      "firstReleaseDate": "1967-01-01",
                      "sourceUrl": "https://musicbrainz.org/release-group/interstellar-space"
                    },
                    "releaseCandidates": [
                      {
                        "id": "release:interstellar-space:impulse",
                        "releaseGroupId": "interstellar-space",
                        "title": "Interstellar Space",
                        "artistName": "John Coltrane",
                        "releaseDate": "1974-01-01",
                        "dateLabel": "1974",
                        "countryCode": "US",
                        "label": "Impulse!",
                        "packaging": "Jewel Case",
                        "format": "CD",
                        "trackCount": 5,
                        "mediumCount": 1,
                        "isOfficial": true,
                        "isDigitalOnly": false,
                        "catalogState": "catalog_only",
                        "packageCompleteness": "none"
                      }
                    ],
                    "providerCandidates": [
                      {
                        "id": "streamrip:qobuz:interstellar-space",
                        "provider": "streamrip",
                        "providerSource": "qobuz",
                        "providerAlbumId": "interstellar-space",
                        "description": "John Coltrane - Interstellar Space",
                        "releaseId": "release:interstellar-space:impulse",
                        "decisionState": "group_linked",
                        "scoreTotal": 0.82,
                        "decisiveFactors": [
                          "Matched release group and artist identity"
                        ]
                      }
                    ]
                  }
                ]
                """);

            var catalog = AtlasCatalogStoreService.LoadCatalog(tempDir.FullName);

            Assert.Single(catalog.Concepts);
            Assert.Equal("mb-release-group:interstellar-space", catalog.Concepts[0].Id);
            Assert.Equal("interstellar-space", catalog.Concepts[0].ReleaseGroupId);
            Assert.Single(catalog.Releases);
            Assert.Single(catalog.RequestLinks);
            Assert.Equal(2, catalog.SourceObservations.Count);
            Assert.Single(catalog.ResolutionRecords);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SyncRequestsAsync_PersistsCatalogEntries()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var request = new AtlasAcquisitionRequestRecord(
            AlbumId: "streamrip:qobuz:interstellar-space",
            ConceptId: "streamrip:qobuz:interstellar-space",
            ReleaseId: null,
            Title: "Interstellar Space",
            ArtistName: "John Coltrane",
            RequestedAt: "2026-03-12T12:00:00.000Z",
            Provider: "streamrip");
        var httpClient = new HttpClient(new StubHandler());

        try
        {
            var catalog = await AtlasCatalogStoreService.SyncRequestsAsync(
                [request],
                tempDir.FullName,
                httpClient);

            var loaded = AtlasCatalogStoreService.LoadCatalog(tempDir.FullName);

            Assert.Single(catalog.Concepts);
            Assert.Single(loaded.Concepts);
            Assert.Equal("mb-release-group:interstellar-space", loaded.Concepts[0].Id);
            Assert.Equal("requested", loaded.Concepts[0].Availability);
            Assert.Equal("interstellar-space", loaded.Concepts[0].ReleaseGroupId);
            Assert.Equal(request.AlbumId, loaded.RequestLinks[0].RequestAlbumId);
            Assert.NotEmpty(loaded.Releases);
            Assert.NotEmpty(loaded.ResolutionRecords);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SyncRequestsAsync_RefreshesProvisionalRecordsWhenHigherConfidenceMatchAppears()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var request = new AtlasAcquisitionRequestRecord(
            AlbumId: "streamrip:qobuz:interstellar-space",
            ConceptId: "streamrip:qobuz:interstellar-space",
            ReleaseId: null,
            Title: "Interstellar Space",
            ArtistName: "John Coltrane",
            RequestedAt: "2026-03-12T12:00:00.000Z",
            Provider: "streamrip");
        var httpClient = new HttpClient(new StubHandler());

        try
        {
            AtlasCatalogStoreService.SaveCatalog(
                new AtlasCatalogDocument(
                    Concepts:
                    [
                        new AtlasCatalogConceptRecord(
                            Id: request.ConceptId,
                            ReleaseGroupId: null,
                            Title: request.Title,
                            ArtistId: null,
                            ArtistName: request.ArtistName,
                            ArtistKey: "john-coltrane",
                            Year: 1970,
                            ImageUrl: null,
                            CanonicalDate: new AtlasCanonicalDateResponse(
                                IsoDate: "1970-01-01",
                                Timestamp: 0,
                                Confidence: "low",
                                Granularity: "year"),
                            Availability: "requested",
                            CatalogState: "conflict_or_review_needed",
                            SourceKind: "musicbrainz",
                            SourceUrl: null,
                            ExternalBinding: null)
                    ],
                    Releases: [],
                    Artists: [],
                    Labels: [],
                    Places: [],
                    RequestLinks:
                    [
                        new AtlasCatalogRequestLinkRecord(
                            RequestAlbumId: request.AlbumId,
                            ConceptId: request.ConceptId,
                            SelectedReleaseId: null)
                    ],
                    SourceObservations: [],
                    ResolutionRecords: []),
                tempDir.FullName);

            var catalog = await AtlasCatalogStoreService.SyncRequestsAsync(
                [request],
                tempDir.FullName,
                httpClient);

            Assert.Single(catalog.Concepts);
            Assert.Equal("mb-release-group:interstellar-space", catalog.Concepts[0].Id);
            Assert.Equal("catalog_only", catalog.Concepts[0].CatalogState);
            Assert.Equal("interstellar-space", catalog.Concepts[0].ReleaseGroupId);
            Assert.Contains(
                catalog.SourceObservations,
                observation => observation.EntityId == "mb-release-group:interstellar-space");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsoluteUri.Contains("/release?", System.StringComparison.Ordinal) == true
                ? """
                  {
                    "releases": [
                      {
                        "id": "impulse-1974",
                        "title": "Interstellar Space",
                        "date": "1974-01-01",
                        "country": "US",
                        "packaging": "Jewel Case",
                        "status": "Official",
                        "label-info": [
                          {
                            "label": { "name": "Impulse!" }
                          }
                        ],
                        "media": [
                          {
                            "format": "CD",
                            "track-count": 5
                          }
                        ]
                      }
                    ]
                  }
                  """
                : """
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
                  """;

            return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
        }
    }
}

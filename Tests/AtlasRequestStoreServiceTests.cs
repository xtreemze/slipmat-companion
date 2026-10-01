using System;
using System.IO;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class AtlasRequestStoreServiceTests
{
    [Fact]
    public void UpsertRequest_PersistsAndLoadsRequests()
    {
        var tempDir = Directory.CreateTempSubdirectory();

        try
        {
            var stored = AtlasRequestStoreService.UpsertRequest(
                new AtlasAcquisitionRequestRecord(
                    AlbumId: "streamrip:qobuz:interstellar-space",
                    ConceptId: "streamrip:qobuz:interstellar-space",
                    ReleaseId: null,
                    Title: "Interstellar Space",
                    ArtistName: "John Coltrane",
                    RequestedAt: "2026-03-12T12:00:00.000Z",
                    Provider: "streamrip"),
                tempDir.FullName);

            var loaded = AtlasRequestStoreService.LoadRequests(tempDir.FullName);

            Assert.Single(loaded);
            Assert.Equal("Interstellar Space", loaded[0].Title);
            Assert.Equal("streamrip", loaded[0].Provider);
            Assert.Equal("qobuz", loaded[0].ProviderSource);
            Assert.Equal("interstellar-space", loaded[0].ProviderAlbumId);
            Assert.False(string.IsNullOrWhiteSpace(stored.ReceivedAt));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}

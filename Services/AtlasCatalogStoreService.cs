using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

public static class AtlasCatalogStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ResolveCatalogPath(string? storeRootOverride = null)
        => Path.Combine(AtlasRequestStoreService.ResolveStoreRoot(storeRootOverride), "catalog.json");

    public static AtlasCatalogDocument LoadCatalog(string? storeRootOverride = null)
    {
        var path = ResolveCatalogPath(storeRootOverride);
        if (!File.Exists(path))
        {
            return CreateEmptyCatalog();
        }

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<AtlasCatalogDocument>(json, JsonOptions);
            if (document is not null)
            {
                return NormalizeDocument(document);
            }
        }
        catch
        {
            // Fall through and try the legacy list format.
        }

        try
        {
            var json = File.ReadAllText(path);
            var legacyItems = JsonSerializer.Deserialize<List<LegacyMaterializedRequestRecord>>(json, JsonOptions);
            return legacyItems is null
                ? CreateEmptyCatalog()
                : BuildDocumentFromMaterializedRecords(legacyItems);
        }
        catch
        {
            return CreateEmptyCatalog();
        }
    }

    public static void SaveCatalog(
        AtlasCatalogDocument document,
        string? storeRootOverride = null)
    {
        var path = ResolveCatalogPath(storeRootOverride);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(NormalizeDocument(document), JsonOptions));
    }

    public static AtlasCatalogConceptRecord? FindConceptById(
        AtlasCatalogDocument document,
        string conceptId)
        => document.Concepts.FirstOrDefault(
            concept => string.Equals(concept.Id, conceptId, StringComparison.Ordinal));

    public static AtlasCatalogConceptRecord? FindConceptByReleaseGroupId(
        AtlasCatalogDocument document,
        string releaseGroupId)
        => document.Concepts.FirstOrDefault(
            concept => string.Equals(concept.ReleaseGroupId, releaseGroupId, StringComparison.Ordinal));

    public static IReadOnlyList<AtlasCatalogReleaseRecord> FindReleasesByConceptId(
        AtlasCatalogDocument document,
        string conceptId)
        => document.Releases
            .Where(release => string.Equals(release.ConceptId, conceptId, StringComparison.Ordinal))
            .OrderBy(release => release.ReleaseDate ?? "9999-12-31")
            .ThenBy(release => release.Title)
            .ToArray();

    public static IReadOnlyList<AtlasCatalogResolutionRecord> FindResolutionRecordsByConceptId(
        AtlasCatalogDocument document,
        string conceptId)
        => document.ResolutionRecords
            .Where(record => string.Equals(record.ConceptId, conceptId, StringComparison.Ordinal))
            .OrderByDescending(record => record.ScoreTotal ?? 0)
            .ThenBy(record => record.Id)
            .ToArray();

    public static IReadOnlyList<AtlasCatalogSourceObservation> FindSourceObservationsByConceptId(
        AtlasCatalogDocument document,
        string conceptId)
    {
        var releaseIds = document.Releases
            .Where(release => string.Equals(release.ConceptId, conceptId, StringComparison.Ordinal))
            .Select(release => release.Id)
            .ToHashSet(StringComparer.Ordinal);

        return document.SourceObservations
            .Where(observation =>
                string.Equals(observation.EntityId, conceptId, StringComparison.Ordinal) ||
                releaseIds.Contains(observation.EntityId))
            .OrderBy(observation => observation.EntityType)
            .ThenBy(observation => observation.Field)
            .ThenBy(observation => observation.Id)
            .ToArray();
    }

    public static async Task<AtlasCatalogDocument> SyncRequestsAsync(
        IReadOnlyList<AtlasAcquisitionRequestRecord> requests,
        string? storeRootOverride = null,
        HttpClient? httpClient = null)
    {
        var existing = LoadCatalog(storeRootOverride);

        var refreshableRequests = requests.Where(
            request =>
            {
                var concept = FindMatchingConcept(existing, request);
                return concept is null || NeedsRefresh(concept, request);
            }).ToList();

        if (refreshableRequests.Count == 0)
        {
            return existing;
        }

        var ingested = await AtlasRequestMaterializationService.MaterializeRequestsAsync(
            refreshableRequests,
            httpClient);

        foreach (var result in ingested)
        {
            existing = UpsertIngestResult(existing, result);
        }

        SaveCatalog(existing, storeRootOverride);
        return existing;
    }

    private static AtlasCatalogDocument CreateEmptyCatalog()
        => new(
            Concepts: Array.Empty<AtlasCatalogConceptRecord>(),
            Releases: Array.Empty<AtlasCatalogReleaseRecord>(),
            Artists: Array.Empty<AtlasCatalogArtistRecord>(),
            Labels: Array.Empty<AtlasCatalogLabelRecord>(),
            Places: Array.Empty<AtlasCatalogPlaceRecord>(),
            RequestLinks: Array.Empty<AtlasCatalogRequestLinkRecord>(),
            SourceObservations: Array.Empty<AtlasCatalogSourceObservation>(),
            ResolutionRecords: Array.Empty<AtlasCatalogResolutionRecord>());

    private static AtlasCatalogDocument NormalizeDocument(AtlasCatalogDocument document)
        => new(
            Concepts: document.Concepts ?? Array.Empty<AtlasCatalogConceptRecord>(),
            Releases: document.Releases ?? Array.Empty<AtlasCatalogReleaseRecord>(),
            Artists: document.Artists ?? Array.Empty<AtlasCatalogArtistRecord>(),
            Labels: document.Labels ?? Array.Empty<AtlasCatalogLabelRecord>(),
            Places: document.Places ?? Array.Empty<AtlasCatalogPlaceRecord>(),
            RequestLinks: document.RequestLinks ?? Array.Empty<AtlasCatalogRequestLinkRecord>(),
            SourceObservations: document.SourceObservations ?? Array.Empty<AtlasCatalogSourceObservation>(),
            ResolutionRecords: document.ResolutionRecords ?? Array.Empty<AtlasCatalogResolutionRecord>());

    private static AtlasCatalogDocument BuildDocumentFromMaterializedRecords(
        IReadOnlyList<LegacyMaterializedRequestRecord> records)
    {
        var document = CreateEmptyCatalog();
        foreach (var record in records)
        {
            document = UpsertMaterializedRecord(document, record);
        }

        return document;
    }

    private static AtlasCatalogDocument UpsertMaterializedRecord(
        AtlasCatalogDocument document,
        LegacyMaterializedRequestRecord record)
    {
        var existingRequestLink = document.RequestLinks.FirstOrDefault(
            link => string.Equals(link.RequestAlbumId, record.Id, StringComparison.Ordinal));
        var supersededConceptId =
            existingRequestLink is not null &&
            !string.Equals(existingRequestLink.ConceptId, record.ConceptId, StringComparison.Ordinal)
                ? existingRequestLink.ConceptId
                : null;

        var concept = new AtlasCatalogConceptRecord(
            Id: record.ConceptId,
            ReleaseGroupId: record.ExternalBinding?.ReleaseGroupId,
            Title: record.Title,
            ArtistId: null,
            ArtistName: record.ArtistName,
            ArtistKey: record.ArtistKey,
            Year: record.Year,
            ImageUrl: record.ImageUrl,
            CanonicalDate: record.CanonicalDate,
            Availability: record.Availability,
            CatalogState: record.CatalogState,
            SourceKind: record.SourceKind,
            SourceUrl: record.ExternalBinding?.SourceUrl,
            ExternalBinding: record.ExternalBinding,
            ProviderCandidates: record.ProviderCandidates);

        var releases = (record.ReleaseCandidates ?? Array.Empty<AtlasCatalogReleaseCandidate>())
            .Select(candidate => new AtlasCatalogReleaseRecord(
                Id: candidate.Id,
                ConceptId: record.ConceptId,
                ReleaseGroupId: candidate.ReleaseGroupId,
                Title: candidate.Title,
                ArtistName: candidate.ArtistName,
                ReleaseDate: candidate.ReleaseDate,
                DateLabel: candidate.DateLabel,
                CountryCode: candidate.CountryCode,
                Label: candidate.Label,
                Packaging: candidate.Packaging,
                Format: candidate.Format,
                TrackCount: candidate.TrackCount,
                MediumCount: candidate.MediumCount,
                IsOfficial: candidate.IsOfficial,
                IsDigitalOnly: candidate.IsDigitalOnly,
                CatalogState: candidate.CatalogState,
                PackageCompleteness: candidate.PackageCompleteness))
            .ToArray();

        var requestLink = new AtlasCatalogRequestLinkRecord(
            RequestAlbumId: record.Id,
            ConceptId: record.ConceptId,
            SelectedReleaseId: record.ReleaseCandidates?.FirstOrDefault()?.Id);

        var sourceObservations = BuildSourceObservations(record);
        var resolutionRecords = BuildResolutionRecords(record);

        var concepts = RemoveSupersededConcept(document, supersededConceptId);
        var releasesAfterRemoval = RemoveSupersededReleases(document, supersededConceptId);
        var requestLinksAfterRemoval = RemoveSupersededRequestLinks(document, supersededConceptId);
        var observationsAfterRemoval = RemoveSupersededObservations(document, supersededConceptId);
        var resolutionAfterRemoval = RemoveSupersededResolutionRecords(document, supersededConceptId);

        return new AtlasCatalogDocument(
            Concepts: UpsertConcept(concepts, concept),
            Releases: UpsertReleases(releasesAfterRemoval, releases),
            Artists: document.Artists,
            Labels: document.Labels,
            Places: document.Places,
            RequestLinks: UpsertRequestLinks(requestLinksAfterRemoval, requestLink),
            SourceObservations: UpsertSourceObservations(observationsAfterRemoval, sourceObservations),
            ResolutionRecords: UpsertResolutionRecords(resolutionAfterRemoval, resolutionRecords));
    }

    private static AtlasCatalogDocument UpsertIngestResult(
        AtlasCatalogDocument document,
        AtlasRequestIngestResult result)
    {
        var existingRequestLink = document.RequestLinks.FirstOrDefault(
            link => string.Equals(link.RequestAlbumId, result.RequestLink.RequestAlbumId, StringComparison.Ordinal));
        var supersededConceptId =
            existingRequestLink is not null &&
            !string.Equals(existingRequestLink.ConceptId, result.Concept.Id, StringComparison.Ordinal)
                ? existingRequestLink.ConceptId
                : null;

        var concepts = RemoveSupersededConcept(document, supersededConceptId);
        var releasesAfterRemoval = RemoveSupersededReleases(document, supersededConceptId);
        var requestLinksAfterRemoval = RemoveSupersededRequestLinks(document, supersededConceptId);
        var observationsAfterRemoval = RemoveSupersededObservations(document, supersededConceptId);
        var resolutionAfterRemoval = RemoveSupersededResolutionRecords(document, supersededConceptId);

        return new AtlasCatalogDocument(
            Concepts: UpsertConcept(concepts, result.Concept),
            Releases: UpsertReleases(releasesAfterRemoval, result.Releases),
            Artists: document.Artists,
            Labels: document.Labels,
            Places: document.Places,
            RequestLinks: UpsertRequestLinks(requestLinksAfterRemoval, result.RequestLink),
            SourceObservations: UpsertSourceObservations(observationsAfterRemoval, result.SourceObservations),
            ResolutionRecords: UpsertResolutionRecords(resolutionAfterRemoval, result.ResolutionRecords));
    }

    private static AtlasCatalogConceptRecord? FindMatchingConcept(
        AtlasCatalogDocument document,
        AtlasAcquisitionRequestRecord request)
    {
        var requestLink = document.RequestLinks.FirstOrDefault(
            link => string.Equals(link.RequestAlbumId, request.AlbumId, StringComparison.Ordinal));
        if (requestLink is not null)
        {
            return FindConceptById(document, requestLink.ConceptId);
        }

        return document.Concepts.FirstOrDefault(
            concept =>
                string.Equals(concept.Id, request.ConceptId, StringComparison.Ordinal) ||
                (concept.ReleaseGroupId is not null &&
                 string.Equals(concept.ReleaseGroupId, request.ConceptId, StringComparison.Ordinal)) ||
                (Normalize(concept.Title) == Normalize(request.Title) &&
                 Normalize(concept.ArtistName) == Normalize(request.ArtistName)));
    }

    private static bool NeedsRefresh(
        AtlasCatalogConceptRecord concept,
        AtlasAcquisitionRequestRecord request)
    {
        if (string.Equals(concept.CatalogState, "conflict_or_review_needed", StringComparison.Ordinal))
        {
            return true;
        }

        if (concept.ReleaseGroupId is null)
        {
            return true;
        }

        if (string.Equals(concept.Id, request.ConceptId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<AtlasCatalogConceptRecord> UpsertConcept(
        IReadOnlyList<AtlasCatalogConceptRecord> concepts,
        AtlasCatalogConceptRecord concept)
    {
        var items = concepts.ToList();
        var existingIndex = items.FindIndex(item => item.Id == concept.Id);
        if (existingIndex >= 0)
        {
            items[existingIndex] = concept;
        }
        else
        {
            items.Add(concept);
        }

        return items;
    }

    private static IReadOnlyList<AtlasCatalogReleaseRecord> UpsertReleases(
        IReadOnlyList<AtlasCatalogReleaseRecord> existing,
        IReadOnlyList<AtlasCatalogReleaseRecord> incoming)
    {
        var items = existing.ToDictionary(release => release.Id, release => release);
        foreach (var release in incoming)
        {
            items[release.Id] = release;
        }

        return items.Values.ToArray();
    }

    private static IReadOnlyList<AtlasCatalogRequestLinkRecord> UpsertRequestLinks(
        IReadOnlyList<AtlasCatalogRequestLinkRecord> existing,
        AtlasCatalogRequestLinkRecord incoming)
    {
        var items = existing.ToDictionary(link => link.RequestAlbumId, link => link);
        items[incoming.RequestAlbumId] = incoming;
        return items.Values.ToArray();
    }

    private static IReadOnlyList<AtlasCatalogSourceObservation> UpsertSourceObservations(
        IReadOnlyList<AtlasCatalogSourceObservation> existing,
        IReadOnlyList<AtlasCatalogSourceObservation> incoming)
    {
        var items = existing.ToDictionary(observation => observation.Id, observation => observation);
        foreach (var observation in incoming)
        {
            items[observation.Id] = observation;
        }

        return items.Values.ToArray();
    }

    private static IReadOnlyList<AtlasCatalogResolutionRecord> UpsertResolutionRecords(
        IReadOnlyList<AtlasCatalogResolutionRecord> existing,
        IReadOnlyList<AtlasCatalogResolutionRecord> incoming)
    {
        var items = existing.ToDictionary(record => record.Id, record => record);
        foreach (var record in incoming)
        {
            items[record.Id] = record;
        }

        return items.Values.ToArray();
    }

    private static IReadOnlyList<AtlasCatalogConceptRecord> RemoveSupersededConcept(
        AtlasCatalogDocument document,
        string? supersededConceptId)
        => string.IsNullOrWhiteSpace(supersededConceptId)
            ? document.Concepts
            : document.Concepts
                .Where(concept => !string.Equals(concept.Id, supersededConceptId, StringComparison.Ordinal))
                .ToArray();

    private static IReadOnlyList<AtlasCatalogReleaseRecord> RemoveSupersededReleases(
        AtlasCatalogDocument document,
        string? supersededConceptId)
        => string.IsNullOrWhiteSpace(supersededConceptId)
            ? document.Releases
            : document.Releases
                .Where(release => !string.Equals(release.ConceptId, supersededConceptId, StringComparison.Ordinal))
                .ToArray();

    private static IReadOnlyList<AtlasCatalogRequestLinkRecord> RemoveSupersededRequestLinks(
        AtlasCatalogDocument document,
        string? supersededConceptId)
        => string.IsNullOrWhiteSpace(supersededConceptId)
            ? document.RequestLinks
            : document.RequestLinks
                .Where(link => !string.Equals(link.ConceptId, supersededConceptId, StringComparison.Ordinal))
                .ToArray();

    private static IReadOnlyList<AtlasCatalogSourceObservation> RemoveSupersededObservations(
        AtlasCatalogDocument document,
        string? supersededConceptId)
        => string.IsNullOrWhiteSpace(supersededConceptId)
            ? document.SourceObservations
            : document.SourceObservations
                .Where(observation => !string.Equals(observation.EntityId, supersededConceptId, StringComparison.Ordinal))
                .ToArray();

    private static IReadOnlyList<AtlasCatalogResolutionRecord> RemoveSupersededResolutionRecords(
        AtlasCatalogDocument document,
        string? supersededConceptId)
        => string.IsNullOrWhiteSpace(supersededConceptId)
            ? document.ResolutionRecords
            : document.ResolutionRecords
                .Where(record => !string.Equals(record.ConceptId, supersededConceptId, StringComparison.Ordinal))
                .ToArray();

    private static IReadOnlyList<AtlasCatalogSourceObservation> BuildSourceObservations(
        LegacyMaterializedRequestRecord record)
    {
        var observations = new List<AtlasCatalogSourceObservation>();
        var observedAt = DateTimeOffset.UtcNow.ToString("O");

        if (record.ExternalBinding?.ReleaseGroupId is not null)
        {
            observations.Add(
                new AtlasCatalogSourceObservation(
                    Id: $"{record.ConceptId}:release-group",
                    EntityType: "concept",
                    EntityId: record.ConceptId,
                    Source: record.ExternalBinding.Provider,
                    Field: "releaseGroupId",
                    Value: record.ExternalBinding.ReleaseGroupId,
                    ObservedAt: observedAt));
        }

        foreach (var candidate in record.ReleaseCandidates ?? Array.Empty<AtlasCatalogReleaseCandidate>())
        {
            observations.Add(
                new AtlasCatalogSourceObservation(
                    Id: $"{candidate.Id}:release-date",
                    EntityType: "release",
                    EntityId: candidate.Id,
                    Source: "musicbrainz",
                    Field: "releaseDate",
                    Value: candidate.ReleaseDate,
                    ObservedAt: observedAt));
        }

        return observations;
    }

    private static IReadOnlyList<AtlasCatalogResolutionRecord> BuildResolutionRecords(
        LegacyMaterializedRequestRecord record)
        => (record.ProviderCandidates ?? Array.Empty<AtlasProviderCandidateRecord>())
            .Select(candidate => new AtlasCatalogResolutionRecord(
                Id: candidate.Id,
                ConceptId: record.ConceptId,
                ReleaseId: candidate.ReleaseId,
                Provider: candidate.Provider,
                ProviderSource: candidate.ProviderSource,
                ProviderAlbumId: candidate.ProviderAlbumId,
                DecisionState: candidate.DecisionState,
                ScoreTotal: candidate.ScoreTotal,
                DecisiveFactors: candidate.DecisiveFactors,
                Reason: candidate.Description,
                EvaluatedAt: DateTimeOffset.UtcNow.ToString("O")))
            .ToArray();

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();

    private sealed record LegacyMaterializedRequestRecord(
        string Id,
        string ConceptId,
        string Title,
        string ArtistName,
        string ArtistKey,
        int? Year,
        string? ImageUrl,
        AtlasCanonicalDateResponse CanonicalDate,
        string Availability,
        string CatalogState,
        string SourceKind,
        AtlasExternalBindingResponse? ExternalBinding,
        IReadOnlyList<AtlasCatalogReleaseCandidate>? ReleaseCandidates = null,
        IReadOnlyList<AtlasProviderCandidateRecord>? ProviderCandidates = null);
}

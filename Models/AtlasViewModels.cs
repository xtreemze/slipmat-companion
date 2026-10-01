using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

public record AtlasPackageSurface(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("isRepresentative")] bool IsRepresentative
);

public record AtlasPlaybackOffer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("availabilityType")] string AvailabilityType,
    [property: JsonPropertyName("decisionState")] string DecisionState,
    [property: JsonPropertyName("scoreTotal")] double ScoreTotal,
    [property: JsonPropertyName("isPlayable")] bool IsPlayable,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("decisiveFactors")] IReadOnlyList<string>? DecisiveFactors = null
);

public record AtlasReleaseSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conceptId")] string ConceptId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("dateLabel")] string DateLabel,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("packaging")] string Packaging,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("formatSummary")] string FormatSummary,
    [property: JsonPropertyName("trackCount")] int TrackCount,
    [property: JsonPropertyName("mediumCount")] int MediumCount,
    [property: JsonPropertyName("isOfficial")] bool IsOfficial,
    [property: JsonPropertyName("isDigitalOnly")] bool IsDigitalOnly,
    [property: JsonPropertyName("isPlayable")] bool IsPlayable,
    [property: JsonPropertyName("catalogState")] string CatalogState,
    [property: JsonPropertyName("packageCompleteness")] string PackageCompleteness,
    [property: JsonPropertyName("packageAssets")] IReadOnlyList<AtlasPackageSurface> PackageAssets,
    [property: JsonPropertyName("audioOffers")] IReadOnlyList<AtlasPlaybackOffer> AudioOffers
);

public record AtlasArtifactViewModel(
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("selectedSurface")] string SelectedSurface,
    [property: JsonPropertyName("surfaces")] IReadOnlyList<AtlasPackageSurface> Surfaces,
    [property: JsonPropertyName("missingPhysicalPackage")] bool MissingPhysicalPackage,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("isDigitalOnly")] bool IsDigitalOnly
);

public record AtlasPlaybackViewModel(
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("bestOffer")] AtlasPlaybackOffer? BestOffer,
    [property: JsonPropertyName("alternateOffers")] IReadOnlyList<AtlasPlaybackOffer> AlternateOffers,
    [property: JsonPropertyName("matchConfidenceLabel")] string MatchConfidenceLabel,
    [property: JsonPropertyName("requestable")] bool Requestable,
    [property: JsonPropertyName("decisionState")] string DecisionState,
    [property: JsonPropertyName("requestTargetId")] string RequestTargetId
);

public record AtlasProvenanceObservation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("observedAt")] string ObservedAt
);

public record AtlasProvenanceViewModel(
    [property: JsonPropertyName("conceptObservations")] IReadOnlyList<AtlasProvenanceObservation> ConceptObservations,
    [property: JsonPropertyName("selectedReleaseObservations")] IReadOnlyList<AtlasProvenanceObservation> SelectedReleaseObservations
);

public record AtlasConceptDetailResponse(
    [property: JsonPropertyName("conceptId")] string ConceptId,
    [property: JsonPropertyName("conceptTitle")] string ConceptTitle,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("releaseGroupId")] string? ReleaseGroupId,
    [property: JsonPropertyName("catalogState")] string CatalogState,
    [property: JsonPropertyName("sourceUrl")] string? SourceUrl,
    [property: JsonPropertyName("relatedPeople")] IReadOnlyList<string> RelatedPeople,
    [property: JsonPropertyName("studios")] IReadOnlyList<string> Studios,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("genres")] IReadOnlyList<string> Genres,
    [property: JsonPropertyName("eras")] IReadOnlyList<string> Eras,
    [property: JsonPropertyName("releases")] IReadOnlyList<AtlasReleaseSummary> Releases,
    [property: JsonPropertyName("selectedReleaseId")] string SelectedReleaseId,
    [property: JsonPropertyName("artifact")] AtlasArtifactViewModel Artifact,
    [property: JsonPropertyName("playback")] AtlasPlaybackViewModel Playback,
    [property: JsonPropertyName("provenance")] AtlasProvenanceViewModel Provenance
);

public record AtlasCanonicalDateResponse(
    [property: JsonPropertyName("isoDate")] string IsoDate,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("granularity")] string Granularity
);

public record AtlasExternalBindingResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("releaseGroupId")] string? ReleaseGroupId,
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("firstReleaseDate")] string? FirstReleaseDate,
    [property: JsonPropertyName("sourceUrl")] string? SourceUrl
);

public record AtlasCatalogConceptRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("releaseGroupId")] string? ReleaseGroupId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artistId")] string? ArtistId,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("artistKey")] string ArtistKey,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("canonicalDate")] AtlasCanonicalDateResponse CanonicalDate,
    [property: JsonPropertyName("availability")] string Availability,
    [property: JsonPropertyName("catalogState")] string CatalogState,
    [property: JsonPropertyName("sourceKind")] string SourceKind,
    [property: JsonPropertyName("sourceUrl")] string? SourceUrl,
    [property: JsonPropertyName("externalBinding")] AtlasExternalBindingResponse? ExternalBinding,
    [property: JsonPropertyName("providerCandidates")] IReadOnlyList<AtlasProviderCandidateRecord>? ProviderCandidates = null,
    [property: JsonPropertyName("labelIds")] IReadOnlyList<string>? LabelIds = null,
    [property: JsonPropertyName("placeIds")] IReadOnlyList<string>? PlaceIds = null,
    [property: JsonPropertyName("relatedPeople")] IReadOnlyList<string>? RelatedPeople = null
);

public record AtlasCatalogReleaseRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conceptId")] string ConceptId,
    [property: JsonPropertyName("releaseGroupId")] string? ReleaseGroupId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("dateLabel")] string DateLabel,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("packaging")] string? Packaging,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("trackCount")] int TrackCount,
    [property: JsonPropertyName("mediumCount")] int MediumCount,
    [property: JsonPropertyName("isOfficial")] bool IsOfficial,
    [property: JsonPropertyName("isDigitalOnly")] bool IsDigitalOnly,
    [property: JsonPropertyName("catalogState")] string CatalogState,
    [property: JsonPropertyName("packageCompleteness")] string PackageCompleteness
);

public record AtlasCatalogArtistRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mbid")] string? Mbid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sortName")] string? SortName
);

public record AtlasCatalogLabelRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mbid")] string? Mbid,
    [property: JsonPropertyName("name")] string Name
);

public record AtlasCatalogPlaceRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mbid")] string? Mbid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string? Kind
);

public record AtlasCatalogRequestLinkRecord(
    [property: JsonPropertyName("requestAlbumId")] string RequestAlbumId,
    [property: JsonPropertyName("conceptId")] string ConceptId,
    [property: JsonPropertyName("selectedReleaseId")] string? SelectedReleaseId
);

public record AtlasCatalogSourceObservation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("observedAt")] string ObservedAt
);

public record AtlasCatalogResolutionRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conceptId")] string ConceptId,
    [property: JsonPropertyName("releaseId")] string? ReleaseId,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("providerSource")] string? ProviderSource,
    [property: JsonPropertyName("providerAlbumId")] string? ProviderAlbumId,
    [property: JsonPropertyName("decisionState")] string DecisionState,
    [property: JsonPropertyName("scoreTotal")] double? ScoreTotal,
    [property: JsonPropertyName("decisiveFactors")] IReadOnlyList<string>? DecisiveFactors,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("evaluatedAt")] string EvaluatedAt
);

public record AtlasCatalogDocument(
    [property: JsonPropertyName("concepts")] IReadOnlyList<AtlasCatalogConceptRecord> Concepts,
    [property: JsonPropertyName("releases")] IReadOnlyList<AtlasCatalogReleaseRecord> Releases,
    [property: JsonPropertyName("artists")] IReadOnlyList<AtlasCatalogArtistRecord> Artists,
    [property: JsonPropertyName("labels")] IReadOnlyList<AtlasCatalogLabelRecord> Labels,
    [property: JsonPropertyName("places")] IReadOnlyList<AtlasCatalogPlaceRecord> Places,
    [property: JsonPropertyName("requestLinks")] IReadOnlyList<AtlasCatalogRequestLinkRecord> RequestLinks,
    [property: JsonPropertyName("sourceObservations")] IReadOnlyList<AtlasCatalogSourceObservation> SourceObservations,
    [property: JsonPropertyName("resolutionRecords")] IReadOnlyList<AtlasCatalogResolutionRecord> ResolutionRecords
);

public record AtlasCanonicalConceptProjection(
    [property: JsonPropertyName("concept")] AtlasCatalogConceptRecord Concept,
    [property: JsonPropertyName("releases")] IReadOnlyList<AtlasCatalogReleaseRecord> Releases,
    [property: JsonPropertyName("providerCandidates")] IReadOnlyList<AtlasProviderCandidateRecord> ProviderCandidates,
    [property: JsonPropertyName("resolutionRecords")] IReadOnlyList<AtlasCatalogResolutionRecord> ResolutionRecords,
    [property: JsonPropertyName("sourceObservations")] IReadOnlyList<AtlasCatalogSourceObservation> SourceObservations
);

public record AtlasCatalogReleaseCandidate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("releaseGroupId")] string ReleaseGroupId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("dateLabel")] string DateLabel,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("packaging")] string? Packaging,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("trackCount")] int TrackCount,
    [property: JsonPropertyName("mediumCount")] int MediumCount,
    [property: JsonPropertyName("isOfficial")] bool IsOfficial,
    [property: JsonPropertyName("isDigitalOnly")] bool IsDigitalOnly,
    [property: JsonPropertyName("catalogState")] string CatalogState,
    [property: JsonPropertyName("packageCompleteness")] string PackageCompleteness
);

public record AtlasProviderCandidateRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("providerSource")] string? ProviderSource,
    [property: JsonPropertyName("providerAlbumId")] string? ProviderAlbumId,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("releaseId")] string? ReleaseId,
    [property: JsonPropertyName("decisionState")] string DecisionState,
    [property: JsonPropertyName("scoreTotal")] double ScoreTotal,
    [property: JsonPropertyName("decisiveFactors")] IReadOnlyList<string>? DecisiveFactors = null
);

public record AtlasRequestIngestResult(
    [property: JsonPropertyName("concept")] AtlasCatalogConceptRecord Concept,
    [property: JsonPropertyName("releases")] IReadOnlyList<AtlasCatalogReleaseRecord> Releases,
    [property: JsonPropertyName("requestLink")] AtlasCatalogRequestLinkRecord RequestLink,
    [property: JsonPropertyName("providerCandidates")] IReadOnlyList<AtlasProviderCandidateRecord> ProviderCandidates,
    [property: JsonPropertyName("sourceObservations")] IReadOnlyList<AtlasCatalogSourceObservation> SourceObservations,
    [property: JsonPropertyName("resolutionRecords")] IReadOnlyList<AtlasCatalogResolutionRecord> ResolutionRecords
);

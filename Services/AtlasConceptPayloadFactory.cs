using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

public static class AtlasConceptPayloadFactory
{
    private static AtlasProvenanceViewModel BuildProvenance(
        AtlasCanonicalConceptProjection projection,
        string selectedReleaseId)
    {
        var conceptObservations = projection.SourceObservations
            .Where(observation => string.Equals(observation.EntityId, projection.Concept.Id, StringComparison.Ordinal))
            .Select(ToProvenanceObservation)
            .ToArray();
        var selectedReleaseObservations = projection.SourceObservations
            .Where(observation => string.Equals(observation.EntityId, selectedReleaseId, StringComparison.Ordinal))
            .Select(ToProvenanceObservation)
            .ToArray();

        return new AtlasProvenanceViewModel(
            ConceptObservations: conceptObservations,
            SelectedReleaseObservations: selectedReleaseObservations);
    }

    private static AtlasProvenanceObservation ToProvenanceObservation(
        AtlasCatalogSourceObservation observation)
        => new(
            Id: observation.Id,
            EntityType: observation.EntityType,
            EntityId: observation.EntityId,
            Source: observation.Source,
            Field: observation.Field,
            Value: observation.Value,
            ObservedAt: observation.ObservedAt);

    private static IReadOnlyList<AtlasProviderCandidateRecord> BuildProviderCandidates(
        AtlasCanonicalConceptProjection projection)
    {
        if (projection.ProviderCandidates.Count > 0)
        {
            return projection.ProviderCandidates;
        }

        if (projection.ResolutionRecords.Count > 0)
        {
            return projection.ResolutionRecords
                .Select(record => new AtlasProviderCandidateRecord(
                    Id: record.Id,
                    Provider: record.Provider ?? "streamrip",
                    ProviderSource: record.ProviderSource,
                    ProviderAlbumId: record.ProviderAlbumId,
                    Description: record.Reason,
                    ReleaseId: record.ReleaseId,
                    DecisionState: record.DecisionState,
                    ScoreTotal: record.ScoreTotal ?? 0,
                    DecisiveFactors: record.DecisiveFactors))
                .ToArray();
        }

        return projection.Concept.ProviderCandidates ?? Array.Empty<AtlasProviderCandidateRecord>();
    }

    private static AtlasPlaybackOffer BuildStreamripOffer(
        string offerId,
        AtlasProviderCandidateRecord? providerCandidate,
        bool matchesSelectedRelease)
    {
        var sourceLabel = providerCandidate?.ProviderSource switch
        {
            "qobuz" => "Qobuz",
            "tidal" => "Tidal",
            "deezer" => "Deezer",
            "soundcloud" => "SoundCloud",
            _ => "Provider",
        };

        var decisiveFactors = providerCandidate?.DecisiveFactors ?? Array.Empty<string>();
        var summary = providerCandidate?.Description ??
            "Search configured services through streamrip after signing in with provider credentials in the streamrip config.";
        if (decisiveFactors.Count > 0)
        {
            summary = $"{summary} Reasons: {string.Join("; ", decisiveFactors)}.";
        }

        return new AtlasPlaybackOffer(
            Id: offerId,
            Provider: "streamrip",
            Label: providerCandidate?.ProviderSource is null
                ? "Search with streamrip"
                : matchesSelectedRelease && providerCandidate?.ReleaseId is not null
                    ? $"{sourceLabel} edition candidate via streamrip"
                    : $"{sourceLabel} candidate via streamrip",
            AvailabilityType: "stream",
            DecisionState: matchesSelectedRelease
                ? providerCandidate?.DecisionState ?? "unresolved"
                : providerCandidate?.ReleaseId is not null
                    ? "group_linked"
                    : providerCandidate?.DecisionState ?? "unresolved",
            ScoreTotal: providerCandidate?.ScoreTotal ?? 0.4,
            IsPlayable: false,
            Summary: summary,
            DecisiveFactors: decisiveFactors);
    }

    private static List<AtlasPackageSurface> BuildRepresentativeSurfaces(
        string releaseId,
        string? imageUrl,
        string sourceKind)
        => new()
        {
            new(
                Id: $"{releaseId}:front",
                Kind: "front",
                Label: imageUrl is null ? "Representative concept cover" : "Front cover",
                ImageUrl: imageUrl,
                Source: sourceKind,
                IsRepresentative: true)
        };

    private static IReadOnlyList<AtlasReleaseSummary> BuildReleaseSummaries(
        AtlasCatalogConceptRecord concept,
        IReadOnlyList<AtlasCatalogReleaseRecord> releases,
        IReadOnlyList<AtlasProviderCandidateRecord> providerCandidates)
    {
        var providerCandidate = providerCandidates.FirstOrDefault();
        var summaries = releases
            .Select(release =>
            {
                var surfaces = BuildRepresentativeSurfaces(release.Id, concept.ImageUrl, concept.SourceKind);
                var matchesSelectedRelease = providerCandidate?.ReleaseId == release.Id;
                var offers = new List<AtlasPlaybackOffer>
                {
                    BuildStreamripOffer($"{release.Id}:streamrip", providerCandidate, matchesSelectedRelease)
                };

                return new AtlasReleaseSummary(
                    Id: release.Id,
                    ConceptId: concept.Id,
                    Title: release.Title,
                    ArtistName: release.ArtistName,
                    ReleaseDate: release.ReleaseDate,
                    DateLabel: release.DateLabel,
                    CountryCode: release.CountryCode,
                    Label: release.Label,
                    Packaging: release.Packaging ?? "Edition",
                    Format: release.Format,
                    FormatSummary: release.TrackCount > 0
                        ? release.MediumCount > 1
                            ? $"{release.TrackCount} tracks across {release.MediumCount} media"
                            : $"{release.TrackCount} tracks"
                        : "Catalog reference edition",
                    TrackCount: release.TrackCount,
                    MediumCount: release.MediumCount,
                    IsOfficial: release.IsOfficial,
                    IsDigitalOnly: release.IsDigitalOnly,
                    IsPlayable: false,
                    CatalogState: release.CatalogState,
                    PackageCompleteness: release.PackageCompleteness,
                    PackageAssets: surfaces,
                    AudioOffers: offers);
            })
            .ToList();

        if (summaries.Count == 0)
        {
            var fallbackReleaseId = $"release:{concept.Id}:reference";
            var surfaces = BuildRepresentativeSurfaces(fallbackReleaseId, concept.ImageUrl, concept.SourceKind);
            var offers = new List<AtlasPlaybackOffer>
            {
                BuildStreamripOffer(
                    $"{fallbackReleaseId}:streamrip",
                    providerCandidate,
                    providerCandidate?.ReleaseId == fallbackReleaseId)
            };
            summaries.Add(
                new AtlasReleaseSummary(
                    Id: fallbackReleaseId,
                    ConceptId: concept.Id,
                    Title: concept.Title,
                    ArtistName: concept.ArtistName,
                    ReleaseDate: concept.CanonicalDate.IsoDate,
                    DateLabel: concept.Year?.ToString() ?? concept.CanonicalDate.IsoDate[..4],
                    CountryCode: null,
                    Label: null,
                    Packaging: "Digital-only release",
                    Format: "Album concept",
                    FormatSummary: "Catalog reference edition",
                    TrackCount: 0,
                    MediumCount: 1,
                    IsOfficial: true,
                    IsDigitalOnly: true,
                    IsPlayable: false,
                    CatalogState: concept.CatalogState,
                    PackageCompleteness: "none",
                    PackageAssets: surfaces,
                    AudioOffers: offers));
        }

        return summaries;
    }

    private static string BuildMatchConfidenceLabel(
        AtlasProviderCandidateRecord? providerCandidate,
        string selectedReleaseId)
        => providerCandidate is null
            ? "Search via streamrip"
            : providerCandidate.DecisionState == "review_needed"
                ? "Provider candidate needs review before exact edition mapping"
                : providerCandidate.ReleaseId == selectedReleaseId
                    ? "Provider candidate mapped to selected edition via streamrip"
                    : "Provider candidate via streamrip; edition not guaranteed";

    private static string BuildPlaybackDecisionState(
        AtlasProviderCandidateRecord? providerCandidate,
        string selectedReleaseId)
        => providerCandidate?.ReleaseId == selectedReleaseId
            ? providerCandidate?.DecisionState ?? "unresolved"
            : providerCandidate?.DecisionState == "review_needed"
                ? "review_needed"
                : providerCandidate?.ReleaseId is not null
                    ? "group_linked"
                    : providerCandidate?.DecisionState ?? "unresolved";

    public static AtlasConceptDetailResponse CreateConceptDetail(
        AtlasCanonicalConceptProjection projection,
        string? selectedReleaseId,
        string? selectedSurface)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var providerCandidates = BuildProviderCandidates(projection);
        var releases = BuildReleaseSummaries(
            projection.Concept,
            projection.Releases,
            providerCandidates);
        var selectedRelease = releases.FirstOrDefault(release => release.Id == selectedReleaseId) ?? releases[0];
        var resolvedSelectedSurface = selectedRelease.PackageAssets.FirstOrDefault(surface => surface.Kind == selectedSurface)?.Kind
            ?? selectedRelease.PackageAssets[0].Kind;
        var providerCandidate = providerCandidates.FirstOrDefault();

        return new AtlasConceptDetailResponse(
            ConceptId: projection.Concept.Id,
            ConceptTitle: projection.Concept.Title,
            ArtistName: projection.Concept.ArtistName,
            ReleaseGroupId: projection.Concept.ReleaseGroupId,
            CatalogState: projection.Concept.CatalogState,
            SourceUrl: projection.Concept.ExternalBinding?.SourceUrl ?? projection.Concept.SourceUrl,
            RelatedPeople: new[] { "People trace pending" },
            Studios: new[] { "Studio trace pending" },
            Labels: Array.Empty<string>(),
            Genres: Array.Empty<string>(),
            Eras: projection.Concept.Year is int year ? new[] { $"{year / 10 * 10}s" } : Array.Empty<string>(),
            Releases: releases,
            SelectedReleaseId: selectedRelease.Id,
            Artifact: new AtlasArtifactViewModel(
                ReleaseId: selectedRelease.Id,
                SelectedSurface: resolvedSelectedSurface,
                Surfaces: selectedRelease.PackageAssets,
                MissingPhysicalPackage: selectedRelease.PackageCompleteness != "complete" || selectedRelease.IsDigitalOnly,
                Note: selectedRelease.IsDigitalOnly
                    ? "Digital-only release. Atlas keeps the artifact view explicit about missing physical packaging."
                    : selectedRelease.PackageCompleteness == "complete"
                        ? "Package surfaces are complete for this edition."
                        : "Package coverage is partial for this edition.",
                IsDigitalOnly: selectedRelease.IsDigitalOnly),
            Playback: new AtlasPlaybackViewModel(
                ReleaseId: selectedRelease.Id,
                BestOffer: selectedRelease.AudioOffers[0],
                AlternateOffers: Array.Empty<AtlasPlaybackOffer>(),
                MatchConfidenceLabel: BuildMatchConfidenceLabel(providerCandidate, selectedRelease.Id),
                Requestable: true,
                DecisionState: BuildPlaybackDecisionState(providerCandidate, selectedRelease.Id),
                RequestTargetId: projection.Concept.Id),
            Provenance: BuildProvenance(projection, selectedRelease.Id));
    }

    public static AtlasConceptDetailResponse CreateConceptDetail(
        string conceptId,
        string? albumId,
        string? releaseGroupId,
        string? selectedReleaseId,
        string? selectedSurface)
    {
        var canonicalConceptId = !string.IsNullOrWhiteSpace(releaseGroupId)
            ? releaseGroupId
            : conceptId;
        var localReleaseId = !string.IsNullOrWhiteSpace(albumId)
            ? $"release:{albumId}"
            : $"release:{canonicalConceptId}:reference";
        var referenceReleaseId = $"release:{canonicalConceptId}:reference";

        var localSurfaces = new List<AtlasPackageSurface>
        {
            new(
                Id: $"{localReleaseId}:front",
                Kind: "front",
                Label: "Front cover",
                ImageUrl: !string.IsNullOrWhiteSpace(albumId) ? $"/Items/{albumId}/Images/Primary" : null,
                Source: !string.IsNullOrWhiteSpace(albumId) ? "library" : "musicbrainz",
                IsRepresentative: true)
        };

        var localOffers = !string.IsNullOrWhiteSpace(albumId)
            ? new List<AtlasPlaybackOffer>
            {
                new(
                    Id: $"{localReleaseId}:local",
                    Provider: "local",
                    Label: "Local library playback",
                    AvailabilityType: "local",
                    DecisionState: !string.IsNullOrWhiteSpace(releaseGroupId) ? "group_linked" : "unresolved",
                    ScoreTotal: 1.0,
                    IsPlayable: true,
                    Summary: "Local album available in the library")
            }
            : new List<AtlasPlaybackOffer>();

        var streamripOffers = string.IsNullOrWhiteSpace(albumId)
            ? new List<AtlasPlaybackOffer>
            {
                new(
                    Id: $"{referenceReleaseId}:streamrip",
                    Provider: "streamrip",
                    Label: "Search with streamrip",
                    AvailabilityType: "stream",
                    DecisionState: "unresolved",
                    ScoreTotal: 0.4,
                    IsPlayable: false,
                    Summary: "Search configured services through streamrip after signing in with provider credentials in the streamrip config.")
            }
            : new List<AtlasPlaybackOffer>();

        var releases = new List<AtlasReleaseSummary>
        {
            new(
                Id: localReleaseId,
                ConceptId: canonicalConceptId,
                Title: "Atlas concept edition",
                ArtistName: "Unknown artist",
                ReleaseDate: "1970-01-01",
                DateLabel: "1970",
                CountryCode: null,
                Label: null,
                Packaging: !string.IsNullOrWhiteSpace(albumId) ? "Library issue" : "Digital release",
                Format: !string.IsNullOrWhiteSpace(albumId) ? "Album" : "Album concept",
                FormatSummary: !string.IsNullOrWhiteSpace(albumId) ? "Playable library edition" : "Catalog-only edition",
                TrackCount: !string.IsNullOrWhiteSpace(albumId) ? 8 : 0,
                MediumCount: 1,
                IsOfficial: true,
                IsDigitalOnly: string.IsNullOrWhiteSpace(albumId),
                IsPlayable: !string.IsNullOrWhiteSpace(albumId),
                CatalogState: !string.IsNullOrWhiteSpace(albumId) ? "playable" : "catalog_only",
                PackageCompleteness: !string.IsNullOrWhiteSpace(albumId) ? "partial" : "none",
                PackageAssets: localSurfaces,
                AudioOffers: !string.IsNullOrWhiteSpace(albumId) ? localOffers : streamripOffers)
        };

        if (!string.Equals(referenceReleaseId, localReleaseId, StringComparison.Ordinal))
        {
            releases.Add(
                new AtlasReleaseSummary(
                    Id: referenceReleaseId,
                    ConceptId: canonicalConceptId,
                    Title: "Reference concept edition",
                    ArtistName: "Unknown artist",
                    ReleaseDate: "1970-01-01",
                    DateLabel: "1970",
                    CountryCode: null,
                    Label: null,
                    Packaging: "Representative release",
                    Format: "Album concept",
                    FormatSummary: "Catalog reference edition",
                    TrackCount: 0,
                    MediumCount: 1,
                    IsOfficial: true,
                    IsDigitalOnly: true,
                    IsPlayable: false,
                    CatalogState: "catalog_only",
                    PackageCompleteness: "none",
                    PackageAssets: new List<AtlasPackageSurface>
                    {
                        new(
                            Id: $"{referenceReleaseId}:front",
                            Kind: "front",
                            Label: "Representative concept cover",
                            ImageUrl: null,
                            Source: "musicbrainz",
                            IsRepresentative: true)
                    },
                    AudioOffers: streamripOffers));
        }

        var selectedRelease = releases.FirstOrDefault(release => release.Id == selectedReleaseId) ?? releases[0];
        var selectedArtifactSurface = selectedRelease.PackageAssets.FirstOrDefault(surface => surface.Kind == selectedSurface)?.Kind
            ?? selectedRelease.PackageAssets.FirstOrDefault()?.Kind
            ?? "front";
        var bestOffer = selectedRelease.AudioOffers.FirstOrDefault();

        return new AtlasConceptDetailResponse(
            ConceptId: canonicalConceptId,
            ConceptTitle: "Atlas concept",
            ArtistName: "Unknown artist",
            ReleaseGroupId: releaseGroupId,
            CatalogState: selectedRelease.CatalogState,
            SourceUrl: !string.IsNullOrWhiteSpace(releaseGroupId)
                ? $"https://musicbrainz.org/release-group/{releaseGroupId}"
                : null,
            RelatedPeople: new[] { "Producer trace ready" },
            Studios: new[] { "Studio trace ready" },
            Labels: Array.Empty<string>(),
            Genres: Array.Empty<string>(),
            Eras: new[] { "1970s" },
            Releases: releases,
            SelectedReleaseId: selectedRelease.Id,
            Artifact: new AtlasArtifactViewModel(
                ReleaseId: selectedRelease.Id,
                SelectedSurface: selectedArtifactSurface,
                Surfaces: selectedRelease.PackageAssets,
                MissingPhysicalPackage: selectedRelease.PackageCompleteness != "complete",
                Note: selectedRelease.IsPlayable
                    ? "Package coverage is partial for this edition."
                    : "Digital-only release. Atlas keeps the artifact view explicit about missing physical packaging.",
                IsDigitalOnly: !selectedRelease.IsPlayable),
            Playback: new AtlasPlaybackViewModel(
                ReleaseId: selectedRelease.Id,
                BestOffer: bestOffer,
                AlternateOffers: selectedRelease.AudioOffers.Skip(1).ToArray(),
                MatchConfidenceLabel: bestOffer?.Provider == "streamrip"
                    ? "Search via streamrip"
                    : bestOffer is null
                    ? "No linked playback offer yet"
                    : "Concept match, edition not guaranteed",
                Requestable: bestOffer is null || !bestOffer.IsPlayable,
                DecisionState: bestOffer?.DecisionState ?? "unresolved",
                RequestTargetId: canonicalConceptId),
            Provenance: new AtlasProvenanceViewModel(
                ConceptObservations: Array.Empty<AtlasProvenanceObservation>(),
                SelectedReleaseObservations: Array.Empty<AtlasProvenanceObservation>()));
    }
}

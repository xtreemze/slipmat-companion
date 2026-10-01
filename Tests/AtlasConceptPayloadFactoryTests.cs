using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class AtlasConceptPayloadFactoryTests
{
    [Fact]
    public void CreateConceptDetail_PrefersReleaseGroupIdentityAndPlayableLocalRelease()
    {
        var detail = AtlasConceptPayloadFactory.CreateConceptDetail(
            conceptId: "album-1",
            albumId: "album-1",
            releaseGroupId: "blue-train-group",
            selectedReleaseId: null,
            selectedSurface: null);

        Assert.Equal("blue-train-group", detail.ConceptId);
        Assert.Equal("release:album-1", detail.SelectedReleaseId);
        Assert.Equal("playable", detail.CatalogState);
        Assert.NotNull(detail.Playback.BestOffer);
        Assert.False(detail.Playback.Requestable);
        Assert.Equal("front", detail.Artifact.SelectedSurface);
    }

    [Fact]
    public void CreateConceptDetail_StaysRequestableWhenNoLocalAlbumExists()
    {
        var detail = AtlasConceptPayloadFactory.CreateConceptDetail(
            conceptId: "interstellar-space",
            albumId: null,
            releaseGroupId: "interstellar-space",
            selectedReleaseId: null,
            selectedSurface: null);

        Assert.Equal("interstellar-space", detail.ConceptId);
        Assert.True(detail.Playback.Requestable);
        Assert.NotNull(detail.Playback.BestOffer);
        Assert.Equal("streamrip", detail.Playback.BestOffer?.Provider);
        Assert.Equal("Search via streamrip", detail.Playback.MatchConfidenceLabel);
        Assert.Equal("catalog_only", detail.CatalogState);
        Assert.NotEmpty(detail.Releases);
    }

    [Fact]
    public void CreateConceptDetail_UsesCatalogRecordForRequestedConcepts()
    {
        var detail = AtlasConceptPayloadFactory.CreateConceptDetail(
            BuildRequestedProjection(
                conceptId: "mb-release-group:interstellar-space",
                releaseGroupId: "interstellar-space",
                title: "Interstellar Space",
                artistName: "John Coltrane",
                year: 1967,
                providerCandidates:
                [
                    new AtlasProviderCandidateRecord(
                        Id: "streamrip:qobuz:interstellar-space",
                        Provider: "streamrip",
                        ProviderSource: "qobuz",
                        ProviderAlbumId: "interstellar-space",
                        Description: "John Coltrane - Interstellar Space",
                        ReleaseId: "release:interstellar-space:digital",
                        DecisionState: "group_linked",
                        ScoreTotal: 0.82,
                        DecisiveFactors:
                        [
                            "Matched release group and artist identity",
                            "Edition-specific evidence is still incomplete",
                        ])
                ],
                releases:
                [
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:impulse",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "1974-01-01",
                        DateLabel: "1974",
                        CountryCode: "US",
                        Label: "Impulse!",
                        Packaging: "Jewel Case",
                        Format: "CD",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: false,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none"),
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:digital",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space (Digital)",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "2020-01-01",
                        DateLabel: "2020",
                        CountryCode: "XW",
                        Label: null,
                        Packaging: "None",
                        Format: "Digital Media",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: true,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none")
                ]),
            selectedReleaseId: null,
            selectedSurface: null);

        Assert.Equal("mb-release-group:interstellar-space", detail.ConceptId);
        Assert.Equal("Interstellar Space", detail.ConceptTitle);
        Assert.Equal("John Coltrane", detail.ArtistName);
        Assert.Equal("interstellar-space", detail.ReleaseGroupId);
        Assert.Equal(2, detail.Releases.Count);
        Assert.Equal("Jewel Case", detail.Releases[0].Packaging);
        Assert.Equal("Impulse!", detail.Releases[0].Label);
        Assert.Equal("release:interstellar-space:impulse", detail.SelectedReleaseId);
        Assert.False(detail.Artifact.IsDigitalOnly);
        Assert.Equal("Qobuz candidate via streamrip", detail.Playback.BestOffer?.Label);
        Assert.Equal("Provider candidate via streamrip; edition not guaranteed", detail.Playback.MatchConfidenceLabel);
        Assert.Equal("group_linked", detail.Playback.DecisionState);
        Assert.True(detail.Playback.Requestable);

        var digitalDetail = AtlasConceptPayloadFactory.CreateConceptDetail(
            BuildRequestedProjection(
                conceptId: "mb-release-group:interstellar-space",
                releaseGroupId: "interstellar-space",
                title: "Interstellar Space",
                artistName: "John Coltrane",
                year: 1967,
                providerCandidates:
                [
                    new AtlasProviderCandidateRecord(
                        Id: "streamrip:qobuz:interstellar-space",
                        Provider: "streamrip",
                        ProviderSource: "qobuz",
                        ProviderAlbumId: "interstellar-space",
                        Description: "John Coltrane - Interstellar Space",
                        ReleaseId: "release:interstellar-space:digital",
                        DecisionState: "release_linked",
                        ScoreTotal: 0.92,
                        DecisiveFactors:
                        [
                            "Single official digital release candidate matched",
                            "Digital release metadata aligned strongly",
                        ])
                ],
                releases:
                [
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:impulse",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "1974-01-01",
                        DateLabel: "1974",
                        CountryCode: "US",
                        Label: "Impulse!",
                        Packaging: "Jewel Case",
                        Format: "CD",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: false,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none"),
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:digital",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space (Digital)",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "2020-01-01",
                        DateLabel: "2020",
                        CountryCode: "XW",
                        Label: null,
                        Packaging: "None",
                        Format: "Digital Media",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: true,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none")
                ]),
            selectedReleaseId: "release:interstellar-space:digital",
            selectedSurface: null);

        Assert.Equal("release:interstellar-space:digital", digitalDetail.SelectedReleaseId);
        Assert.Equal("Qobuz edition candidate via streamrip", digitalDetail.Playback.BestOffer?.Label);
        Assert.Equal("Provider candidate mapped to selected edition via streamrip", digitalDetail.Playback.MatchConfidenceLabel);
        Assert.Equal("release_linked", digitalDetail.Playback.DecisionState);
    }

    [Fact]
    public void CreateConceptDetail_ProjectsCanonicalConceptRecords()
    {
        var detail = AtlasConceptPayloadFactory.CreateConceptDetail(
            new AtlasCanonicalConceptProjection(
                Concept: new AtlasCatalogConceptRecord(
                    Id: "mb-release-group:interstellar-space",
                    ReleaseGroupId: "interstellar-space",
                    Title: "Interstellar Space",
                    ArtistId: null,
                    ArtistName: "John Coltrane",
                    ArtistKey: "john-coltrane",
                    Year: 1967,
                    ImageUrl: null,
                    CanonicalDate: new AtlasCanonicalDateResponse(
                        IsoDate: "1967-01-01",
                        Timestamp: -94694400000,
                        Confidence: "high",
                        Granularity: "day"),
                    Availability: "requested",
                    CatalogState: "catalog_only",
                    SourceKind: "musicbrainz",
                    SourceUrl: "https://musicbrainz.org/release-group/interstellar-space",
                    ExternalBinding: new AtlasExternalBindingResponse(
                        Provider: "musicbrainz",
                        ArtistName: "John Coltrane",
                        ReleaseGroupId: "interstellar-space",
                        Score: 98,
                        FirstReleaseDate: "1967-01-01",
                        SourceUrl: "https://musicbrainz.org/release-group/interstellar-space"),
                    ProviderCandidates:
                    [
                        new AtlasProviderCandidateRecord(
                            Id: "streamrip:qobuz:interstellar-space",
                            Provider: "streamrip",
                            ProviderSource: "qobuz",
                            ProviderAlbumId: "interstellar-space",
                            Description: "John Coltrane - Interstellar Space",
                            ReleaseId: "release:interstellar-space:digital",
                            DecisionState: "release_linked",
                            ScoreTotal: 0.92,
                            DecisiveFactors:
                            [
                                "Single official digital release candidate matched",
                                "Digital release metadata aligned strongly",
                            ])
                    ]),
                Releases:
                [
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:impulse",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "1974-01-01",
                        DateLabel: "1974",
                        CountryCode: "US",
                        Label: "Impulse!",
                        Packaging: "Jewel Case",
                        Format: "CD",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: false,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none"),
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:digital",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space (Digital)",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "2020-01-01",
                        DateLabel: "2020",
                        CountryCode: "XW",
                        Label: null,
                        Packaging: "None",
                        Format: "Digital Media",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: true,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none")
                ],
                ProviderCandidates:
                [
                    new AtlasProviderCandidateRecord(
                        Id: "streamrip:qobuz:interstellar-space",
                        Provider: "streamrip",
                        ProviderSource: "qobuz",
                        ProviderAlbumId: "interstellar-space",
                        Description: "John Coltrane - Interstellar Space",
                        ReleaseId: "release:interstellar-space:digital",
                        DecisionState: "release_linked",
                        ScoreTotal: 0.92,
                        DecisiveFactors:
                        [
                            "Single official digital release candidate matched",
                            "Digital release metadata aligned strongly",
                        ])
                ],
                ResolutionRecords: [],
                SourceObservations: []),
            selectedReleaseId: "release:interstellar-space:digital",
            selectedSurface: null);

        Assert.Equal("mb-release-group:interstellar-space", detail.ConceptId);
        Assert.Equal("interstellar-space", detail.ReleaseGroupId);
        Assert.Equal(2, detail.Releases.Count);
        Assert.Equal("release:interstellar-space:digital", detail.SelectedReleaseId);
        Assert.Equal("Qobuz edition candidate via streamrip", detail.Playback.BestOffer?.Label);
        Assert.Equal("release_linked", detail.Playback.DecisionState);
    }

    [Fact]
    public void CreateConceptDetail_SurfacesReviewBandForAmbiguousEditionCandidate()
    {
        var detail = AtlasConceptPayloadFactory.CreateConceptDetail(
            BuildRequestedProjection(
                conceptId: "mb-release-group:giant-steps",
                releaseGroupId: "giant-steps",
                title: "Giant Steps",
                artistName: "John Coltrane",
                year: 1960,
                providerCandidates:
                [
                    new AtlasProviderCandidateRecord(
                        Id: "streamrip:tidal:giant-steps",
                        Provider: "streamrip",
                        ProviderSource: "tidal",
                        ProviderAlbumId: "giant-steps",
                        Description: "John Coltrane - Giant Steps",
                        ReleaseId: null,
                        DecisionState: "review_needed",
                        ScoreTotal: 0.88,
                        DecisiveFactors:
                        [
                            "Multiple official digital releases matched this provider candidate",
                            "Exact edition mapping remains ambiguous",
                        ])
                ],
                releases:
                [
                    new AtlasCatalogReleaseRecord(
                        Id: "release:giant-steps:digital-a",
                        ConceptId: "mb-release-group:giant-steps",
                        ReleaseGroupId: "giant-steps",
                        Title: "Giant Steps (Digital A)",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "2019-01-01",
                        DateLabel: "2019",
                        CountryCode: "XW",
                        Label: null,
                        Packaging: "None",
                        Format: "Digital Media",
                        TrackCount: 7,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: true,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none"),
                    new AtlasCatalogReleaseRecord(
                        Id: "release:giant-steps:digital-b",
                        ConceptId: "mb-release-group:giant-steps",
                        ReleaseGroupId: "giant-steps",
                        Title: "Giant Steps (Digital B)",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "2020-01-01",
                        DateLabel: "2020",
                        CountryCode: "XW",
                        Label: null,
                        Packaging: "None",
                        Format: "Digital Media",
                        TrackCount: 7,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: true,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none")
                ]),
            selectedReleaseId: "release:giant-steps:digital-a",
            selectedSurface: null);

        Assert.Equal("Tidal candidate via streamrip", detail.Playback.BestOffer?.Label);
        Assert.Equal("Provider candidate needs review before exact edition mapping", detail.Playback.MatchConfidenceLabel);
        Assert.Equal("review_needed", detail.Playback.DecisionState);
        Assert.Equal(
            "John Coltrane - Giant Steps Reasons: Multiple official digital releases matched this provider candidate; Exact edition mapping remains ambiguous.",
            detail.Playback.BestOffer?.Summary);
    }

    [Fact]
    public void CreateConceptDetail_FallsBackToResolutionRecordsWhenProviderSnapshotIsMissing()
    {
        var detail = AtlasConceptPayloadFactory.CreateConceptDetail(
            new AtlasCanonicalConceptProjection(
                Concept: new AtlasCatalogConceptRecord(
                    Id: "mb-release-group:interstellar-space",
                    ReleaseGroupId: "interstellar-space",
                    Title: "Interstellar Space",
                    ArtistId: null,
                    ArtistName: "John Coltrane",
                    ArtistKey: "john-coltrane",
                    Year: 1967,
                    ImageUrl: null,
                    CanonicalDate: new AtlasCanonicalDateResponse(
                        IsoDate: "1967-01-01",
                        Timestamp: -94694400000,
                        Confidence: "high",
                        Granularity: "day"),
                    Availability: "requested",
                    CatalogState: "catalog_only",
                    SourceKind: "musicbrainz",
                    SourceUrl: "https://musicbrainz.org/release-group/interstellar-space",
                    ExternalBinding: new AtlasExternalBindingResponse(
                        Provider: "musicbrainz",
                        ArtistName: "John Coltrane",
                        ReleaseGroupId: "interstellar-space",
                        Score: 98,
                        FirstReleaseDate: "1967-01-01",
                        SourceUrl: "https://musicbrainz.org/release-group/interstellar-space"),
                    ProviderCandidates: []),
                Releases:
                [
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:digital",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space (Digital)",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "2020-01-01",
                        DateLabel: "2020",
                        CountryCode: "XW",
                        Label: null,
                        Packaging: "None",
                        Format: "Digital Media",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: true,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none")
                ],
                ProviderCandidates: [],
                ResolutionRecords:
                [
                    new AtlasCatalogResolutionRecord(
                        Id: "streamrip:qobuz:interstellar-space",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseId: "release:interstellar-space:digital",
                        Provider: "streamrip",
                        ProviderSource: "qobuz",
                        ProviderAlbumId: "interstellar-space",
                        DecisionState: "release_linked",
                        ScoreTotal: 0.92,
                        DecisiveFactors:
                        [
                            "Single official digital release candidate matched",
                            "Digital release metadata aligned strongly",
                        ],
                        Reason: "John Coltrane - Interstellar Space",
                        EvaluatedAt: "2026-03-24T00:00:00.000Z")
                ],
                SourceObservations: []),
            selectedReleaseId: "release:interstellar-space:digital",
            selectedSurface: null);

        Assert.Equal("Qobuz edition candidate via streamrip", detail.Playback.BestOffer?.Label);
        Assert.Equal("release_linked", detail.Playback.DecisionState);
        Assert.Equal(
            "John Coltrane - Interstellar Space Reasons: Single official digital release candidate matched; Digital release metadata aligned strongly.",
            detail.Playback.BestOffer?.Summary);
    }

    [Fact]
    public void CreateConceptDetail_ProjectsSourceObservationsIntoProvenance()
    {
        var detail = AtlasConceptPayloadFactory.CreateConceptDetail(
            new AtlasCanonicalConceptProjection(
                Concept: new AtlasCatalogConceptRecord(
                    Id: "mb-release-group:interstellar-space",
                    ReleaseGroupId: "interstellar-space",
                    Title: "Interstellar Space",
                    ArtistId: null,
                    ArtistName: "John Coltrane",
                    ArtistKey: "john-coltrane",
                    Year: 1967,
                    ImageUrl: null,
                    CanonicalDate: new AtlasCanonicalDateResponse(
                        IsoDate: "1967-01-01",
                        Timestamp: -94694400000,
                        Confidence: "high",
                        Granularity: "day"),
                    Availability: "requested",
                    CatalogState: "catalog_only",
                    SourceKind: "musicbrainz",
                    SourceUrl: "https://musicbrainz.org/release-group/interstellar-space",
                    ExternalBinding: new AtlasExternalBindingResponse(
                        Provider: "musicbrainz",
                        ArtistName: "John Coltrane",
                        ReleaseGroupId: "interstellar-space",
                        Score: 98,
                        FirstReleaseDate: "1967-01-01",
                        SourceUrl: "https://musicbrainz.org/release-group/interstellar-space"),
                    ProviderCandidates: []),
                Releases:
                [
                    new AtlasCatalogReleaseRecord(
                        Id: "release:interstellar-space:impulse",
                        ConceptId: "mb-release-group:interstellar-space",
                        ReleaseGroupId: "interstellar-space",
                        Title: "Interstellar Space",
                        ArtistName: "John Coltrane",
                        ReleaseDate: "1974-01-01",
                        DateLabel: "1974",
                        CountryCode: "US",
                        Label: "Impulse!",
                        Packaging: "Jewel Case",
                        Format: "CD",
                        TrackCount: 5,
                        MediumCount: 1,
                        IsOfficial: true,
                        IsDigitalOnly: false,
                        CatalogState: "catalog_only",
                        PackageCompleteness: "none")
                ],
                ProviderCandidates: [],
                ResolutionRecords: [],
                SourceObservations:
                [
                    new AtlasCatalogSourceObservation(
                        Id: "mb-release-group:interstellar-space:release-group",
                        EntityType: "concept",
                        EntityId: "mb-release-group:interstellar-space",
                        Source: "musicbrainz",
                        Field: "releaseGroupId",
                        Value: "interstellar-space",
                        ObservedAt: "2026-03-25T00:00:00.000Z"),
                    new AtlasCatalogSourceObservation(
                        Id: "release:interstellar-space:impulse:release-date",
                        EntityType: "release",
                        EntityId: "release:interstellar-space:impulse",
                        Source: "musicbrainz",
                        Field: "releaseDate",
                        Value: "1974-01-01",
                        ObservedAt: "2026-03-25T00:00:00.000Z")
                ]),
            selectedReleaseId: "release:interstellar-space:impulse",
            selectedSurface: null);

        Assert.Single(detail.Provenance.ConceptObservations);
        Assert.Single(detail.Provenance.SelectedReleaseObservations);
        Assert.Equal("releaseGroupId", detail.Provenance.ConceptObservations[0].Field);
        Assert.Equal("releaseDate", detail.Provenance.SelectedReleaseObservations[0].Field);
    }

    private static AtlasCanonicalConceptProjection BuildRequestedProjection(
        string conceptId,
        string releaseGroupId,
        string title,
        string artistName,
        int year,
        IReadOnlyList<AtlasProviderCandidateRecord> providerCandidates,
        IReadOnlyList<AtlasCatalogReleaseRecord> releases)
        => new(
            Concept: new AtlasCatalogConceptRecord(
                Id: conceptId,
                ReleaseGroupId: releaseGroupId,
                Title: title,
                ArtistId: null,
                ArtistName: artistName,
                ArtistKey: "john-coltrane",
                Year: year,
                ImageUrl: null,
                CanonicalDate: new AtlasCanonicalDateResponse(
                    IsoDate: $"{year}-01-01",
                    Timestamp: year == 1960 ? -315619200000 : -94694400000,
                    Confidence: "high",
                    Granularity: "day"),
                Availability: "requested",
                CatalogState: "catalog_only",
                SourceKind: "musicbrainz",
                SourceUrl: $"https://musicbrainz.org/release-group/{releaseGroupId}",
                ExternalBinding: new AtlasExternalBindingResponse(
                    Provider: "musicbrainz",
                    ArtistName: artistName,
                    ReleaseGroupId: releaseGroupId,
                    Score: year == 1960 ? 97 : 98,
                    FirstReleaseDate: $"{year}-01-01",
                    SourceUrl: $"https://musicbrainz.org/release-group/{releaseGroupId}"),
                ProviderCandidates: providerCandidates),
            Releases: releases,
            ProviderCandidates: providerCandidates,
            ResolutionRecords: [],
            SourceObservations: []);
}

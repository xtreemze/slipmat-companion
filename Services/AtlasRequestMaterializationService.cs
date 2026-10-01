using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

public static class AtlasRequestMaterializationService
{
    private static readonly HttpClient DefaultHttpClient = new();

    public static async Task<IReadOnlyList<AtlasRequestIngestResult>> MaterializeRequestsAsync(
        IReadOnlyList<AtlasAcquisitionRequestRecord> requests,
        HttpClient? httpClient = null)
    {
        if (requests.Count == 0)
        {
            return Array.Empty<AtlasRequestIngestResult>();
        }

        var client = httpClient ?? DefaultHttpClient;
        var results = new List<AtlasRequestIngestResult>();

        foreach (var request in requests)
        {
            results.Add(await MaterializeRequestAsync(request, client));
        }

        return results;
    }

    public static async Task<AtlasRequestIngestResult> MaterializeRequestAsync(
        AtlasAcquisitionRequestRecord request,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = httpClient ?? DefaultHttpClient;
        var releaseGroup = await SearchReleaseGroupAsync(request, client);
        if (releaseGroup is null)
        {
            return BuildProvisionalResult(request);
        }

        var conceptId = !string.IsNullOrWhiteSpace(releaseGroup.Id)
            ? $"mb-release-group:{releaseGroup.Id}"
            : request.ConceptId;
        var isoDate = NormalizeIsoDate(releaseGroup.FirstReleaseDate ?? "1970");
        var releaseCandidates = await SearchReleaseCandidatesAsync(
            releaseGroup.Id,
            request.ArtistName,
            client);
        var releases = BuildCanonicalReleases(conceptId, releaseCandidates);
        var providerCandidates = BuildProviderCandidates(request, releaseCandidates);
        var externalBinding = new AtlasExternalBindingResponse(
            Provider: "musicbrainz",
            ArtistName: request.ArtistName,
            ReleaseGroupId: releaseGroup.Id,
            Score: releaseGroup.Score,
            FirstReleaseDate: releaseGroup.FirstReleaseDate,
            SourceUrl: !string.IsNullOrWhiteSpace(releaseGroup.Id)
                ? $"https://musicbrainz.org/release-group/{releaseGroup.Id}"
                : null);
        var concept = new AtlasCatalogConceptRecord(
            Id: conceptId,
            ReleaseGroupId: releaseGroup.Id,
            Title: request.Title,
            ArtistId: null,
            ArtistName: request.ArtistName,
            ArtistKey: NormalizeArtistKey(request.ArtistName),
            Year: ParseYear(isoDate),
            ImageUrl: null,
            CanonicalDate: BuildCanonicalDate(
                isoDate,
                "high",
                releaseGroup.FirstReleaseDate?.Length == 4 ? "year" : "day"),
            Availability: "requested",
            CatalogState: "catalog_only",
            SourceKind: "musicbrainz",
            SourceUrl: externalBinding.SourceUrl,
            ExternalBinding: externalBinding,
            ProviderCandidates: providerCandidates);

        return BuildIngestResult(request, concept, releases, providerCandidates);
    }

    private static async Task<MusicBrainzReleaseGroup?> SearchReleaseGroupAsync(
        AtlasAcquisitionRequestRecord request,
        HttpClient httpClient)
    {
        var url =
            "https://musicbrainz.org/ws/2/release-group" +
            $"?query={Uri.EscapeDataString($"artist:\"{request.ArtistName}\" AND releasegroup:\"{request.Title}\" AND primarytype:album")}" +
            "&fmt=json&limit=5";

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await httpClient.SendAsync(message);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("release-groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return groups.EnumerateArray()
                .Select(group => new MusicBrainzReleaseGroup(
                    Id: group.TryGetProperty("id", out var id) ? id.GetString() : null,
                    Title: group.TryGetProperty("title", out var title) ? title.GetString() : null,
                    Score: ParseOptionalInt(group.TryGetProperty("score", out var score) ? score : default),
                    FirstReleaseDate: group.TryGetProperty("first-release-date", out var firstReleaseDate)
                        ? firstReleaseDate.GetString()
                        : null,
                    ArtistNames: group.TryGetProperty("artist-credit", out var artistCredit) && artistCredit.ValueKind == JsonValueKind.Array
                        ? artistCredit.EnumerateArray()
                            .Select(entry =>
                                entry.TryGetProperty("artist", out var artist) &&
                                artist.TryGetProperty("name", out var artistName)
                                    ? artistName.GetString()
                                    : entry.TryGetProperty("name", out var creditName)
                                        ? creditName.GetString()
                                        : null)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Cast<string>()
                            .ToArray()
                        : Array.Empty<string>()))
                .Where(group => MatchesArtist(group, request.ArtistName))
                .OrderByDescending(group => group.Score ?? 0)
                .FirstOrDefault(group => (group.Score ?? 0) >= 70);
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesArtist(MusicBrainzReleaseGroup group, string artistName)
    {
        if (group.ArtistNames.Count == 0)
        {
            return true;
        }

        var normalizedArtist = NormalizeText(artistName);
        return group.ArtistNames.Any(name => NormalizeText(name).Contains(normalizedArtist));
    }

    private static AtlasRequestIngestResult BuildProvisionalResult(AtlasAcquisitionRequestRecord request)
    {
        const string IsoDate = "1970-01-01";
        var providerCandidates = BuildProviderCandidates(request, Array.Empty<AtlasCatalogReleaseCandidate>());
        var concept = new AtlasCatalogConceptRecord(
            Id: request.ConceptId,
            ReleaseGroupId: null,
            Title: request.Title,
            ArtistId: null,
            ArtistName: request.ArtistName,
            ArtistKey: NormalizeArtistKey(request.ArtistName),
            Year: null,
            ImageUrl: null,
            CanonicalDate: BuildCanonicalDate(IsoDate, "low", "year"),
            Availability: "requested",
            CatalogState: "conflict_or_review_needed",
            SourceKind: "musicbrainz",
            SourceUrl: null,
            ExternalBinding: new AtlasExternalBindingResponse(
                Provider: "musicbrainz",
                ArtistName: request.ArtistName,
                ReleaseGroupId: null,
                Score: null,
                FirstReleaseDate: null,
                SourceUrl: null),
            ProviderCandidates: providerCandidates);

        return BuildIngestResult(
            request,
            concept,
            Array.Empty<AtlasCatalogReleaseRecord>(),
            providerCandidates);
    }

    private static async Task<IReadOnlyList<AtlasCatalogReleaseCandidate>> SearchReleaseCandidatesAsync(
        string? releaseGroupId,
        string artistName,
        HttpClient httpClient)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupId))
        {
            return Array.Empty<AtlasCatalogReleaseCandidate>();
        }

        var url =
            "https://musicbrainz.org/ws/2/release" +
            $"?release-group={Uri.EscapeDataString(releaseGroupId)}&fmt=json&limit=8";

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await httpClient.SendAsync(message);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<AtlasCatalogReleaseCandidate>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            if (!document.RootElement.TryGetProperty("releases", out var releases) ||
                releases.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<AtlasCatalogReleaseCandidate>();
            }

            return releases.EnumerateArray()
                .Select(release => ToReleaseCandidate(release, releaseGroupId, artistName))
                .Where(candidate => candidate is not null)
                .Cast<AtlasCatalogReleaseCandidate>()
                .OrderByDescending(candidate => candidate.IsOfficial)
                .ThenBy(candidate => candidate.ReleaseDate ?? "9999-12-31")
                .ThenBy(candidate => candidate.Title)
                .ToArray();
        }
        catch
        {
            return Array.Empty<AtlasCatalogReleaseCandidate>();
        }
    }

    private static IReadOnlyList<AtlasCatalogReleaseRecord> BuildCanonicalReleases(
        string conceptId,
        IReadOnlyList<AtlasCatalogReleaseCandidate> releaseCandidates)
        => releaseCandidates.Select(candidate => new AtlasCatalogReleaseRecord(
                Id: candidate.Id,
                ConceptId: conceptId,
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

    private static AtlasCanonicalDateResponse BuildCanonicalDate(
        string isoDate,
        string confidence,
        string granularity)
    {
        var date = DateTimeOffset.Parse($"{isoDate}T00:00:00+00:00");
        return new AtlasCanonicalDateResponse(
            IsoDate: isoDate,
            Timestamp: date.ToUnixTimeMilliseconds(),
            Confidence: confidence,
            Granularity: granularity);
    }

    private static string NormalizeIsoDate(string date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return "1970-01-01";
        }

        if (date.Length == 4)
        {
            return $"{date}-01-01";
        }

        return date;
    }

    private static int? ParseYear(string isoDate)
        => int.TryParse(isoDate[..4], out var year) ? year : null;

    private static int? ParseOptionalInt(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.String when int.TryParse(element.GetString(), out var stringValue) => stringValue,
            _ => null,
        };
    }

    private static string NormalizeText(string value)
        => value.Trim().ToLowerInvariant().Replace("  ", " ");

    private static string NormalizeArtistKey(string artistName)
        => NormalizeText(artistName)
            .Replace(" ", "-")
            .Replace("/", "-");

    private static AtlasCatalogReleaseCandidate? ToReleaseCandidate(
        JsonElement release,
        string releaseGroupId,
        string artistName)
    {
        var id = release.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        var title = release.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var releaseDate = release.TryGetProperty("date", out var dateValue) ? NormalizeIsoDate(dateValue.GetString() ?? string.Empty) : null;
        var dateLabel = !string.IsNullOrWhiteSpace(releaseDate)
            ? releaseDate[..4]
            : "Undated";
        var countryCode = release.TryGetProperty("country", out var countryValue) ? countryValue.GetString() : null;
        var packaging = release.TryGetProperty("packaging", out var packagingValue) ? packagingValue.GetString() : null;
        var status = release.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
        var label = release.TryGetProperty("label-info", out var labelInfoValue) && labelInfoValue.ValueKind == JsonValueKind.Array
            ? labelInfoValue.EnumerateArray()
                .Select(entry =>
                    entry.TryGetProperty("label", out var labelValueElement) &&
                    labelValueElement.TryGetProperty("name", out var labelNameValue)
                        ? labelNameValue.GetString()
                        : null)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            : null;

        var media = release.TryGetProperty("media", out var mediaValue) && mediaValue.ValueKind == JsonValueKind.Array
            ? mediaValue.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var mediumCount = Math.Max(1, media.Length);
        var trackCount = media.Sum(entry => ParseOptionalInt(entry.TryGetProperty("track-count", out var countValue) ? countValue : default) ?? 0);
        var format = media
            .Select(entry => entry.TryGetProperty("format", out var formatValue) ? formatValue.GetString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var isDigitalOnly =
            string.Equals(format, "Digital Media", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(packaging, "None", StringComparison.OrdinalIgnoreCase);

        return new AtlasCatalogReleaseCandidate(
            Id: $"release:{id}",
            ReleaseGroupId: releaseGroupId,
            Title: title,
            ArtistName: artistName,
            ReleaseDate: releaseDate,
            DateLabel: dateLabel,
            CountryCode: countryCode,
            Label: label,
            Packaging: packaging,
            Format: format,
            TrackCount: trackCount,
            MediumCount: mediumCount,
            IsOfficial: string.Equals(status, "Official", StringComparison.OrdinalIgnoreCase),
            IsDigitalOnly: isDigitalOnly,
            CatalogState: "catalog_only",
            PackageCompleteness: "none");
    }

    private static IReadOnlyList<AtlasProviderCandidateRecord> BuildProviderCandidates(
        AtlasAcquisitionRequestRecord request,
        IReadOnlyList<AtlasCatalogReleaseCandidate> releaseCandidates)
    {
        if (!string.Equals(request.Provider, "streamrip", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<AtlasProviderCandidateRecord>();
        }

        var providerSource = request.ProviderSource ?? InferProviderSource(request.AlbumId);
        var providerAlbumId = request.ProviderAlbumId ?? InferProviderAlbumId(request.AlbumId);
        var description = request.ProviderDescription ?? $"{request.ArtistName} - {request.Title}";
        var releaseLinkedCandidate = PickProviderReleaseTarget(releaseCandidates);
        var hasAmbiguousTargets = HasAmbiguousReleaseTargets(releaseCandidates);

        return
        [
            new AtlasProviderCandidateRecord(
                Id: request.AlbumId,
                Provider: "streamrip",
                ProviderSource: providerSource,
                ProviderAlbumId: providerAlbumId,
                Description: description,
                ReleaseId: releaseLinkedCandidate?.Id,
                DecisionState: releaseLinkedCandidate is null
                    ? hasAmbiguousTargets
                        ? "review_needed"
                        : releaseCandidates.Count > 0
                            ? "group_linked"
                            : "unresolved"
                    : "release_linked",
                ScoreTotal: releaseLinkedCandidate is null
                    ? hasAmbiguousTargets
                        ? 0.88
                        : releaseCandidates.Count > 0 ? 0.82 : 0.70
                    : 0.92,
                DecisiveFactors: BuildDecisiveFactors(
                    releaseLinkedCandidate,
                    hasAmbiguousTargets,
                    releaseCandidates.Count > 0))
        ];
    }

    private static IReadOnlyList<string> BuildDecisiveFactors(
        AtlasCatalogReleaseCandidate? releaseLinkedCandidate,
        bool hasAmbiguousTargets,
        bool hasAnyReleaseCandidates)
    {
        if (releaseLinkedCandidate is not null)
        {
            return
            [
                "Single official digital release candidate matched",
                "Digital release metadata aligned strongly",
            ];
        }

        if (hasAmbiguousTargets)
        {
            return
            [
                "Multiple official digital releases matched this provider candidate",
                "Exact edition mapping remains ambiguous",
            ];
        }

        if (hasAnyReleaseCandidates)
        {
            return
            [
                "Matched release group and artist identity",
                "Edition-specific evidence is still incomplete",
            ];
        }

        return
        [
            "No canonical release candidates were available yet",
            "Provider evidence remains unresolved",
        ];
    }

    private static AtlasCatalogReleaseCandidate? PickProviderReleaseTarget(
        IReadOnlyList<AtlasCatalogReleaseCandidate> releaseCandidates)
    {
        var digitalCandidates = releaseCandidates
            .Where(candidate => candidate.IsOfficial && candidate.IsDigitalOnly)
            .OrderBy(candidate => candidate.ReleaseDate ?? "9999-12-31")
            .ToArray();

        return digitalCandidates.Length == 1 ? digitalCandidates[0] : null;
    }

    private static bool HasAmbiguousReleaseTargets(
        IReadOnlyList<AtlasCatalogReleaseCandidate> releaseCandidates)
    {
        var digitalCandidateCount = releaseCandidates.Count(
            candidate => candidate.IsOfficial && candidate.IsDigitalOnly);

        return digitalCandidateCount > 1;
    }

    private static AtlasRequestIngestResult BuildIngestResult(
        AtlasAcquisitionRequestRecord request,
        AtlasCatalogConceptRecord concept,
        IReadOnlyList<AtlasCatalogReleaseRecord> releases,
        IReadOnlyList<AtlasProviderCandidateRecord> providerCandidates)
    {
        var selectedReleaseId = providerCandidates
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.ReleaseId))
            ?.ReleaseId ?? releases.FirstOrDefault()?.Id;

        return new AtlasRequestIngestResult(
            Concept: concept,
            Releases: releases,
            RequestLink: new AtlasCatalogRequestLinkRecord(
                RequestAlbumId: request.AlbumId,
                ConceptId: concept.Id,
                SelectedReleaseId: selectedReleaseId),
            ProviderCandidates: providerCandidates,
            SourceObservations: BuildSourceObservations(concept, releases),
            ResolutionRecords: BuildResolutionRecords(concept, providerCandidates));
    }

    private static IReadOnlyList<AtlasCatalogSourceObservation> BuildSourceObservations(
        AtlasCatalogConceptRecord concept,
        IReadOnlyList<AtlasCatalogReleaseRecord> releases)
    {
        var observations = new List<AtlasCatalogSourceObservation>();
        var observedAt = DateTimeOffset.UtcNow.ToString("O");

        if (!string.IsNullOrWhiteSpace(concept.ReleaseGroupId))
        {
            observations.Add(
                new AtlasCatalogSourceObservation(
                    Id: $"{concept.Id}:release-group",
                    EntityType: "concept",
                    EntityId: concept.Id,
                    Source: concept.SourceKind,
                    Field: "releaseGroupId",
                    Value: concept.ReleaseGroupId,
                    ObservedAt: observedAt));
        }

        foreach (var release in releases)
        {
            observations.Add(
                new AtlasCatalogSourceObservation(
                    Id: $"{release.Id}:release-date",
                    EntityType: "release",
                    EntityId: release.Id,
                    Source: "musicbrainz",
                    Field: "releaseDate",
                    Value: release.ReleaseDate,
                    ObservedAt: observedAt));
        }

        return observations;
    }

    private static IReadOnlyList<AtlasCatalogResolutionRecord> BuildResolutionRecords(
        AtlasCatalogConceptRecord concept,
        IReadOnlyList<AtlasProviderCandidateRecord> providerCandidates)
        => providerCandidates
            .Select(candidate => new AtlasCatalogResolutionRecord(
                Id: candidate.Id,
                ConceptId: concept.Id,
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

    private static string? InferProviderSource(string albumId)
    {
        var parts = albumId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[1] : null;
    }

    private static string? InferProviderAlbumId(string albumId)
    {
        var parts = albumId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : null;
    }

    private sealed record MusicBrainzReleaseGroup(
        string? Id,
        string? Title,
        int? Score,
        string? FirstReleaseDate,
        IReadOnlyList<string> ArtistNames);
}

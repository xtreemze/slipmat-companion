using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>Separate album-sequence loudness evidence and exact source membership.</summary>
public sealed record AlbumLoudnessSidecarV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("subjectStoreKey")] string SubjectStoreKey,
    [property: JsonPropertyName("sourceFingerprint")] string SourceFingerprint,
    [property: JsonPropertyName("manifest")] AlbumLoudnessManifestV1 Manifest,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("measurement")] LoudnessMeasurementEvidenceV1 Measurement,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt)
{
    public const int CurrentVersion = 1;
    private static readonly Regex FingerprintPattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsCurrentFor(string subjectStoreKey)
    {
        if (Version != CurrentVersion
            || !AnalysisSubjectV1.IsStoreKey(SubjectStoreKey)
            || SubjectStoreKey != subjectStoreKey
            || SourceFingerprint is null
            || !FingerprintPattern.IsMatch(SourceFingerprint)
            || !HasCurrentManifest()
            || Measurement is null
            || Measurement.Version != 1
            || Measurement.Scope != "album"
            || Measurement.Authority != "server-analysis"
            || Measurement.Analyzer is null
            || string.IsNullOrWhiteSpace(Measurement.Analyzer.Name)
            || string.IsNullOrWhiteSpace(Measurement.Analyzer.Version)
            || !IsSha256(Measurement.Analyzer.ConfigurationDigest)
            || !double.IsFinite(Measurement.IntegratedLufs)
            || (Measurement.TruePeakDbtp is not null && !double.IsFinite(Measurement.TruePeakDbtp.Value))
            || (Measurement.LoudnessRangeLu is not null && !double.IsFinite(Measurement.LoudnessRangeLu.Value))
            || DurationMs < 0
            || !DateTimeOffset.TryParse(GeneratedAt, out _))
        {
            return false;
        }

        return string.Equals(
            AlbumLoudnessManifestV1.ComputeSourceFingerprint(Manifest.Members),
            SourceFingerprint,
            StringComparison.Ordinal);
    }

    private bool HasCurrentManifest()
        => Manifest is not null
            && Manifest.Members is { Count: > 0 }
            && Manifest.Members.All(member => member.SourceFingerprint is not null)
            && AlbumLoudnessManifestV1.TryCreate(Manifest.Members, out var checkedManifest, out _)
            && checkedManifest?.MembershipFingerprint == Manifest.MembershipFingerprint;

    private static bool IsSha256(string? value)
        => value is not null
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && FingerprintPattern.IsMatch(value["sha256:".Length..]);
}

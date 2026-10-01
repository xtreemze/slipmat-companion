using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Projects a compatible host-neutral analyzer sidecar into the portable
/// analysis-artifact descriptor. Store paths and Jellyfin IDs never cross this
/// boundary.
/// </summary>
public static class PortableAnalysisProjector
{
    public static PortableAnalysisArtifactDescriptorV1 FromSidecar(AnalysisSidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        if (!sidecar.IsCurrentFor(sidecar.SubjectStoreKey))
        {
            throw new ArgumentException(
                "Analysis sidecar is incompatible with the portable protocol.",
                nameof(sidecar));
        }
        StorePaths.ValidateStoreKey(sidecar.SubjectStoreKey);
        if (!IsSha256Hex(sidecar.SourceFingerprint))
        {
            throw new ArgumentException(
                "Analysis source fingerprint must be a lowercase SHA-256 digest.",
                nameof(sidecar));
        }
        if (string.IsNullOrEmpty(sidecar.ProducerVersion))
        {
            throw new ArgumentException(
                "Analysis producer version must not be empty.",
                nameof(sidecar));
        }

        var artifacts = new List<PortableAnalysisArtifactRefV1>(
            sidecar.WaveformRefs.Count + (sidecar.SpectralRefs?.Count ?? 0));

        foreach (var reference in sidecar.WaveformRefs)
        {
            ValidateArtifactReference(reference);
            artifacts.Add(new PortableAnalysisArtifactRefV1(
                Kind: "waveform",
                Variant: reference.Variant,
                RatePerSecond: reference.Pps,
                Etag: reference.Etag,
                AnalysisVersion: null));
        }

        if (sidecar.SpectralRefs is not null)
        {
            foreach (var reference in sidecar.SpectralRefs)
            {
                ValidateArtifactReference(reference);
                artifacts.Add(new PortableAnalysisArtifactRefV1(
                    Kind: "spectral-waveform",
                    Variant: reference.Variant,
                    RatePerSecond: reference.Pps,
                    Etag: reference.Etag,
                    AnalysisVersion: sidecar.SpectralAnalysisVersion));
            }
        }

        if (artifacts.Count == 0)
        {
            throw new ArgumentException(
                "Portable analysis descriptor requires at least one artifact.",
                nameof(sidecar));
        }

        return new PortableAnalysisArtifactDescriptorV1(
            ProtocolVersion: PortableAnalysisProtocolV1.Version,
            Subject: new PortableAnalysisSubjectRefV1(
                Version: sidecar.SubjectVersion,
                StoreKey: sidecar.SubjectStoreKey),
            SourceFingerprint: sidecar.SourceFingerprint,
            SidecarSchemaVersion: sidecar.SchemaVersion,
            ProducerVersion: sidecar.ProducerVersion,
            DurationMs: sidecar.DurationMs,
            Artifacts: artifacts);
    }

    private static void ValidateArtifactReference(StoredWaveformRef reference)
    {
        var variant = reference.Variant;
        if (string.IsNullOrEmpty(variant)
            || variant.Contains('/')
            || variant.Contains('\\')
            || variant.Contains("..", StringComparison.Ordinal)
            || reference.Pps <= 0
            || string.IsNullOrEmpty(reference.Etag))
        {
            throw new ArgumentException(
                "Portable analysis artifact reference is structurally invalid.",
                nameof(reference));
        }
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character) || char.IsUpper(character))
            {
                return false;
            }
        }

        return true;
    }
}

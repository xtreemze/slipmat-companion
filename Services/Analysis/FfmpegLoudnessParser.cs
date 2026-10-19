using System;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal static class FfmpegLoudnessParser
{
    public const int MeasurementContractVersion = 1;
    public const string MeasurementScope = "track";
    public const string MeasurementAuthority = "server-analysis";
    public const string MeasurementSemantics = "ffmpeg-loudnorm-input";
    public const string AnalyzerName = "ffmpeg-loudnorm";
    public const string AnalyzerVersion = "1";
    public const string AnalyzerConfigurationDigest =
        "sha256:d067e146e6dd806228d993e7cb40501a80c464fb84af3af7ecfa32f5fca2b4ec";

    public static LoudnessAnalysis? Parse(string diagnosticOutput)
    {
        if (string.IsNullOrWhiteSpace(diagnosticOutput))
        {
            return null;
        }

        return LastMeasurement(diagnosticOutput);
    }

    /// <summary>
    /// Project FFmpeg loudnorm input statistics into the provider-neutral
    /// measured-evidence contract. This does not derive normalization gain.
    ///
    /// The configuration digest identifies the reviewed recipe:
    /// ffmpeg-loudnorm-input-v1|filter=loudnorm=print_format=json|
    /// fields=input_i,input_tp,input_lra|scope=full-track
    /// </summary>
    public static LoudnessMeasurementEvidenceV1 ToMeasurementEvidence(
        LoudnessAnalysis loudness)
        => new(
            Version: MeasurementContractVersion,
            Scope: MeasurementScope,
            Authority: MeasurementAuthority,
            Semantics: MeasurementSemantics,
            Analyzer: new LoudnessAnalyzerIdentityV1(
                AnalyzerName,
                AnalyzerVersion,
                AnalyzerConfigurationDigest),
            IntegratedLufs: loudness.IntegratedLufs,
            TruePeakDbtp: loudness.TruePeak,
            LoudnessRangeLu: loudness.DynamicRange);

    private static LoudnessAnalysis? LastMeasurement(string text)
    {
        var foundReport = false;
        LoudnessAnalysis? latest = null;
        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] != '{')
            {
                continue;
            }

            var end = FindObjectEnd(text, start);
            if (end < 0)
            {
                break;
            }

            var json = text.AsMemory(start, end - start + 1);
            if (ContainsMeasurementField(json.Span))
            {
                // Each loudnorm report is authoritative as a unit. If the last
                // report is partial or malformed, do not fill it from stale data.
                foundReport = true;
                latest = null;
                try
                {
                    using var document = JsonDocument.Parse(json);
                    var report = document.RootElement;
                    if (report.ValueKind == JsonValueKind.Object
                        && TryReadMeasurement(report, out var measurement))
                    {
                        latest = measurement;
                    }
                }
                catch (JsonException)
                {
                    // Keep the recognized but malformed report as the latest result.
                }
            }

            start = end;
        }

        return foundReport ? latest : null;
    }

    private static int FindObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool ContainsMeasurementField(ReadOnlySpan<char> json)
        => json.Contains("\"input_i\"", StringComparison.Ordinal)
            || json.Contains("\"input_tp\"", StringComparison.Ordinal)
            || json.Contains("\"input_lra\"", StringComparison.Ordinal);

    private static bool TryReadMeasurement(JsonElement report, out LoudnessAnalysis? measurement)
    {
        measurement = null;
        if (!TryReadFiniteString(report, "input_i", out var integrated)
            || !TryReadFiniteString(report, "input_tp", out var truePeak)
            || !TryReadFiniteString(report, "input_lra", out var loudnessRange))
        {
            return false;
        }

        measurement = new LoudnessAnalysis(
            integrated,
            truePeak,
            Math.Max(0d, loudnessRange));
        return true;
    }

    private static bool TryReadFiniteString(JsonElement report, string name, out double value)
    {
        value = 0;
        if (!report.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = property.GetString();
        return raw is not null
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }
}

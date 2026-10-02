using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal static partial class FfmpegLoudnessParser
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

        var integrated = LastValue(InputIntegratedRegex(), diagnosticOutput);
        var truePeak = LastValue(InputTruePeakRegex(), diagnosticOutput);
        var loudnessRange = LastValue(InputLraRegex(), diagnosticOutput);
        if (!integrated.HasValue || !truePeak.HasValue || !loudnessRange.HasValue)
        {
            return null;
        }

        return new LoudnessAnalysis(
            integrated.Value,
            truePeak.Value,
            Math.Max(0d, loudnessRange.Value));
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

    private static double? LastValue(Regex regex, string text)
    {
        var matches = regex.Matches(text);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var raw = matches[index].Groups["value"].Value;
            if (double.TryParse(
                    raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) &&
                double.IsFinite(value))
            {
                return value;
            }
        }

        return null;
    }

    [GeneratedRegex(
        "\\\"input_i\\\"\\s*:\\s*\\\"(?<value>[-+0-9.eE]+)\\\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex InputIntegratedRegex();

    [GeneratedRegex(
        "\\\"input_tp\\\"\\s*:\\s*\\\"(?<value>[-+0-9.eE]+)\\\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex InputTruePeakRegex();

    [GeneratedRegex(
        "\\\"input_lra\\\"\\s*:\\s*\\\"(?<value>[-+0-9.eE]+)\\\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex InputLraRegex();
}

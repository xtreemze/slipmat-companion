using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal static partial class FfmpegLoudnessParser
{
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

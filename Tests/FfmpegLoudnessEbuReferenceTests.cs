using System.Diagnostics;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class FfmpegLoudnessEbuReferenceTests
{
    [Fact]
    public async Task JellyfinFfmpeg_MeasuresEbuTech3341TestSixFivePointZeroTone()
    {
        // EBU Tech 3341:2023 Test 6 uses a 20-second, 1 kHz 5.0-channel signal:
        // L/R -28 dBFS, C -24 dBFS, and Ls/Rs -30 dBFS. Its integrated
        // loudness is -23.0 LUFS +/- 0.1 LU. Generate it in FFmpeg so this
        // exercises channel weighting, the installed filter, and our parser.
        var ffmpeg = Environment.GetEnvironmentVariable("SLIPMAT_PARITY_FFMPEG");
        Assert.False(
            string.IsNullOrWhiteSpace(ffmpeg),
            "SLIPMAT_PARITY_FFMPEG must point to the pinned Jellyfin FFmpeg binary.");

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg!,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(
            "aevalsrc=0.0398107171*sin(2*PI*1000*t)|0.0398107171*sin(2*PI*1000*t)|"
            + "0.0630957344*sin(2*PI*1000*t)|0.0316227766*sin(2*PI*1000*t)|"
            + "0.0316227766*sin(2*PI*1000*t):s=48000:d=20:c=5.0");
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add("loudnorm=I=-23:TP=-2:LRA=7:print_format=json");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Jellyfin FFmpeg did not start.");
        var diagnosticOutput = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Jellyfin FFmpeg did not finish the EBU reference signal in 30 seconds.");
        }

        var diagnostics = await diagnosticOutput;
        Assert.True(
            process.ExitCode == 0,
            $"Jellyfin FFmpeg exited {process.ExitCode}: {diagnostics}");

        var result = FfmpegLoudnessParser.Parse(diagnostics);

        Assert.NotNull(result);
        Assert.InRange(result!.IntegratedLufs, -23.1d, -22.9d);
    }
}

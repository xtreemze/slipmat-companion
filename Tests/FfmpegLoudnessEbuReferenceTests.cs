using System.Diagnostics;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class FfmpegLoudnessEbuReferenceTests
{
    [Fact]
    public async Task JellyfinFfmpeg_MeasuresEbuTech3341TestThreeToneGatingSequence()
    {
        // EBU Tech 3341:2023 Test 3 is 10 s at -36 dBFS, 60 s at -23 dBFS,
        // then 10 s at -36 dBFS, using in-phase 1 kHz stereo tones. The
        // integrated result is -23.0 LUFS +/- 0.1 LU; this exercises gating
        // across changing programme levels rather than a constant tone.
        var ffmpeg = RequireFfmpeg();
        const string signal =
            "aevalsrc=if(lt(t\\,10)\\,0.0158489319\\,if(lt(t\\,70)\\,0.0707945784\\,0.0158489319))"
            + "*sin(2*PI*1000*t)|if(lt(t\\,10)\\,0.0158489319\\,if(lt(t\\,70)\\,0.0707945784\\,0.0158489319))"
            + "*sin(2*PI*1000*t):s=48000:d=80:c=stereo";

        var result = await MeasureAsync(ffmpeg, signal);

        Assert.InRange(result.IntegratedLufs, -23.1d, -22.9d);
    }

    [Fact]
    public async Task JellyfinFfmpeg_MeasuresEbuTech3341TestSixFivePointZeroTone()
    {
        // EBU Tech 3341:2023 Test 6 uses a 20-second, 1 kHz 5.0-channel signal:
        // L/R -28 dBFS, C -24 dBFS, and Ls/Rs -30 dBFS. Its integrated
        // loudness is -23.0 LUFS +/- 0.1 LU. Generate it in FFmpeg so this
        // exercises channel weighting, the installed filter, and our parser.
        var result = await MeasureAsync(
            RequireFfmpeg(),
            "aevalsrc=0.0398107171*sin(2*PI*1000*t)|0.0398107171*sin(2*PI*1000*t)|"
            + "0.0630957344*sin(2*PI*1000*t)|0.0316227766*sin(2*PI*1000*t)|"
            + "0.0316227766*sin(2*PI*1000*t):s=48000:d=20:c=5.0");

        Assert.InRange(result.IntegratedLufs, -23.1d, -22.9d);
    }

    [Fact]
    public async Task JellyfinFfmpeg_MeasuresEbuGatingAcrossOrderedAlbumTracks()
    {
        // EBU Tech 3341:2023 Test 3 is 10 s at -36 dBFS, 60 s at -23 dBFS,
        // and 10 s at -36 dBFS. Split each programme segment into its own
        // track so loudnorm must gate the concatenated album as one sequence.
        var ffmpeg = RequireFfmpeg();
        var directory = Path.Combine(Path.GetTempPath(), $"slipmat-album-loudness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var tracks = new[]
            {
                (DurationSeconds: 10, Amplitude: "0.0158489319"),
                (DurationSeconds: 60, Amplitude: "0.0707945784"),
                (DurationSeconds: 10, Amplitude: "0.0158489319"),
            };
            var paths = new string[tracks.Length];
            for (var index = 0; index < tracks.Length; index++)
            {
                var (durationSeconds, amplitude) = tracks[index];
                paths[index] = Path.Combine(directory, $"track-{index + 1}.wav");
                var tone =
                    $"{amplitude}*sin(2*PI*1000*t)|{amplitude}*sin(2*PI*1000*t):s=48000:d={durationSeconds}:c=stereo";
                await RunFfmpegAsync(ffmpeg, new[]
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", $"aevalsrc={tone}",
                    "-c:a", "pcm_f32le", paths[index],
                });
            }

            var result = await MeasureAlbumSequenceAsync(ffmpeg, paths);

            Assert.NotNull(result);
            Assert.InRange(result!.IntegratedLufs, -23.1d, -22.9d);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JellyfinFfmpeg_DoesNotProduceAlbumMeasurementForSilence()
    {
        var ffmpeg = RequireFfmpeg();
        var directory = Path.Combine(Path.GetTempPath(), $"slipmat-silent-album-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var trackPath = Path.Combine(directory, "silence.wav");
            await RunFfmpegAsync(ffmpeg, new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "aevalsrc=0|0:s=48000:d=5:c=stereo",
                "-c:a", "pcm_f32le", trackPath,
            });

            var result = await MeasureAlbumSequenceAsync(ffmpeg, [trackPath]);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RequireFfmpeg()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SLIPMAT_PARITY_FFMPEG");
        Assert.False(
            string.IsNullOrWhiteSpace(ffmpeg),
            "SLIPMAT_PARITY_FFMPEG must point to the Jellyfin FFmpeg binary.");
        return ffmpeg!;
    }

    private static async Task<LoudnessAnalysis> MeasureAsync(string ffmpeg, string signal)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(signal);
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
        return result!;
    }

    private static async Task RunFfmpegAsync(string ffmpeg, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Jellyfin FFmpeg did not start.");
        var diagnosticsTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("Jellyfin FFmpeg did not generate an album track in 30 seconds.");
        }

        var diagnostics = await diagnosticsTask;
        Assert.True(process.ExitCode == 0, $"Jellyfin FFmpeg exited {process.ExitCode}: {diagnostics}");
    }

    private static async Task<LoudnessAnalysis?> MeasureAlbumSequenceAsync(
        string ffmpeg,
        IReadOnlyList<string> trackPaths)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("-filter_complex_threads");
        startInfo.ArgumentList.Add("1");
        foreach (var path in trackPaths)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);
        }
        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(IntegratedAudioAnalyzer.BuildAlbumLoudnessFilterGraph(
            Enumerable.Repeat(0, trackPaths.Count).ToArray(),
            "stereo"));
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("[measured]");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-threads");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Jellyfin FFmpeg did not start.");
        var diagnosticsTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("Jellyfin FFmpeg did not finish the album sequence in 30 seconds.");
        }

        var diagnostics = await diagnosticsTask;
        Assert.True(process.ExitCode == 0, $"Jellyfin FFmpeg exited {process.ExitCode}: {diagnostics}");
        return FfmpegLoudnessParser.Parse(diagnostics);
    }
}

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

/// <summary>
/// Mirrors Slipmat's production Symphonia media corpus in
/// services/audio-analyzer/tests/source_boundary_media_corpus.rs, but decodes
/// each generated PCM16 WAV through the same FFmpeg filter graph used by the
/// companion before feeding the canonical boundary accumulator.
/// </summary>
public sealed class SourceBoundaryMediaCorpusTests
{
    private const int SampleRate = 44_100;
    private const double DurationSeconds = 12d;
    private const double BoundaryToleranceSeconds = 0.15d;
    private const double FadeOnsetToleranceSeconds = 0.75d;
    private const double ContentRmsAtUnitAmplitude = 0.353_553_390_593_273_8d;

    [Fact]
    public async Task DecodedWav_LocalizesLeadingAndTrailingDigitalSilence()
    {
        var boundaries = await AnalyzeFixtureAsync(
            "silence-edges",
            seconds => seconds >= 1.5d && seconds < 10.5d ? 0.62f : 0f);

        AssertSecondsClose(
            boundaries.AudibleStartSeconds,
            1.5d,
            BoundaryToleranceSeconds,
            "decoded audible start");
        AssertSecondsClose(
            boundaries.AudibleEndSeconds,
            10.5d,
            BoundaryToleranceSeconds,
            "decoded audible end");
        Assert.Equal(OutroBoundaryKind.HardEnd, boundaries.Outro.Kind);
    }

    [Fact]
    public async Task DecodedWav_PreservesAuthoredFadeAndLocalizesItsOnset()
    {
        const double fadeStart = 7d;
        var boundaries = await AnalyzeFixtureAsync(
            "authored-fade",
            seconds =>
            {
                if (seconds < fadeStart)
                {
                    return 0.62f;
                }

                var progress = Math.Clamp(
                    (seconds - fadeStart) / (DurationSeconds - fadeStart),
                    0d,
                    1d);
                return (float)(0.62d * (1d - progress) + 0.05d * progress);
            });

        Assert.Equal(OutroBoundaryKind.FadeOut, boundaries.Outro.Kind);
        AssertSecondsClose(
            boundaries.Outro.StartSeconds,
            fadeStart,
            FadeOnsetToleranceSeconds,
            "decoded authored-fade onset");
        AssertSecondsClose(
            boundaries.AudibleEndSeconds,
            DurationSeconds,
            BoundaryToleranceSeconds,
            "decoded authored-fade audible end");
    }

    [Fact]
    public async Task DecodedWav_KeepsIntentionalQuietIntroAudibleFromSourceZero()
    {
        var quiet = AmplitudeForRms(0.04d);
        var boundaries = await AnalyzeFixtureAsync(
            "quiet-intro",
            seconds => seconds < 2d ? quiet : 0.62f);

        AssertSecondsClose(
            boundaries.AudibleStartSeconds,
            0d,
            BoundaryToleranceSeconds,
            "intentional quiet intro");
    }

    [Fact]
    public async Task DecodedWav_DoesNotMisclassifyAbruptQuietPlateauAsFade()
    {
        var boundaries = await AnalyzeFixtureAsync(
            "quiet-plateau",
            seconds => seconds < 7d ? 0.62f : 0.06f);

        Assert.NotEqual(OutroBoundaryKind.FadeOut, boundaries.Outro.Kind);
        AssertSecondsClose(
            boundaries.AudibleEndSeconds,
            DurationSeconds,
            BoundaryToleranceSeconds,
            "quiet plateau audible end");
    }

    [Fact]
    public async Task DecodedWav_KeepsModulatedQuietTailWithoutInventingFade()
    {
        var boundaries = await AnalyzeFixtureAsync(
            "modulated-quiet-tail",
            seconds => seconds < 7d
                ? 0.62f
                : (float)(0.07d + 0.015d * Math.Sin(Math.Tau * 0.73d * seconds)));

        Assert.NotEqual(OutroBoundaryKind.FadeOut, boundaries.Outro.Kind);
        AssertSecondsClose(
            boundaries.AudibleEndSeconds,
            DurationSeconds,
            BoundaryToleranceSeconds,
            "modulated quiet tail audible end");
    }

    [Fact]
    public async Task DecodedWav_RecoveryAfterDeclineDoesNotBecomeTerminalFade()
    {
        var boundaries = await AnalyzeFixtureAsync(
            "fade-recovery",
            seconds =>
            {
                if (seconds < 7d)
                {
                    return 0.62f;
                }

                if (seconds < 9.5d)
                {
                    var progress = (seconds - 7d) / 2.5d;
                    return (float)(0.62d * (1d - progress) + 0.10d * progress);
                }

                return 0.50f;
            });

        Assert.NotEqual(OutroBoundaryKind.FadeOut, boundaries.Outro.Kind);
        AssertSecondsClose(
            boundaries.AudibleEndSeconds,
            DurationSeconds,
            BoundaryToleranceSeconds,
            "recovered tail audible end");
    }


    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("aac")]
    [InlineData("vorbis")]
    public async Task CompressedCodec_LocalizesSilenceEdges(string codec)
    {
        var tolerance = CodecBoundaryTolerance(codec);
        var boundaries = await AnalyzeFixtureAsync(
            $"compressed-silence-{codec}",
            seconds => seconds >= 1.5d && seconds < 10.5d ? 0.62f : 0f,
            codec);

        AssertSecondsClose(
            boundaries.AudibleStartSeconds,
            1.5d,
            tolerance,
            $"{codec} audible start");
        AssertSecondsClose(
            boundaries.AudibleEndSeconds,
            10.5d,
            tolerance,
            $"{codec} audible end");
        Assert.Equal(OutroBoundaryKind.HardEnd, boundaries.Outro.Kind);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("aac")]
    [InlineData("vorbis")]
    public async Task CompressedCodec_PreservesAuthoredFadeClassification(string codec)
    {
        const double fadeStart = 7d;
        var boundaries = await AnalyzeFixtureAsync(
            $"compressed-fade-{codec}",
            seconds =>
            {
                if (seconds < fadeStart)
                {
                    return 0.62f;
                }

                var progress = Math.Clamp(
                    (seconds - fadeStart) / (DurationSeconds - fadeStart),
                    0d,
                    1d);
                return (float)(0.62d * (1d - progress) + 0.05d * progress);
            },
            codec);

        Assert.Equal(OutroBoundaryKind.FadeOut, boundaries.Outro.Kind);
        AssertSecondsClose(
            boundaries.Outro.StartSeconds,
            fadeStart,
            CodecFadeTolerance(codec),
            $"{codec} authored-fade onset");
        AssertSecondsClose(
            boundaries.AudibleEndSeconds,
            DurationSeconds,
            CodecBoundaryTolerance(codec),
            $"{codec} authored-fade audible end");
    }

    private static async Task<TrackBoundaryAnalysis> AnalyzeFixtureAsync(
        string name,
        Func<double, float> amplitude,
        string? codec = null)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("SLIPMAT_PARITY_FFMPEG");
        Assert.False(
            string.IsNullOrWhiteSpace(ffmpeg),
            "SLIPMAT_PARITY_FFMPEG must point to the FFmpeg binary for decoder-parity certification.");

        var sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"slipmat-{name}-{Guid.NewGuid():N}.wav");
        string? encodedPath = null;
        try
        {
            WritePcm16MonoWav(sourcePath, amplitude);
            var inputPath = sourcePath;
            if (codec is not null)
            {
                encodedPath = Path.ChangeExtension(
                    sourcePath,
                    CodecExtension(codec));
                await EncodeCompressedAsync(
                    ffmpeg!,
                    sourcePath,
                    encodedPath,
                    codec);
                inputPath = encodedPath;
            }

            var decoded = await DecodeWithProductionFilterGraphAsync(ffmpeg!, inputPath);
            var decodedSeconds = decoded.Length / (double)(SampleRate * sizeof(float));
            if (codec is null)
            {
                Assert.Equal(DurationSeconds, decodedSeconds, 6);
            }
            else
            {
                Assert.InRange(decodedSeconds, DurationSeconds - 0.5d, DurationSeconds + 0.5d);
            }

            var accumulator = new BoundaryAccumulator(SampleRate, 1);
            var frame = new float[1];
            for (var offset = 0; offset < decoded.Length; offset += sizeof(float))
            {
                var bits = BinaryPrimitives.ReadInt32LittleEndian(
                    decoded.AsSpan(offset, sizeof(float)));
                frame[0] = BitConverter.Int32BitsToSingle(bits);
                accumulator.PushFrame(frame);
            }

            return accumulator.Complete()
                ?? throw new InvalidDataException("FFmpeg corpus decode produced no boundary evidence.");
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
            }

            if (encodedPath is not null && File.Exists(encodedPath))
            {
                File.Delete(encodedPath);
            }
        }
    }


    private static async Task EncodeCompressedAsync(
        string ffmpeg,
        string sourcePath,
        string outputPath,
        string codec)
    {
        var codecArguments = codec switch
        {
            "flac" => new[] { "-c:a", "flac", "-compression_level", "5" },
            "mp3" => new[] { "-c:a", "libmp3lame", "-b:a", "192k" },
            "aac" => new[] { "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" },
            "vorbis" => new[] { "-c:a", "libvorbis", "-q:a", "5" },
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported parity codec."),
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", sourcePath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in codecArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), $"FFmpeg {codec} parity encoder did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(
            process.ExitCode == 0,
            $"FFmpeg {codec} parity encode exited {process.ExitCode}: {stdout}\n{stderr}");
        Assert.True(File.Exists(outputPath), $"FFmpeg {codec} parity output is missing.");
    }

    private static string CodecExtension(string codec)
        => codec switch
        {
            "flac" => ".flac",
            "mp3" => ".mp3",
            "aac" => ".m4a",
            "vorbis" => ".ogg",
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported parity codec."),
        };

    private static double CodecBoundaryTolerance(string codec)
        => codec == "flac" ? BoundaryToleranceSeconds : 0.35d;

    private static double CodecFadeTolerance(string codec)
        => codec == "flac" ? FadeOnsetToleranceSeconds : 1.0d;

    private static async Task<byte[]> DecodeWithProductionFilterGraphAsync(
        string ffmpeg,
        string inputPath)
    {
        var sourceFormat = new IntegratedAudioAnalyzer.SourceAudioFormat(
            StreamIndex: 0,
            SampleRate,
            SourceChannels: 1,
            AnalysisChannels: 1);
        var projection = IntegratedAudioAnalyzer.BuildAnalysisProjection(sourceFormat);
        var filterGraph = IntegratedAudioAnalyzer.BuildFilterGraph(sourceFormat, projection);

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel", "info",
            "-i", inputPath,
            "-filter_complex", filterGraph,
            "-map", "[pcmout]",
            "-vn",
            "-sn",
            "-dn",
            "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", "1",
            "-f", "f32le",
            "-acodec", "pcm_f32le",
            "pipe:1",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "FFmpeg parity process did not start.");

        var stderrTask = process.StandardError.ReadToEndAsync();
        await using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        var stderr = await stderrTask;

        Assert.True(
            process.ExitCode == 0,
            $"FFmpeg parity decode exited {process.ExitCode}: {stderr}");
        return output.ToArray();
    }

    private static void WritePcm16MonoWav(
        string path,
        Func<double, float> amplitude)
    {
        var frameCount = checked((uint)Math.Round(
            DurationSeconds * SampleRate,
            MidpointRounding.AwayFromZero));
        var dataBytes = checked(frameCount * 2u);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(checked(36u + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)SampleRate);
        writer.Write((uint)(SampleRate * 2));
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (uint frame = 0; frame < frameCount; frame++)
        {
            var seconds = frame / (double)SampleRate;
            var sample = Math.Clamp(
                ContentSignal(seconds) * amplitude(seconds),
                -0.98f,
                0.98f);
            var pcm = checked((short)Math.Round(
                sample * short.MaxValue,
                MidpointRounding.AwayFromZero));
            writer.Write(pcm);
        }
    }

    private static float ContentSignal(double seconds)
    {
        var sum =
            Math.Sin(Math.Tau * 80d * seconds) +
            Math.Sin(Math.Tau * 250d * seconds) +
            Math.Sin(Math.Tau * 1_000d * seconds) +
            Math.Sin(Math.Tau * 8_000d * seconds);
        return (float)(sum / 4d);
    }

    private static float AmplitudeForRms(double rms)
        => (float)(rms / ContentRmsAtUnitAmplitude);

    private static void AssertSecondsClose(
        double actual,
        double expected,
        double tolerance,
        string label)
    {
        var error = Math.Abs(actual - expected);
        Assert.True(
            error <= tolerance,
            $"{label}: expected {expected:F3}s ± {tolerance:F3}s, got {actual:F3}s (error {error:F3}s)");
    }
}

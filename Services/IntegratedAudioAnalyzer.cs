using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

public enum IntegratedAnalysisStatus
{
    Generated,
    Skipped,
    Unsupported,
    Failed,
}

public sealed record IntegratedAnalysisResult(
    IntegratedAnalysisStatus Status,
    string? Reason = null);

/// <summary>
/// Self-contained companion analyzer. Jellyfin's managed FFmpeg decodes the
/// source exactly once. The resulting PCM stream feeds amplitude, spectral,
/// rhythm, harmonic, and structure analyzers while a split FFmpeg filter branch
/// measures EBU R128 loudness/true-peak/LRA.
///
/// All artifacts are host-neutral and the sidecar is written last as the
/// readiness gate. Playback remains independent of analysis success.
/// </summary>
public sealed class IntegratedAudioAnalyzer
{
    public const string AmplitudeVariant = "awf_v1_native_mono_b8";
    public const int DecodeSampleRate = 48_000;

    private static readonly JsonSerializerOptions SidecarJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly JellyfinAnalysisSubjectFactory _subjectFactory;
    private readonly SidecarLoader _sidecarLoader;
    private readonly ILogger<IntegratedAudioAnalyzer> _logger;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);

    public IntegratedAudioAnalyzer(
        ILibraryManager library,
        IMediaEncoder mediaEncoder,
        JellyfinAnalysisSubjectFactory subjectFactory,
        SidecarLoader sidecarLoader,
        ILogger<IntegratedAudioAnalyzer> logger)
    {
        _library = library;
        _mediaEncoder = mediaEncoder;
        _subjectFactory = subjectFactory;
        _sidecarLoader = sidecarLoader;
        _logger = logger;
    }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath) &&
        _mediaEncoder.SupportsFilter("loudnorm");

    public async Task<IntegratedAnalysisResult> AnalyzeItemAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = _library.GetItemById(itemId);
        if (item is not Audio audio ||
            !audio.IsFileProtocol ||
            string.IsNullOrWhiteSpace(audio.Path) ||
            !File.Exists(audio.Path))
        {
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Unsupported);
        }

        await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AnalyzeLocalAudioAsync(audio, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private async Task<IntegratedAnalysisResult> AnalyzeLocalAudioAsync(
        Audio audio,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Failed,
                "Jellyfin FFmpeg or loudnorm support is unavailable.");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var storeRoot = RuntimeSettings.ResolveStoreRoot(config);
        var subject = _subjectFactory.ForItem(audio.Id);
        var storeKey = subject.StoreKey();
        var fingerprint = await FingerprintAsync(audio.Path, cancellationToken).ConfigureAwait(false);

        var existing = _sidecarLoader.LoadSidecarFromStore(storeRoot, storeKey);
        var matchingExisting = existing is not null &&
            string.Equals(existing.SourceFingerprint, fingerprint, StringComparison.Ordinal)
                ? existing
                : null;
        if (matchingExisting is not null &&
            HasAllAmplitudeTiers(storeRoot, storeKey) &&
            HasCompleteHigherOrderAnalysis(storeRoot, storeKey, matchingExisting))
        {
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Skipped);
        }

        var sidecarPath = StorePaths.SidecarPath(storeRoot, storeKey);
        TryDelete(sidecarPath);

        try
        {
            var decoded = await DecodeAndAnalyzeAsync(audio, cancellationToken).ConfigureAwait(false);
            if (decoded.Amplitude.SampleCount == 0)
            {
                return new IntegratedAnalysisResult(
                    IntegratedAnalysisStatus.Failed,
                    "FFmpeg decoded no audio samples.");
            }

            var shortEtag = $"\"{fingerprint[..8]}\"";
            var amplitudeTiers = decoded.Amplitude.Complete();
            var waveformRefs = new List<StoredWaveformRef>(amplitudeTiers.Count);
            foreach (var pps in AmplitudeEnvelopeBuilder.Tiers)
            {
                var path = StorePaths.WaveformDatPath(storeRoot, storeKey, AmplitudeVariant, pps);
                var bytes = AmplitudeEnvelopeBuilder.EncodeRiff(pps, amplitudeTiers[pps]);
                await AtomicWriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                waveformRefs.Add(new StoredWaveformRef(
                    AmplitudeVariant,
                    pps,
                    shortEtag,
                    $"waveforms/{storeKey}/{AmplitudeVariant}/pps_{pps}.dat"));
            }

            var spectralRefs =
                new List<StoredWaveformRef>(decoded.HigherOrder.SpectralTiers.Count);
            foreach (var pps in SpectralArtifactEncoder.Tiers)
            {
                var path = StorePaths.WaveformDatPath(
                    storeRoot,
                    storeKey,
                    SpectralArtifactEncoder.Variant,
                    pps);
                await AtomicWriteAsync(
                    path,
                    decoded.HigherOrder.SpectralTiers[pps],
                    cancellationToken).ConfigureAwait(false);
                spectralRefs.Add(new StoredWaveformRef(
                    SpectralArtifactEncoder.Variant,
                    pps,
                    shortEtag,
                    $"waveforms/{storeKey}/{SpectralArtifactEncoder.Variant}/pps_{pps}.dat"));
            }

            var sidecar = new AnalysisSidecar(
                SchemaVersion: AnalysisSidecar.CurrentSchemaVersion,
                SubjectVersion: AnalysisSubjectV1.SubjectVersion,
                SubjectStoreKey: storeKey,
                SourceFingerprint: fingerprint,
                DurationMs: decoded.Amplitude.DurationMs,
                WaveformRefs: waveformRefs,
                ProducerVersion: AnalyzerVersion(),
                GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
                Analysis: decoded.HigherOrder.Analysis,
                SpectralRefs: spectralRefs,
                SpectralAnalysisVersion: SpectralArtifactEncoder.FormatVersion);

            var sidecarBytes = JsonSerializer.SerializeToUtf8Bytes(sidecar, SidecarJsonOptions);
            await AtomicWriteAsync(sidecarPath, sidecarBytes, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Generated integrated full-track analysis for Jellyfin item {ItemId} ({DurationMs} ms, {SpectralTierCount} spectral tiers)",
                audio.Id,
                decoded.Amplitude.DurationMs,
                spectralRefs.Count);
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Generated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Integrated analysis failed for Jellyfin item {ItemId}",
                audio.Id);
            TryDelete(sidecarPath);
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Failed,
                ex.GetType().Name);
        }
    }

    private async Task<DecodedAnalysis> DecodeAndAnalyzeAsync(
        Audio audio,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
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
            "-i", audio.Path,
            "-filter_complex",
            $"[0:a:0]asplit=2[loud][pcm];" +
            $"[loud]loudnorm=print_format=json[loudout];" +
            $"[loudout]anullsink;" +
            $"[pcm]aresample={DecodeSampleRate}," +
            "aformat=sample_fmts=flt:channel_layouts=mono[pcmout]",
            "-map", "[pcmout]",
            "-vn",
            "-sn",
            "-dn",
            "-f", "f32le",
            "-acodec", "pcm_f32le",
            "pipe:1",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Jellyfin FFmpeg.");
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to lower integrated analyzer FFmpeg priority.");
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var child = (Process)state!;
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best-effort shutdown; cancellation remains authoritative.
                }
            },
            process);

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var amplitude = new AmplitudeEnvelopeBuilder(DecodeSampleRate);
        var higherOrder = new HigherOrderAudioAnalysisBuilder(DecodeSampleRate);
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024 + sizeof(float)];
        var carry = 0;

        while (true)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(carry, buffer.Length - carry),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var total = carry + read;
            var usable = total - (total % sizeof(float));
            for (var offset = 0; offset < usable; offset += sizeof(float))
            {
                var bits = BinaryPrimitives.ReadInt32LittleEndian(
                    buffer.AsSpan(offset, sizeof(float)));
                var sample = BitConverter.Int32BitsToSingle(bits);
                amplitude.Push(sample);
                higherOrder.Push(sample);
            }

            carry = total - usable;
            if (carry > 0)
            {
                buffer.AsSpan(usable, carry).CopyTo(buffer);
            }
        }

        if (carry != 0)
        {
            throw new InvalidDataException("FFmpeg returned a truncated float PCM sample.");
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.LogDebug(
                "Integrated analyzer FFmpeg exited with code {ExitCode}; diagnostic bytes={DiagnosticLength}",
                process.ExitCode,
                stderr.Length);
            throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}.");
        }

        var loudness = FfmpegLoudnessParser.Parse(stderr)
            ?? throw new InvalidDataException(
                "Jellyfin FFmpeg completed without parseable loudnorm input statistics.");

        return new DecodedAnalysis(
            amplitude,
            higherOrder.Complete(loudness));
    }

    private static async Task<string> FingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool HasAllAmplitudeTiers(string storeRoot, string storeKey)
    {
        foreach (var pps in AmplitudeEnvelopeBuilder.Tiers)
        {
            if (!File.Exists(StorePaths.WaveformDatPath(storeRoot, storeKey, AmplitudeVariant, pps)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasCompleteHigherOrderAnalysis(
        string storeRoot,
        string storeKey,
        AnalysisSidecar sidecar)
    {
        if (sidecar.Analysis is null ||
            sidecar.SpectralAnalysisVersion != SpectralArtifactEncoder.FormatVersion ||
            sidecar.SpectralRefs is null ||
            sidecar.SpectralRefs.Count != SpectralArtifactEncoder.Tiers.Length)
        {
            return false;
        }

        foreach (var pps in SpectralArtifactEncoder.Tiers)
        {
            if (!File.Exists(
                    StorePaths.WaveformDatPath(
                        storeRoot,
                        storeKey,
                        SpectralArtifactEncoder.Variant,
                        pps)))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task AtomicWriteAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Artifact path has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed cleanup is non-authoritative; next generation uses a new temp path.
        }
    }

    private static string AnalyzerVersion()
        => $"audio-gateway/{typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown"}";

    private sealed record DecodedAnalysis(
        AmplitudeEnvelopeBuilder Amplitude,
        HigherOrderAnalysisResult HigherOrder);
}

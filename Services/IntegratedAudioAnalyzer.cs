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
/// In-process companion analyzer adapter. Jellyfin's managed FFmpeg performs
/// decoding; this service consumes bounded streaming mono PCM and writes the
/// existing host-neutral V2 amplitude artifact contract.
///
/// It deliberately does not synthesize spectral/rhythm/harmonic facts. Those
/// remain absent unless produced by a parity-certified canonical analyzer.
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

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath);

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
                "Jellyfin FFmpeg is unavailable.");
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
        if (matchingExisting is not null && HasAllAmplitudeTiers(storeRoot, storeKey))
        {
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Skipped);
        }

        var sidecarPath = StorePaths.SidecarPath(storeRoot, storeKey);
        TryDelete(sidecarPath);

        try
        {
            var envelope = await DecodeEnvelopeAsync(audio, cancellationToken).ConfigureAwait(false);
            if (envelope.SampleCount == 0)
            {
                return new IntegratedAnalysisResult(
                    IntegratedAnalysisStatus.Failed,
                    "FFmpeg decoded no audio samples.");
            }

            var tiers = envelope.Complete();
            var refs = new List<StoredWaveformRef>(tiers.Count);
            var shortEtag = $"\"{fingerprint[..8]}\"";

            foreach (var pps in AmplitudeEnvelopeBuilder.Tiers)
            {
                var path = StorePaths.WaveformDatPath(storeRoot, storeKey, AmplitudeVariant, pps);
                var bytes = AmplitudeEnvelopeBuilder.EncodeRiff(pps, tiers[pps]);
                await AtomicWriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                refs.Add(new StoredWaveformRef(
                    AmplitudeVariant,
                    pps,
                    shortEtag,
                    $"waveforms/{storeKey}/{AmplitudeVariant}/pps_{pps}.dat"));
            }

            var sidecar = new AnalysisSidecar(
                SchemaVersion: AnalysisSidecar.CurrentSchemaVersion,
                SubjectVersion: AnalysisSubjectV1.SubjectVersion,
                SubjectStoreKey: storeKey,
                SourceFingerprint: fingerprint,
                DurationMs: envelope.DurationMs,
                WaveformRefs: refs,
                ProducerVersion: AnalyzerVersion(),
                GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
                Analysis: matchingExisting?.Analysis,
                SpectralRefs: matchingExisting?.SpectralRefs,
                SpectralAnalysisVersion: matchingExisting?.SpectralAnalysisVersion ?? 0);

            var sidecarBytes = JsonSerializer.SerializeToUtf8Bytes(sidecar, SidecarJsonOptions);
            await AtomicWriteAsync(sidecarPath, sidecarBytes, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Generated integrated amplitude analysis for Jellyfin item {ItemId} ({DurationMs} ms)",
                audio.Id,
                envelope.DurationMs);
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

    private async Task<AmplitudeEnvelopeBuilder> DecodeEnvelopeAsync(
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
            "-loglevel", "error",
            "-i", audio.Path,
            "-map", "0:a:0",
            "-vn",
            "-sn",
            "-dn",
            "-ac", "1",
            "-ar", DecodeSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
        var envelope = new AmplitudeEnvelopeBuilder(DecodeSampleRate);
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
                var bits = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, sizeof(float)));
                envelope.Push(BitConverter.Int32BitsToSingle(bits));
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

        return envelope;
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
}

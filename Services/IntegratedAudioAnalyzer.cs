using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioGateway.Services;

public enum IntegratedAnalysisStatus
{
    Generated,
    Skipped,
    Deferred,
    CloudExcluded,
    Unsupported,
    Failed,
}

public sealed record IntegratedAnalysisResult(
    IntegratedAnalysisStatus Status,
    string? Reason = null);

/// <summary>
/// Self-contained companion analyzer. Jellyfin's managed FFmpeg decodes the
/// source once. The PCM analysis branch retains the source-declared sample rate
/// and the first one or two source channels, matching Slipmat's canonical Rust
/// accumulators. Multichannel sources select c0/c1 rather than downmixing.
///
/// The sidecar is written last as the readiness gate. Missing source stream
/// metadata or analysis failure remains ordinary client fallback.
/// </summary>
public sealed class IntegratedAudioAnalyzer
{
    public const string AmplitudeVariant = "awf_v1_riff_mono_u8_peak";

    private static readonly JsonSerializerOptions SidecarJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly JellyfinAnalysisSubjectFactory _subjectFactory;
    private readonly SidecarLoader _sidecarLoader;
    private readonly AnalysisArtifactRetentionService _artifactRetention;
    private readonly ILogger<IntegratedAudioAnalyzer> _logger;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly object _availabilityGate = new();
    private string? _probedEncoderPath;
    private bool _probedAvailability;

    /// <summary>Creates the single-authority Jellyfin audio analyzer.</summary>
    public IntegratedAudioAnalyzer(
        ILibraryManager library,
        IMediaEncoder mediaEncoder,
        JellyfinAnalysisSubjectFactory subjectFactory,
        SidecarLoader sidecarLoader,
        AnalysisArtifactRetentionService artifactRetention,
        ILogger<IntegratedAudioAnalyzer> logger)
    {
        _library = library;
        _mediaEncoder = mediaEncoder;
        _subjectFactory = subjectFactory;
        _sidecarLoader = sidecarLoader;
        _artifactRetention = artifactRetention;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            var encoderPath = _mediaEncoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(encoderPath))
            {
                return false;
            }

            lock (_availabilityGate)
            {
                if (!string.Equals(_probedEncoderPath, encoderPath, StringComparison.Ordinal))
                {
                    // Jellyfin's SupportsFilter surface intentionally reports only its
                    // own transcoding allowlist, which does not include loudnorm even
                    // when the managed FFmpeg binary supports it. Probe the binary we
                    // will actually execute instead.
                    _probedAvailability = ProbeLoudnormSupport(encoderPath);
                    _probedEncoderPath = encoderPath;
                    if (!_probedAvailability)
                    {
                        _logger.LogWarning(
                            "Integrated analyzer could not execute the loudnorm filter with Jellyfin FFmpeg at {EncoderPath}",
                            encoderPath);
                    }
                }

                return _probedAvailability;
            }
        }
    }

    internal static bool ProbeLoudnormSupport(string encoderPath)
    {
        if (string.IsNullOrWhiteSpace(encoderPath))
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = encoderPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            foreach (var argument in new[]
            {
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-f", "lavfi",
                "-i", "anullsrc=r=48000:cl=stereo",
                "-t", "0.02",
                "-filter:a", "loudnorm",
                "-f", "null",
                "-",
            })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort. Availability remains false.
                }

                Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1_000);
                return false;
            }

            Task.WaitAll(stdoutTask, stderrTask);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Analyzes one Jellyfin item; scheduled backfill may skip intentional retention evictions.</summary>
    public async Task<IntegratedAnalysisResult> AnalyzeItemAsync(
        Guid itemId,
        CancellationToken cancellationToken,
        bool onDemand = true)
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
            return await AnalyzeLocalAudioAsync(audio, cancellationToken, onDemand).ConfigureAwait(false);
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    /// <summary>Measures one complete, local album sequence using EBU R128 gating.</summary>
    public async Task<IntegratedAnalysisResult> AnalyzeAlbumAsync(
        Guid albumId,
        CancellationToken cancellationToken,
        bool onDemand = true)
    {
        var acquired = onDemand
            ? await _analysisGate.WaitAsync(Timeout.Infinite, cancellationToken).ConfigureAwait(false)
            : await _analysisGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Deferred,
                "Background album analysis yielded to active on-demand work.");
        }

        try
        {
            if (_library.GetItemById(albumId) is not MusicAlbum album)
            {
                return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Unsupported);
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var buildStatus = JellyfinAlbumLoudnessManifestFactory.TryBuild(
                album,
                config,
                cancellationToken,
                out var inputs,
                out var manifest,
                out var invalidReason);
            if (buildStatus != AlbumManifestBuildStatus.Ready)
            {
                return new IntegratedAnalysisResult(
                    buildStatus == AlbumManifestBuildStatus.CloudExcluded
                        ? IntegratedAnalysisStatus.CloudExcluded
                        : IntegratedAnalysisStatus.Unsupported,
                    invalidReason);
            }

            return await AnalyzeAlbumSequenceAsync(
                album,
                inputs,
                manifest!,
                RuntimeSettings.ResolveStoreRoot(config),
                _subjectFactory.ForItem(album.Id).StoreKey(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private async Task<IntegratedAnalysisResult> AnalyzeAlbumSequenceAsync(
        MusicAlbum album,
        IReadOnlyList<AlbumLoudnessSourceInput> inputs,
        AlbumLoudnessManifestV1 manifest,
        string storeRoot,
        string storeKey,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Failed,
                "Jellyfin FFmpeg or loudnorm support is unavailable.");
        }

        using var lease = await _artifactRetention
            .AcquireAsync(storeRoot, storeKey, cancellationToken)
            .ConfigureAwait(false);
        var existing = _sidecarLoader.LoadAlbumLoudnessSidecarFromStore(storeRoot, storeKey);
        var currentRecipe = existing?.Measurement.Analyzer.ConfigurationDigest
            == FfmpegLoudnessParser.AlbumAnalyzerConfigurationDigest;
        if (currentRecipe
            && existing!.Manifest.MembershipFingerprint == manifest.MembershipFingerprint)
        {
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Skipped);
        }

        try
        {
            var priorMembers = existing?.Manifest.Members.ToDictionary(member => member.ItemId);
            var membersWithFingerprints = new List<AlbumLoudnessMemberV1>(manifest.Members.Count);
            foreach (var member in manifest.Members)
            {
                var input = inputs.Single(value => value.Audio.Id.ToString("N") == member.ItemId);
                string? fingerprint = null;
                if (priorMembers is not null
                    && priorMembers.TryGetValue(member.ItemId, out var prior)
                    && SameAlbumSourceStamp(prior, member)
                    && prior.SourceFingerprint is not null)
                {
                    fingerprint = prior.SourceFingerprint;
                }

                fingerprint ??= await FingerprintAsync(input.Audio.Path, cancellationToken).ConfigureAwait(false);
                membersWithFingerprints.Add(member with { SourceFingerprint = fingerprint });
            }

            var fingerprintedManifest = manifest with { Members = membersWithFingerprints };
            var sourceFingerprint = AlbumLoudnessManifestV1.ComputeSourceFingerprint(membersWithFingerprints);
            if (existing is not null
                && currentRecipe
                && string.Equals(existing.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal))
            {
                var refreshed = existing with
                {
                    Manifest = fingerprintedManifest,
                    GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
                };
                await WriteAlbumLoudnessSidecarAsync(storeRoot, storeKey, refreshed, cancellationToken)
                    .ConfigureAwait(false);
                return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Skipped);
            }

            var beforeMeasure = BuildCurrentAlbumManifest(album.Id, cancellationToken);
            if (beforeMeasure?.MembershipFingerprint != manifest.MembershipFingerprint)
            {
                return new IntegratedAnalysisResult(
                    IntegratedAnalysisStatus.Failed,
                    "Album membership or a source changed while fingerprinting.");
            }

            var loudness = await MeasureAlbumSequenceAsync(inputs, cancellationToken).ConfigureAwait(false);
            var afterMeasure = BuildCurrentAlbumManifest(album.Id, cancellationToken);
            if (afterMeasure?.MembershipFingerprint != manifest.MembershipFingerprint)
            {
                return new IntegratedAnalysisResult(
                    IntegratedAnalysisStatus.Failed,
                    "Album membership or a source changed while analysis was running.");
            }

            var durationMs = inputs.Sum(input => Math.Max(0L, input.Audio.RunTimeTicks ?? 0L))
                / TimeSpan.TicksPerMillisecond;
            var sidecar = new AlbumLoudnessSidecarV1(
                Version: AlbumLoudnessSidecarV1.CurrentVersion,
                SubjectStoreKey: storeKey,
                SourceFingerprint: sourceFingerprint,
                Manifest: fingerprintedManifest,
                DurationMs: durationMs,
                Measurement: FfmpegLoudnessParser.ToAlbumMeasurementEvidence(loudness),
                GeneratedAt: DateTimeOffset.UtcNow.ToString("O"));
            await WriteAlbumLoudnessSidecarAsync(storeRoot, storeKey, sidecar, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Generated album-sequence loudness evidence for Jellyfin album {AlbumId} ({TrackCount} local tracks, {DurationMs} ms)",
                album.Id,
                inputs.Count,
                durationMs);
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Generated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Album-sequence loudness analysis failed for Jellyfin album {AlbumId}", album.Id);
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Failed, ex.GetType().Name);
        }
    }

    private AlbumLoudnessManifestV1? BuildCurrentAlbumManifest(
        Guid albumId,
        CancellationToken cancellationToken)
    {
        if (_library.GetItemById(albumId) is not MusicAlbum album) return null;
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        cancellationToken.ThrowIfCancellationRequested();
        return JellyfinAlbumLoudnessManifestFactory.TryBuild(
            album,
            config,
            cancellationToken,
            out _,
            out var manifest,
            out _) == AlbumManifestBuildStatus.Ready
                ? manifest
                : null;
    }

    private async Task<LoudnessAnalysis> MeasureAlbumSequenceAsync(
        IReadOnlyList<AlbumLoudnessSourceInput> inputs,
        CancellationToken cancellationToken)
    {
        var layout = inputs[0].ChannelLayout.Trim().ToLowerInvariant();
        var filter = BuildAlbumLoudnessFilterGraph(
            inputs.Select(input => input.StreamIndex).ToArray(),
            layout);
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
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
        foreach (var input in inputs)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(input.Audio.Path);
        }
        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(filter);
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
        if (!process.Start()) throw new InvalidOperationException("Unable to start Jellyfin FFmpeg.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to lower album loudness FFmpeg priority.");
        }

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var child = (Process)state!;
                try
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                }
                catch { }
            },
            process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _logger.LogDebug(
                "Album loudness FFmpeg exited with code {ExitCode}; diagnostic bytes={DiagnosticLength}",
                process.ExitCode,
                stderr.Length);
            throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}.");
        }

        return FfmpegLoudnessParser.Parse(stderr)
            ?? throw new InvalidDataException("FFmpeg completed without parseable album loudnorm input statistics.");
    }

    internal static string BuildAlbumLoudnessFilterGraph(
        IReadOnlyList<int> streamIndices,
        string channelLayout)
    {
        if (streamIndices.Count == 0) throw new ArgumentException("Album sequence has no tracks.", nameof(streamIndices));
        if (streamIndices.Any(streamIndex => streamIndex < 0))
            throw new ArgumentOutOfRangeException(nameof(streamIndices), "Audio stream indices must be non-negative.");
        var graph = streamIndices.Select((streamIndex, index) =>
                $"[{index}:{streamIndex}]aresample=48000:async=0:first_pts=0," +
                $"aformat=sample_fmts=fltp:channel_layouts={channelLayout}," +
                $"asetpts=PTS-STARTPTS[a{index}]")
            .ToList();
        var concatInputs = string.Concat(Enumerable.Range(0, streamIndices.Count).Select(index => $"[a{index}]"));
        graph.Add($"{concatInputs}concat=n={streamIndices.Count}:v=0:a=1[album]");
        graph.Add("[album]loudnorm=I=-23:TP=-2:LRA=7:print_format=json[measured]");
        return string.Join(';', graph);
    }

    private static bool SameAlbumSourceStamp(
        AlbumLoudnessMemberV1 left,
        AlbumLoudnessMemberV1 right)
        => left.ItemId == right.ItemId
            && left.SourceLength == right.SourceLength
            && left.SourceLastWriteUtcTicks == right.SourceLastWriteUtcTicks
            && left.SourceLocatorFingerprint == right.SourceLocatorFingerprint;

    private static async Task WriteAlbumLoudnessSidecarAsync(
        string storeRoot,
        string storeKey,
        AlbumLoudnessSidecarV1 sidecar,
        CancellationToken cancellationToken)
    {
        var path = StorePaths.AlbumLoudnessSidecarPath(storeRoot, storeKey);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sidecar, SidecarJsonOptions);
        await AtomicWriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IntegratedAnalysisResult> AnalyzeLocalAudioAsync(
        Audio audio,
        CancellationToken cancellationToken,
        bool onDemand)
    {
        if (!IsAvailable)
        {
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Failed,
                "Jellyfin FFmpeg or loudnorm support is unavailable.");
        }

        var sourceFormat = ResolveSourceFormat(audio);
        if (sourceFormat is null)
        {
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Unsupported,
                "Jellyfin source audio metadata does not declare a valid sample rate/channel count.");
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var storeRoot = RuntimeSettings.ResolveStoreRoot(config);
        var subject = _subjectFactory.ForItem(audio.Id);
        var storeKey = subject.StoreKey();
        using var artifactLease = await _artifactRetention
            .AcquireAsync(storeRoot, storeKey, cancellationToken)
            .ConfigureAwait(false);
        var sourceStamp = AnalysisSourceStamp.Capture(audio.Path);
        if (sourceStamp is null)
        {
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Failed,
                "Source file metadata is unavailable.");
        }

        if (!onDemand && AnalysisSourceStamp.WereArtifactsEvicted(storeRoot, storeKey, audio.Path))
        {
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Skipped);
        }

        var existing = _sidecarLoader.LoadSidecarFromStore(storeRoot, storeKey);
        // The scheduled backfill visits every local item. Reuse complete artifacts
        // when the persisted size/mtime stamp matches, without reading the media
        // payload merely to recompute a SHA-256 fingerprint on every run.
        if (CanSkipUnmodifiedSource(storeRoot, storeKey, audio.Path, existing))
        {
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Skipped);
        }

        // Missing/changed stamps still require the definitive full-source hash.
        // Keep the existing matching-fingerprint path for metadata-only changes
        // and artifacts generated before source stamps existed.
        var fingerprint = await FingerprintAsync(audio.Path, cancellationToken).ConfigureAwait(false);

        var matchingExisting = existing is not null &&
            string.Equals(existing.SourceFingerprint, fingerprint, StringComparison.Ordinal)
                ? existing
                : null;
        if (matchingExisting is not null &&
            HasAllAmplitudeTiers(storeRoot, storeKey) &&
            HasCompleteHigherOrderAnalysis(storeRoot, storeKey, matchingExisting))
        {
            await AnalysisSourceStamp.WriteAsync(
                    storeRoot,
                    storeKey,
                    sourceStamp,
                    cancellationToken)
                .ConfigureAwait(false);
            return new IntegratedAnalysisResult(IntegratedAnalysisStatus.Skipped);
        }

        var sidecarPath = StorePaths.SidecarPath(storeRoot, storeKey);
        TryDelete(sidecarPath);
        AnalysisSourceStamp.Delete(storeRoot, storeKey);

        try
        {
            var decoded = await DecodeAndAnalyzeAsync(
                audio,
                sourceFormat,
                cancellationToken).ConfigureAwait(false);
            if (decoded.Amplitude.SampleCount == 0)
            {
                return new IntegratedAnalysisResult(
                    IntegratedAnalysisStatus.Failed,
                    "FFmpeg decoded no audio samples.");
            }

            var finalSourceStamp = AnalysisSourceStamp.Capture(audio.Path);
            if (finalSourceStamp is null || finalSourceStamp != sourceStamp)
            {
                return new IntegratedAnalysisResult(
                    IntegratedAnalysisStatus.Failed,
                    "Source file changed while analysis was running.");
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
            await AnalysisSourceStamp.WriteAsync(
                    storeRoot,
                    storeKey,
                    finalSourceStamp,
                    cancellationToken)
                .ConfigureAwait(false);
            await AtomicWriteAsync(sidecarPath, sidecarBytes, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Generated exact-source companion analysis for Jellyfin item {ItemId} ({SampleRate} Hz, {SourceChannels} source channels, {AnalysisChannels} analysis lanes, {DurationMs} ms)",
                audio.Id,
                sourceFormat.SampleRate,
                sourceFormat.SourceChannels,
                sourceFormat.AnalysisChannels,
                decoded.Amplitude.DurationMs);
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
            AnalysisSourceStamp.Delete(storeRoot, storeKey);
            return new IntegratedAnalysisResult(
                IntegratedAnalysisStatus.Failed,
                ex.GetType().Name);
        }
    }

    private async Task<DecodedAnalysis> DecodeAndAnalyzeAsync(
        Audio audio,
        SourceAudioFormat sourceFormat,
        CancellationToken cancellationToken)
    {
        var projection = BuildAnalysisProjection(sourceFormat);
        var filterGraph = BuildFilterGraph(sourceFormat, projection);

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
            filterGraph,
            "-map", "[pcmout]",
            "-vn",
            "-sn",
            "-dn",
            "-ar", sourceFormat.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", sourceFormat.AnalysisChannels.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
        var amplitude = new AmplitudeEnvelopeBuilder(sourceFormat.SampleRate);
        var higherOrder = new HigherOrderAudioAnalysisBuilder(
            sourceFormat.SampleRate,
            sourceFormat.AnalysisChannels);
        var stream = process.StandardOutput.BaseStream;
        var frameBytes = sizeof(float) * sourceFormat.AnalysisChannels;
        var buffer = new byte[64 * 1024 + frameBytes - 1];
        var frame = new float[sourceFormat.AnalysisChannels];
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
            var usable = total - (total % frameBytes);
            for (var offset = 0; offset < usable; offset += frameBytes)
            {
                for (var lane = 0; lane < sourceFormat.AnalysisChannels; lane++)
                {
                    var sampleOffset = offset + lane * sizeof(float);
                    var bits = BinaryPrimitives.ReadInt32LittleEndian(
                        buffer.AsSpan(sampleOffset, sizeof(float)));
                    frame[lane] = BitConverter.Int32BitsToSingle(bits);
                }

                amplitude.Push(AmplitudeProjection(frame));
                higherOrder.PushFrame(frame);
            }

            carry = total - usable;
            if (carry > 0)
            {
                buffer.AsSpan(usable, carry).CopyTo(buffer);
            }
        }

        if (carry != 0)
        {
            throw new InvalidDataException("FFmpeg returned a truncated interleaved PCM frame.");
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

    internal static SourceAudioFormat? ResolveSourceFormat(Audio audio)
    {
        var stream = audio.GetMediaStreams()
            .Where(value =>
                value.Type == MediaStreamType.Audio &&
                value.Index >= 0 &&
                value.SampleRate is > 0 &&
                value.Channels is > 0)
            .OrderByDescending(value => value.IsDefault)
            .ThenBy(value => value.Index)
            .FirstOrDefault();
        if (stream is null)
        {
            return null;
        }

        return new SourceAudioFormat(
            stream.Index,
            stream.SampleRate!.Value,
            stream.Channels!.Value,
            Math.Clamp(stream.Channels.Value, 1, 2));
    }

    internal static string BuildAnalysisProjection(SourceAudioFormat sourceFormat)
        => sourceFormat.SourceChannels switch
        {
            1 => "aformat=sample_fmts=flt:channel_layouts=mono",
            2 => "aformat=sample_fmts=flt:channel_layouts=stereo",
            _ => "pan=stereo|c0=c0|c1=c1,aformat=sample_fmts=flt:channel_layouts=stereo",
        };

    internal static string BuildFilterGraph(
        SourceAudioFormat sourceFormat,
        string projection)
        => $"[0:{sourceFormat.StreamIndex}]asplit=2[loud][analysis];" +
            "[loud]loudnorm=print_format=json[loudout];" +
            "[loudout]anullsink;" +
            $"[analysis]{projection}[pcmout]";

    internal static float AmplitudeProjection(ReadOnlySpan<float> frame)
    {
        if (frame.IsEmpty)
        {
            return 0f;
        }

        var lanes = Math.Min(frame.Length, 2);
        var selected = 0f;
        var maximum = -1f;
        for (var lane = 0; lane < lanes; lane++)
        {
            var sample = float.IsFinite(frame[lane]) ? frame[lane] : 0f;
            var magnitude = Math.Abs(sample);
            if (magnitude > maximum)
            {
                maximum = magnitude;
                selected = sample;
            }
        }

        return selected;
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

    internal static bool CanSkipUnmodifiedSource(
        string storeRoot,
        string storeKey,
        string mediaPath,
        AnalysisSidecar? existing)
        => existing?.IsCurrentFor(storeKey) == true
            && AnalysisSourceStamp.IsCurrent(storeRoot, storeKey, mediaPath)
            && HasAllAmplitudeTiers(storeRoot, storeKey)
            && HasCompleteHigherOrderAnalysis(storeRoot, storeKey, existing);

    private static bool HasAllAmplitudeTiers(string storeRoot, string storeKey)
    {
        foreach (var pps in AmplitudeEnvelopeBuilder.Tiers)
        {
            if (!IsValidAmplitudeArtifact(
                    StorePaths.WaveformDatPath(storeRoot, storeKey, AmplitudeVariant, pps),
                    pps))
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
            if (!IsValidSpectralArtifact(
                    StorePaths.WaveformDatPath(
                        storeRoot,
                        storeKey,
                        SpectralArtifactEncoder.Variant,
                        pps),
                    pps))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidAmplitudeArtifact(string path, int pointsPerSecond)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[60];
            if (stream.Length < header.Length)
            {
                return false;
            }

            stream.ReadExactly(header);

            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(header[56..60]);
            var expectedLength = 60L + dataLength + (dataLength & 1);
            return header[..4].SequenceEqual("RIFF"u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) == stream.Length - 8
                && header[8..12].SequenceEqual("WAVE"u8)
                && header[12..16].SequenceEqual("fmt "u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]) == 16
                && BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]) == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(header[22..24]) == 1
                && BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]) == pointsPerSecond
                && BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]) == pointsPerSecond
                && BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]) == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(header[34..36]) == 8
                && header[36..40].SequenceEqual("JUNK"u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]) == 8
                && header[52..56].SequenceEqual("data"u8)
                && dataLength > 0
                && expectedLength == stream.Length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsValidSpectralArtifact(string path, int framesPerSecond)
    {
        const int baseHeaderLength = 52;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[baseHeaderLength];
            if (stream.Length < header.Length)
            {
                return false;
            }

            stream.ReadExactly(header);

            var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
            var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
            var channelCount = header[20];
            var payloadLength = (ulong)frameCount * channelCount * 6;
            if (!header[..4].SequenceEqual("SLWS"u8)
                || BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]) != SpectralArtifactEncoder.FormatVersion
                || headerLength < baseHeaderLength
                || headerLength > stream.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]) != framesPerSecond
                || frameCount == 0
                || channelCount is < 1 or > 2
                || payloadLength != (ulong)(stream.Length - headerLength))
            {
                return false;
            }

            stream.Position = headerLength;
            return ComputeCrc32(stream) == BinaryPrimitives.ReadUInt32LittleEndian(header[48..52]);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static uint ComputeCrc32(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var crc = 0xFFFF_FFFFu;
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                crc ^= buffer[index];
                for (var bit = 0; bit < 8; bit++)
                {
                    var mask = (uint)-(int)(crc & 1);
                    crc = (crc >> 1) ^ (0xEDB8_8320u & mask);
                }
            }
        }

        return ~crc;
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

    internal sealed record SourceAudioFormat(
        int StreamIndex,
        int SampleRate,
        int SourceChannels,
        int AnalysisChannels);

    private sealed record DecodedAnalysis(
        AmplitudeEnvelopeBuilder Amplitude,
        HigherOrderAnalysisResult HigherOrder);
}

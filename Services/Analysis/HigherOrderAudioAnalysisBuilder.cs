using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal sealed record HigherOrderAnalysisResult(
    IReadOnlyDictionary<int, byte[]> SpectralTiers,
    AnalysisBlocks? Analysis,
    TrackBoundaryAnalysis? Boundaries);

internal sealed class HigherOrderAudioAnalysisBuilder
{
    private readonly SpectralAccumulator _spectral;
    private readonly BoundaryAccumulator _boundary;
    private readonly RhythmAccumulator _rhythm;
    private readonly HarmonicAccumulator _harmonic;

    public HigherOrderAudioAnalysisBuilder(int sampleRate, int channelCount)
    {
        _spectral = new SpectralAccumulator(sampleRate, channelCount);
        _boundary = new BoundaryAccumulator(sampleRate, channelCount);
        _rhythm = new RhythmAccumulator(sampleRate, channelCount);
        _harmonic = new HarmonicAccumulator(sampleRate);
    }

    public void PushFrame(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        _spectral.PushFrame(samples);
        _boundary.PushFrame(samples);
        _rhythm.PushFrame(samples);
        _harmonic.Push(HarmonicProjection(samples));
    }

    public HigherOrderAnalysisResult Complete(LoudnessAnalysis? loudness)
    {
        var boundaries = _boundary.Complete();
        var rhythm = _rhythm.Complete(boundaries);
        var harmonic = _harmonic.Complete();
        var spectral = SpectralArtifactEncoder.EncodeTiers(
            _spectral.Complete(),
            boundaries,
            rhythm,
            harmonic);

        AnalysisBlocks? blocks = null;
        if (loudness is not null)
        {
            var timing = new TimingAnalysis(
                Bpm: rhythm.Bpm ?? 0d,
                BpmConfidence: rhythm.Bpm.HasValue ? rhythm.Confidence : 0d,
                Downbeats: rhythm.Downbeats.Count == 0
                    ? null
                    : rhythm.Downbeats
                        .Select(seconds => checked((long)Math.Round(seconds * 1_000d)))
                        .ToList(),
                BeatGrid: rhythm.HasGrid
                    ? new BeatGrid(
                        rhythm.Beats[0] * 1_000d,
                        60_000d / rhythm.Bpm!.Value)
                    : null);

            // Slipmat's canonical Rust path currently defines the optional
            // structure contract but does not produce an energy curve. The
            // companion must not create a second analyzer authority by
            // synthesizing one from its private rhythm frames.
            blocks = new AnalysisBlocks(
                timing,
                new HarmonicAnalysis(
                    harmonic.Key,
                    harmonic.CamelotKey,
                    harmonic.Confidence),
                loudness,
                Structure: null,
                Transitions: null,
                LoudnessMeasurement: FfmpegLoudnessParser.ToMeasurementEvidence(loudness));
        }

        return new HigherOrderAnalysisResult(
            spectral,
            blocks,
            boundaries);
    }

    internal static float HarmonicProjection(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        // Slipmat's local AudioBuffer/Rust-WASM analyzers consume channel 0.
        // Spectral, boundary, and rhythm analysis remain multi-lane.
        return float.IsFinite(samples[0]) ? samples[0] : 0f;
    }

}

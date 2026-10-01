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
    private const int EnergyCurveMaximumPoints = 1_024;

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
        var spectral = SpectralArtifactEncoder.EncodeTiers(
            _spectral.Complete(),
            boundaries,
            rhythm);
        var harmonic = _harmonic.Complete();

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

            var structure = new StructureAnalysis(
                EnergyCurve: BuildEnergyCurve(_rhythm.Frames),
                Sections: null);

            blocks = new AnalysisBlocks(
                timing,
                new HarmonicAnalysis(
                    harmonic.Key,
                    harmonic.CamelotKey,
                    harmonic.Confidence),
                loudness,
                structure,
                Transitions: null);
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

    private static List<EnergyCurvePoint>? BuildEnergyCurve(
        IReadOnlyList<RhythmEnergyFrame> frames)
    {
        if (frames.Count == 0)
        {
            return null;
        }

        var maxEnergy = 0d;
        foreach (var frame in frames)
        {
            maxEnergy = Math.Max(maxEnergy, frame.Overall);
        }

        if (maxEnergy <= 1e-9)
        {
            return null;
        }

        var stride = Math.Max(
            RhythmAccumulator.FramesPerSecond,
            (int)Math.Ceiling(frames.Count / (double)EnergyCurveMaximumPoints));
        var points = new List<EnergyCurvePoint>(
            (frames.Count + stride - 1) / stride);

        for (var start = 0; start < frames.Count; start += stride)
        {
            var end = Math.Min(frames.Count, start + stride);
            var sumSquares = 0d;
            for (var index = start; index < end; index++)
            {
                var value = frames[index].Overall;
                sumSquares += value * value;
            }

            var rms = Math.Sqrt(sumSquares / Math.Max(1, end - start));
            var normalized = Math.Clamp(rms / maxEnergy, 0d, 1d);
            var timeMs = checked(
                (long)Math.Round(
                    start * 1_000d / RhythmAccumulator.FramesPerSecond));
            points.Add(new EnergyCurvePoint(timeMs, normalized));
        }

        return points;
    }
}

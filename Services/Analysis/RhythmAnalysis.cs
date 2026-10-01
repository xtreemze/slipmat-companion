using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal readonly record struct RhythmEnergyFrame(
    float Overall,
    float Low,
    float LowMid,
    float Mid,
    float High);

internal sealed record RhythmAnalysisResult(
    double? Bpm,
    double Confidence,
    IReadOnlyList<double> Beats,
    IReadOnlyList<double> Downbeats,
    byte? BeatsPerBar)
{
    public bool HasGrid => Bpm.HasValue && Beats.Count >= 2;
}

internal sealed class RhythmAccumulator
{
    public const int FramesPerSecond = 20;

    private readonly int _sampleRate;
    private readonly AnalysisBandSplitter _splitter;
    private readonly List<RhythmEnergyFrame> _frames = [];
    private long _sourceFrameIndex;
    private long _analysisFrameIndex;
    private EnergySums _current;
    private bool _frameHasData;

    public RhythmAccumulator(int sampleRate)
    {
        _sampleRate = sampleRate;
        _splitter = new AnalysisBandSplitter(AnalysisCrossover.Rhythm, sampleRate);
    }

    public IReadOnlyList<RhythmEnergyFrame> Frames => _frames;

    public void Push(float sample)
    {
        while (_sourceFrameIndex >= Boundary(_analysisFrameIndex + 1))
        {
            Emit();
        }

        var value = double.IsFinite(sample) ? sample : 0d;
        var bands = _splitter.Process(value);
        _current.Observe(value, bands.Bass, bands.Mid, bands.Presence, bands.Air);
        _frameHasData = true;
        _sourceFrameIndex++;
    }

    public RhythmAnalysisResult Complete()
    {
        if (_frameHasData)
        {
            Emit();
        }

        var durationSeconds = _sampleRate > 0
            ? _sourceFrameIndex / (double)_sampleRate
            : 0d;
        return RhythmGridAnalyzer.Analyze(_frames, FramesPerSecond, durationSeconds);
    }

    private long Boundary(long index)
        => index * _sampleRate / FramesPerSecond;

    private void Emit()
    {
        _frames.Add(_current.Complete());
        _current = default;
        _analysisFrameIndex++;
        _frameHasData = false;
    }

    private struct EnergySums
    {
        private double _overall;
        private double _low;
        private double _lowMid;
        private double _mid;
        private double _high;
        private long _observations;

        public void Observe(double overall, double low, double lowMid, double mid, double high)
        {
            _overall += overall * overall;
            _low += low * low;
            _lowMid += lowMid * lowMid;
            _mid += mid * mid;
            _high += high * high;
            _observations++;
        }

        public RhythmEnergyFrame Complete()
        {
            if (_observations == 0)
            {
                return default;
            }

            var count = (double)_observations;
            return new RhythmEnergyFrame(
                (float)Math.Sqrt(_overall / count),
                (float)Math.Sqrt(_low / count),
                (float)Math.Sqrt(_lowMid / count),
                (float)Math.Sqrt(_mid / count),
                (float)Math.Sqrt(_high / count));
        }
    }
}

internal static class RhythmGridAnalyzer
{
    private const double MinBpm = 60d;
    private const double MaxBpm = 200d;
    private const double MinTrackSeconds = 8d;
    private const double MinPeriodicity = 0.42d;
    private const double MinPeriodicityMargin = 0.025d;
    private const double MinBeatSupport = 0.55d;
    private const double MinDownbeatMargin = 0.08d;
    private const int MinBarsForDownbeat = 3;

    public static RhythmAnalysisResult Analyze(
        IReadOnlyList<RhythmEnergyFrame> frames,
        int framesPerSecond,
        double durationSeconds)
    {
        if (framesPerSecond <= 0 ||
            !double.IsFinite(durationSeconds) ||
            durationSeconds < MinTrackSeconds ||
            frames.Count < framesPerSecond * MinTrackSeconds)
        {
            return Empty();
        }

        var onset = OnsetEnvelope(frames);
        var maxOnset = onset.Count == 0 ? 0d : onset.Max();
        var totalOnset = onset.Sum();
        if (maxOnset <= 1e-6 || totalOnset <= 1e-4)
        {
            return Empty();
        }

        var minLag = Math.Max(2, (int)Math.Round(framesPerSecond * 60d / MaxBpm));
        var maxLag = Math.Min(
            frames.Count - 2,
            Math.Max(minLag + 1, (int)Math.Round(framesPerSecond * 60d / MinBpm)));
        if (minLag >= maxLag)
        {
            return Empty();
        }

        var scored = new List<(int Lag, double Score)>();
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            scored.Add((lag, NormalizedAutocorrelation(onset, lag)));
        }

        scored.Sort((left, right) => right.Score.CompareTo(left.Score));
        if (scored.Count == 0)
        {
            return Empty();
        }

        var best = scored[0];
        var secondScore = scored
            .Where(item =>
                Math.Abs(item.Lag - best.Lag) > 1 &&
                !IsNearHarmonic(item.Lag, best.Lag))
            .Select(item => item.Score)
            .DefaultIfEmpty(0d)
            .First();

        if (best.Score < MinPeriodicity ||
            best.Score - secondScore < MinPeriodicityMargin)
        {
            return Empty();
        }

        var phase = BestPhase(onset, best.Lag);
        var beats = GridTimes(
            phase,
            best.Lag,
            framesPerSecond,
            durationSeconds);
        if (beats.Count < 8)
        {
            return Empty();
        }

        var support = BeatSupport(onset, beats, framesPerSecond, maxOnset);
        if (support < MinBeatSupport)
        {
            return Empty();
        }

        var bpm = framesPerSecond * 60d / best.Lag;
        bpm = Math.Round(bpm, 2, MidpointRounding.AwayFromZero);
        var downbeats = InferDownbeats(
            frames,
            beats,
            best.Score,
            support,
            framesPerSecond);

        return new RhythmAnalysisResult(
            bpm,
            Math.Clamp(Math.Min(best.Score, support), 0d, 1d),
            beats,
            downbeats,
            downbeats.Count >= MinBarsForDownbeat ? (byte)4 : null);
    }

    private static RhythmAnalysisResult Empty()
        => new(null, 0d, Array.Empty<double>(), Array.Empty<double>(), null);

    private static List<double> OnsetEnvelope(IReadOnlyList<RhythmEnergyFrame> frames)
    {
        var output = Enumerable.Repeat(0d, frames.Count).ToList();
        for (var index = 1; index < frames.Count; index++)
        {
            var current = frames[index];
            var previous = frames[index - 1];
            output[index] =
                PositiveLogDelta(current.Overall, previous.Overall) * 0.45d +
                PositiveLogDelta(current.Low, previous.Low) * 0.22d +
                PositiveLogDelta(current.LowMid, previous.LowMid) * 0.16d +
                PositiveLogDelta(current.Mid, previous.Mid) * 0.11d +
                PositiveLogDelta(current.High, previous.High) * 0.06d;
        }

        var sorted = output.OrderBy(value => value).ToArray();
        var floor = sorted.Length == 0 ? 0d : sorted[sorted.Length / 2];
        for (var index = 0; index < output.Count; index++)
        {
            output[index] = Math.Max(0d, output[index] - floor);
        }

        return output;
    }

    private static double PositiveLogDelta(float current, float previous)
        => Math.Max(
            0d,
            Math.Log1p(Math.Max(0d, current) * 1_000d) -
            Math.Log1p(Math.Max(0d, previous) * 1_000d));

    private static double NormalizedAutocorrelation(IReadOnlyList<double> values, int lag)
    {
        if (lag <= 0 || lag >= values.Count)
        {
            return 0d;
        }

        var dot = 0d;
        var leftEnergy = 0d;
        var rightEnergy = 0d;
        for (var index = lag; index < values.Count; index++)
        {
            var left = values[index];
            var right = values[index - lag];
            dot += left * right;
            leftEnergy += left * left;
            rightEnergy += right * right;
        }

        if (leftEnergy <= double.Epsilon || rightEnergy <= double.Epsilon)
        {
            return 0d;
        }

        return Math.Clamp(dot / (Math.Sqrt(leftEnergy) * Math.Sqrt(rightEnergy)), 0d, 1d);
    }

    private static bool IsNearHarmonic(int candidate, int basis)
    {
        if (candidate <= 0 || basis <= 0)
        {
            return false;
        }

        return IsNearMultiple(candidate, basis) || IsNearMultiple(basis, candidate);
    }

    private static bool IsNearMultiple(int value, int factor)
    {
        var nearest = (int)Math.Round(value / (double)factor) * factor;
        return Math.Abs(nearest - value) <= 1;
    }

    private static int BestPhase(IReadOnlyList<double> onset, int lag)
    {
        var bestPhase = 0;
        var bestScore = double.NegativeInfinity;
        for (var phase = 0; phase < lag; phase++)
        {
            var score = 0d;
            for (var index = phase; index < onset.Count; index += lag)
            {
                score += onset[index];
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
            }
        }

        return bestPhase;
    }

    private static List<double> GridTimes(
        int phase,
        int lag,
        int framesPerSecond,
        double durationSeconds)
    {
        var result = new List<double>();
        for (var frame = phase; ; frame += lag)
        {
            var seconds = frame / (double)framesPerSecond;
            if (seconds > durationSeconds)
            {
                break;
            }

            result.Add(seconds);
        }

        return result;
    }

    private static double BeatSupport(
        IReadOnlyList<double> onset,
        IReadOnlyList<double> beats,
        int framesPerSecond,
        double maxOnset)
    {
        if (beats.Count == 0 || maxOnset <= 0d)
        {
            return 0d;
        }

        var threshold = maxOnset * 0.18d;
        var supported = 0;
        foreach (var seconds in beats)
        {
            var center = (int)Math.Round(seconds * framesPerSecond);
            var found = false;
            for (var offset = -1; offset <= 1; offset++)
            {
                var index = center + offset;
                if (index >= 0 && index < onset.Count && onset[index] >= threshold)
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                supported++;
            }
        }

        return supported / (double)beats.Count;
    }

    private static List<double> InferDownbeats(
        IReadOnlyList<RhythmEnergyFrame> frames,
        IReadOnlyList<double> beats,
        double periodicity,
        double support,
        int framesPerSecond)
    {
        if (beats.Count < MinBarsForDownbeat * 4 ||
            periodicity < MinPeriodicity ||
            support < MinBeatSupport)
        {
            return [];
        }

        var lowOnset = LowFrequencyOnset(frames);
        var phaseScores = new double[4];
        var phaseCounts = new int[4];

        for (var beatIndex = 0; beatIndex < beats.Count; beatIndex++)
        {
            var frame = (int)Math.Round(beats[beatIndex] * framesPerSecond);
            var value = frame >= 0 && frame < lowOnset.Count ? lowOnset[frame] : 0d;
            var phase = beatIndex % 4;
            phaseScores[phase] += value;
            phaseCounts[phase]++;
        }

        for (var phase = 0; phase < 4; phase++)
        {
            if (phaseCounts[phase] > 0)
            {
                phaseScores[phase] /= phaseCounts[phase];
            }
        }

        var ranked = Enumerable.Range(0, 4)
            .Select(phase => (Phase: phase, Score: phaseScores[phase]))
            .OrderByDescending(item => item.Score)
            .ToArray();
        var best = ranked[0];
        var second = ranked.Length > 1 ? ranked[1].Score : 0d;
        if (best.Score <= 1e-6)
        {
            return [];
        }

        var margin = (best.Score - second) / Math.Max(best.Score, 1e-6);
        if (margin < MinDownbeatMargin)
        {
            return [];
        }

        return beats
            .Where((_, index) => index % 4 == best.Phase)
            .ToList();
    }

    private static List<double> LowFrequencyOnset(IReadOnlyList<RhythmEnergyFrame> frames)
    {
        var output = Enumerable.Repeat(0d, frames.Count).ToList();
        for (var index = 1; index < frames.Count; index++)
        {
            output[index] =
                PositiveLogDelta(frames[index].Low, frames[index - 1].Low) * 0.72d +
                PositiveLogDelta(frames[index].LowMid, frames[index - 1].LowMid) * 0.28d;
        }

        return output;
    }
}

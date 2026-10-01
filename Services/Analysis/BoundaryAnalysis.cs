using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal enum IntroBoundaryKind : byte
{
    Unknown = 0,
    HardEntry = 1,
    FadeIn = 2,
    GradualEntry = 3,
}

internal enum OutroBoundaryKind : byte
{
    Unknown = 0,
    HardEnd = 1,
    FadeOut = 2,
    SustainedEnd = 3,
}

internal readonly record struct IntroBoundary(
    IntroBoundaryKind Kind,
    double StartSeconds,
    double EndSeconds,
    float Confidence);

internal readonly record struct OutroBoundary(
    OutroBoundaryKind Kind,
    double StartSeconds,
    double EndSeconds,
    float Confidence);

internal readonly record struct QuickFadeCandidate(
    double StartSeconds,
    double EndSeconds,
    float Confidence);

internal sealed record TrackBoundaryAnalysis(
    double AudibleStartSeconds,
    double AudibleEndSeconds,
    IntroBoundary Intro,
    OutroBoundary Outro,
    QuickFadeCandidate? QuickFade,
    float NoiseFloorRms);

internal readonly record struct BoundaryEnergyFrame(
    float Overall,
    float Low,
    float LowMid,
    float Mid,
    float High);

internal sealed class BoundaryAccumulator
{
    public const int FramesPerSecond = 20;
    public const int EnvelopeBins = 1_800;

    private readonly int _sampleRate;
    private readonly AnalysisBandSplitter[] _splitters;
    private readonly List<BoundaryEnergyFrame> _frames = [];
    private long _sourceFrameIndex;
    private long _analysisFrameIndex;
    private EnergySums _current;
    private bool _frameHasData;

    public BoundaryAccumulator(int sampleRate, int channelCount)
    {
        _sampleRate = sampleRate;
        var lanes = Math.Clamp(channelCount, 1, 2);
        _splitters = new AnalysisBandSplitter[lanes];
        for (var lane = 0; lane < lanes; lane++)
        {
            _splitters[lane] = new AnalysisBandSplitter(AnalysisCrossover.Rhythm, sampleRate);
        }
    }

    public void PushFrame(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        while (_sourceFrameIndex >= Boundary(_analysisFrameIndex + 1))
        {
            Emit();
        }

        for (var lane = 0; lane < _splitters.Length; lane++)
        {
            var value = samples[Math.Min(lane, samples.Length - 1)];
            if (!float.IsFinite(value))
            {
                value = 0f;
            }

            var bands = _splitters[lane].Process(value);
            _current.Observe(value, bands.Bass, bands.Mid, bands.Presence, bands.Air);
        }

        _frameHasData = true;
        _sourceFrameIndex++;
    }

    public TrackBoundaryAnalysis? Complete()
    {
        if (_frameHasData)
        {
            Emit();
        }

        if (_sampleRate <= 0 || _sourceFrameIndex == 0 || _frames.Count == 0)
        {
            return null;
        }

        var durationSeconds = _sourceFrameIndex / (double)_sampleRate;
        var rms = Resample(frame => frame.Overall);
        var low = Resample(frame => frame.Low);
        var lowMid = Resample(frame => frame.LowMid);
        var mid = Resample(frame => frame.Mid);
        var high = Resample(frame => frame.High);
        return BoundaryClassifier.Derive(
            durationSeconds,
            rms,
            [low, lowMid, mid, high]);
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

    private float[] Resample(Func<BoundaryEnergyFrame, float> select)
    {
        var output = new float[EnvelopeBins];
        var sourceLength = _frames.Count;
        for (var target = 0; target < EnvelopeBins; target++)
        {
            var start = target * sourceLength / EnvelopeBins;
            var end = Math.Min(
                sourceLength,
                Math.Max(start + 1, (target + 1) * sourceLength / EnvelopeBins));
            start = Math.Min(start, sourceLength - 1);

            var sumSquares = 0d;
            var count = 0;
            for (var index = start; index < end; index++)
            {
                var value = select(_frames[index]);
                sumSquares += value * value;
                count++;
            }

            output[target] = count == 0
                ? 0f
                : (float)Math.Sqrt(sumSquares / count);
        }

        return output;
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

        public BoundaryEnergyFrame Complete()
        {
            if (_observations == 0)
            {
                return default;
            }

            var count = (double)_observations;
            return new BoundaryEnergyFrame(
                (float)Math.Sqrt(_overall / count),
                (float)Math.Sqrt(_low / count),
                (float)Math.Sqrt(_lowMid / count),
                (float)Math.Sqrt(_mid / count),
                (float)Math.Sqrt(_high / count));
        }
    }
}

internal static class BoundaryClassifier
{
    private const double MinActivityRms = 0.01d;
    private const double MinAbsoluteFloorRms = 0.02d;
    private const double FadeMinSeconds = 3d;
    private const double FadeMaxSeconds = 30d;
    private const double FadeAttenuationRatio = 4d;
    private const double FadeDistributedStepRatio = 0.92d;
    private const int FadeMinDistributedSteps = 3;
    private const double FadeOnsetLocalDropRatio = 0.98d;
    private const double BandAttenuationRatio = 2.5d;
    private const double QuickFadeMinSeconds = 0.5d;
    private const double QuickFadeMaxSeconds = 2.5d;
    private const double QuickFadeAttenuationRatio = 3d;
    private const double HardEndBodyRatio = 0.5d;
    private const double TailDecayRatio = 0.5d;
    private const double IntroBodyReachedRatio = 0.85d;
    private const double IntroQuietRatio = 0.5d;
    private const double IntroSearchSeconds = 12d;
    private const int IntroSmoothingBins = 3;
    private const int FadeInMinDistributedSteps = 3;

    public static TrackBoundaryAnalysis? Derive(
        double durationSeconds,
        IReadOnlyList<float> rms,
        IReadOnlyList<float[]>? bands = null)
    {
        if (!double.IsFinite(durationSeconds) ||
            durationSeconds <= 0d ||
            rms.Count == 0)
        {
            return null;
        }

        var sustained = rms
            .Where(value => value > MinActivityRms)
            .Select(value => (double)value)
            .ToArray();
        var bodyLevel = Median(sustained);
        var floor = bodyLevel.HasValue
            ? Math.Max(bodyLevel.Value * 0.05d, MinAbsoluteFloorRms)
            : MinAbsoluteFloorRms;

        var first = -1;
        var last = -1;
        for (var index = 0; index < rms.Count; index++)
        {
            if (rms[index] > floor)
            {
                first = index;
                break;
            }
        }

        for (var index = rms.Count - 1; index >= 0; index--)
        {
            if (rms[index] > floor)
            {
                last = index;
                break;
            }
        }

        if (first < 0 || last < first)
        {
            return null;
        }

        var binSeconds = durationSeconds / rms.Count;
        var audibleStart = first * binSeconds;
        var audibleEnd = Math.Min((last + 1) * binSeconds, durationSeconds);
        var intro = ClassifyIntro(
            rms,
            first,
            binSeconds,
            audibleStart,
            bodyLevel);
        var outro = ClassifyOutro(
            rms,
            bands,
            first,
            last,
            binSeconds,
            audibleEnd,
            durationSeconds,
            bodyLevel);
        var quickFade = outro.Kind == OutroBoundaryKind.FadeOut
            ? null
            : ClassifyQuickFade(
                rms,
                bands,
                first,
                last,
                binSeconds,
                audibleEnd);

        return new TrackBoundaryAnalysis(
            audibleStart,
            audibleEnd,
            intro,
            outro,
            quickFade,
            (float)floor);
    }

    private static IntroBoundary ClassifyIntro(
        IReadOnlyList<float> rms,
        int first,
        double binSeconds,
        double audibleStart,
        double? bodyLevel)
    {
        IntroBoundary Immediate(IntroBoundaryKind kindIfLate)
            => new(
                audibleStart <= 0.5d
                    ? IntroBoundaryKind.HardEntry
                    : kindIfLate,
                audibleStart,
                audibleStart,
                audibleStart <= 0.5d ? 0.8f : 0.65f);

        if (!bodyLevel.HasValue || bodyLevel.Value <= 0d)
        {
            return Immediate(IntroBoundaryKind.GradualEntry);
        }

        var limit = Math.Min(
            rms.Count,
            first + Math.Max(2, BinsForSeconds(IntroSearchSeconds, binSeconds)));
        var reach = -1;
        for (var index = first; index < limit; index++)
        {
            if (Mean(rms, index, Math.Min(rms.Count, index + IntroSmoothingBins)) >=
                bodyLevel.Value * IntroBodyReachedRatio)
            {
                reach = index;
                break;
            }
        }

        if (reach < 0 ||
            (reach - first) * binSeconds <= 0.5d)
        {
            return Immediate(IntroBoundaryKind.GradualEntry);
        }

        if (Mean(rms, first, reach) > bodyLevel.Value * IntroQuietRatio)
        {
            return Immediate(IntroBoundaryKind.GradualEntry);
        }

        var endSeconds = reach * binSeconds;
        var window = Math.Max(1, (reach - first) / 5);
        if (DistributedRisingSteps(rms, first, reach, window) >= FadeInMinDistributedSteps)
        {
            var plateau = reach;
            while (plateau + 1 < limit &&
                Smoothed(rms, plateau + 1) > Smoothed(rms, plateau) * 1.005d)
            {
                plateau++;
            }

            return new IntroBoundary(
                IntroBoundaryKind.FadeIn,
                audibleStart,
                (plateau + IntroSmoothingBins / 2) * binSeconds,
                0.8f);
        }

        return new IntroBoundary(
            IntroBoundaryKind.GradualEntry,
            audibleStart,
            endSeconds,
            0.7f);
    }

    private static OutroBoundary ClassifyOutro(
        IReadOnlyList<float> rms,
        IReadOnlyList<float[]>? bands,
        int first,
        int last,
        double binSeconds,
        double audibleEnd,
        double durationSeconds,
        double? bodyLevel)
    {
        var minBins = Math.Max(2, BinsForSeconds(FadeMinSeconds, binSeconds));
        var maxBins = Math.Max(minBins, BinsForSeconds(FadeMaxSeconds, binSeconds));
        var earliest = Math.Max(first, Math.Max(0, last - maxBins));
        var latest = Math.Max(0, last - minBins);

        if (earliest <= latest)
        {
            for (var start = earliest; start <= latest; start++)
            {
                var sliceLength = last - start + 1;
                var window = Math.Clamp(
                    BinsForSeconds(1d, binSeconds),
                    1,
                    sliceLength);
                var startLevel = Mean(rms, start, start + window);
                var endLevel = Mean(rms, last + 1 - window, last + 1);
                var falling = MonotonicFraction(rms, start, last + 1, false);
                var distributedSteps =
                    DistributedAttenuationSteps(rms, start, last + 1, window);
                if (endLevel <= 0d ||
                    startLevel / endLevel < FadeAttenuationRatio ||
                    falling < 0.70d ||
                    distributedSteps < FadeMinDistributedSteps)
                {
                    continue;
                }

                var bandScore = BandScore(
                    bands,
                    start,
                    last,
                    window,
                    BandAttenuationRatio);
                if (bands is not null && bandScore < 3)
                {
                    continue;
                }

                var confidence = bands is null
                    ? Math.Min(0.78d, 0.60d + 0.2d * falling)
                    : Math.Min(0.98d, 0.72d + 0.06d * bandScore);
                var refinedStart =
                    RefineFadeOnset(rms, start, last, binSeconds);
                return new OutroBoundary(
                    OutroBoundaryKind.FadeOut,
                    refinedStart * binSeconds,
                    audibleEnd,
                    (float)confidence);
            }
        }

        var trailingSilence = Math.Max(0d, durationSeconds - audibleEnd);
        var tailWindow = Math.Max(1, BinsForSeconds(1d, binSeconds));
        var endStart = Math.Max(0, last + 1 - tailWindow);
        var endLevelFinal = Mean(rms, endStart, last + 1);
        var previousStart = Math.Max(0, last + 1 - 2 * tailWindow);
        var previousEnd = Math.Max(
            previousStart + 1,
            last + 1 - tailWindow);
        previousEnd = Math.Min(previousEnd, last + 1);
        var previousLevel = Mean(rms, previousStart, previousEnd);
        var decaying =
            previousLevel > 0d &&
            endLevelFinal < previousLevel * TailDecayRatio;
        var atBodyLevel =
            bodyLevel.HasValue &&
            bodyLevel.Value > 0d &&
            endLevelFinal >= bodyLevel.Value * HardEndBodyRatio;

        var kind = trailingSilence >= 0.25d
            ? OutroBoundaryKind.HardEnd
            : decaying
                ? OutroBoundaryKind.Unknown
                : atBodyLevel
                    ? OutroBoundaryKind.HardEnd
                    : OutroBoundaryKind.SustainedEnd;
        return new OutroBoundary(
            kind,
            audibleEnd,
            audibleEnd,
            trailingSilence >= 0.25d ? 0.8f : 0.6f);
    }

    private static QuickFadeCandidate? ClassifyQuickFade(
        IReadOnlyList<float> rms,
        IReadOnlyList<float[]>? bands,
        int first,
        int last,
        double binSeconds,
        double audibleEnd)
    {
        var minBins = Math.Max(
            2,
            BinsForSeconds(QuickFadeMinSeconds, binSeconds));
        var maxBins = Math.Max(
            minBins,
            BinsForSeconds(QuickFadeMaxSeconds, binSeconds));
        var earliest = Math.Max(
            first,
            Math.Max(0, last - Math.Max(0, maxBins - 1)));
        var latest = Math.Max(
            0,
            last - Math.Max(0, minBins - 1));

        if (earliest > latest)
        {
            return null;
        }

        for (var start = latest; start >= earliest; start--)
        {
            var sliceLength = last - start + 1;
            var window = Math.Clamp(
                BinsForSeconds(0.5d, binSeconds),
                1,
                sliceLength);
            var startLevel = Mean(rms, start, start + window);
            var endLevel = Mean(rms, last + 1 - window, last + 1);
            var falling = MonotonicFraction(rms, start, last + 1, false);
            if (endLevel <= 0d ||
                startLevel / endLevel < QuickFadeAttenuationRatio ||
                falling < 0.70d)
            {
                continue;
            }

            var bandScore = BandScore(
                bands,
                start,
                last,
                window,
                QuickFadeAttenuationRatio);
            if (bands is not null && bandScore < 3)
            {
                continue;
            }

            var confidence = bands is null
                ? Math.Min(0.80d, 0.58d + 0.25d * falling)
                : Math.Min(0.98d, 0.70d + 0.07d * bandScore);
            return new QuickFadeCandidate(
                start * binSeconds,
                audibleEnd,
                (float)confidence);
        }

        return null;
    }

    private static int BandScore(
        IReadOnlyList<float[]>? bands,
        int start,
        int last,
        int window,
        double attenuationRatio)
    {
        if (bands is null)
        {
            return 0;
        }

        var score = 0;
        foreach (var values in bands)
        {
            if (values.Length == 0)
            {
                continue;
            }

            var startEnd = Math.Min(values.Length, start + window);
            var bandStart = Mean(values, Math.Min(start, values.Length), startEnd);
            var endStart = Math.Max(0, last + 1 - window);
            var bandEnd = Mean(
                values,
                Math.Min(endStart, values.Length),
                Math.Min(last + 1, values.Length));
            if (bandEnd > 0d &&
                bandStart / bandEnd >= attenuationRatio)
            {
                score++;
            }
        }

        return score;
    }

    private static int RefineFadeOnset(
        IReadOnlyList<float> rms,
        int candidateStart,
        int last,
        double binSeconds)
    {
        var window = Math.Max(1, BinsForSeconds(1d, binSeconds));
        var latest = Math.Max(0, last - 2 * window);
        if (candidateStart >= latest)
        {
            return candidateStart;
        }

        for (var start = candidateStart; start <= latest; start++)
        {
            var beforeStart = Math.Max(0, start - window);
            if (beforeStart >= start)
            {
                continue;
            }

            var before = Mean(rms, beforeStart, start);
            var onsetEnd = start + window;
            var sustainEnd = Math.Min(start + 2 * window, last + 1);
            if (onsetEnd >= sustainEnd)
            {
                break;
            }

            var onset = Mean(rms, start, onsetEnd);
            var sustain = Mean(rms, onsetEnd, sustainEnd);
            var localFalling =
                MonotonicFraction(rms, start, sustainEnd, false);
            if (before > 0d &&
                onset <= before * FadeOnsetLocalDropRatio &&
                sustain <= onset &&
                localFalling >= 0.70d)
            {
                return start;
            }
        }

        return candidateStart;
    }

    private static int DistributedRisingSteps(
        IReadOnlyList<float> values,
        int start,
        int end,
        int window)
    {
        var reversed = new float[Math.Max(0, end - start)];
        for (var index = 0; index < reversed.Length; index++)
        {
            reversed[index] = values[end - 1 - index];
        }

        return DistributedAttenuationSteps(
            reversed,
            0,
            reversed.Length,
            window);
    }

    private static int DistributedAttenuationSteps(
        IReadOnlyList<float> values,
        int start,
        int end,
        int window)
    {
        var length = end - start;
        if (length < 2 || window <= 0)
        {
            return 0;
        }

        var maxStart = Math.Max(0, length - window);
        var starts = new[]
        {
            0,
            maxStart / 4,
            maxStart / 2,
            maxStart * 3 / 4,
            maxStart,
        };
        var levels = starts
            .Select(offset => Mean(
                values,
                start + offset,
                Math.Min(end, start + offset + window)))
            .ToArray();

        var matching = 0;
        for (var index = 0; index + 1 < levels.Length; index++)
        {
            if (levels[index + 1] <=
                levels[index] * FadeDistributedStepRatio)
            {
                matching++;
            }
        }

        return matching;
    }

    private static double Smoothed(
        IReadOnlyList<float> values,
        int index)
        => Mean(
            values,
            index,
            Math.Min(values.Count, index + IntroSmoothingBins));

    private static int BinsForSeconds(
        double seconds,
        double binSeconds)
    {
        if (binSeconds <= 0d)
        {
            return 0;
        }

        return Math.Max(
            1,
            (int)Math.Round(
                seconds / binSeconds,
                MidpointRounding.AwayFromZero));
    }

    private static double Mean(
        IReadOnlyList<float> values,
        int start,
        int end)
    {
        start = Math.Clamp(start, 0, values.Count);
        end = Math.Clamp(end, start, values.Count);
        if (start >= end)
        {
            return 0d;
        }

        var sum = 0d;
        for (var index = start; index < end; index++)
        {
            sum += values[index];
        }

        return sum / (end - start);
    }

    private static double MonotonicFraction(
        IReadOnlyList<float> values,
        int start,
        int end,
        bool rising)
    {
        if (end - start < 2)
        {
            return 0d;
        }

        var matching = 0;
        for (var index = start; index + 1 < end; index++)
        {
            if (rising
                ? values[index + 1] >= values[index] * 0.92f
                : values[index + 1] <= values[index] * 1.08f)
            {
                matching++;
            }
        }

        return matching / (double)(end - start - 1);
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2d
            : sorted[middle];
    }
}

internal static class BoundaryArtifactEncoder
{
    public const ushort ExtensionVersion = 2;
    public const ushort ExtensionLength = 104;
    public const ushort AnalyzerVersion = 6;
    public const string AnalyzerConfigurationDigest =
        "sha256:5863dda9f9cd436b89d373b09124810be5ae31e15a85fa544fdda994ed285327";

    public static byte[] Encode(
        SpectralWaveformData waveform,
        TrackBoundaryAnalysis? boundaries)
    {
        if (boundaries is null ||
            !IsValid(boundaries, waveform.SourceFrameCount / (double)waveform.SampleRate))
        {
            return [];
        }

        var value = boundaries;
        var bytes = new byte[ExtensionLength];
        var span = bytes.AsSpan();
        Encoding.ASCII.GetBytes("SBND").CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], ExtensionVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], ExtensionLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..12], AnalyzerVersion);
        BinaryPrimitives.WriteInt64LittleEndian(
            span[12..20],
            BitConverter.DoubleToInt64Bits(value.AudibleStartSeconds));
        BinaryPrimitives.WriteInt64LittleEndian(
            span[20..28],
            BitConverter.DoubleToInt64Bits(value.AudibleEndSeconds));
        BinaryPrimitives.WriteInt32LittleEndian(
            span[28..32],
            BitConverter.SingleToInt32Bits(value.NoiseFloorRms));
        span[32] = (byte)value.Intro.Kind;
        span[33] = (byte)value.Outro.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], 0);
        BinaryPrimitives.WriteInt64LittleEndian(
            span[36..44],
            BitConverter.DoubleToInt64Bits(value.Intro.StartSeconds));
        BinaryPrimitives.WriteInt64LittleEndian(
            span[44..52],
            BitConverter.DoubleToInt64Bits(value.Intro.EndSeconds));
        BinaryPrimitives.WriteInt32LittleEndian(
            span[52..56],
            BitConverter.SingleToInt32Bits(value.Intro.Confidence));
        BinaryPrimitives.WriteInt64LittleEndian(
            span[56..64],
            BitConverter.DoubleToInt64Bits(value.Outro.StartSeconds));
        BinaryPrimitives.WriteInt64LittleEndian(
            span[64..72],
            BitConverter.DoubleToInt64Bits(value.Outro.EndSeconds));
        BinaryPrimitives.WriteInt32LittleEndian(
            span[72..76],
            BitConverter.SingleToInt32Bits(value.Outro.Confidence));
        span[76] = value.QuickFade.HasValue ? (byte)1 : (byte)0;
        span[77] = 0;
        span[78] = 0;
        span[79] = 0;

        if (value.QuickFade is { } quickFade)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                span[80..88],
                BitConverter.DoubleToInt64Bits(quickFade.StartSeconds));
            BinaryPrimitives.WriteInt64LittleEndian(
                span[88..96],
                BitConverter.DoubleToInt64Bits(quickFade.EndSeconds));
            BinaryPrimitives.WriteInt32LittleEndian(
                span[96..100],
                BitConverter.SingleToInt32Bits(quickFade.Confidence));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            span[100..104],
            SpectralArtifactEncoder.Crc32(span[..100]));
        return bytes;
    }

    private static bool IsValid(
        TrackBoundaryAnalysis boundaries,
        double durationSeconds)
        => double.IsFinite(durationSeconds) &&
            durationSeconds >= 0d &&
            double.IsFinite(boundaries.AudibleStartSeconds) &&
            double.IsFinite(boundaries.AudibleEndSeconds) &&
            boundaries.AudibleStartSeconds >= 0d &&
            boundaries.AudibleStartSeconds <= boundaries.AudibleEndSeconds &&
            boundaries.AudibleEndSeconds <= durationSeconds + 0.001d &&
            double.IsFinite(boundaries.Intro.StartSeconds) &&
            double.IsFinite(boundaries.Intro.EndSeconds) &&
            double.IsFinite(boundaries.Outro.StartSeconds) &&
            double.IsFinite(boundaries.Outro.EndSeconds) &&
            boundaries.Intro.Confidence is >= 0f and <= 1f &&
            boundaries.Outro.Confidence is >= 0f and <= 1f &&
            float.IsFinite(boundaries.NoiseFloorRms) &&
            boundaries.NoiseFloorRms >= 0f &&
            (!boundaries.QuickFade.HasValue ||
                (double.IsFinite(boundaries.QuickFade.Value.StartSeconds) &&
                 double.IsFinite(boundaries.QuickFade.Value.EndSeconds) &&
                 boundaries.QuickFade.Value.StartSeconds >= boundaries.AudibleStartSeconds &&
                 boundaries.QuickFade.Value.StartSeconds <= boundaries.QuickFade.Value.EndSeconds &&
                 boundaries.QuickFade.Value.EndSeconds <= boundaries.AudibleEndSeconds + 0.001d &&
                 boundaries.QuickFade.Value.Confidence is >= 0f and <= 1f));
}

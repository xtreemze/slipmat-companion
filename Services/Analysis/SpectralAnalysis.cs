using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal readonly record struct SpectralPeakFrame(
    byte Overall,
    byte Sub,
    byte Bass,
    byte Mid,
    byte Presence,
    byte Air)
{
    public SpectralPeakFrame Max(SpectralPeakFrame other)
        => new(
            Math.Max(Overall, other.Overall),
            Math.Max(Sub, other.Sub),
            Math.Max(Bass, other.Bass),
            Math.Max(Mid, other.Mid),
            Math.Max(Presence, other.Presence),
            Math.Max(Air, other.Air));
}

internal sealed record SpectralWaveformData(
    int SampleRate,
    int FramesPerSecond,
    long SourceFrameCount,
    IReadOnlyList<SpectralPeakFrame> Frames);

internal sealed class SpectralAccumulator
{
    public const int DetailedFramesPerSecond = 100;

    private readonly int _sampleRate;
    private readonly AnalysisBandSplitter _splitter;
    private readonly List<SpectralPeakFrame> _frames = [];
    private long _sourceFrameIndex;
    private long _visualFrameIndex;
    private bool _frameHasData;
    private PeakAccumulator _current;

    public SpectralAccumulator(int sampleRate)
    {
        _sampleRate = sampleRate;
        _splitter = new AnalysisBandSplitter(AnalysisCrossover.Spectral, sampleRate);
    }

    public void Push(float sample)
    {
        while (_sourceFrameIndex >= Boundary(_visualFrameIndex + 1))
        {
            Emit();
        }

        var value = float.IsFinite(sample) ? sample : 0f;
        var bands = _splitter.Process(value);
        _current.Observe(
            value,
            bands.Sub,
            bands.Bass,
            bands.Mid,
            bands.Presence,
            bands.Air);
        _frameHasData = true;
        _sourceFrameIndex++;
    }

    public SpectralWaveformData Complete()
    {
        if (_frameHasData)
        {
            Emit();
        }

        return new SpectralWaveformData(
            _sampleRate,
            DetailedFramesPerSecond,
            _sourceFrameIndex,
            _frames);
    }

    private long Boundary(long index)
        => index * _sampleRate / DetailedFramesPerSecond;

    private void Emit()
    {
        _frames.Add(_current.Complete());
        _current = default;
        _visualFrameIndex++;
        _frameHasData = false;
    }

    private struct PeakAccumulator
    {
        private double _overall;
        private double _sub;
        private double _bass;
        private double _mid;
        private double _presence;
        private double _air;

        public void Observe(
            double overall,
            double sub,
            double bass,
            double mid,
            double presence,
            double air)
        {
            _overall = Math.Max(_overall, Math.Abs(overall));
            _sub = Math.Max(_sub, Math.Abs(sub));
            _bass = Math.Max(_bass, Math.Abs(bass));
            _mid = Math.Max(_mid, Math.Abs(mid));
            _presence = Math.Max(_presence, Math.Abs(presence));
            _air = Math.Max(_air, Math.Abs(air));
        }

        public SpectralPeakFrame Complete()
            => new(
                Quantize(_overall),
                Quantize(_sub),
                Quantize(_bass),
                Quantize(_mid),
                Quantize(_presence),
                Quantize(_air));

        private static byte Quantize(double value)
        {
            if (!double.IsFinite(value))
            {
                return 0;
            }

            return (byte)Math.Clamp(
                (int)Math.Round(Math.Clamp(value, 0d, 1d) * 255d, MidpointRounding.AwayFromZero),
                0,
                255);
        }
    }
}

internal static class SpectralArtifactEncoder
{
    public const string Variant = "slws_v2_stereo_u8_max_5band";
    public const ushort FormatVersion = 2;
    public static readonly int[] Tiers = [1, 10, 100];

    private const ushort BaseHeaderLength = 52;
    private const byte MonoLayout = 1;
    private const byte LinearU8Quantization = 0;
    private const byte MaxAggregation = 0;
    private const byte BandCount = 5;

    public static IReadOnlyDictionary<int, byte[]> EncodeTiers(
        SpectralWaveformData detailed,
        RhythmAnalysisResult rhythm)
    {
        var result = new Dictionary<int, byte[]>(Tiers.Length);
        foreach (var fps in Tiers)
        {
            var frames = fps == detailed.FramesPerSecond
                ? detailed.Frames
                : Downsample(detailed.Frames, detailed.FramesPerSecond, fps);
            result.Add(
                fps,
                Encode(
                    new SpectralWaveformData(
                        detailed.SampleRate,
                        fps,
                        detailed.SourceFrameCount,
                        frames),
                    rhythm));
        }

        return result;
    }

    private static IReadOnlyList<SpectralPeakFrame> Downsample(
        IReadOnlyList<SpectralPeakFrame> source,
        int sourceFps,
        int targetFps)
    {
        if (targetFps <= 0 || sourceFps % targetFps != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps));
        }

        var factor = sourceFps / targetFps;
        var output = new List<SpectralPeakFrame>((source.Count + factor - 1) / factor);
        for (var start = 0; start < source.Count; start += factor)
        {
            var frame = source[start];
            var end = Math.Min(source.Count, start + factor);
            for (var index = start + 1; index < end; index++)
            {
                frame = frame.Max(source[index]);
            }

            output.Add(frame);
        }

        return output;
    }

    private static byte[] Encode(
        SpectralWaveformData waveform,
        RhythmAnalysisResult rhythm)
    {
        var payload = new byte[waveform.Frames.Count * 6];
        for (var index = 0; index < waveform.Frames.Count; index++)
        {
            var frame = waveform.Frames[index];
            var offset = index * 6;
            payload[offset] = frame.Overall;
            payload[offset + 1] = frame.Sub;
            payload[offset + 2] = frame.Bass;
            payload[offset + 3] = frame.Mid;
            payload[offset + 4] = frame.Presence;
            payload[offset + 5] = frame.Air;
        }

        var rhythmExtension = EncodeRhythmExtension(waveform, rhythm);
        var headerLength = checked((ushort)(BaseHeaderLength + rhythmExtension.Length));
        var output = new byte[headerLength + payload.Length];
        var span = output.AsSpan();

        Encoding.ASCII.GetBytes("SLWS").CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], headerLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], checked((uint)waveform.SampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], checked((uint)waveform.FramesPerSecond));
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], checked((uint)waveform.Frames.Count));
        span[20] = MonoLayout;
        span[21] = LinearU8Quantization;
        span[22] = MaxAggregation;
        span[23] = BandCount;
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], 80);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], 250);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..36], 2_000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..40], 8_000);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..48], checked((ulong)waveform.SourceFrameCount));
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..52], Crc32(payload));

        rhythmExtension.CopyTo(span[BaseHeaderLength..headerLength]);
        payload.CopyTo(span[headerLength..]);
        return output;
    }

    private static byte[] EncodeRhythmExtension(
        SpectralWaveformData waveform,
        RhythmAnalysisResult rhythm)
    {
        if (!rhythm.HasGrid ||
            rhythm.Bpm is null ||
            rhythm.Bpm < 30d ||
            rhythm.Bpm > 300d ||
            rhythm.Beats.Count > ushort.MaxValue ||
            rhythm.Downbeats.Count > ushort.MaxValue)
        {
            return [];
        }

        var durationSeconds = waveform.SourceFrameCount / (double)waveform.SampleRate;
        if (!ValidTimes(rhythm.Beats, durationSeconds) ||
            !ValidTimes(rhythm.Downbeats, durationSeconds))
        {
            return [];
        }

        var hasDownbeats = rhythm.Downbeats.Count > 0 && rhythm.BeatsPerBar.HasValue;
        var length = checked(
            24 +
            rhythm.Beats.Count * sizeof(float) +
            (hasDownbeats ? rhythm.Downbeats.Count * sizeof(float) : 0) +
            4);
        if (length > ushort.MaxValue)
        {
            return [];
        }

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        Encoding.ASCII.GetBytes("SRHY").CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8], checked((ushort)length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..10], checked((ushort)(hasDownbeats ? 3 : 1)));
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..12], 1);
        BinaryPrimitives.WriteInt32LittleEndian(
            span[12..16],
            BitConverter.SingleToInt32Bits((float)rhythm.Bpm.Value));
        span[16] = hasDownbeats ? rhythm.BeatsPerBar!.Value : (byte)0;
        span[17] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..20], checked((ushort)rhythm.Beats.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(
            span[20..22],
            checked((ushort)(hasDownbeats ? rhythm.Downbeats.Count : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], 0);

        var offset = 24;
        foreach (var seconds in rhythm.Beats)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                span[offset..(offset + 4)],
                BitConverter.SingleToInt32Bits((float)seconds));
            offset += 4;
        }

        if (hasDownbeats)
        {
            foreach (var seconds in rhythm.Downbeats)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    span[offset..(offset + 4)],
                    BitConverter.SingleToInt32Bits((float)seconds));
                offset += 4;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..(offset + 4)], Crc32(span[..offset]));
        return bytes;
    }

    private static bool ValidTimes(IReadOnlyList<double> times, double durationSeconds)
    {
        var previous = -1d;
        foreach (var value in times)
        {
            if (!double.IsFinite(value) ||
                value < 0d ||
                value > durationSeconds + 0.001d ||
                value <= previous)
            {
                return false;
            }

            previous = value;
        }

        return true;
    }

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = (uint)-(int)(crc & 1);
                crc = (crc >> 1) ^ (0xEDB8_8320u & mask);
            }
        }

        return ~crc;
    }
}

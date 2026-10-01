using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Streaming max-peak envelope generator used by the integrated analyzer.
/// Integer-derived bucket boundaries keep output stable at source rates that do
/// not divide evenly by the requested points-per-second tier.
/// </summary>
public sealed class AmplitudeEnvelopeBuilder
{
    public static readonly int[] Tiers = [1, 10, 100];

    private readonly int _sampleRate;
    private readonly Dictionary<int, TierAccumulator> _tiers;

    public AmplitudeEnvelopeBuilder(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _sampleRate = sampleRate;
        _tiers = [];
        foreach (var pps in Tiers)
        {
            _tiers.Add(pps, new TierAccumulator(sampleRate, pps));
        }
    }

    public long SampleCount { get; private set; }

    public long DurationMs => SampleCount * 1000L / _sampleRate;

    public void Push(float sample)
    {
        if (!float.IsFinite(sample))
        {
            sample = 0;
        }

        var magnitude = Math.Clamp(Math.Abs(sample), 0f, 1f);
        foreach (var tier in _tiers.Values)
        {
            tier.Push(magnitude);
        }

        SampleCount++;
    }

    public IReadOnlyDictionary<int, byte[]> Complete()
    {
        var result = new Dictionary<int, byte[]>(_tiers.Count);
        foreach (var (pps, tier) in _tiers)
        {
            result.Add(pps, tier.Complete());
        }

        return result;
    }

    public static byte[] EncodeRiff(int pointsPerSecond, ReadOnlySpan<byte> peaks)
    {
        if (pointsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointsPerSecond));
        }

        const int junkPayloadLength = 8;
        const int dataHeaderOffset = 52;
        const int dataPayloadOffset = 60;

        var dataLength = checked((uint)peaks.Length);
        var padding = dataLength % 2;
        var totalLength = checked(dataPayloadOffset + peaks.Length + (int)padding);
        var riffSize = checked((uint)(totalLength - 8));
        var bytes = new byte[totalLength];
        var span = bytes.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], riffSize);
        "WAVE"u8.CopyTo(span[8..12]);
        "fmt "u8.CopyTo(span[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], checked((uint)pointsPerSecond));
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], checked((uint)pointsPerSecond));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..34], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], 8);

        "JUNK"u8.CopyTo(span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], junkPayloadLength);

        "data"u8.CopyTo(span[dataHeaderOffset..(dataHeaderOffset + 4)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            span[(dataHeaderOffset + 4)..dataPayloadOffset],
            dataLength);
        peaks.CopyTo(span[dataPayloadOffset..]);

        return bytes;
    }

    private sealed class TierAccumulator
    {
        private readonly int _sampleRate;
        private readonly int _pointsPerSecond;
        private readonly List<byte> _peaks = [];
        private long _sampleIndex;
        private long _bucketIndex;
        private float _peak;
        private bool _bucketHasData;

        public TierAccumulator(int sampleRate, int pointsPerSecond)
        {
            _sampleRate = sampleRate;
            _pointsPerSecond = pointsPerSecond;
        }

        public void Push(float magnitude)
        {
            while (_sampleIndex >= Boundary(_bucketIndex + 1))
            {
                Flush();
            }

            _peak = Math.Max(_peak, magnitude);
            _bucketHasData = true;
            _sampleIndex++;
        }

        public byte[] Complete()
        {
            if (_bucketHasData)
            {
                Flush();
            }

            return _peaks.ToArray();
        }

        private long Boundary(long index)
            => index * _sampleRate / _pointsPerSecond;

        private void Flush()
        {
            _peaks.Add((byte)Math.Clamp(
                (int)Math.Round(_peak * byte.MaxValue, MidpointRounding.AwayFromZero),
                byte.MinValue,
                byte.MaxValue));
            _bucketIndex++;
            _peak = 0f;
            _bucketHasData = false;
        }
    }
}

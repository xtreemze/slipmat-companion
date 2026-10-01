using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Streaming max-peak envelope generator used by the integrated analyzer.
/// It stores only one byte per visual bucket and never retains decoded PCM.
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
            if (sampleRate % pps != 0)
            {
                throw new ArgumentException(
                    $"Sample rate {sampleRate} must be divisible by tier {pps}.",
                    nameof(sampleRate));
            }

            _tiers.Add(pps, new TierAccumulator(sampleRate / pps));
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

        var dataLength = checked((uint)peaks.Length);
        var padding = dataLength % 2;
        var riffSize = checked(36u + dataLength + padding);
        var bytes = new byte[checked(44 + peaks.Length + (int)padding)];
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
        "data"u8.CopyTo(span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], dataLength);
        peaks.CopyTo(span[44..]);

        return bytes;
    }

    private sealed class TierAccumulator
    {
        private readonly int _samplesPerBucket;
        private readonly List<byte> _peaks = [];
        private int _inBucket;
        private float _peak;

        public TierAccumulator(int samplesPerBucket)
        {
            _samplesPerBucket = samplesPerBucket;
        }

        public void Push(float magnitude)
        {
            _peak = Math.Max(_peak, magnitude);
            _inBucket++;
            if (_inBucket == _samplesPerBucket)
            {
                Flush();
            }
        }

        public byte[] Complete()
        {
            if (_inBucket > 0)
            {
                Flush();
            }

            return _peaks.ToArray();
        }

        private void Flush()
        {
            _peaks.Add((byte)Math.Clamp(
                (int)Math.Round(_peak * byte.MaxValue, MidpointRounding.AwayFromZero),
                byte.MinValue,
                byte.MaxValue));
            _inBucket = 0;
            _peak = 0;
        }
    }
}

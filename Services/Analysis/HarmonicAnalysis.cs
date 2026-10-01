using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal sealed record HarmonicAnalysisResult(
    string Key,
    string CamelotKey,
    double Confidence);

/// <summary>
/// Streaming projection of Slipmat's Rust/WASM key detector.
///
/// Preserves channel-independent analyzer details: 4096-sample frames advance
/// by 2048 samples, the final frame is analyzed only when at least one later
/// sample exists, the Hann denominator is exactly FrameSize, and FFT/chroma/
/// profile scoring use single precision.
/// </summary>
internal sealed class HarmonicAccumulator
{
    internal const int FrameSize = 4096;
    internal const int HopSize = 2048;

    private static readonly float[] MajorProfile =
    [
        6.35f, 2.23f, 3.48f, 2.33f, 4.38f, 4.09f,
        2.52f, 5.19f, 2.39f, 3.66f, 2.29f, 2.88f,
    ];

    private static readonly float[] MinorProfile =
    [
        6.33f, 2.68f, 3.52f, 5.38f, 2.60f, 3.53f,
        2.54f, 4.75f, 3.98f, 2.69f, 3.34f, 3.17f,
    ];

    private static readonly string[] KeyNames =
    [
        "C", "C#", "D", "D#", "E", "F",
        "F#", "G", "G#", "A", "A#", "B",
    ];

    private readonly int _sampleRate;
    private readonly List<float> _buffer = new(FrameSize + 1);
    private readonly float[] _chroma = new float[12];
    private int _framesAnalyzed;

    public HarmonicAccumulator(int sampleRate)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _sampleRate = sampleRate;
    }

    public void Push(float sample)
    {
        _buffer.Add(float.IsFinite(sample) ? sample : 0f);

        // Rust iterates 0..samples.len().saturating_sub(FrameSize), excluding
        // a frame whose last sample is also the final source sample.
        if (_buffer.Count <= FrameSize)
        {
            return;
        }

        AnalyzeFrame(_buffer);
        _buffer.RemoveRange(0, HopSize);
    }

    public HarmonicAnalysisResult Complete()
    {
        if (_framesAnalyzed == 0)
        {
            return Unknown();
        }

        var chromaMax = 0f;
        foreach (var value in _chroma)
        {
            chromaMax = MathF.Max(chromaMax, value);
        }

        if (!float.IsFinite(chromaMax) || chromaMax <= 0f)
        {
            return Unknown();
        }

        var bestMajorRoot = 0;
        var bestMajorScore = -1f;
        var bestMinorRoot = 0;
        var bestMinorScore = -1f;

        for (var root = 0; root < 12; root++)
        {
            var major = 0f;
            var minor = 0f;
            for (var index = 0; index < 12; index++)
            {
                var chromaIndex = (index + root) % 12;
                major += _chroma[chromaIndex] * MajorProfile[index];
                minor += _chroma[chromaIndex] * MinorProfile[index];
            }

            if (major > bestMajorScore)
            {
                bestMajorScore = major;
                bestMajorRoot = root;
            }

            if (minor > bestMinorScore)
            {
                bestMinorScore = minor;
                bestMinorRoot = root;
            }
        }

        var majorConfidence = bestMajorScore / chromaMax / Sum(MajorProfile);
        var minorConfidence = bestMinorScore / chromaMax / Sum(MinorProfile);
        var isMajor = majorConfidence > minorConfidence;
        var rootIndex = isMajor ? bestMajorRoot : bestMinorRoot;
        var confidence = MathF.Min(
            isMajor ? majorConfidence : minorConfidence,
            1f);

        if (!float.IsFinite(confidence))
        {
            return Unknown();
        }

        var key = $"{KeyNames[rootIndex]} {(isMajor ? "Major" : "Minor")}";
        return new HarmonicAnalysisResult(
            key,
            ToCamelot(rootIndex, isMajor),
            confidence);
    }

    private void AnalyzeFrame(IReadOnlyList<float> samples)
    {
        var real = new float[FrameSize];
        var imag = new float[FrameSize];

        for (var index = 0; index < FrameSize; index++)
        {
            var window =
                0.5f *
                (1f - MathF.Cos(2f * MathF.PI * index / FrameSize));
            real[index] = samples[index] * window;
        }

        Fft(real, imag);

        for (var bin = 0; bin < FrameSize / 2; bin++)
        {
            var frequency = bin * _sampleRate / (float)FrameSize;
            if (frequency < 40f || frequency > 5_000f)
            {
                continue;
            }

            var magnitude = MathF.Sqrt(
                real[bin] * real[bin] +
                imag[bin] * imag[bin]);
            var note = (12f * MathF.Log2(frequency / 440f) + 69f) % 12f;
            var chromaIndex =
                (int)MathF.Round(note, MidpointRounding.AwayFromZero) % 12;
            if (chromaIndex < 0)
            {
                chromaIndex += 12;
            }

            _chroma[chromaIndex] += magnitude;
        }

        _framesAnalyzed++;
    }

    private static void Fft(float[] real, float[] imag)
    {
        var n = real.Length;
        var reversed = 0;

        for (var index = 1; index < n; index++)
        {
            var bit = n >> 1;
            while ((reversed & bit) != 0)
            {
                reversed ^= bit;
                bit >>= 1;
            }

            reversed ^= bit;
            if (index < reversed)
            {
                (real[index], real[reversed]) = (real[reversed], real[index]);
            }
        }

        for (var width = 2; ; width *= 2)
        {
            var half = width / 2;
            var angleStep = -2f * MathF.PI / width;
            var stepCos = MathF.Cos(angleStep);
            var stepSin = MathF.Sin(angleStep);

            for (var start = 0; start < n; start += width)
            {
                var twiddleReal = 1f;
                var twiddleImag = 0f;

                for (var offset = 0; offset < half; offset++)
                {
                    var even = start + offset;
                    var odd = even + half;
                    var oddReal = real[odd];
                    var oddImag = imag[odd];
                    var productReal =
                        twiddleReal * oddReal -
                        twiddleImag * oddImag;
                    var productImag =
                        twiddleReal * oddImag +
                        twiddleImag * oddReal;
                    var evenReal = real[even];
                    var evenImag = imag[even];

                    real[even] = evenReal + productReal;
                    imag[even] = evenImag + productImag;
                    real[odd] = evenReal - productReal;
                    imag[odd] = evenImag - productImag;

                    var nextReal =
                        twiddleReal * stepCos -
                        twiddleImag * stepSin;
                    twiddleImag =
                        twiddleReal * stepSin +
                        twiddleImag * stepCos;
                    twiddleReal = nextReal;
                }
            }

            if (width == n)
            {
                break;
            }
        }
    }

    private static float Sum(float[] values)
    {
        var sum = 0f;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum;
    }

    internal static string ToCamelot(int rootIndex, bool major)
    {
        string[] majorKeys =
        [
            "8B", "3B", "10B", "5B", "12B", "7B",
            "2B", "9B", "4B", "11B", "6B", "1B",
        ];
        string[] minorKeys =
        [
            "8A", "3A", "10A", "5A", "12A", "7A",
            "2A", "9A", "4A", "11A", "6A", "1A",
        ];

        return major ? majorKeys[rootIndex] : minorKeys[rootIndex];
    }

    private static HarmonicAnalysisResult Unknown()
        => new("Unknown", "?", 0d);
}

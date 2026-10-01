using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal sealed record HarmonicAnalysisResult(
    string Key,
    string CamelotKey,
    double Confidence);

internal sealed class HarmonicAccumulator
{
    private const int FrameSize = 4096;
    private const int HopSize = 2048;

    private static readonly double[] MajorProfile =
    [
        6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88,
    ];

    private static readonly double[] MinorProfile =
    [
        6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17,
    ];

    private static readonly string[] KeyNames =
    [
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B",
    ];

    private readonly int _sampleRate;
    private readonly List<float> _buffer = new(FrameSize);
    private readonly double[] _chroma = new double[12];
    private int _framesAnalyzed;

    public HarmonicAccumulator(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    public void Push(float sample)
    {
        _buffer.Add(float.IsFinite(sample) ? sample : 0f);
        if (_buffer.Count < FrameSize)
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

        var chromaMax = 0d;
        foreach (var value in _chroma)
        {
            chromaMax = Math.Max(chromaMax, value);
        }

        if (!double.IsFinite(chromaMax) || chromaMax <= 1e-12)
        {
            return Unknown();
        }

        var bestMajor = (Root: 0, Score: double.NegativeInfinity);
        var bestMinor = (Root: 0, Score: double.NegativeInfinity);
        for (var root = 0; root < 12; root++)
        {
            var major = 0d;
            var minor = 0d;
            for (var index = 0; index < 12; index++)
            {
                var chromaIndex = (index + root) % 12;
                major += _chroma[chromaIndex] * MajorProfile[index];
                minor += _chroma[chromaIndex] * MinorProfile[index];
            }

            if (major > bestMajor.Score)
            {
                bestMajor = (root, major);
            }

            if (minor > bestMinor.Score)
            {
                bestMinor = (root, minor);
            }
        }

        var majorConfidence =
            bestMajor.Score / chromaMax / Sum(MajorProfile);
        var minorConfidence =
            bestMinor.Score / chromaMax / Sum(MinorProfile);

        var isMajor = majorConfidence > minorConfidence;
        var rootIndex = isMajor ? bestMajor.Root : bestMinor.Root;
        var confidence = Math.Clamp(
            isMajor ? majorConfidence : minorConfidence,
            0d,
            1d);
        if (!double.IsFinite(confidence))
        {
            return Unknown();
        }

        var key = $"{KeyNames[rootIndex]} {(isMajor ? "Major" : "Minor")}";
        return new HarmonicAnalysisResult(key, ToCamelot(rootIndex, isMajor), confidence);
    }

    private void AnalyzeFrame(IReadOnlyList<float> samples)
    {
        var real = new double[FrameSize];
        var imag = new double[FrameSize];

        for (var index = 0; index < FrameSize; index++)
        {
            var window =
                0.5d *
                (1d - Math.Cos(2d * Math.PI * index / (FrameSize - 1d)));
            real[index] = samples[index] * window;
        }

        Fft(real, imag);
        var half = FrameSize / 2;
        for (var bin = 1; bin < half; bin++)
        {
            var frequency = bin * _sampleRate / (double)FrameSize;
            if (frequency < 40d || frequency > 5_000d)
            {
                continue;
            }

            var magnitude = Math.Sqrt(real[bin] * real[bin] + imag[bin] * imag[bin]);
            var note = 12d * Math.Log2(frequency / 440d) + 69d;
            var chromaIndex = ((int)Math.Round(note) % 12 + 12) % 12;
            _chroma[chromaIndex] += magnitude;
        }

        _framesAnalyzed++;
    }

    private static void Fft(double[] real, double[] imag)
    {
        var n = real.Length;
        for (var index = 1, reversed = 0; index < n; index++)
        {
            var bit = n >> 1;
            for (; (reversed & bit) != 0; bit >>= 1)
            {
                reversed ^= bit;
            }

            reversed ^= bit;
            if (index < reversed)
            {
                (real[index], real[reversed]) = (real[reversed], real[index]);
                (imag[index], imag[reversed]) = (imag[reversed], imag[index]);
            }
        }

        for (var width = 2; width <= n; width <<= 1)
        {
            var half = width >> 1;
            var angle = -2d * Math.PI / width;
            var stepReal = Math.Cos(angle);
            var stepImag = Math.Sin(angle);

            for (var start = 0; start < n; start += width)
            {
                var twiddleReal = 1d;
                var twiddleImag = 0d;
                for (var offset = 0; offset < half; offset++)
                {
                    var even = start + offset;
                    var odd = even + half;
                    var oddReal =
                        twiddleReal * real[odd] -
                        twiddleImag * imag[odd];
                    var oddImag =
                        twiddleReal * imag[odd] +
                        twiddleImag * real[odd];
                    var evenReal = real[even];
                    var evenImag = imag[even];

                    real[even] = evenReal + oddReal;
                    imag[even] = evenImag + oddImag;
                    real[odd] = evenReal - oddReal;
                    imag[odd] = evenImag - oddImag;

                    var nextReal =
                        twiddleReal * stepReal -
                        twiddleImag * stepImag;
                    twiddleImag =
                        twiddleReal * stepImag +
                        twiddleImag * stepReal;
                    twiddleReal = nextReal;
                }
            }
        }
    }

    private static double Sum(double[] values)
    {
        var sum = 0d;
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

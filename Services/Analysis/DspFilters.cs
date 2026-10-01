using System;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

internal readonly record struct AnalysisCrossover(
    double SubBassHz,
    double BassMidHz,
    double MidPresenceHz,
    double PresenceAirHz)
{
    public static AnalysisCrossover Spectral => new(80, 250, 2_000, 8_000);
    public static AnalysisCrossover Rhythm => new(20, 120, 400, 4_000);
}

internal readonly record struct AnalysisBandSample(
    double Sub,
    double Bass,
    double Mid,
    double Presence,
    double Air);

internal sealed class AnalysisBiquad
{
    private double _b0;
    private double _b1;
    private double _b2;
    private double _a1;
    private double _a2;
    private double _s1;
    private double _s2;

    private AnalysisBiquad()
    {
    }

    public static AnalysisBiquad LowPass(double k, double q)
    {
        var norm = 1d / (1d + k / q + k * k);
        var b0 = k * k * norm;
        return new AnalysisBiquad
        {
            _b0 = b0,
            _b1 = 2d * b0,
            _b2 = b0,
            _a1 = 2d * (k * k - 1d) * norm,
            _a2 = (1d - k / q + k * k) * norm,
        };
    }

    public static AnalysisBiquad HighPass(double k, double q)
    {
        var norm = 1d / (1d + k / q + k * k);
        return new AnalysisBiquad
        {
            _b0 = norm,
            _b1 = -2d * norm,
            _b2 = norm,
            _a1 = 2d * (k * k - 1d) * norm,
            _a2 = (1d - k / q + k * k) * norm,
        };
    }

    public double Process(double input)
    {
        var output = _b0 * input + _s1;
        _s1 = _b1 * input - _a1 * output + _s2;
        _s2 = _b2 * input - _a2 * output;
        return output;
    }
}

internal sealed class AnalysisBessel4
{
    private static readonly (double W0, double Q)[] Sections =
    [
        (1.430_190, 0.521_888),
        (1.603_357, 0.805_536),
    ];

    private readonly AnalysisBiquad[] _sections;

    private AnalysisBessel4(AnalysisBiquad[] sections)
    {
        _sections = sections;
    }

    public static AnalysisBessel4 LowPass(double cutoffHz, double sampleRate)
    {
        var shared = Prewarp(cutoffHz, sampleRate);
        return new AnalysisBessel4(
            Array.ConvertAll(
                Sections,
                section => AnalysisBiquad.LowPass(shared * section.W0, section.Q)));
    }

    public static AnalysisBessel4 HighPass(double cutoffHz, double sampleRate)
    {
        var shared = Prewarp(cutoffHz, sampleRate);
        return new AnalysisBessel4(
            Array.ConvertAll(
                Sections,
                section => AnalysisBiquad.HighPass(shared / section.W0, section.Q)));
    }

    public double Process(double input)
    {
        var value = input;
        foreach (var section in _sections)
        {
            value = section.Process(value);
        }

        return value;
    }

    private static double Prewarp(double cornerHz, double sampleRate)
    {
        var nyquist = sampleRate / 2d;
        var clamped = Math.Clamp(cornerHz, 1d, nyquist * 0.98d);
        return Math.Tan(Math.PI * clamped / sampleRate);
    }
}

internal sealed class AnalysisBandSplitter
{
    private readonly AnalysisBessel4 _sub;
    private readonly AnalysisBessel4 _bassHigh;
    private readonly AnalysisBessel4 _bassLow;
    private readonly AnalysisBessel4 _midHigh;
    private readonly AnalysisBessel4 _midLow;
    private readonly AnalysisBessel4 _presenceHigh;
    private readonly AnalysisBessel4 _presenceLow;
    private readonly AnalysisBessel4 _air;

    public AnalysisBandSplitter(AnalysisCrossover crossover, double sampleRate)
    {
        _sub = AnalysisBessel4.LowPass(crossover.SubBassHz, sampleRate);
        _bassHigh = AnalysisBessel4.HighPass(crossover.SubBassHz, sampleRate);
        _bassLow = AnalysisBessel4.LowPass(crossover.BassMidHz, sampleRate);
        _midHigh = AnalysisBessel4.HighPass(crossover.BassMidHz, sampleRate);
        _midLow = AnalysisBessel4.LowPass(crossover.MidPresenceHz, sampleRate);
        _presenceHigh = AnalysisBessel4.HighPass(crossover.MidPresenceHz, sampleRate);
        _presenceLow = AnalysisBessel4.LowPass(crossover.PresenceAirHz, sampleRate);
        _air = AnalysisBessel4.HighPass(crossover.PresenceAirHz, sampleRate);
    }

    public AnalysisBandSample Process(double input)
        => new(
            _sub.Process(input),
            _bassLow.Process(_bassHigh.Process(input)),
            _midLow.Process(_midHigh.Process(input)),
            _presenceLow.Process(_presenceHigh.Process(input)),
            _air.Process(input));
}

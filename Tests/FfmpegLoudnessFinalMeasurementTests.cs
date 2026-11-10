using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class FfmpegLoudnessFinalMeasurementTests
{
    [Fact]
    public void Parse_MapsEbuTech3341StereoCalibrationReport()
    {
        // EBU Tech 3341:2023 Test 1 is a 20-second, in-phase stereo 1 kHz
        // sine with a -23 dBFS peak on each channel. Its integrated result is
        // -23.0 LUFS +/- 0.1 LU. This report was captured with Jellyfin FFmpeg
        // 8.1.3-1 and loudnorm=print_format=json.
        const string output = """
            {
                "input_i":"-23.05",
                "input_tp":"-23.00",
                "input_lra":"0.00",
                "input_thresh":"-33.05"
            }
            """;

        var result = FfmpegLoudnessParser.Parse(output);

        Assert.NotNull(result);
        Assert.InRange(result!.IntegratedLufs, -23.1d, -22.9d);
        Assert.Equal(-23.0d, result.TruePeak, 2);
        Assert.Equal(0.0d, result.DynamicRange, 2);
    }

    [Fact]
    public void Parse_AcceptsLatestCompleteLoudnormReport()
    {
        const string output = """
            {"input_i":"-18.0","input_tp":"-2.0","input_lra":"4.0"}
            {"input_i":"-14.2","input_tp":"-0.8","input_lra":"6.1"}
            """;

        var result = FfmpegLoudnessParser.Parse(output);

        Assert.NotNull(result);
        Assert.Equal(-14.2d, result.IntegratedLufs);
        Assert.Equal(-0.8d, result.TruePeak);
        Assert.Equal(6.1d, result.DynamicRange);
    }

    [Fact]
    public void Parse_RejectsFieldsSplitAcrossReports()
    {
        const string output = """
            {"input_i":"-18.0","input_tp":"-2.0","input_lra":"4.0"}
            {"input_tp":"-0.8"}
            """;

        Assert.Null(FfmpegLoudnessParser.Parse(output));
    }

    [Theory]
    [InlineData("input_i")]
    [InlineData("input_tp")]
    [InlineData("input_lra")]
    public void Parse_RejectsOutOfRangeFinalMeasurementInsteadOfUsingStaleData(string field)
    {
        const string previous = """
            {"input_i":"-18.0","input_tp":"-2.0","input_lra":"4.0"}
            """;
        var final = """
            {"input_i":"-14.2","input_tp":"-0.8","input_lra":"6.1"}
            """.Replace(
                field switch
                {
                    "input_i" => "\"input_i\":\"-14.2\"",
                    "input_tp" => "\"input_tp\":\"-0.8\"",
                    _ => "\"input_lra\":\"6.1\"",
                },
                $"\"{field}\":\"1e999\"");

        Assert.Null(FfmpegLoudnessParser.Parse(previous + final));
    }
}

using Jellyfin.Plugin.AudioGateway.Services.Analysis;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public class FfmpegLoudnessFinalMeasurementTests
{
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

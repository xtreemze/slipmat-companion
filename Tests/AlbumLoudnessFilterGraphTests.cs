using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class AlbumLoudnessFilterGraphTests
{
    [Fact]
    public void BuildAlbumLoudnessFilterGraphMeasuresTheOrderedSequenceAfterConcatenation()
    {
        var graph = IntegratedAudioAnalyzer.BuildAlbumLoudnessFilterGraph([2, 0, 5], "stereo");

        Assert.Equal(
            "[0:2]aresample=48000:async=0:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS[a0];" +
            "[1:0]aresample=48000:async=0:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS[a1];" +
            "[2:5]aresample=48000:async=0:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS[a2];" +
            "[a0][a1][a2]concat=n=3:v=0:a=1[album];" +
            "[album]loudnorm=I=-23:TP=-2:LRA=7:print_format=json[measured]",
            graph);
    }

    [Fact]
    public void BuildAlbumLoudnessFilterGraphRejectsEmptyOrInvalidStreams()
    {
        Assert.Throws<System.ArgumentException>(
            () => IntegratedAudioAnalyzer.BuildAlbumLoudnessFilterGraph([], "stereo"));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => IntegratedAudioAnalyzer.BuildAlbumLoudnessFilterGraph([0, -1], "stereo"));
    }
}

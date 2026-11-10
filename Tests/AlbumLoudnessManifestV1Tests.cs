using System;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class AlbumLoudnessManifestV1Tests
{
    private static AlbumLoudnessMemberV1 Member(
        string id,
        int? disc = 1,
        int? track = 1,
        string layout = "stereo",
        int streamIndex = 0,
        int sampleRate = 48_000,
        int channelCount = 2,
        long length = 1_000,
        long modified = 638_000_000_000_000_000)
        => new(id, disc, track, layout, streamIndex, sampleRate, channelCount, length, modified);

    [Fact]
    public void TryCreate_OrdersMembersAndNormalizesIdentityAndLayout()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var created = AlbumLoudnessManifestV1.TryCreate(
            [Member(second.ToString(), disc: 2, track: 1, layout: " STEREO "),
             Member(first.ToString("D"), disc: 1, track: 2)],
            out var manifest,
            out var reason);

        Assert.True(created);
        Assert.Null(reason);
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.Version);
        Assert.Equal([first.ToString("N"), second.ToString("N")],
            new[] { manifest.Members[0].ItemId, manifest.Members[1].ItemId });
        Assert.Equal("stereo", manifest.Members[1].ChannelLayout);
    }

    [Fact]
    public void TryCreate_FingerprintIsIndependentOfInputOrder()
    {
        var members = new[]
        {
            Member("00000000-0000-0000-0000-000000000002", disc: 2),
            Member("00000000-0000-0000-0000-000000000001", disc: 1, track: 2),
        };

        Assert.True(AlbumLoudnessManifestV1.TryCreate(members, out var left, out _));
        Assert.True(AlbumLoudnessManifestV1.TryCreate(members.Reverse(), out var right, out _));
        Assert.Equal(left!.MembershipFingerprint, right!.MembershipFingerprint);
    }

    [Fact]
    public void TryCreate_FingerprintChangesForMembershipOrderingFormatOrSourceStamp()
    {
        var first = Member("00000000-0000-0000-0000-000000000001", track: 1);
        var second = Member("00000000-0000-0000-0000-000000000002", track: 2);
        Assert.True(AlbumLoudnessManifestV1.TryCreate([first, second], out var baseline, out _));

        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [second with { TrackNumber = 1 }, first with { TrackNumber = 2 }], out var reordered, out _));
        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [first, second with { SampleRate = 44_100 }], out var changedRate, out _));
        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [first, second with { StreamIndex = 1 }], out var changedStream, out _));
        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [first, second with { SourceLength = second.SourceLength + 1 }], out var changedSource, out _));
        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [first, second with { SourceLastWriteUtcTicks = second.SourceLastWriteUtcTicks + 1 }], out var changedStamp, out _));
        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [first, second with { SourceLocatorFingerprint = new string('a', 64) }], out var changedLocator, out _));

        Assert.NotEqual(baseline!.MembershipFingerprint, reordered!.MembershipFingerprint);
        Assert.NotEqual(baseline.MembershipFingerprint, changedRate!.MembershipFingerprint);
        Assert.NotEqual(baseline.MembershipFingerprint, changedStream!.MembershipFingerprint);
        Assert.NotEqual(baseline.MembershipFingerprint, changedSource!.MembershipFingerprint);
        Assert.NotEqual(baseline.MembershipFingerprint, changedStamp!.MembershipFingerprint);
        Assert.NotEqual(baseline.MembershipFingerprint, changedLocator!.MembershipFingerprint);
    }

    [Fact]
    public void ComputeSourceFingerprint_ChangesWhenAlbumSequenceOrderChanges()
    {
        var first = Member("00000000-0000-0000-0000-000000000001", track: 1) with
        {
            SourceFingerprint = new string('a', 64),
        };
        var second = Member("00000000-0000-0000-0000-000000000002", track: 2) with
        {
            SourceFingerprint = new string('b', 64),
        };
        Assert.True(AlbumLoudnessManifestV1.TryCreate([first, second], out var baseline, out _));
        Assert.True(AlbumLoudnessManifestV1.TryCreate(
            [second with { TrackNumber = 1 }, first with { TrackNumber = 2 }],
            out var reordered,
            out _));

        Assert.NotEqual(
            AlbumLoudnessManifestV1.ComputeSourceFingerprint(baseline!.Members),
            AlbumLoudnessManifestV1.ComputeSourceFingerprint(reordered!.Members));
    }

    [Fact]
    public void TryCreate_FailsClosedForEmptyInvalidDuplicateOrIncompatibleMembership()
    {
        Assert.False(AlbumLoudnessManifestV1.TryCreate([], out _, out _));
        Assert.False(AlbumLoudnessManifestV1.TryCreate([Member("not-a-guid")], out _, out _));
        Assert.False(AlbumLoudnessManifestV1.TryCreate(null, out _, out _));
        Assert.False(AlbumLoudnessManifestV1.TryCreate(
            [Member("00000000-0000-0000-0000-000000000001"),
             Member("00000000-0000-0000-0000-000000000001", track: 2)], out _, out _));
        Assert.False(AlbumLoudnessManifestV1.TryCreate(
            [Member("00000000-0000-0000-0000-000000000001"),
             Member("00000000-0000-0000-0000-000000000002", track: 2, layout: "5.1", channelCount: 6)], out _, out _));
        Assert.False(AlbumLoudnessManifestV1.TryCreate(
            [Member("00000000-0000-0000-0000-000000000001"),
             Member("00000000-0000-0000-0000-000000000002", track: 2, layout: "2.0")], out _, out _));
        Assert.False(AlbumLoudnessManifestV1.TryCreate(
            [Member("00000000-0000-0000-0000-000000000001") with { SourceLocatorFingerprint = "path" }], out _, out _));
    }
}

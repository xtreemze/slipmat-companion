using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class ReversibleJpegXlEncoderTests
{
    private static readonly byte[] SmallJxl = [0xff, 0x0a, 0x01, 0x02];

    [Fact]
    public async Task AdmitsOnlyBitExactReconstructionAndCleansTemporaryFiles()
    {
        var jpeg = Jpeg();
        var paths = new List<string>();
        var encoder = CreateEncoder(observe: path => paths.Add(path));

        var result = await encoder.TryEncodeAsync(jpeg);

        Assert.NotNull(result);
        Assert.Equal("image/jxl", VerifiedJpegXl.ContentType);
        Assert.Equal(SmallJxl, result.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(jpeg)), result.SourceJpegSha256);
        Assert.Equal(128, jpeg.Length);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task RejectsMismatchedReconstructedJpeg()
    {
        var jpeg = Jpeg();
        var changed = Jpeg();
        changed[32] ^= 0x01;

        Assert.Null(await CreateEncoder(reconstruct: changed).TryEncodeAsync(jpeg));
    }

    [Fact]
    public async Task RejectsMissingReconstructionOrUnavailableTool()
    {
        var jpeg = Jpeg();
        Assert.Null(await CreateEncoder(encodeSucceeds: false).TryEncodeAsync(jpeg));
        Assert.Null(await CreateEncoder(decodeSucceeds: false).TryEncodeAsync(jpeg));
        Assert.Null(await CreateEncoder(writeReconstruction: false).TryEncodeAsync(jpeg));
    }

    [Fact]
    public async Task RejectsNonJpegAndOversizedSourceBeforeInvokingTools()
    {
        var calls = 0;
        var encoder = new ReversibleJpegXlEncoder(
            "cjxl", "djxl",
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(true);
            });

        Assert.Null(await encoder.TryEncodeAsync([0x89, 0x50, 0x4e, 0x47]));
        var oversized = new byte[ReversibleJpegXlEncoder.MaxSourceBytes + 1];
        oversized[0] = 0xff;
        oversized[1] = 0xd8;
        oversized[2] = 0xff;
        Assert.Null(await encoder.TryEncodeAsync(oversized));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RejectsJxlThatIsNotSmallerOrHasWrongSignature()
    {
        var jpeg = Jpeg();
        Assert.Null(await CreateEncoder(encoded: new byte[jpeg.Length]).TryEncodeAsync(jpeg));
        Assert.Null(await CreateEncoder(encoded: [0x01, 0x02, 0x03]).TryEncodeAsync(jpeg));
    }

    [Fact]
    public async Task DoesNotTurnCallerCancellationIntoACacheMiss()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateEncoder().TryEncodeAsync(Jpeg(), cancellation.Token));
    }

    [Fact]
    public async Task LocalCodecTimeoutReturnsAMiss()
    {
        var encoder = new ReversibleJpegXlEncoder(
            "cjxl", "djxl",
            async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            },
            TimeSpan.FromMilliseconds(10));

        Assert.Null(await encoder.TryEncodeAsync(Jpeg()));
    }

    private static byte[] Jpeg()
    {
        var bytes = new byte[128];
        bytes[0] = 0xff;
        bytes[1] = 0xd8;
        bytes[2] = 0xff;
        bytes[3] = 0xe0;
        bytes[^2] = 0xff;
        bytes[^1] = 0xd9;
        return bytes;
    }

    private static ReversibleJpegXlEncoder CreateEncoder(
        byte[]? reconstruct = null,
        byte[]? encoded = null,
        bool encodeSucceeds = true,
        bool decodeSucceeds = true,
        bool writeReconstruction = true,
        Action<string>? observe = null)
    {
        return new ReversibleJpegXlEncoder(
            "cjxl", "djxl",
            async (executable, arguments, cancellationToken) =>
            {
                Assert.True(arguments.Length >= 2);
                observe?.Invoke(arguments[1]);

                if (executable == "cjxl")
                {
                    Assert.Contains("--lossless_jpeg=1", arguments);
                    Assert.Contains("--allow_jpeg_reconstruction=1", arguments);
                    if (!encodeSucceeds) return false;
                    await File.WriteAllBytesAsync(
                        arguments[1], encoded ?? SmallJxl, cancellationToken);
                    return true;
                }

                Assert.Equal("djxl", executable);
                if (!decodeSucceeds) return false;
                if (writeReconstruction)
                {
                    await File.WriteAllBytesAsync(
                        arguments[1], reconstruct ?? Jpeg(), cancellationToken);
                }

                return true;
            });
    }
}

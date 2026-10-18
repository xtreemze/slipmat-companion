using System;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Selects an optional reconstructed-JPEG-preserving derivative only when the
/// caller has explicitly established JPEG XL decode support. This is a pure
/// representation policy; HTTP content negotiation and storage are separate.
/// </summary>
public static class JpegXlDeliverySelection
{
    public const string JpegContentType = "image/jpeg";

    public static ImageRepresentation Select(
        byte[] sourceJpeg,
        VerifiedJpegXl? derivative,
        bool supportsJpegXl)
    {
        ArgumentNullException.ThrowIfNull(sourceJpeg);

        var fallback = new ImageRepresentation(sourceJpeg, JpegContentType, false);

        // A codec-capability flag must be positive and bound to the actual
        // decoding client; accept=*/* alone is not proof of JXL support.
        if (!supportsJpegXl || derivative is null
            || derivative.Bytes.Length == 0
            || derivative.Bytes.Length >= sourceJpeg.Length)
        {
            return fallback;
        }

        // Derivatives belong to a specific source. Never deliver stale cached
        // JXL bytes after the source artwork is replaced.
        var hash = Convert.ToHexStringLower(SHA256.HashData(sourceJpeg));
        if (!string.Equals(
                hash,
                derivative.SourceJpegSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return new ImageRepresentation(
            derivative.Bytes, VerifiedJpegXl.ContentType, true);
    }
}

public sealed record ImageRepresentation(
    byte[] Bytes,
    string ContentType,
    bool IsDerivative);

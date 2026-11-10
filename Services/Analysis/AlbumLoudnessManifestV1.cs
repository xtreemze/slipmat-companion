using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Services.Analysis;

/// <summary>
/// Cheap, deterministic identity for the ordered local source set used by an
/// album-scoped loudness measurement. It contains no paths or media payloads.
/// </summary>
public sealed record AlbumLoudnessManifestV1(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("membershipFingerprint")] string MembershipFingerprint,
    [property: JsonPropertyName("members")] IReadOnlyList<AlbumLoudnessMemberV1> Members)
{
    public const int CurrentVersion = 1;

    public static bool TryCreate(
        IEnumerable<AlbumLoudnessMemberV1>? members,
        out AlbumLoudnessManifestV1? manifest,
        out string? reason)
    {
        manifest = null;
        reason = null;
        if (members is null)
        {
            reason = "Album membership is unavailable.";
            return false;
        }

        var normalized = new List<AlbumLoudnessMemberV1>();
        foreach (var member in members)
        {
            if (member is null ||
                !Guid.TryParse(member.ItemId, out var itemId) ||
                member.SampleRate <= 0 ||
                member.ChannelCount <= 0 ||
                member.SourceLength <= 0 ||
                member.SourceLastWriteUtcTicks <= 0 ||
                string.IsNullOrWhiteSpace(member.ChannelLayout))
            {
                reason = "An album member is missing valid identity, audio layout, or source metadata.";
                return false;
            }

            normalized.Add(member with
            {
                ItemId = itemId.ToString("N"),
                ChannelLayout = member.ChannelLayout.Trim().ToLowerInvariant(),
            });
        }

        if (normalized.Count == 0)
        {
            reason = "Album has no analyzable tracks.";
            return false;
        }

        var ordered = normalized
            .OrderBy(member => member.DiscNumber ?? int.MaxValue)
            .ThenBy(member => member.TrackNumber ?? int.MaxValue)
            .ThenBy(member => member.ItemId, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Select(member => member.ItemId).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            reason = "Album membership contains a duplicate track identity.";
            return false;
        }

        var first = ordered[0];
        if (ordered.Any(member =>
                member.ChannelCount != first.ChannelCount ||
                !string.Equals(member.ChannelLayout, first.ChannelLayout, StringComparison.Ordinal)))
        {
            reason = "Album tracks have incompatible channel layouts.";
            return false;
        }

        manifest = new AlbumLoudnessManifestV1(
            CurrentVersion,
            ComputeFingerprint(ordered),
            ordered);
        return true;
    }

    private static string ComputeFingerprint(IReadOnlyList<AlbumLoudnessMemberV1> members)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "slipmat-album-loudness-membership-v1");
        Append(hash, members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var member in members)
        {
            Append(hash, member.ItemId);
            Append(hash, member.DiscNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
            Append(hash, member.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
            Append(hash, member.ChannelLayout);
            Append(hash, member.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, member.ChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, member.SourceLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, member.SourceLastWriteUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

/// <summary>One ordered track and its cheap source/audio-format identity.</summary>
public sealed record AlbumLoudnessMemberV1(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("discNumber")] int? DiscNumber,
    [property: JsonPropertyName("trackNumber")] int? TrackNumber,
    [property: JsonPropertyName("channelLayout")] string ChannelLayout,
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("channelCount")] int ChannelCount,
    [property: JsonPropertyName("sourceLength")] long SourceLength,
    [property: JsonPropertyName("sourceLastWriteUtcTicks")] long SourceLastWriteUtcTicks);

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AudioGateway.Models;

/// <summary>
/// Host-neutral lookup/provenance identity for an analyzable media resource.
/// The deterministic store key is byte-for-byte compatible with the Rust
/// AnalysisSubjectV1 implementation.
/// </summary>
public sealed record AnalysisSubjectV1(
    [property: JsonPropertyName("hostKind")] string HostKind,
    [property: JsonPropertyName("providerInstanceId")] string ProviderInstanceId,
    [property: JsonPropertyName("resourceId")] string ResourceId,
    [property: JsonPropertyName("representationId")] string? RepresentationId = null)
{
    public const int SubjectVersion = 1;
    private const string StoreKeyPrefix = "asv1-";
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("slipmat-analysis-subject-v1\0");

    public static bool IsStoreKey(string? value)
    {
        if (value is null ||
            !value.StartsWith(StoreKeyPrefix, StringComparison.Ordinal) ||
            value.Length != StoreKeyPrefix.Length + 64)
        {
            return false;
        }

        foreach (var character in value.AsSpan(StoreKeyPrefix.Length))
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public string StoreKey()
    {
        Validate();

        using var canonical = new MemoryStream();
        canonical.Write(Domain);
        WriteString(canonical, HostKind);
        WriteString(canonical, ProviderInstanceId);
        WriteString(canonical, ResourceId);

        if (RepresentationId is null)
        {
            canonical.WriteByte(0);
        }
        else
        {
            canonical.WriteByte(1);
            WriteString(canonical, RepresentationId);
        }

        var digest = SHA256.HashData(canonical.ToArray());
        return StoreKeyPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private void Validate()
    {
        if (string.IsNullOrEmpty(HostKind))
            throw new ArgumentException("Analysis subject host kind must not be empty.", nameof(HostKind));
        if (string.IsNullOrEmpty(ProviderInstanceId))
            throw new ArgumentException("Analysis subject provider instance ID must not be empty.", nameof(ProviderInstanceId));
        if (string.IsNullOrEmpty(ResourceId))
            throw new ArgumentException("Analysis subject resource ID must not be empty.", nameof(ResourceId));
        if (RepresentationId is not null && RepresentationId.Length == 0)
            throw new ArgumentException("Analysis subject representation ID must not be empty when present.", nameof(RepresentationId));
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }
}

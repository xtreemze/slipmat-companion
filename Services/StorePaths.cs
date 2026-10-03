using System;
using System.IO;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Resolves filesystem paths within the host-neutral artifact store.
///
/// Store layout:
///   {storeRoot}/waveforms/{subjectStoreKey}/{variant}/pps_{pps}.dat
///   {storeRoot}/analysis/{subjectStoreKey}.json
/// </summary>
public static class StorePaths
{
    /// <summary>
    /// Validates a deterministic host-neutral analysis subject key.
    /// </summary>
    public static void ValidateStoreKey(string storeKey)
    {
        if (!Jellyfin.Plugin.AudioGateway.Models.AnalysisSubjectV1.IsStoreKey(storeKey))
        {
            throw new ArgumentException("Invalid host-neutral analysis subject store key.", nameof(storeKey));
        }
    }

    /// <summary>
    /// Validates a non-secret path segment such as an artifact variant.
    /// </summary>
    public static void ValidatePathSegment(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Store path segment must not be null or whitespace.", nameof(key));

        if (key.Contains('/') || key.Contains('\\'))
            throw new ArgumentException("Store path segment must not contain path separators.", nameof(key));

        if (key.Contains(".."))
            throw new ArgumentException("Store path segment must not contain '..'.", nameof(key));
    }

    public static string WaveformDatPath(string storeRoot, string storeKey, string variant, int pps)
    {
        ValidateStoreKey(storeKey);
        ValidatePathSegment(variant);
        return Path.Combine(storeRoot, "waveforms", storeKey, variant, $"pps_{pps}.dat");
    }

    public static string SidecarPath(string storeRoot, string storeKey)
    {
        ValidateStoreKey(storeKey);
        return Path.Combine(storeRoot, "analysis", $"{storeKey}.json");
    }

    public static string AnalysisSourceStampPath(string storeRoot, string storeKey)
    {
        ValidateStoreKey(storeKey);
        return Path.Combine(storeRoot, "analysis-source", $"{storeKey}.json");
    }

    public static bool IsWithinRoot(string root, string candidatePath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candFull = Path.GetFullPath(candidatePath);
        return candFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }
}

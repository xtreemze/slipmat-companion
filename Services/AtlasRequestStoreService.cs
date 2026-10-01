using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

public static class AtlasRequestStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ResolveStoreRoot(string? storeRootOverride = null)
    {
        var configuredRoot = storeRootOverride ?? Plugin.Instance?.Configuration.StoreRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(configuredRoot, "atlas");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jellyfin.AudioGateway",
            "atlas");
    }

    public static string ResolveRequestsPath(string? storeRootOverride = null)
        => Path.Combine(ResolveStoreRoot(storeRootOverride), "requests.json");

    public static IReadOnlyList<AtlasAcquisitionRequestRecord> LoadRequests(string? storeRootOverride = null)
    {
        var path = ResolveRequestsPath(storeRootOverride);
        if (!File.Exists(path))
        {
            return Array.Empty<AtlasAcquisitionRequestRecord>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var items = JsonSerializer.Deserialize<List<AtlasAcquisitionRequestRecord>>(json, JsonOptions);
            return items ?? new List<AtlasAcquisitionRequestRecord>();
        }
        catch
        {
            return Array.Empty<AtlasAcquisitionRequestRecord>();
        }
    }

    public static AtlasAcquisitionRequestRecord UpsertRequest(
        AtlasAcquisitionRequestRecord request,
        string? storeRootOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var items = LoadRequests(storeRootOverride).ToList();
        var normalized = request with
        {
            ReceivedAt = request.ReceivedAt ?? DateTimeOffset.UtcNow.ToString("O"),
            ProviderSource = request.ProviderSource ?? InferProviderSource(request),
            ProviderAlbumId = request.ProviderAlbumId ?? InferProviderAlbumId(request),
        };

        var existingIndex = items.FindIndex(item => item.AlbumId == normalized.AlbumId);
        if (existingIndex >= 0)
        {
            items[existingIndex] = normalized;
        }
        else
        {
            items.Add(normalized);
        }

        SaveRequests(items, storeRootOverride);
        return normalized;
    }

    public static void SaveRequests(
        IReadOnlyList<AtlasAcquisitionRequestRecord> requests,
        string? storeRootOverride = null)
    {
        var path = ResolveRequestsPath(storeRootOverride);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(requests, JsonOptions));
    }

    private static string? InferProviderSource(AtlasAcquisitionRequestRecord request)
    {
        if (!string.Equals(request.Provider, "streamrip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = request.AlbumId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[1] : null;
    }

    private static string? InferProviderAlbumId(AtlasAcquisitionRequestRecord request)
    {
        if (!string.Equals(request.Provider, "streamrip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = request.AlbumId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : null;
    }
}

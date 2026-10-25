using System;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Allowlisted numeric projection of rclone JSON log statistics; no URLs or filenames.</summary>
public static class RcloneMigrationProgress
{
    public static bool TryParse(string line, out CloudMigrationProgress? progress)
    {
        progress = null;
        if (line.Length > 65536) return false;
        try
        {
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("stats", out var stats)
                || stats.ValueKind != JsonValueKind.Object) return false;
            long? Number(string key) => stats.TryGetProperty(key, out var value)
                && value.TryGetInt64(out var number) && number >= 0 ? number : null;
            var bytes = Number("bytes");
            var total = Number("totalBytes");
            if (bytes is null || total is null) return false;
            progress = new(bytes.Value, total.Value, Number("checks") ?? 0,
                Number("transfers") ?? 0, Number("errors") ?? 0, DateTimeOffset.UtcNow);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

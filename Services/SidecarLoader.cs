using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Reads host-neutral V2 analysis sidecars written by any compatible analyzer.
/// Missing, corrupt, incompatible, or subject-mismatched sidecars are all
/// treated as ordinary artifact absence.
/// </summary>
public class SidecarLoader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public AnalysisSidecar? LoadSidecarFromStore(string storeRoot, string subjectStoreKey)
    {
        var path = StorePaths.SidecarPath(storeRoot, subjectStoreKey);
        var sidecar = LoadSidecar(path);
        return sidecar?.IsCurrentFor(subjectStoreKey) == true ? sidecar : null;
    }

    public AnalysisSidecar? LoadSidecar(string absolutePath)
    {
        if (!File.Exists(absolutePath)) return null;
        try
        {
            var json = File.ReadAllText(absolutePath);
            return JsonSerializer.Deserialize<AnalysisSidecar>(json, _options);
        }
        catch
        {
            return null;
        }
    }

    public AnalysisSidecar? LoadSidecarFromJson(string json)
    {
        try { return JsonSerializer.Deserialize<AnalysisSidecar>(json, _options); }
        catch { return null; }
    }
}

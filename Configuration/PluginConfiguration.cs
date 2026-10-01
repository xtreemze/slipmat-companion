using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AudioGateway.Configuration;

/// <summary>
/// Plugin configuration. Extend with store paths, analyzer URL, etc. in M1/M2.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the audio-analyzer service.
    /// </summary>
    public string AnalyzerBaseUrl { get; set; } = "http://localhost:8765";

    /// <summary>
    /// Gets or sets the local store root directory used for artifact caching.
    /// </summary>
    public string StoreRoot { get; set; } = "/store";
}

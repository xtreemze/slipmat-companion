using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AudioGateway.Configuration;

/// <summary>
/// Persisted Audio Gateway configuration. External services are opt-in; a
/// catalog install requires no analyzer or manually provisioned store.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets whether the external analyzer endpoint was explicitly enabled.
    /// Environment configuration can still enable it independently.
    /// </summary>
    public bool AnalyzerEnabled { get; set; }

    /// <summary>
    /// Gets or sets the base URL of an explicitly deployed audio-analyzer service.
    /// </summary>
    public string AnalyzerBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional store-root override. When empty, the plugin uses
    /// a managed directory under Jellyfin's data path.
    /// </summary>
    public string StoreRoot { get; set; } = string.Empty;
}

using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AudioGateway.Configuration;

/// <summary>
/// Persisted Audio Gateway configuration. Catalog installs are self-contained;
/// only storage placement remains user-overridable.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets an optional store-root override. When empty, the plugin uses
    /// a managed directory under Jellyfin's data path.
    /// </summary>
    public string StoreRoot { get; set; } = string.Empty;
}

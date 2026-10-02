using System;
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

    /// <summary>
    /// Gets or sets the server-wide iptv-org channel IDs published through
    /// Jellyfin's Live TV service. This is source configuration, not Slipmat's
    /// per-user client subscription authority.
    /// </summary>
    public string[] IptvOrgLiveTvChannelIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether the operator-managed rclone cloud
    /// projection is enabled. rclone installation/authentication remain external.
    /// </summary>
    public bool CloudProjectionEnabled { get; set; }

    /// <summary>
    /// Gets or sets the configured rclone remote name, without the trailing colon.
    /// </summary>
    public string CloudRemoteName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the selected folder path inside the rclone remote.
    /// </summary>
    public string CloudRemotePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional absolute local projection-root override.
    /// Empty uses a Jellyfin-managed path.
    /// </summary>
    public string CloudProjectionPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin library name created for the projection.
    /// </summary>
    public string CloudLibraryName { get; set; } = "Cloud Media";

    /// <summary>
    /// Gets or sets the Jellyfin collection type for the managed projection.
    /// </summary>
    public string CloudCollectionType { get; set; } = "music";

    /// <summary>
    /// Gets or sets a value indicating whether the companion may create the
    /// Jellyfin virtual folder after a completed materialization.
    /// </summary>
    public bool CloudAutoCreateLibrary { get; set; } = true;

}

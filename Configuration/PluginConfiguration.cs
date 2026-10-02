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
    /// Gets or sets a value indicating whether the operator-managed Jottacloud
    /// projection is enabled. Installation and login remain external SSH/admin work.
    /// </summary>
    public bool JottacloudProjectionEnabled { get; set; }

    /// <summary>
    /// Gets or sets the remote Jottacloud folder to materialize.
    /// </summary>
    public string JottacloudRemotePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dedicated local projection root read by Jellyfin.
    /// </summary>
    public string JottacloudProjectionPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin library name created for the projection.
    /// </summary>
    public string JottacloudLibraryName { get; set; } = "Jottacloud";

    /// <summary>
    /// Gets or sets the Jellyfin collection type for the managed projection.
    /// </summary>
    public string JottacloudCollectionType { get; set; } = "music";

    /// <summary>
    /// Gets or sets a value indicating whether the companion may create the
    /// Jellyfin virtual folder after a completed projection exists.
    /// </summary>
    public bool JottacloudAutoCreateLibrary { get; set; } = true;
}

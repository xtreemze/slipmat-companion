using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.AudioGateway.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AudioGateway;

/// <summary>
/// Optional Slipmat acceleration/enrichment plugin entry point.
/// Product semantics and authoritative state remain client/Rust-owned.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin ID — do not change after first release.
    /// </summary>
    public static readonly Guid StaticId = new("d3a2b1c0-e4f5-6789-abcd-ef0123456789");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Audio Gateway";

    /// <inheritdoc />
    public override Guid Id => StaticId;

    /// <inheritdoc />
    public override string Description =>
        "Optional Slipmat metadata batching and precomputed audio-artifact acceleration. " +
        "Slipmat remains fully functional without this plugin.";

    /// <summary>Gets the singleton instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        ];
    }
}

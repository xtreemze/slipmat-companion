using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Exact, job-derived profile bindings for a supported offline XML plan.</summary>
/// <param name="ProfileId">Stable profile identifier.</param>
/// <param name="RemoteName">Expected remote name.</param>
/// <param name="RemotePath">Expected remote path.</param>
/// <param name="LibraryName">Expected library name.</param>
/// <param name="PreviousProjectionPath">Original profile projection path.</param>
/// <param name="PreviousEnabled">Original profile enabled state.</param>
/// <param name="LocalMountPath">Absolute target mount path.</param>
public sealed record CloudMaintenanceProfileBinding(string ProfileId, string RemoteName, string RemotePath, string LibraryName, string PreviousProjectionPath, bool PreviousEnabled, string LocalMountPath);

/// <summary>Conservative profile and scan policy plan builders. No filesystem I/O or authorization.</summary>
public static class CloudMaintenanceProfileConfiguration
{
    /// <summary>Build a profile plan; multiple explicit profiles are deliberately unsupported.</summary>
    public static CloudMaintenanceXmlPlan PrepareProfile(byte[] bytes, string hash, CloudMaintenanceProfileBinding binding, bool promote)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (string.IsNullOrWhiteSpace(binding.ProfileId) || string.IsNullOrWhiteSpace(binding.RemoteName)
            || string.IsNullOrWhiteSpace(binding.LibraryName) || !Path.IsPathFullyQualified(binding.LocalMountPath)
            || Path.GetFullPath(binding.LocalMountPath) != binding.LocalMountPath)
            throw new InvalidOperationException("Invalid profile binding.");
        var root = Parse(bytes, "PluginConfiguration");
        var cloud = One(root, "CloudLibraries", required: false);
        var profiles = cloud?.Elements().ToArray() ?? [];
        string[] prefix;
        string enabled, projection, remote, remotePath, library;
        if (profiles.Length == 0)
        {
            if (binding.ProfileId != "legacy") throw new InvalidOperationException("Profile binding mismatch.");
            prefix = [];
            enabled = "CloudProjectionEnabled"; projection = "CloudProjectionPath"; remote = "CloudRemoteName"; remotePath = "CloudRemotePath"; library = "CloudLibraryName";
        }
        else
        {
            if (profiles.Length != 1 || profiles[0].Name != "CloudLibraryProfile") throw new InvalidOperationException("Unsupported profile structure.");
            if (Text(profiles[0], "Id") != binding.ProfileId) throw new InvalidOperationException("Profile binding mismatch.");
            prefix = ["CloudLibraries", "CloudLibraryProfile"];
            enabled = "Enabled"; projection = "ProjectionPath"; remote = "RemoteName"; remotePath = "RemotePath"; library = "LibraryName";
        }
        var parent = profiles.Length == 0 ? root : profiles[0];
        if (Text(parent, remote) != binding.RemoteName || Text(parent, remotePath) != binding.RemotePath || Text(parent, library) != binding.LibraryName)
            throw new InvalidOperationException("Profile binding mismatch.");
        var currentEnabled = Text(parent, enabled);
        var currentPath = Text(parent, projection);
        var beforeEnabled = Bool(binding.PreviousEnabled); var afterEnabled = "true";
        var validBefore = currentEnabled == beforeEnabled && currentPath == binding.PreviousProjectionPath;
        var validAfter = currentEnabled == afterEnabled && currentPath == binding.LocalMountPath;
        if (!validBefore && !validAfter) throw new InvalidOperationException("Profile state mismatch.");
        var targetEnabled = promote ? afterEnabled : beforeEnabled;
        var targetPath = promote ? binding.LocalMountPath : binding.PreviousProjectionPath;
        var edits = new List<CloudMaintenanceXmlEdit> {
            new(prefix.Append(enabled).ToArray(), currentEnabled, targetEnabled),
            new(prefix.Append(projection).ToArray(), currentPath, targetPath),
        };
        // RuntimeSettings.ApplyCloudLibraries mirrors the first profile into the legacy fields.
        if (profiles.Length == 1)
        {
            if (Text(root, "CloudRemoteName") != binding.RemoteName || Text(root, "CloudRemotePath") != binding.RemotePath || Text(root, "CloudLibraryName") != binding.LibraryName
                || Text(root, "CloudProjectionEnabled") != currentEnabled || Text(root, "CloudProjectionPath") != currentPath)
                throw new InvalidOperationException("Legacy mirror mismatch.");
            edits.Add(new(["CloudProjectionEnabled"], currentEnabled, targetEnabled));
            edits.Add(new(["CloudProjectionPath"], currentPath, targetPath));
        }
        return CloudMaintenanceConfiguration.Prepare(bytes, hash, "PluginConfiguration", edits);
    }

    /// <summary>Build policy plan for a single exact path and an already-empty audio fetcher list.</summary>
    public static CloudMaintenanceXmlPlan PreparePolicy(byte[] bytes, string hash, string localPath, CloudLibraryScanPolicy previous, bool promote)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var root = Parse(bytes, "LibraryOptions");
        var paths = One(root, "PathInfos")!;
        var entries = paths.Elements().ToArray();
        if (entries.Length != 1 || entries[0].Name != "MediaPathInfo" || Text(entries[0], "Path") != localPath)
            throw new InvalidOperationException("Library path binding mismatch.");
        // Changing or creating TypeOptions needs a structurally-aware adapter; refuse it here.
        if (previous.AudioImageFetchers is null || previous.AudioImageFetchers.Length != 0)
            throw new InvalidOperationException("Unsupported audio fetcher policy.");
        var types = One(root, "TypeOptions")!;
        var audio = types.Elements().Where(e => e.Name == "TypeOptions" && Text(e, "Type") == "Audio").ToArray();
        if (audio.Length != 1 || One(audio[0], "ImageFetchers")!.Elements().Any())
            throw new InvalidOperationException("Unsupported audio fetcher structure.");
        var values = new (string, bool)[] {
            ("EnableRealtimeMonitor", previous.EnableRealtimeMonitor), ("EnableLUFSScan", previous.EnableLufsScan),
            ("EnableChapterImageExtraction", previous.EnableChapterImageExtraction), ("ExtractChapterImagesDuringLibraryScan", previous.ExtractChapterImagesDuringLibraryScan),
            ("EnableTrickplayImageExtraction", previous.EnableTrickplayImageExtraction), ("ExtractTrickplayImagesDuringLibraryScan", previous.ExtractTrickplayImagesDuringLibraryScan),
            ("SaveLocalMetadata", previous.SaveLocalMetadata), ("SaveSubtitlesWithMedia", previous.SaveSubtitlesWithMedia),
            ("SaveLyricsWithMedia", previous.SaveLyricsWithMedia), ("SaveTrickplayWithMedia", previous.SaveTrickplayWithMedia),
        };
        var edits = new List<CloudMaintenanceXmlEdit>();
        var allPrevious = true; var allCloud = true;
        foreach (var (name, previousValue) in values)
        {
            var node = One(root, name, required: false);
            if (node is null)
            {
                if (name is not ("SaveLyricsWithMedia" or "SaveTrickplayWithMedia") || previousValue)
                    throw new InvalidOperationException("Unsupported missing scan policy field.");
                continue;
            }
            var current = Text(root, name); var prior = Bool(previousValue);
            allPrevious &= current == prior; allCloud &= current == "false";
            edits.Add(new([name], current, promote ? "false" : prior));
        }
        if (!allPrevious && !allCloud) throw new InvalidOperationException("Scan policy mismatch.");
        return CloudMaintenanceConfiguration.Prepare(bytes, hash, "LibraryOptions", edits);
    }

    private static string Bool(bool value) => value ? "true" : "false";
    private static XElement Parse(byte[] bytes, string name)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Configuration too large.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
        return root is not null && root.Name == name ? root : throw new InvalidOperationException("Configuration root mismatch.");
    }
    private static XElement? One(XElement parent, string name, bool required = true)
    {
        var matches = parent.Elements().Where(x => x.Name.LocalName == name).Take(2).ToArray();
        if (matches.Length > 1 || (required && matches.Length != 1) || (matches.Length == 1 && matches[0].Name.NamespaceName.Length != 0))
            throw new InvalidOperationException("Ambiguous configuration field.");
        return matches.FirstOrDefault();
    }
    private static string Text(XElement parent, string name)
    {
        var node = One(parent, name)!;
        if (node.HasElements || node.Nodes().Any(x => x is not XText)) throw new InvalidOperationException("Invalid configuration field.");
        return node.Value;
    }
}

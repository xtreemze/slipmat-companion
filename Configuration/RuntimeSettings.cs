using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AudioGateway.Configuration;

/// <summary>
/// Resolves companion runtime storage without deployment-only assumptions.
/// </summary>
public static class RuntimeSettings
{
    public const string StoreRootEnvironmentVariable = "SLIPMAT_ARTIFACT_STORE_ROOT";
    public const string LegacyStoreRoot = "/store";

    public static string ResolveStoreRoot(PluginConfiguration config)
        => ResolveStoreRoot(
            config,
            Plugin.Instance?.HostApplicationPaths.DataPath,
            Environment.GetEnvironmentVariable(StoreRootEnvironmentVariable),
            Directory.Exists(LegacyStoreRoot));

    public static string ResolveStoreRoot(
        PluginConfiguration config,
        string? jellyfinDataPath,
        string? environmentRoot,
        bool legacyStoreAvailable)
    {
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            return Path.GetFullPath(environmentRoot.Trim());
        }

        var configuredRoot = config.StoreRoot?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredRoot) &&
            !string.Equals(configuredRoot, LegacyStoreRoot, StringComparison.Ordinal))
        {
            return Path.GetFullPath(configuredRoot);
        }

        if (legacyStoreAvailable)
        {
            return LegacyStoreRoot;
        }

        if (!string.IsNullOrWhiteSpace(jellyfinDataPath))
        {
            return Path.Combine(jellyfinDataPath, "audio-gateway");
        }

        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jellyfin.AudioGateway");
    }
    /// <summary>
    /// Resolves the local materialization directory for one remote Jottacloud folder.
    /// A configured absolute override wins. Fresh installs use a deterministic
    /// Jellyfin-managed directory so cloud setup does not require filesystem knowledge.
    /// </summary>
    public static string ResolveJottacloudProjectionPath(
        PluginConfiguration config,
        string remotePath)
        => ResolveJottacloudProjectionPath(
            config,
            remotePath,
            Plugin.Instance?.HostApplicationPaths.DataPath);

    public static string ResolveJottacloudProjectionPath(
        PluginConfiguration config,
        string remotePath,
        string? jellyfinDataPath)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        var configuredRoot = config.JottacloudProjectionPath?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var managedBase = !string.IsNullOrWhiteSpace(jellyfinDataPath)
            ? Path.Combine(jellyfinDataPath, "audio-gateway")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jellyfin.AudioGateway");

        var normalizedRemotePath = remotePath.Trim().Replace('\\', '/');
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRemotePath)))
            .ToLowerInvariant()[..16];

        return Path.Combine(
            managedBase,
            "cloud",
            "jottacloud",
            digest);
    }

}

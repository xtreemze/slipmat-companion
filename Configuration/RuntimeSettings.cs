using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Returns normalized cloud-library profiles. Existing single-projection
    /// configuration is projected as one stable legacy profile.
    /// </summary>
    public static IReadOnlyList<CloudLibraryProfile> GetCloudLibraries(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var configured = (config.CloudLibraries ?? Array.Empty<CloudLibraryProfile>())
            .Where(profile => profile is not null)
            .Select(NormalizeCloudLibrary)
            .ToArray();
        if (configured.Length > 0)
        {
            return configured;
        }

        if (!config.CloudProjectionEnabled
            && string.IsNullOrWhiteSpace(config.CloudRemoteName)
            && string.IsNullOrWhiteSpace(config.CloudRemotePath)
            && string.IsNullOrWhiteSpace(config.CloudProjectionPath))
        {
            return Array.Empty<CloudLibraryProfile>();
        }

        return
        [
            new CloudLibraryProfile
            {
                Id = "legacy",
                Enabled = config.CloudProjectionEnabled,
                RemoteName = config.CloudRemoteName ?? string.Empty,
                RemotePath = config.CloudRemotePath ?? string.Empty,
                ProjectionPath = config.CloudProjectionPath ?? string.Empty,
                CachePath = config.CloudCachePath ?? string.Empty,
                CacheMaxSizeGiB = config.CloudCacheMaxSizeGiB,
                CacheMaxAgeHours = config.CloudCacheMaxAgeHours,
                CacheMinFreeSpaceGiB = config.CloudCacheMinFreeSpaceGiB,
                LibraryName = config.CloudLibraryName ?? "Cloud Media",
                CollectionType = config.CloudCollectionType ?? "music",
                AutoCreateLibrary = config.CloudAutoCreateLibrary,
            },
        ];
    }

    /// <summary>
    /// Resolves the local read-only mount point for one configured rclone root.
    /// A configured absolute override wins. Fresh installs use a deterministic
    /// Jellyfin-managed mount directory keyed by remote name + remote path.
    /// </summary>
    public static string ResolveCloudProjectionPath(
        PluginConfiguration config,
        string remoteName,
        string remotePath)
        => ResolveCloudProjectionPath(
            config,
            remoteName,
            remotePath,
            Plugin.Instance?.HostApplicationPaths.DataPath);

    public static string ResolveCloudProjectionPath(
        PluginConfiguration config,
        string remoteName,
        string remotePath,
        string? jellyfinDataPath)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);

        var configuredRoot = config.CloudProjectionPath?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var managedBase = !string.IsNullOrWhiteSpace(jellyfinDataPath)
            ? Path.Combine(jellyfinDataPath, "audio-gateway")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jellyfin.AudioGateway");

        var digest = CloudIdentityDigest(remoteName, remotePath);

        return Path.Combine(
            managedBase,
            "cloud",
            "rclone",
            digest);
    }

    public static string ResolveCloudCachePath(
        PluginConfiguration config,
        string remoteName,
        string remotePath,
        string? jellyfinDataPath = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);

        var configuredRoot = config.CloudCachePath?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        var managedBase = !string.IsNullOrWhiteSpace(jellyfinDataPath)
            ? Path.Combine(jellyfinDataPath, "audio-gateway")
            : !string.IsNullOrWhiteSpace(Plugin.Instance?.HostApplicationPaths.DataPath)
                ? Path.Combine(Plugin.Instance.HostApplicationPaths.DataPath, "audio-gateway")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Jellyfin.AudioGateway");

        return Path.Combine(
            managedBase,
            "cloud",
            "rclone",
            "cache",
            CloudIdentityDigest(remoteName, remotePath));
    }

    public static bool IsCloudProjectionPath(
        PluginConfiguration config,
        string? itemPath,
        string? jellyfinDataPath = null)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        foreach (var profile in GetCloudLibraries(config))
        {
            if (!profile.Enabled)
            {
                continue;
            }

            try
            {
                string projectionRoot;
                if (!string.IsNullOrWhiteSpace(profile.ProjectionPath))
                {
                    projectionRoot = Path.GetFullPath(profile.ProjectionPath.Trim());
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(profile.RemoteName))
                    {
                        continue;
                    }

                    projectionRoot = ResolveCloudProjectionPath(
                        ProfileAsLegacyConfiguration(profile),
                        profile.RemoteName,
                        profile.RemotePath,
                        jellyfinDataPath ?? Plugin.Instance?.HostApplicationPaths.DataPath);
                }

                if (IsWithinPath(itemPath, projectionRoot))
                {
                    return true;
                }
            }
            catch
            {
                // A malformed profile must not make an unrelated item look local.
            }
        }

        return false;
    }

    private static bool IsWithinPath(string itemPath, string projectionRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectionRoot));
        var candidate = Path.GetFullPath(itemPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(candidate, root, comparison))
        {
            return true;
        }

        var prefix = string.Concat(root, Path.DirectorySeparatorChar);
        return candidate.StartsWith(prefix, comparison);
    }

    public static PluginConfiguration ProfileAsLegacyConfiguration(CloudLibraryProfile profile)
        => new()
        {
            CloudProjectionEnabled = profile.Enabled,
            CloudRemoteName = profile.RemoteName,
            CloudRemotePath = profile.RemotePath,
            CloudProjectionPath = profile.ProjectionPath,
            CloudCachePath = profile.CachePath,
            CloudCacheMaxSizeGiB = profile.CacheMaxSizeGiB,
            CloudCacheMaxAgeHours = profile.CacheMaxAgeHours,
            CloudCacheMinFreeSpaceGiB = profile.CacheMinFreeSpaceGiB,
            CloudLibraryName = profile.LibraryName,
            CloudCollectionType = profile.CollectionType,
            CloudAutoCreateLibrary = profile.AutoCreateLibrary,
        };

    private static CloudLibraryProfile NormalizeCloudLibrary(CloudLibraryProfile profile)
    {
        var remoteName = profile.RemoteName?.Trim() ?? string.Empty;
        var remotePath = profile.RemotePath?.Trim() ?? string.Empty;
        var libraryName = profile.LibraryName?.Trim() ?? string.Empty;
        var id = profile.Id?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            id = string.Concat(
                "profile-",
                CloudIdentityDigest(
                    remoteName,
                    string.Concat(remotePath, "\n", libraryName)));
        }

        return new CloudLibraryProfile
        {
            Id = id,
            Enabled = profile.Enabled,
            RemoteName = remoteName,
            RemotePath = remotePath,
            ProjectionPath = profile.ProjectionPath?.Trim() ?? string.Empty,
            CachePath = profile.CachePath?.Trim() ?? string.Empty,
            CacheMaxSizeGiB = profile.CacheMaxSizeGiB,
            CacheMaxAgeHours = profile.CacheMaxAgeHours,
            CacheMinFreeSpaceGiB = profile.CacheMinFreeSpaceGiB,
            LibraryName = string.IsNullOrWhiteSpace(libraryName) ? "Cloud Media" : libraryName,
            CollectionType = string.IsNullOrWhiteSpace(profile.CollectionType)
                ? "music"
                : profile.CollectionType.Trim(),
            AutoCreateLibrary = profile.AutoCreateLibrary,
        };
    }

    private static string CloudIdentityDigest(string remoteName, string remotePath)
    {
        var normalizedIdentity = string.Concat(
            remoteName.Trim().TrimEnd(':').ToLowerInvariant(),
            "\n",
            remotePath.Trim().Replace('\\', '/'));

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalizedIdentity)))
            .ToLowerInvariant()[..16];
    }

}

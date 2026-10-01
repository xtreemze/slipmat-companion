using System;
using System.IO;

namespace Jellyfin.Plugin.AudioGateway.Configuration;

/// <summary>
/// Resolves optional companion runtime settings without making deployment-only
/// assumptions part of the plugin contract.
/// </summary>
public static class RuntimeSettings
{
    public const string AnalyzerUrlEnvironmentVariable = "SLIPMAT_ANALYZER_URL";
    public const string StoreRootEnvironmentVariable = "SLIPMAT_ARTIFACT_STORE_ROOT";
    public const string LegacyStoreRoot = "/store";

    /// <summary>
    /// Resolves the explicitly enabled analyzer endpoint. Environment
    /// configuration wins over persisted plugin configuration.
    /// </summary>
    public static AnalyzerEndpoint ResolveAnalyzerEndpoint(PluginConfiguration config)
        => ResolveAnalyzerEndpoint(
            config,
            Environment.GetEnvironmentVariable(AnalyzerUrlEnvironmentVariable));

    /// <summary>
    /// Pure analyzer resolver used by tests and diagnostics.
    /// </summary>
    public static AnalyzerEndpoint ResolveAnalyzerEndpoint(
        PluginConfiguration config,
        string? environmentUrl)
    {
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            return new AnalyzerEndpoint(environmentUrl.Trim(), true);
        }

        if (!config.AnalyzerEnabled || string.IsNullOrWhiteSpace(config.AnalyzerBaseUrl))
        {
            return new AnalyzerEndpoint(null, false);
        }

        return new AnalyzerEndpoint(config.AnalyzerBaseUrl.Trim(), true);
    }

    /// <summary>
    /// Resolves companion storage. Fresh catalog installs use Jellyfin's own
    /// writable data directory. The historical /store path is retained only
    /// when it actually exists, preserving the original Compose deployment.
    /// </summary>
    public static string ResolveStoreRoot(PluginConfiguration config)
        => ResolveStoreRoot(
            config,
            Plugin.Instance?.ApplicationPaths.DataPath,
            Environment.GetEnvironmentVariable(StoreRootEnvironmentVariable),
            Directory.Exists(LegacyStoreRoot));

    /// <summary>
    /// Pure store-root resolver used by tests.
    /// </summary>
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
}

/// <summary>
/// Effective analyzer endpoint and whether failure should be surfaced as
/// deployment degradation.
/// </summary>
public sealed record AnalyzerEndpoint(string? BaseUrl, bool ExplicitlyConfigured);

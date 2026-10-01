using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

public static class StreamripCliService
{
    private static readonly string[] SupportedSources = ["qobuz", "tidal", "deezer", "soundcloud"];
    private static readonly string[] SupportedMediaTypes = ["album", "track", "artist", "playlist", "label"];

    public static StreamripServiceStatus GetStatus()
    {
        var commandPath = FindCommandPath();
        var configPath = ResolveConfigPath();
        var configuredSources = ReadConfiguredSources(configPath);

        if (commandPath is null)
        {
            return new StreamripServiceStatus(
                Available: false,
                Command: null,
                Version: null,
                ConfigPath: configPath,
                ConfiguredSources: configuredSources,
                Notes: new[]
                {
                    "streamrip CLI was not found on PATH.",
                    "Install the latest upstream release and configure credentials before Atlas attempts provider search.",
                });
        }

        return new StreamripServiceStatus(
            Available: true,
            Command: commandPath,
            Version: TryReadVersion(commandPath),
            ConfigPath: configPath,
            ConfiguredSources: configuredSources,
            Notes: new[]
            {
                "Atlas uses streamrip as the provider-search integration layer instead of querying streaming services directly.",
                "Provider credentials are expected to live in the streamrip config.",
            });
    }

    public static StreamripSearchResponse Search(
        StreamripSearchRequest request,
        string? commandPathOverride = null,
        string? configPathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = (request.Source ?? string.Empty).Trim().ToLowerInvariant();
        var mediaType = (request.MediaType ?? string.Empty).Trim().ToLowerInvariant();
        var query = (request.Query ?? string.Empty).Trim();
        var limit = Math.Clamp(request.Limit, 1, 100);

        if (!SupportedSources.Contains(source, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported streamrip source '{request.Source}'.", nameof(request));
        }

        if (!SupportedMediaTypes.Contains(mediaType, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported streamrip media type '{request.MediaType}'.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("streamrip search query cannot be empty.", nameof(request));
        }

        var commandPath = commandPathOverride ?? FindCommandPath();
        var configPath = configPathOverride ?? ResolveConfigPath();
        var configuredSources = ReadConfiguredSources(configPath);

        if (commandPath is null)
        {
            return new StreamripSearchResponse(
                Available: false,
                Source: source,
                MediaType: mediaType,
                Query: query,
                Limit: limit,
                ConfiguredSources: configuredSources,
                Results: Array.Empty<StreamripSearchResult>(),
                Error: "streamrip CLI was not found on PATH.",
                Notes:
                [
                    "Install the latest upstream streamrip release before Atlas attempts provider search.",
                ]);
        }

        if (configuredSources.Count > 0 && !configuredSources.Contains(source, StringComparer.Ordinal))
        {
            return new StreamripSearchResponse(
                Available: true,
                Source: source,
                MediaType: mediaType,
                Query: query,
                Limit: limit,
                ConfiguredSources: configuredSources,
                Results: Array.Empty<StreamripSearchResult>(),
                Error: $"The streamrip config does not appear to have credentials for '{source}'.",
                Notes:
                [
                    "Atlas delegates provider search to streamrip and expects provider credentials to be configured there.",
                ]);
        }

        var tempOutputPath = Path.Combine(
            Path.GetTempPath(),
            $"atlas-streamrip-search-{Guid.NewGuid():N}.json");

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = commandPath;
            process.StartInfo.ArgumentList.Add("search");
            process.StartInfo.ArgumentList.Add("--output-file");
            process.StartInfo.ArgumentList.Add(tempOutputPath);
            process.StartInfo.ArgumentList.Add("--num-results");
            process.StartInfo.ArgumentList.Add(limit.ToString());
            process.StartInfo.ArgumentList.Add(source);
            process.StartInfo.ArgumentList.Add(mediaType);
            process.StartInfo.ArgumentList.Add(query);
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            if (!process.Start())
            {
                return SearchFailure(source, mediaType, query, limit, configuredSources, "Failed to start streamrip.");
            }

            if (!process.WaitForExit(15000))
            {
                TryKill(process);
                return SearchFailure(source, mediaType, query, limit, configuredSources, "streamrip search timed out.");
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();

            if (process.ExitCode != 0)
            {
                var errorOutput = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return SearchFailure(
                    source,
                    mediaType,
                    query,
                    limit,
                    configuredSources,
                    string.IsNullOrWhiteSpace(errorOutput)
                        ? $"streamrip search exited with code {process.ExitCode}."
                        : errorOutput);
            }

            if (!File.Exists(tempOutputPath))
            {
                return SearchFailure(
                    source,
                    mediaType,
                    query,
                    limit,
                    configuredSources,
                    "streamrip search completed without producing a results file.");
            }

            return new StreamripSearchResponse(
                Available: true,
                Source: source,
                MediaType: mediaType,
                Query: query,
                Limit: limit,
                ConfiguredSources: configuredSources,
                Results: ParseSearchResults(tempOutputPath),
                Error: null,
                Notes:
                [
                    "Search results were produced by streamrip in JSON output-file mode.",
                ]);
        }
        catch (Exception ex)
        {
            return SearchFailure(source, mediaType, query, limit, configuredSources, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempOutputPath))
                {
                    File.Delete(tempOutputPath);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    internal static string? FindCommandPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "rip.exe", "streamrip.exe" }
            : new[] { "rip", "streamrip" };

        foreach (var directory in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static string? ResolveConfigPath()
    {
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Combine(xdgConfigHome, "streamrip", "config.toml");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                home,
                "Library",
                "Application Support",
                "streamrip",
                "config.toml");
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "streamrip", "config.toml");
        }

        return Path.Combine(home, ".config", "streamrip", "config.toml");
    }

    internal static string? TryReadVersion(string commandPath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = commandPath;
            process.StartInfo.ArgumentList.Add("--version");
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            if (!process.Start())
            {
                return null;
            }

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill failures; the version read is already best-effort.
                }

                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ReadConfiguredSources(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var text = File.ReadAllText(configPath);
            var configuredSources = new List<string>();

            foreach (var provider in new[] { "qobuz", "tidal", "deezer", "soundcloud" })
            {
                if (HasConfiguredSection(text, provider))
                {
                    configuredSources.Add(provider);
                }
            }

            return configuredSources;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<StreamripSearchResult> ParseSearchResults(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            return Array.Empty<StreamripSearchResult>();
        }

        using var stream = File.OpenRead(outputPath);
        using var document = JsonDocument.Parse(stream);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<StreamripSearchResult>();
        }

        var results = new List<StreamripSearchResult>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            results.Add(
                new StreamripSearchResult(
                    Source: element.TryGetProperty("source", out var source) ? source.GetString() ?? string.Empty : string.Empty,
                    MediaType: element.TryGetProperty("media_type", out var mediaType) ? mediaType.GetString() ?? string.Empty : string.Empty,
                    Id: element.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Description: element.TryGetProperty("desc", out var description) ? description.GetString() ?? string.Empty : string.Empty));
        }

        return results;
    }

    private static bool HasConfiguredSection(string text, string sectionName)
    {
        var sectionHeader = $"[{sectionName}]";
        var startIndex = text.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            return false;
        }

        var nextSectionIndex = text.IndexOf("\n[", startIndex + sectionHeader.Length, StringComparison.Ordinal);
        var sectionText = nextSectionIndex >= 0
            ? text[startIndex..nextSectionIndex]
            : text[startIndex..];

        return sectionText
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line =>
                !line.StartsWith('#') &&
                (line.StartsWith("email", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("username", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("password", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("access_token", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("app_id", StringComparison.OrdinalIgnoreCase)));
    }

    private static StreamripSearchResponse SearchFailure(
        string source,
        string mediaType,
        string query,
        int limit,
        IReadOnlyList<string> configuredSources,
        string error)
        => new(
            Available: true,
            Source: source,
            MediaType: mediaType,
            Query: query,
            Limit: limit,
            ConfiguredSources: configuredSources,
            Results: Array.Empty<StreamripSearchResult>(),
            Error: error,
            Notes:
            [
                "Atlas uses streamrip as a search bridge only here; it is not calling provider APIs directly.",
            ]);

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore best-effort termination failures.
        }
    }
}

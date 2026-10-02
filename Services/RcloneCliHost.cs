using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

public enum RcloneCliHealth
{
    Unavailable,
    Ready,
    Degraded,
    TimedOut,
}

public sealed record RcloneCommandResult(
    bool StartSucceeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated);

public sealed record RcloneCliStatus(
    RcloneCliHealth Health,
    string? Version,
    string Code,
    IReadOnlyList<RcloneRemoteDescriptor> Remotes);

public sealed record RcloneDirectoryListing(
    bool Success,
    IReadOnlyList<CloudBrowserEntry> Entries,
    string? ErrorCode,
    bool OutputTruncated);

public interface IRcloneProcessRunner
{
    Task<RcloneCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class RcloneProcessRunner : IRcloneProcessRunner
{
    internal const int MaxCapturedCharacters = 2 * 1024 * 1024;

    public async Task<RcloneCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        using var process = new Process();
        process.StartInfo.FileName = executable;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return StartFailure();
            }
        }
        catch
        {
            return StartFailure();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            MaxCapturedCharacters,
            timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(
            process.StandardError,
            MaxCapturedCharacters,
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return new RcloneCommandResult(
                true,
                null,
                string.Empty,
                string.Empty,
                true,
                false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveReadersAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new RcloneCommandResult(
            true,
            process.ExitCode,
            stdout.Text,
            stderr.Text,
            false,
            stdout.Truncated || stderr.Truncated);
    }

    private static RcloneCommandResult StartFailure()
        => new(false, null, string.Empty, string.Empty, false, false);

    private static async Task<BoundedText> ReadBoundedAsync(
        StreamReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var text = new StringBuilder(Math.Min(maxCharacters, 4096));
        var truncated = false;

        while (true)
        {
            var count = await reader
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var remaining = maxCharacters - text.Length;
            if (remaining > 0)
            {
                text.Append(buffer, 0, Math.Min(remaining, count));
            }

            if (count > remaining)
            {
                truncated = true;
            }
        }

        return new BoundedText(text.ToString(), truncated);
    }

    private static async Task ObserveReadersAsync(
        Task<BoundedText> stdoutTask,
        Task<BoundedText> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private sealed record BoundedText(string Text, bool Truncated);
}

/// <summary>
/// Narrow command adapter over an operator-installed/configured rclone.
/// It never reads rclone.conf, dumps configuration, starts RC, or accepts credentials.
/// </summary>
public sealed partial class RcloneCliHost
{
    public const string ExecutableEnvironmentVariable = "SLIPMAT_RCLONE";

    private const int MaxRemotePathLength = 4096;
    private const int MaxRemoteCount = 512;
    private const int MaxListingEntries = 2000;
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CopyCommandTimeout = TimeSpan.FromHours(12);

    private readonly string _executable;
    private readonly IRcloneProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public RcloneCliHost(
        string? executable = null,
        IRcloneProcessRunner? runner = null,
        TimeSpan? timeout = null)
    {
        var configuredExecutable = executable;
        if (string.IsNullOrWhiteSpace(configuredExecutable))
        {
            configuredExecutable = Environment.GetEnvironmentVariable(
                ExecutableEnvironmentVariable);
        }

        _executable = string.IsNullOrWhiteSpace(configuredExecutable)
            ? "rclone"
            : configuredExecutable.Trim();
        _runner = runner ?? new RcloneProcessRunner();
        _timeout = timeout ?? DefaultCommandTimeout;

        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }
    }

    public async Task<RcloneCliStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var versionResult = await RunAsync(["version"], _timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!versionResult.StartSucceeded)
        {
            return Status(RcloneCliHealth.Unavailable, null, "rclone-unavailable");
        }

        if (versionResult.TimedOut)
        {
            return Status(RcloneCliHealth.TimedOut, null, "version-timeout");
        }

        if (versionResult.ExitCode != 0)
        {
            return Status(RcloneCliHealth.Degraded, null, "version-failed");
        }

        var version = TryExtractVersion(versionResult.StandardOutput);
        if (version is null)
        {
            return Status(RcloneCliHealth.Degraded, null, "version-unrecognized");
        }

        var remotesResult = await RunAsync(
                ["listremotes", "--long", "--json"],
                _timeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (!remotesResult.StartSucceeded)
        {
            return Status(RcloneCliHealth.Unavailable, version, "rclone-unavailable");
        }

        if (remotesResult.TimedOut)
        {
            return Status(RcloneCliHealth.TimedOut, version, "remotes-timeout");
        }

        if (remotesResult.ExitCode != 0)
        {
            return Status(RcloneCliHealth.Degraded, version, "remotes-unavailable");
        }

        if (!TryParseRemotes(remotesResult.StandardOutput, out var remotes))
        {
            return Status(RcloneCliHealth.Degraded, version, "remotes-unrecognized");
        }

        return remotes.Count == 0
            ? Status(RcloneCliHealth.Degraded, version, "no-remotes-configured", remotes)
            : Status(RcloneCliHealth.Ready, version, "ready", remotes);
    }

    public async Task<RcloneDirectoryListing> ListAsync(
        string remoteName,
        string? remotePath,
        CancellationToken cancellationToken = default)
    {
        var remote = NormalizeRemoteName(remoteName);
        var path = NormalizeRemotePath(remotePath);
        var source = BuildRemoteSpec(remote, path);

        var result = await RunAsync(
                ["lsjson", source, "--no-modtime", "--no-mimetype"],
                _timeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.StartSucceeded)
        {
            return ListingFailure("rclone-unavailable", result.OutputTruncated);
        }

        if (result.TimedOut)
        {
            return ListingFailure("browse-timeout", result.OutputTruncated);
        }

        if (result.ExitCode != 0)
        {
            return ListingFailure("browse-failed", result.OutputTruncated);
        }

        if (!TryParseListing(result.StandardOutput, out var entries, out var truncated))
        {
            return ListingFailure("browse-unrecognized", result.OutputTruncated);
        }

        return new RcloneDirectoryListing(
            true,
            entries,
            null,
            result.OutputTruncated || truncated);
    }

    public async Task<RcloneCommandResult> CreateDirectoryAsync(
        string remoteName,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var remote = NormalizeRemoteName(remoteName);
        var path = NormalizeRemotePath(remotePath);
        if (path.Length == 0)
        {
            throw new ArgumentException("Remote directory path is required.", nameof(remotePath));
        }

        return await RunAsync(
                ["mkdir", BuildRemoteSpec(remote, path)],
                _timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RcloneCommandResult> CopyToLocalAsync(
        string remoteName,
        string? remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        var remote = NormalizeRemoteName(remoteName);
        var path = NormalizeRemotePath(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        if (!Path.IsPathFullyQualified(localPath))
        {
            throw new ArgumentException("Local projection path must be absolute.", nameof(localPath));
        }

        var fullLocalPath = Path.GetFullPath(localPath);
        return await RunAsync(
                [
                    "copy",
                    BuildRemoteSpec(remote, path),
                    fullLocalPath,
                    "--create-empty-src-dirs",
                    "--stats=0",
                    "--log-level=ERROR",
                ],
                CopyCommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static string NormalizeRemoteName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim().TrimEnd(':');
        if (normalized.Length == 0
            || normalized.Length > 256
            || normalized.IndexOfAny(['\0', '\r', '\n', ':']) >= 0
            || normalized.StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid rclone remote name.", nameof(value));
        }

        return normalized;
    }

    public static string NormalizeRemotePath(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace('\\', '/')
            .Trim('/');

        if (normalized.Length > MaxRemotePathLength
            || normalized.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Invalid rclone remote path.", nameof(value));
        }

        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Relative path segments are not allowed.", nameof(value));
        }

        return string.Join('/', segments);
    }

    public static string BuildRemoteSpec(string remoteName, string? remotePath)
    {
        var remote = NormalizeRemoteName(remoteName);
        var path = NormalizeRemotePath(remotePath);
        return path.Length == 0
            ? string.Concat(remote, ":")
            : string.Concat(remote, ":", path);
    }

    internal static bool TryParseRemotes(
        string json,
        out IReadOnlyList<RcloneRemoteDescriptor> remotes)
    {
        remotes = Array.Empty<RcloneRemoteDescriptor>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var values = new List<RcloneRemoteDescriptor>();

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(root, "remotes", out var remotesElement)
                && remotesElement.ValueKind == JsonValueKind.Array)
            {
                array = remotesElement;
            }
            else
            {
                return false;
            }

            foreach (var element in array.EnumerateArray().Take(MaxRemoteCount))
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var name = element.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        values.Add(new RcloneRemoteDescriptor(
                            NormalizeRemoteName(name),
                            null,
                            null,
                            null));
                    }

                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                values.Add(new RcloneRemoteDescriptor(
                    NormalizeRemoteName(name),
                    GetString(element, "type"),
                    GetString(element, "description"),
                    GetString(element, "source")));
            }

            remotes = values
                .OrderBy(remote => remote.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryParseListing(
        string json,
        out IReadOnlyList<CloudBrowserEntry> entries,
        out bool truncated)
    {
        entries = Array.Empty<CloudBrowserEntry>();
        truncated = false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var values = new List<CloudBrowserEntry>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (values.Count >= MaxListingEntries)
                {
                    truncated = true;
                    break;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(element, "Name") ?? GetString(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var path = GetString(element, "Path")
                    ?? GetString(element, "path")
                    ?? name;
                var isDirectory = GetBoolean(element, "IsDir")
                    ?? GetBoolean(element, "isDir")
                    ?? false;
                var size = GetInt64(element, "Size")
                    ?? GetInt64(element, "size")
                    ?? -1;

                values.Add(new CloudBrowserEntry(
                    name,
                    path,
                    isDirectory,
                    size));
            }

            entries = values
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Task<RcloneCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => _runner.RunAsync(_executable, arguments, timeout, cancellationToken);

    private static RcloneCliStatus Status(
        RcloneCliHealth health,
        string? version,
        string code,
        IReadOnlyList<RcloneRemoteDescriptor>? remotes = null)
        => new(
            health,
            version,
            code,
            remotes ?? Array.Empty<RcloneRemoteDescriptor>());

    private static RcloneDirectoryListing ListingFailure(
        string code,
        bool truncated)
        => new(false, Array.Empty<CloudBrowserEntry>(), code, truncated);

    private static string? TryExtractVersion(string output)
    {
        var match = VersionRegex().Match(output);
        return match.Success ? match.Groups["version"].Value.Trim() : null;
    }

    private static string? GetString(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool? GetBoolean(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

    private static long? GetInt64(JsonElement element, string name)
        => TryGetPropertyIgnoreCase(element, name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
                ? parsed
                : null;

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    [GeneratedRegex(
        @"(?m)^rclone\s+v(?<version>[^\r\n\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}

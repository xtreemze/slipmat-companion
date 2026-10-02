using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Health state reported by an operator-installed Jottacloud CLI/daemon.
/// This is execution-adapter health only; it is never canonical playback state.
/// </summary>
public enum JottacloudCliHealth
{
    Unavailable,
    DaemonUnavailable,
    AuthenticationRequired,
    TimedOut,
    Ready,
    Degraded,
}

/// <summary>
/// Bounded result from one direct jotta-cli invocation.
/// </summary>
public sealed record JottacloudCliCommandResult(
    bool StartSucceeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated);

/// <summary>
/// Sanitized status projection for the companion.
/// Raw CLI output is deliberately not retained here.
/// </summary>
public sealed record JottacloudCliStatus(
    JottacloudCliHealth Health,
    string? CliVersion,
    string Code,
    string? AccountFingerprint);

/// <summary>
/// Read-only remote listing returned by jotta-cli.
/// </summary>
public sealed record JottacloudCliDirectoryListing(
    bool Success,
    IReadOnlyList<string> Lines,
    string? ErrorCode,
    bool OutputTruncated);

/// <summary>
/// State of the daemon-managed Jottacloud download queue.
/// </summary>
public sealed record JottacloudCliDownloadQueue(
    bool Known,
    int QueueEntryCount,
    string Code)
{
    /// <summary>
    /// Gets a value indicating whether jotta-cli reports no outstanding queue entries.
    /// Jottacloud retains completed downloads with failed files in this list, so any
    /// entry must block automatic scanning until the operator retries or clears it.
    /// </summary>
    public bool IsClear => Known && QueueEntryCount == 0;
}

/// <summary>
/// Process boundary used by <see cref="JottacloudCliHost"/>.
/// Tests can supply a deterministic fake without executing a real CLI.
/// </summary>
public interface IJottacloudCliProcessRunner
{
    Task<JottacloudCliCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes jotta-cli directly without a shell and caps captured process output.
/// </summary>
public sealed class JottacloudCliProcessRunner : IJottacloudCliProcessRunner
{
    internal const int MaxCapturedCharacters = 128 * 1024;

    public async Task<JottacloudCliCommandResult> RunAsync(
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
            await ObserveReaderTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            return new JottacloudCliCommandResult(
                StartSucceeded: true,
                ExitCode: null,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                TimedOut: true,
                OutputTruncated: false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveReaderTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new JottacloudCliCommandResult(
            StartSucceeded: true,
            ExitCode: process.ExitCode,
            StandardOutput: stdout.Text,
            StandardError: stderr.Text,
            TimedOut: false,
            OutputTruncated: stdout.Truncated || stderr.Truncated);
    }

    private static JottacloudCliCommandResult StartFailure()
        => new(
            StartSucceeded: false,
            ExitCode: null,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            TimedOut: false,
            OutputTruncated: false);

    private static async Task<BoundedText> ReadBoundedAsync(
        System.IO.StreamReader reader,
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

    private static async Task ObserveReaderTasksAsync(
        Task<BoundedText> stdoutTask,
        Task<BoundedText> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when command timeout/cancellation closes the bounded reads.
        }
        catch (ObjectDisposedException)
        {
            // Process teardown may close redirected streams before the read completes.
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
            // Best effort only. The timed-out/cancelled operation already fails closed.
        }
    }

    private sealed record BoundedText(string Text, bool Truncated);
}

/// <summary>
/// Narrow adapter over an operator-installed, operator-authenticated Jottacloud daemon.
/// It never performs login or receives Jottacloud credentials.
/// </summary>
public sealed partial class JottacloudCliHost
{
    public const string SupportedCliVersion = "0.17.176206";
    public const string ExecutableEnvironmentVariable = "SLIPMAT_JOTTACLOUD_CLI";

    private const int MaxRemotePathLength = 4096;
    private const int MaxLocalPathLength = 4096;
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ManagedDownloadCommandTimeout = TimeSpan.FromMinutes(5);

    private readonly string _executable;
    private readonly IJottacloudCliProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public JottacloudCliHost(
        string? executable = null,
        IJottacloudCliProcessRunner? runner = null,
        TimeSpan? timeout = null)
    {
        var configuredExecutable = executable;
        if (string.IsNullOrWhiteSpace(configuredExecutable))
        {
            configuredExecutable = Environment.GetEnvironmentVariable(
                ExecutableEnvironmentVariable);
        }

        _executable = string.IsNullOrWhiteSpace(configuredExecutable)
            ? "jotta-cli"
            : configuredExecutable.Trim();
        _runner = runner ?? new JottacloudCliProcessRunner();
        _timeout = timeout ?? DefaultCommandTimeout;

        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }
    }

    /// <summary>
    /// Probes the local CLI/daemon and returns only sanitized health facts.
    /// </summary>
    public async Task<JottacloudCliStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var versionResult = await RunAsync(["version"], cancellationToken).ConfigureAwait(false);
        if (!versionResult.StartSucceeded)
        {
            return Status(JottacloudCliHealth.Unavailable, null, "cli-unavailable");
        }

        if (versionResult.TimedOut)
        {
            return Status(JottacloudCliHealth.TimedOut, null, "version-timeout");
        }

        var cliVersion = TryExtractCliVersion(versionResult);

        var statusResult = await RunAsync(["status", "--json"], cancellationToken).ConfigureAwait(false);
        if (!statusResult.StartSucceeded)
        {
            return Status(JottacloudCliHealth.Unavailable, cliVersion, "cli-unavailable");
        }

        if (statusResult.TimedOut)
        {
            return Status(JottacloudCliHealth.TimedOut, cliVersion, "status-timeout");
        }

        var combined = CombineOutput(statusResult);
        if (ContainsAuthenticationFailure(combined))
        {
            return Status(JottacloudCliHealth.AuthenticationRequired, cliVersion, "authentication-required");
        }

        if (ContainsDaemonFailure(combined))
        {
            return Status(JottacloudCliHealth.DaemonUnavailable, cliVersion, "daemon-unavailable");
        }

        if (statusResult.ExitCode != 0)
        {
            return Status(JottacloudCliHealth.Degraded, cliVersion, "status-failed");
        }

        if (cliVersion is null)
        {
            return Status(JottacloudCliHealth.Degraded, null, "version-unrecognized");
        }

        if (!string.Equals(cliVersion, SupportedCliVersion, StringComparison.Ordinal))
        {
            return Status(JottacloudCliHealth.Degraded, cliVersion, "unsupported-cli-version");
        }

        // The human status surface documents the Account value. Read it only to
        // derive a one-way fingerprint used to bind local projection state to the
        // currently authenticated account; never expose or persist the raw value.
        var identityResult = await RunAsync(["status"], cancellationToken).ConfigureAwait(false);
        if (!identityResult.StartSucceeded)
        {
            return Status(JottacloudCliHealth.Unavailable, cliVersion, "cli-unavailable");
        }

        if (identityResult.TimedOut)
        {
            return Status(JottacloudCliHealth.TimedOut, cliVersion, "identity-timeout");
        }

        var identityCombined = CombineOutput(identityResult);
        if (ContainsAuthenticationFailure(identityCombined))
        {
            return Status(JottacloudCliHealth.AuthenticationRequired, cliVersion, "authentication-required");
        }

        if (ContainsDaemonFailure(identityCombined))
        {
            return Status(JottacloudCliHealth.DaemonUnavailable, cliVersion, "daemon-unavailable");
        }

        if (identityResult.ExitCode != 0)
        {
            return Status(JottacloudCliHealth.Degraded, cliVersion, "identity-status-failed");
        }

        var accountFingerprint = TryExtractAccountFingerprint(identityResult.StandardOutput);
        return accountFingerprint is null
            ? Status(JottacloudCliHealth.Degraded, cliVersion, "account-identity-unavailable")
            : Status(JottacloudCliHealth.Ready, cliVersion, "ready", accountFingerprint);
    }

    /// <summary>
    /// Lists one remote Jottacloud path without invoking a shell.
    /// </summary>
    public async Task<JottacloudCliDirectoryListing> ListAsync(
        string? remotePath,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "ls" };
        if (includeDetails)
        {
            arguments.Add("-a");
        }

        if (!string.IsNullOrWhiteSpace(remotePath))
        {
            ValidateRemotePath(remotePath);
            arguments.Add(remotePath);
        }

        var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.StartSucceeded)
        {
            return FailureListing("cli-unavailable", result.OutputTruncated);
        }

        if (result.TimedOut)
        {
            return FailureListing("list-timeout", result.OutputTruncated);
        }

        var combined = CombineOutput(result);
        if (ContainsAuthenticationFailure(combined))
        {
            return FailureListing("authentication-required", result.OutputTruncated);
        }

        if (ContainsDaemonFailure(combined))
        {
            return FailureListing("daemon-unavailable", result.OutputTruncated);
        }

        if (result.ExitCode != 0)
        {
            return FailureListing("list-failed", result.OutputTruncated);
        }

        var lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2000)
            .ToArray();

        return new JottacloudCliDirectoryListing(
            Success: true,
            Lines: lines,
            ErrorCode: null,
            OutputTruncated: result.OutputTruncated || lines.Length == 2000);
    }

    /// <summary>
    /// Reads the daemon download queue through the JSON list surface introduced in
    /// the 0.17 CLI line. Unknown JSON shapes fail closed.
    /// </summary>
    public async Task<JottacloudCliDownloadQueue> GetDownloadQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["list", "downloads", "--json"], cancellationToken).ConfigureAwait(false);
        if (!result.StartSucceeded)
        {
            return new JottacloudCliDownloadQueue(false, 0, "cli-unavailable");
        }

        if (result.TimedOut)
        {
            return new JottacloudCliDownloadQueue(false, 0, "downloads-timeout");
        }

        var combined = CombineOutput(result);
        if (ContainsAuthenticationFailure(combined))
        {
            return new JottacloudCliDownloadQueue(false, 0, "authentication-required");
        }

        if (ContainsDaemonFailure(combined))
        {
            return new JottacloudCliDownloadQueue(false, 0, "daemon-unavailable");
        }

        if (result.ExitCode != 0 || result.OutputTruncated)
        {
            return new JottacloudCliDownloadQueue(false, 0, "downloads-unavailable");
        }

        return TryParseDownloadQueueEntryCount(result.StandardOutput, out var count)
            ? new JottacloudCliDownloadQueue(
                true,
                count,
                count == 0 ? "queue-clear" : "queue-entries-present")
            : new JottacloudCliDownloadQueue(false, 0, "downloads-unrecognized");
    }

    /// <summary>
    /// Starts a safe merge refresh of one remote folder into a dedicated local
    /// projection directory. The transfer itself continues under jottad.
    /// </summary>
    public Task<JottacloudCliCommandResult> StartManagedDownloadAsync(
        string remotePath,
        string localDestination,
        CancellationToken cancellationToken = default)
    {
        ValidateRemotePath(remotePath);
        ValidateLocalPath(localDestination);

        return _runner.RunAsync(
            _executable,
            [
                "download",
                remotePath,
                localDestination,
                "--merge",
                "--mergemode=metadata",
            ],
            ManagedDownloadCommandTimeout,
            cancellationToken);
    }

    public static bool TryParseDownloadQueueEntryCount(string json, out int count)
    {
        count = 0;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                count = root.GetArrayLength();
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!property.Name.Equals("downloads", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                count = property.Value.GetArrayLength();
                return true;
            }

            // Some CLI builds may serialize one download object directly.
            if (root.EnumerateObject().Any(property =>
                    property.Name.Contains("download", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number))
            {
                count = 1;
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Task<JottacloudCliCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => _runner.RunAsync(_executable, arguments, _timeout, cancellationToken);

    private static JottacloudCliStatus Status(
        JottacloudCliHealth health,
        string? version,
        string code,
        string? accountFingerprint = null)
        => new(health, version, code, accountFingerprint);

    private static JottacloudCliDirectoryListing FailureListing(
        string errorCode,
        bool outputTruncated)
        => new(
            Success: false,
            Lines: Array.Empty<string>(),
            ErrorCode: errorCode,
            OutputTruncated: outputTruncated);

    private static void ValidateRemotePath(string remotePath)
    {
        ValidatePathData(remotePath, MaxRemotePathLength, nameof(remotePath));

        if (remotePath.StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Remote path must not begin with an option prefix.",
                nameof(remotePath));
        }
    }

    private static void ValidateLocalPath(string localPath)
    {
        ValidatePathData(localPath, MaxLocalPathLength, nameof(localPath));
    }

    private static void ValidatePathData(string value, int maxLength, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Path must not exceed {0} characters.",
                    maxLength));
        }

        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                "Path contains a forbidden control character.",
                paramName);
        }
    }

    private static string CombineOutput(JottacloudCliCommandResult result)
        => string.Concat(result.StandardOutput, "\n", result.StandardError);

    private static bool ContainsAuthenticationFailure(string text)
        => text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("stale token", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login required", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsDaemonFailure(string text)
        => text.Contains("could not connect to jottad", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is it running on", StringComparison.OrdinalIgnoreCase)
            || text.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase)
                && text.Contains("socket", StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractAccountFingerprint(string statusOutput)
    {
        var match = AccountRegex().Match(statusOutput);
        if (!match.Success)
        {
            return null;
        }

        var account = match.Groups["account"].Value.Trim().ToLowerInvariant();
        if (account.Length == 0)
        {
            return null;
        }

        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)))
            .ToLowerInvariant();
    }

    private static string? TryExtractCliVersion(JottacloudCliCommandResult result)
    {
        var match = CliVersionRegex().Match(CombineOutput(result));
        return match.Success ? match.Groups["version"].Value.Trim() : null;
    }

    [GeneratedRegex(
        @"(?m)^\s*Account\s*:\s*(?<account>[^\r\n]+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccountRegex();

    [GeneratedRegex(
        @"jotta-cli(?:\s+version)?\s*:?[ \t]*(?<version>[0-9]+(?:\.[0-9]+){1,2}(?:[-+][^\r\n\s]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliVersionRegex();
}

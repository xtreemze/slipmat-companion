using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Slipmat.MaintenanceRunner;

/// <summary>Verifies the installed guard against the live, unmasked systemd unit without starting it.</summary>
internal static class SourcePathRepairEffectiveStartupHook
{
    private const string Command = "source-repair-startup";
    private const string HookMismatch = "source-repair-effective-hook-mismatch";
    private const string UnitNotStopped = "source-repair-hook-unit-not-stopped";
    private static readonly (string Name, string Value)[] StoppedUnit =
    [
        ("Id", "jellyfin.service"),
        ("LoadState", "loaded"),
        ("ActiveState", "inactive"),
        ("Job", ""),
        ("NeedDaemonReload", "no"),
        ("MainPID", "0"),
        ("ControlPID", "0"),
    ];

    internal static async Task VerifyAsync(string journalDirectory, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(journalDirectory)) throw new InvalidOperationException(HookMismatch);
        journalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(journalDirectory));
        var runner = Environment.ProcessPath ?? throw new InvalidOperationException("source-repair-apphost-required");
        SourcePathRepairStartupHook.VerifyInstalled(journalDirectory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var unitArguments = new[] { "--system", "--no-pager", "show", "--all", "jellyfin.service",
            "--property=" + string.Join(',', StoppedUnit.Select(field => field.Name)) };
        VerifyStoppedState(await QueryAsync("/usr/bin/systemctl", unitArguments, timeout.Token).ConfigureAwait(false));
        var reply = await QueryAsync("/usr/bin/busctl",
            ["--system", "--no-pager", "--json=short", "get-property", "org.freedesktop.systemd1",
             "/org/freedesktop/systemd1/unit/jellyfin_2eservice", "org.freedesktop.systemd1.Service", "ExecStartPreEx"],
            timeout.Token).ConfigureAwait(false);
        VerifyCommands(reply, runner, journalDirectory);
        VerifyStoppedState(await QueryAsync("/usr/bin/systemctl", unitArguments, timeout.Token).ConfigureAwait(false));
    }

    internal static void VerifyCommands(string reply, string runner, string journalDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("type", out var type) || !StringIs(type, "a(sasasttttuii)")
                || !root.TryGetProperty("data", out var commands) || commands.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(HookMismatch);

            var found = false;
            foreach (var command in commands.EnumerateArray())
            {
                if (!ValidCommandTuple(command)) throw new InvalidOperationException(HookMismatch);
                var arguments = command[1];
                if (arguments.GetArrayLength() < 2 || !StringIs(arguments[1], Command)) continue;

                var flags = command[2];
                if (found || !StringIs(command[0], runner) || arguments.GetArrayLength() != 3
                    || !StringIs(arguments[0], runner) || !StringIs(arguments[2], journalDirectory)
                    || flags.GetArrayLength() != 1 || !StringIs(flags[0], "privileged"))
                    throw new InvalidOperationException(HookMismatch);
                found = true;
            }
            if (!found) throw new InvalidOperationException(HookMismatch);
        }
        catch (JsonException ex) { throw new InvalidOperationException(HookMismatch, ex); }
    }

    private static bool ValidCommandTuple(JsonElement command)
    {
        if (command.ValueKind != JsonValueKind.Array || command.GetArrayLength() != 10
            || command[0].ValueKind != JsonValueKind.String
            || !StringVector(command[1]) || !StringVector(command[2]))
            return false;
        // The four timestamps may reflect an earlier service activation.
        for (var index = 3; index <= 6; index++)
        {
            if (command[index].ValueKind != JsonValueKind.Number || !command[index].TryGetUInt64(out _)) return false;
        }
        return command[7].ValueKind == JsonValueKind.Number && command[7].TryGetUInt32(out _)
            && command[8].ValueKind == JsonValueKind.Number && command[8].TryGetInt32(out _)
            && command[9].ValueKind == JsonValueKind.Number && command[9].TryGetInt32(out _);
    }

    private static bool StringVector(JsonElement value)
        => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(element => element.ValueKind == JsonValueKind.String);

    private static bool StringIs(JsonElement value, string expected)
        => value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    internal static void VerifyStoppedState(string reply)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in reply.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var delimiter = line.IndexOf('=');
            if (delimiter <= 0 || !fields.TryAdd(line[..delimiter], line[(delimiter + 1)..]))
                throw new InvalidOperationException(UnitNotStopped);
        }
        if (fields.Count != StoppedUnit.Length || StoppedUnit.Any(field => fields.GetValueOrDefault(field.Name) != field.Value))
            throw new InvalidOperationException(UnitNotStopped);
    }

    private static async Task<string> QueryAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear();
        info.Environment["LC_ALL"] = "C";
        info.Environment["PATH"] = "/usr/bin:/bin";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("source-repair-hook-query-unavailable"); }
        catch (Win32Exception ex) { throw new InvalidOperationException("source-repair-hook-query-unavailable", ex); }
        using (process)
        {
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            try
            {
                var output = await ReadQueryOutputAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("source-repair-hook-query-failed");
                return output;
            }
            catch
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<string> ReadQueryOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int limit = 64 * 1024;
        var buffer = new char[2048];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (count > limit - output.Length) throw new InvalidOperationException("source-repair-hook-query-too-large");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}

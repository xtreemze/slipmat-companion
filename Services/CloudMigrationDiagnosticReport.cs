using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AudioGateway.Models;

namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>Advisory, bounded path-only projection. Never verification or transfer authority.</summary>
public static class CloudMigrationDiagnosticReport
{
    public static CloudMigrationDiagnostics Parse(RcloneCommandResult result)
    {
        var entries = new List<CloudMigrationDiagnosticEntry>();
        var truncated = result.OutputTruncated || result.StandardOutput.Length > 2 * 1024 * 1024;
        var output = result.StandardOutput[..Math.Min(result.StandardOutput.Length, 2 * 1024 * 1024)];
        // A killed or bounded process may leave a partial path; never report it as an object.
        if ((result.TimedOut || truncated) && !output.EndsWith('\n'))
        {
            var lastNewline = output.LastIndexOf('\n');
            output = lastNewline < 0 ? string.Empty : output[..(lastNewline + 1)];
        }
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 3 || line[1] != ' ') continue;
            var kind = line[0] switch { '*' => "different", '+' => "missing-local", '!' => "read-error", _ => null };
            if (kind is null) continue;
            var path = line[2..];
            if (path.Length is 0 or > 1024 || Path.IsPathRooted(path)
                || path.Any(char.IsControl) || path.IndexOfAny(['\\', ':', '?', '#', '=']) >= 0
                || path.Split('/').Any(part => part is "" or "." or "..")) continue;
            if (entries.Count == 100) { truncated = true; break; }
            entries.Add(new(kind, path));
        }
        return new("remote-subset-hash-diagnostic-only", DateTimeOffset.UtcNow,
            result.StartSucceeded, result.ExitCode, result.TimedOut, truncated, entries);
    }
}

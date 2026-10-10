using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AudioGateway.Configuration;

namespace Jellyfin.Plugin.AudioGateway.Services;

public sealed record CloudPreflightCheck(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("fix")] string? Fix);

public sealed record CloudPreflightResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checks")] IReadOnlyList<CloudPreflightCheck> Checks);

/// <summary>Host facts the preflight needs; replaceable in tests.</summary>
public interface ICloudHostProbe
{
    bool FileExists(string path);

    bool ExistsOnPath(string executable);

    long? AvailableFreeBytes(string path);
}

public sealed class CloudHostProbe : ICloudHostProbe
{
    public bool FileExists(string path) => File.Exists(path) || Directory.Exists(path);

    public bool ExistsOnPath(string executable)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, executable)));

    public long? AvailableFreeBytes(string path)
    {
        try
        {
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent))
                {
                    return null;
                }

                probe = parent;
            }

            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Read-only, credential-free readiness checklist for the rclone VFS projection. It
/// reuses rclone status codes and adds host checks; it never mounts or writes.
/// </summary>
public sealed class CloudPreflightService
{
    private const long GiB = 1024L * 1024 * 1024;

    private readonly ICloudProjectionConfigurationSource _configurationSource;
    private readonly IRcloneProcessRunner _runner;
    private readonly ICloudHostProbe _probe;

    public CloudPreflightService(
        ICloudProjectionConfigurationSource configurationSource,
        IRcloneProcessRunner runner)
        : this(configurationSource, runner, new CloudHostProbe())
    {
    }

    internal CloudPreflightService(
        ICloudProjectionConfigurationSource configurationSource,
        IRcloneProcessRunner runner,
        ICloudHostProbe probe)
    {
        _configurationSource = configurationSource;
        _runner = runner;
        _probe = probe;
    }

    public async Task<CloudPreflightResponse> RunAsync(CancellationToken cancellationToken)
    {
        var checks = new List<CloudPreflightCheck>();
        var cli = await new RcloneCliHost(runner: _runner)
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        checks.Add(cli.Health == RcloneCliHealth.Ready
            ? Pass("rclone", $"rclone {cli.Version} is usable.")
            : Fail("rclone", $"rclone is not usable ({cli.Code}).", RcloneFix(cli.Code)));

        if (cli.Health == RcloneCliHealth.Ready)
        {
            checks.Add(cli.Remotes.Count > 0
                ? Pass("remote", $"{cli.Remotes.Count} rclone remote(s) configured.")
                : Fail(
                    "remote",
                    "No rclone remote is configured for the Jellyfin service.",
                    "Run `rclone config` as the Jellyfin service user (or set RCLONE_CONFIG) and confirm `rclone listremotes`."));
        }

        checks.Add(MountCapability());

        var profiles = RuntimeSettings.GetCloudLibraries(_configurationSource.GetCurrent())
            .Where(profile => profile.Enabled)
            .ToArray();
        foreach (var profile in profiles)
        {
            var check = CacheSpace(profile);
            if (check is not null)
            {
                checks.Add(check);
            }
        }

        var status = checks.Any(check => check.Status == "fail")
            ? "fail"
            : checks.Any(check => check.Status == "warn") ? "warn" : "pass";
        return new CloudPreflightResponse(status, checks);
    }

    private CloudPreflightCheck MountCapability()
    {
        if (OperatingSystem.IsLinux())
        {
            if (!_probe.FileExists("/dev/fuse"))
            {
                return Fail(
                    "mount",
                    "/dev/fuse is not available to the Jellyfin process.",
                    "Run the container with `--device /dev/fuse --cap-add SYS_ADMIN` (and an unconfined AppArmor profile where required).");
            }

            return _probe.ExistsOnPath("fusermount3") || _probe.ExistsOnPath("fusermount")
                ? Pass("mount", "FUSE device and fusermount are available.")
                : Fail("mount", "fusermount is not installed.", "Install fuse3 in the Jellyfin host or image.");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Warn(
                "mount",
                "macOS: Homebrew rclone refuses `rclone mount`; macFUSE or the official rclone binary is required.",
                "Run Jellyfin in a container (see the Docker recipe) or install the official rclone build with macFUSE.");
        }

        return Warn("mount", "Mount capability is not verified on this platform.", null);
    }

    private CloudPreflightCheck? CacheSpace(CloudLibraryProfile profile)
    {
        string cachePath;
        try
        {
            var legacy = RuntimeSettings.ProfileAsLegacyConfiguration(profile);
            cachePath = RuntimeSettings.ResolveCloudCachePath(
                legacy,
                profile.RemoteName,
                profile.RemotePath,
                Plugin.Instance?.HostApplicationPaths.DataPath);
        }
        catch (Exception)
        {
            return Fail(
                $"cache:{profile.Id}",
                "Cache path could not be resolved.",
                "Choose an absolute cache path or leave it empty for the managed default.");
        }

        var free = _probe.AvailableFreeBytes(cachePath);
        if (free is null)
        {
            return Warn($"cache:{profile.Id}", $"Free space for {cachePath} could not be read.", null);
        }

        var needed = (profile.CacheMaxSizeGiB + profile.CacheMinFreeSpaceGiB) * GiB;
        return free >= needed
            ? Pass($"cache:{profile.Id}", $"{free / GiB} GiB free for a {profile.CacheMaxSizeGiB} GiB cache.")
            : Fail(
                $"cache:{profile.Id}",
                $"{free / GiB} GiB free; cache cap plus free-space floor needs {needed / GiB} GiB.",
                "Lower the cache cap, free space, or move the cache path to a larger local volume.");
    }

    private static string? RcloneFix(string code) => code switch
    {
        "rclone-unavailable" => "Put rclone on the Jellyfin service PATH or set SLIPMAT_RCLONE.",
        "rclone-version-unsupported" => "Upgrade rclone: this build lacks `listremotes --json` (install the current official release).",
        "remotes-unavailable" => "Confirm `rclone listremotes` works for the Jellyfin service user (RCLONE_CONFIG).",
        _ => null,
    };

    private static CloudPreflightCheck Pass(string id, string detail) => new(id, "pass", detail, null);

    private static CloudPreflightCheck Warn(string id, string detail, string? fix) => new(id, "warn", detail, fix);

    private static CloudPreflightCheck Fail(string id, string detail, string? fix) => new(id, "fail", detail, fix);
}

// Disposable VM fixture only. Never point this tool at a real migration journal.
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Slipmat.MaintenanceRunner;

if (!OperatingSystem.IsLinux() || Environment.UserName != "root" || args.Length < 2) throw new InvalidOperationException("VM root required");
var root = args[1];
if (!root.StartsWith("/mnt/slipmat-maintenance-fixture-", StringComparison.Ordinal) || Path.GetFullPath(root) != root || root.Contains("..")) throw new InvalidOperationException("fixture path rejected");
if (args[0] == "inspect")
{
    if (args.Length != 4 || !File.Exists(root + "/journal/.writer.lock")) throw new ArgumentException("inspect requires existing job and expected phase");
    await using var lease = await new CloudMigrationJournal(root + "/journal").AcquireAsync();
    var current = await lease.ReadAsync(args[2]) ?? throw new InvalidOperationException("missing job");
    var observed = current.Job;
    var promoted = args[3] == CloudMigrationPhases.ReadyToFinalize;
    var rolledBack = args[3] == CloudMigrationPhases.RolledBack;
    if ((!promoted && !rolledBack) || observed.Phase != args[3] || observed.Maintenance?.Phase != CloudMaintenancePhase.Stable
        || observed.Maintenance.Direction != (promoted ? CloudMaintenanceDirection.Promote : CloudMaintenanceDirection.Rollback)
        || observed.BackupPath != (promoted ? root + "/music.backup" : null)
        || !Directory.Exists(root + "/music") || Directory.Exists(root + "/music.backup") != promoted)
        throw new InvalidOperationException("fixture canonical state mismatch");
    Console.WriteLine($"canonical phase={observed.Phase} backup={observed.BackupPath ?? "none"} local={root}/music");
    return;
}
if (args[0] != "init" || args.Length != 4 || Directory.Exists(root)) throw new InvalidOperationException("fresh fixture required");
var rclone = args[2]; var helperSource = args[3];
if (!Path.IsPathFullyQualified(rclone) || !Path.IsPathFullyQualified(helperSource)) throw new ArgumentException("absolute inputs required");
Directory.CreateDirectory(root);
foreach (var name in new[] { "music", "remote", "cache", "bundle", "journal" }) Directory.CreateDirectory(root + "/" + name);
File.SetUnixFileMode(root + "/bundle", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
File.SetUnixFileMode(root + "/journal", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
File.WriteAllText(root + "/music/track.flac", "fixture track\n"); File.Copy(root + "/music/track.flac", root + "/remote/track.flac");
File.Copy(helperSource, root + "/custody.py"); File.WriteAllText(root + "/rclone.conf", "[fixture]\ntype = local\n");
var profile = root + "/profile.xml"; var policy = root + "/policy.xml"; var source = root + "/music";
File.WriteAllText(profile, $"<PluginConfiguration><CloudLibraries/><CloudProjectionEnabled>false</CloudProjectionEnabled><CloudProjectionPath>/previous</CloudProjectionPath><CloudRemoteName>fixture</CloudRemoteName><CloudRemotePath>{root}/remote</CloudRemotePath><CloudLibraryName>Music</CloudLibraryName></PluginConfiguration>");
string[] names = ["EnableRealtimeMonitor", "EnableLUFSScan", "EnableChapterImageExtraction", "ExtractChapterImagesDuringLibraryScan", "EnableTrickplayImageExtraction", "ExtractTrickplayImagesDuringLibraryScan", "SaveLocalMetadata", "SaveSubtitlesWithMedia", "SaveLyricsWithMedia", "SaveTrickplayWithMedia"];
File.WriteAllText(policy, $"<LibraryOptions><PathInfos><MediaPathInfo><Path>{source}</Path></MediaPathInfo></PathInfos><TypeOptions><TypeOptions><Type>Audio</Type><ImageFetchers/></TypeOptions></TypeOptions>" + string.Concat(names.Select(n => $"<{n}>false</{n}>")) + "</LibraryOptions>");
string Hash(string p) => Convert.ToHexString(SHA256.HashData(LinuxTrustedFile.Read(p, 268435456)));
var id = Guid.NewGuid().ToString("N"); var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(); var ns = new FileInfo("/proc/1/ns/mnt").LinkTarget!;
var parent = LinuxTrustedFile.InspectDirectory(root); var src = LinuxTrustedFile.InspectDirectory(source);
var parts = File.ReadAllLines("/proc/self/mountinfo").Single(x => x.StartsWith(src.MountId + " ")).Split(' '); var sep = Array.IndexOf(parts, "-"); var dev = parts[2].Split(':');
var mount = new MountInstance(1, boot, ns, src.MountId, uint.Parse(dev[0]), uint.Parse(dev[1]), parts[sep + 1], parts[sep + 2]);
var scan = new CloudLibraryScanPolicy(false, false, false, false, false, false, false, false, false, false, []);
var manifest = await CloudMigrationManifestBuilder.CaptureAsync(source, default);
var admission = new CloudMaintenanceAdmission(Guid.NewGuid(), "Music", source, policy, profile, scan, "legacy", "fixture", root + "/remote", manifest.DigestSha256, "rclone-check-download-content", Hash(profile), Hash(policy), false, "/previous", DateTimeOffset.UtcNow);
var job = new CloudMigrationJob(id, "legacy", "Music", "local-to-vfs", "ready-for-cutover", "ready", source, null, null, "fixture", root + "/remote", "/previous", false, admission.Verification, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ManifestVersion: manifest.Version, ManifestDigestSha256: manifest.DigestSha256, ManifestFileCount: manifest.FileCount, ManifestTotalBytes: manifest.TotalBytes, MaintenanceAdmission: admission);
var python = "/usr/bin/python3.14"; var helper = root + "/custody.py";
var spec = new HostSpecification(1, id, boot, ns, "jellyfin.service", parent, src, root + "/music.backup", mount, rclone, Hash(rclone), root + "/rclone.conf", 65534, 65534, python, Hash(python), helper, Hash(helper), root + "/cache", 1, 1, 0, 2, new(profile, root + "/bundle/profile-original.xml", admission.PluginConfigurationSha256, new string('0', 64)), new(policy, root + "/bundle/policy-original.xml", admission.OptionsSha256, new string('0', 64)));
await using (var lease = await new CloudMigrationJournal(root + "/journal").AcquireAsync()) await lease.CompareExchangeAsync(null, job);
LinuxTrustedFile.CreateNew(root + "/draft.json", JsonSerializer.SerializeToUtf8Bytes(spec, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
Console.WriteLine(id);

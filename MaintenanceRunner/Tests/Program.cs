using Jellyfin.Plugin.AudioGateway.Models;
using Slipmat.MaintenanceRunner;
using Jellyfin.Plugin.AudioGateway.Services;
using System.Security.Cryptography;
using System.Text.Json;

var jobId = Guid.NewGuid().ToString("N");
if (HostSpecification.IsValidHash("bad")) throw new Exception("invalid hash accepted");
if (!HostSpecification.IsValidHash(new string('a', 64))) throw new Exception("valid hash rejected");
if (OperatingSystem.IsLinux() && Environment.UserName == "root")
{
    var trustRoot = Path.Combine("/root", "maintenance-trust-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(trustRoot);
    try
    {
        var identity = LinuxTrustedFile.InspectDirectory(trustRoot);
        if (LinuxTrustedFile.TryInspectDirectory(trustRoot) != identity) throw new Exception("optional directory inspection changed its identity");
        if (LinuxTrustedFile.TryInspectDirectory(Path.Combine(trustRoot, "absent")) is not null) throw new Exception("missing directory reported as present");
        if (LinuxTrustedFile.TryInspectDirectory(Path.Combine(trustRoot, "absent-parent", "child")) is not null) throw new Exception("missing parent directory reported as present");
        using (var stat = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/stat") { ArgumentList = { "-c", "%d:%i", trustRoot }, RedirectStandardOutput = true }) ?? throw new Exception("stat unavailable"))
        {
            var expected = await stat.StandardOutput.ReadToEndAsync();
            await stat.WaitForExitAsync();
            if ($"{identity.Device}:{identity.Inode}" != expected.Trim() || identity.MountId <= 0) throw new Exception("directory identity mismatch");
        }
        var trustedFile = Path.Combine(trustRoot, "spec.json");
        LinuxTrustedFile.CreateNew(trustedFile, System.Text.Encoding.UTF8.GetBytes("hello"));
        if (System.Text.Encoding.UTF8.GetString(LinuxTrustedFile.Read(trustedFile)) != "hello") throw new Exception("trusted read failed");
        if (File.GetUnixFileMode(trustedFile) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) throw new Exception("snapshot not private");
        try { LinuxTrustedFile.CreateNew(trustedFile, [1]); throw new Exception("snapshot overwritten"); }
        catch (IOException) { }
        var link = Path.Combine(trustRoot, "link");
        File.CreateSymbolicLink(link, trustedFile);
        try { LinuxTrustedFile.Read(link); throw new Exception("symlink accepted"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException && ex.Message == "trusted-path-not-root-controlled") { }
        try { LinuxTrustedFile.TryInspectDirectory(link); throw new Exception("directory symlink accepted"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        var danglingDirectoryLink = Path.Combine(trustRoot, "dangling-directory");
        Directory.CreateSymbolicLink(danglingDirectoryLink, Path.Combine(trustRoot, "absent-target"));
        try { LinuxTrustedFile.TryInspectDirectory(Path.Combine(danglingDirectoryLink, "child")); throw new Exception("missing path beneath symlink reported absent"); }
        catch (IOException) { }
        var fifo = Path.Combine(trustRoot, "fifo");
        using (var process = System.Diagnostics.Process.Start("/usr/bin/mkfifo", fifo) ?? throw new Exception("mkfifo unavailable"))
        {
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("mkfifo failed");
        }
        try { LinuxTrustedFile.Read(fifo); throw new Exception("FIFO accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "trusted-path-not-root-controlled") { }
        File.SetUnixFileMode(trustedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);
        try { LinuxTrustedFile.Read(trustedFile); throw new Exception("writable spec accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "trusted-path-not-root-controlled") { }
    }
    finally { Directory.Delete(trustRoot, recursive: true); }
}
var custody = new CloudMaintenanceCustody("11111111-1111-1111-1111-111111111111", "Music", "/music", "/backup", "profile", "remote", "path", new string('a', 64), "rclone-check-download-content", new string('b', 64));
var intent = new CloudMaintenanceIntent(jobId, CloudMaintenanceDirection.Promote, CloudMaintenancePhase.MoveIntent, "inode", null, custody);
var boot = Guid.NewGuid().ToString();
var mountPointIdentity = new DirectoryIdentity("/mnt/music", 4, 13, 8);
var mountIdentity = new MountInstance(1, boot, "mnt:[42]", 222, 0, 55, "fuse.rclone", "remote:path", "slipmat-vfs-" + jobId + ".service", 123, 456, "/system.slice/slipmat-vfs-" + jobId + ".service", mountPointIdentity);
var mountToken = MountIdentityCodec.Encode(mountIdentity);
if (MountIdentityCodec.Decode(mountToken, boot, "mnt:[42]", "remote:path", "/mnt/music") != mountIdentity) throw new Exception("mount identity roundtrip failed");
try { MountIdentityCodec.Decode(mountToken, boot, "mnt:[42]", "remote:path", "/mnt/other"); throw new Exception("replacement mountpoint accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "mount-identity-mismatch") { }
try { MountIdentityCodec.Decode(mountToken, Guid.NewGuid().ToString(), "mnt:[42]", "remote:path", "/mnt/music"); throw new Exception("stale boot accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "mount-identity-mismatch") { }
try { MountIdentityCodec.Encode(mountIdentity with { MountPointIdentity = null }); throw new Exception("missing mountpoint identity accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "mount-identity-invalid") { }
var hostSpec = new HostSpecification(1, jobId, boot, "mnt:[42]", "jellyfin.service",
    new("/mnt", 4, 11, 8), new("/mnt/music", 4, 12, 8), "/mnt/backup",
    new(1, boot, "mnt:[42]", 8, 0, 44, "fuseblk", "/dev/sdb1"),
    "/usr/bin/rclone", new string('a', 64), "/etc/rclone.conf", 1000, 1000,
    "/usr/bin/python3.11", new string('a', 64), "/usr/local/libexec/custody.py", new string('b', 64),
    "/var/cache/music", 10, 24, 1, 180,
    new("/etc/profile.xml", "/root/bundle/profile.xml", new string('b', 64), new string('c', 64)),
    new("/etc/policy.xml", "/root/bundle/policy.xml", new string('d', 64), new string('e', 64)));
await LinuxMaintenanceBarrierTests.Run(hostSpec);
await LinuxMountSupervisorTests.Run(hostSpec);
var specBytes = JsonSerializer.SerializeToUtf8Bytes(hostSpec, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var specIntent = intent with { Custody = custody with { LocalPath = "/mnt/music", BackupPath = "/mnt/backup", ConfigurationDigest = Convert.ToHexString(SHA256.HashData(specBytes)) } };
var admission = new CloudMaintenanceAdmission(Guid.Parse(custody.LibraryId), "Music", "/mnt/music", "/etc/policy.xml", "/etc/profile.xml",
    new CloudLibraryScanPolicy(false, false, false, false, false, false, false, false, false, false),
    "profile", "remote", "path", new string('a', 64), "rclone-check-download-content", new string('b', 64), new string('d', 64), false, "/previous", DateTimeOffset.UtcNow);
if (OperatingSystem.IsLinux() && Environment.UserName == "root" && File.Exists("/usr/bin/setpriv"))
{
    var rootVerify = Path.Combine("/srv", "maintenance-verify-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(rootVerify);
    try
    {
        File.SetUnixFileMode(rootVerify, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var source = Path.Combine(rootVerify, "music"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "track"), "test");
        var executable = Path.Combine(rootVerify, "rclone");
        var script = System.Text.Encoding.UTF8.GetBytes("#!/bin/sh\ncase \"$*\" in *'check '*" + source + "*' --download --stats=0 --log-level=ERROR') exit 0;; *) exit 9;; esac\n");
        LinuxTrustedFile.CreateNew(executable, script);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var config = Path.Combine(rootVerify, "rclone.conf"); LinuxTrustedFile.CreateNew(config, []);
        File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var manifest = await CloudMigrationManifestBuilder.CaptureAsync(source);
        var checkAdmission = admission with { LocalPath = source, ManifestDigestSha256 = manifest.DigestSha256 };
        var checkJob = new CloudMigrationJob(jobId, "profile", "Music", "local-to-vfs", "ready-for-cutover", "ready", source, null, null, "remote", "path", "/previous", false, "rclone-check-download-content", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ManifestVersion: manifest.Version, ManifestDigestSha256: manifest.DigestSha256, ManifestFileCount: manifest.FileCount, ManifestTotalBytes: manifest.TotalBytes, MaintenanceAdmission: checkAdmission);
        var checkSpec = hostSpec with { RclonePath = executable, RcloneSha256 = Convert.ToHexString(SHA256.HashData(script)), RcloneConfigPath = config, ServiceUid = 65534, ServiceGid = 65534 };
        await HostVerification.VerifyAsync(checkJob, checkSpec, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "track"), "changed");
        try { await HostVerification.VerifyAsync(checkJob, checkSpec, CancellationToken.None); throw new Exception("source drift accepted"); }
        catch (InvalidOperationException ex) when (ex.Message == "source-manifest-changed") { }
    }
    finally { Directory.Delete(rootVerify, recursive: true); }
}
if (HostSpecification.Parse(specBytes, specIntent, admission) != hostSpec) throw new Exception("valid host specification rejected");
try { HostSpecification.Parse(specBytes, intent, admission); throw new Exception("wrong specification hash accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "host-specification-hash-mismatch") { }
try { HostSpecification.Parse(specBytes, specIntent, admission with { RemotePath = "different" }); throw new Exception("changed admission accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "invalid-host-specification") { }
var profileBytes = System.Text.Encoding.UTF8.GetBytes("<PluginConfiguration><CloudLibraries/><CloudProjectionEnabled>false</CloudProjectionEnabled><CloudProjectionPath>/previous</CloudProjectionPath><CloudRemoteName>remote</CloudRemoteName><CloudRemotePath>path</CloudRemotePath><CloudLibraryName>Music</CloudLibraryName></PluginConfiguration>");
var names = new[] { "EnableRealtimeMonitor", "EnableLUFSScan", "EnableChapterImageExtraction", "ExtractChapterImagesDuringLibraryScan", "EnableTrickplayImageExtraction", "ExtractTrickplayImagesDuringLibraryScan", "SaveLocalMetadata", "SaveSubtitlesWithMedia", "SaveLyricsWithMedia", "SaveTrickplayWithMedia" };
var policyBytes = System.Text.Encoding.UTF8.GetBytes("<LibraryOptions><PathInfos><MediaPathInfo><Path>/mnt/music</Path></MediaPathInfo></PathInfos><TypeOptions><TypeOptions><Type>Audio</Type><ImageFetchers/></TypeOptions></TypeOptions>" + string.Concat(names.Select(n => $"<{n}>false</{n}>")) + "</LibraryOptions>");
var testAdmission = admission with { ProfileId = "legacy", PreviousLibraryScanPolicy = admission.PreviousLibraryScanPolicy with { AudioImageFetchers = [] } };
var binding = new CloudMaintenanceProfileBinding("legacy", "remote", "path", "Music", "/previous", false, "/mnt/music");
var profilePlan = CloudMaintenanceProfileConfiguration.PrepareProfile(profileBytes, Convert.ToHexString(SHA256.HashData(profileBytes)), binding, true);
var policyPlan = CloudMaintenanceProfileConfiguration.PreparePolicy(policyBytes, Convert.ToHexString(SHA256.HashData(policyBytes)), "/mnt/music", testAdmission.PreviousLibraryScanPolicy, true);
var planSpec = hostSpec with { Profile = hostSpec.Profile with { BeforeHash = profilePlan.BeforeHash, AfterHash = profilePlan.AfterHash }, Policy = hostSpec.Policy with { BeforeHash = policyPlan.BeforeHash, AfterHash = policyPlan.AfterHash } };
HostConfigurationPlans.Derive(planSpec, testAdmission, profileBytes, policyBytes);
try { HostConfigurationPlans.Derive(planSpec with { Profile = planSpec.Profile with { AfterHash = new string('f', 64) } }, testAdmission, profileBytes, policyBytes); throw new Exception("unapproved configuration target accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "configuration-target-hash-mismatch") { }
if (OperatingSystem.IsLinux() && Environment.UserName == "root")
{
    var privateRoot = Path.Combine("/root", "maintenance-prepare-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(privateRoot);
    try
    {
        var profilePath = Path.Combine(privateRoot, "live-profile.xml");
        var policyPath = Path.Combine(privateRoot, "live-policy.xml");
        LinuxTrustedFile.CreateNew(profilePath, profileBytes);
        LinuxTrustedFile.CreateNew(policyPath, policyBytes);
        var preparedAdmission = testAdmission with { PluginConfigurationPath = profilePath, OptionsPath = policyPath,
            PluginConfigurationSha256 = profilePlan.BeforeHash, OptionsSha256 = policyPlan.BeforeHash };
        var draft = planSpec with { Profile = planSpec.Profile with { Path = profilePath, OriginalSnapshotPath = Path.Combine(privateRoot, "profile-original.xml") },
            Policy = planSpec.Policy with { Path = policyPath, OriginalSnapshotPath = Path.Combine(privateRoot, "policy-original.xml") } };
        var job = new CloudMigrationJob(jobId, "legacy", "Music", "local-to-vfs", "ready-for-cutover", "ready", "/mnt/music", null, null, "remote", "path", "/previous", false, "rclone-check-download-content", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ManifestDigestSha256: new string('a', 64), MaintenanceAdmission: preparedAdmission);
        var (prepared, digest) = HostPreparation.Prepare(draft, job, Path.Combine(privateRoot, "spec.json"));
        if (prepared.Profile.AfterHash != profilePlan.AfterHash || !HostSpecification.IsValidHash(digest)) throw new Exception("preparation did not derive target");
        if (!LinuxTrustedFile.Read(prepared.Profile.OriginalSnapshotPath).SequenceEqual(profileBytes) || !LinuxTrustedFile.Read(prepared.Policy.OriginalSnapshotPath).SequenceEqual(policyBytes)) throw new Exception("original snapshots differ");
        var preparedCustody = new CloudMaintenanceCustody(preparedAdmission.LibraryId.ToString(), preparedAdmission.LibraryName,
            preparedAdmission.LocalPath, prepared.BackupPath, preparedAdmission.ProfileId, preparedAdmission.RemoteName,
            preparedAdmission.RemotePath, preparedAdmission.ManifestDigestSha256, preparedAdmission.Verification, digest);
        var specIntentForProvider = new CloudMaintenanceIntent(job.Id, CloudMaintenanceDirection.Promote, CloudMaintenancePhase.Prepared,
            "pending-host-identity", null, preparedCustody);
        var specJobForProvider = job with { Maintenance = specIntentForProvider };
        var loadedSpec = new LinuxHostSpecificationProvider(Path.Combine(privateRoot, "spec.json"))
            .Read(new MaintenanceSnapshot(specJobForProvider, "prepared-spec-rev1"));
        if (loadedSpec != prepared) throw new Exception("trusted specification provider changed the prepared spec");
        try { HostPreparation.Prepare(draft, job, Path.Combine(privateRoot, "spec.json")); throw new Exception("snapshots overwritten"); }
        catch (IOException) { }
    }
    finally { Directory.Delete(privateRoot, recursive: true); }
}
var leasedIntent = intent with { Custody = custody with { LocalPath = admission.LocalPath, BackupPath = "/mnt/backup" } };
var leasedJob = new CloudMigrationJob(jobId, admission.ProfileId, admission.LibraryName, CloudMigrationDirections.LocalToVfs, CloudMigrationPhases.ReadyForCutover, "ready", admission.LocalPath, null, null, admission.RemoteName, admission.RemotePath, admission.PreviousProjectionPath, admission.PreviousEnabled, admission.Verification, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ManifestDigestSha256: admission.ManifestDigestSha256, Maintenance: leasedIntent, MaintenanceAdmission: admission);
await LinuxMaintenanceHostTests.Run(hostSpec, admission, leasedJob);
var lease = new FakeLease(new(leasedJob, "rev1"));
var host = new FakeHost();
var adapter = new LeasedOperations(lease, host);
await adapter.ReadAsync(jobId, default);
lease.Current = lease.Current with { Revision = "rev2" };
try { await adapter.ExecuteAsync(leasedIntent, CloudMaintenanceAction.MoveSourceToBackup, default); throw new Exception("stale revision accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "journal-changed") { }
if (host.Effects != 0) throw new Exception("effect on stale journal");
await adapter.ReadAsync(jobId, default);
try { await adapter.ExecuteAsync(leasedIntent, CloudMaintenanceAction.Unmount, default); throw new Exception("wrong action accepted"); }
catch (InvalidOperationException ex) when (ex.Message == "maintenance-action-rejected") { }
if (host.Effects != 0) throw new Exception("unauthorized effect");
await adapter.ExecuteAsync(leasedIntent, CloudMaintenanceAction.MoveSourceToBackup, default);
if (host.Effects != 1) throw new Exception("valid guarded action did not reach host");
if (host.LastJob != leasedJob || host.LastAdmission != admission) throw new Exception("host did not receive the complete admitted job snapshot");
var racingLease = new FakeLease(new(leasedJob, "rev1"));
var racingHost = new FakeHost { AfterObserve = () => racingLease.Current = racingLease.Current with { Revision = "rev2" } };
var racingAdapter = new LeasedOperations(racingLease, racingHost);
await racingAdapter.ReadAsync(jobId, default);
try { await racingAdapter.ExecuteAsync(leasedIntent, CloudMaintenanceAction.MoveSourceToBackup, default); throw new Exception("journal change during observation reached a host effect"); }
catch (InvalidOperationException ex) when (ex.Message == "journal-changed") { }
if (racingHost.Effects != 0) throw new Exception("effect ran after journal changed during observation");
var preparedIntent = leasedIntent with { Phase = CloudMaintenancePhase.Prepared };
var preparedJob = leasedJob with { Maintenance = preparedIntent };
var forgedCasLease = new FakeLease(new(preparedJob, "cas-rev1"));
var forgedCasAdapter = new LeasedOperations(forgedCasLease, new FakeHost());
await forgedCasAdapter.ReadAsync(jobId, default);
if (await forgedCasAdapter.CompareExchangeAsync(preparedIntent, preparedIntent with { Phase = CloudMaintenancePhase.Stable }, default))
    throw new Exception("forged maintenance phase advance accepted");
if (forgedCasLease.CompareExchangeCalls != 0) throw new Exception("forged phase reached durable compare/exchange");
var authorizedCasLease = new FakeLease(new(preparedJob, "authorized-cas-rev1"));
var authorizedCasAdapter = new LeasedOperations(authorizedCasLease, new FakeHost());
await authorizedCasAdapter.ReadAsync(jobId, default);
if (!await authorizedCasAdapter.CompareExchangeAsync(preparedIntent, preparedIntent with { Phase = CloudMaintenancePhase.MoveIntent }, default))
    throw new Exception("planner-approved phase advance rejected");
if (authorizedCasLease.Current.Intent.Phase != CloudMaintenancePhase.MoveIntent) throw new Exception("approved phase was not persisted");
var mountIntent = leasedIntent with { Phase = CloudMaintenancePhase.MountIntent };
var mountJob = leasedJob with { Maintenance = mountIntent };
var mountLease = new FakeLease(new(mountJob, "mount-cas-rev1"));
var mountHost = new FakeHost
{
    Observation = new(true, true, CloudMaintenancePathState.Missing, CloudMaintenancePathState.Expected, CloudMaintenanceMountState.Missing, false, false),
    ExecuteResult = "verified-mount-instance",
};
var mountAdapter = new LeasedOperations(mountLease, mountHost);
await mountAdapter.ReadAsync(jobId, default);
if (await mountAdapter.ExecuteAsync(mountIntent, CloudMaintenanceAction.Mount, default) != "verified-mount-instance")
    throw new Exception("verified mount identity was not returned");
if (!await mountAdapter.CompareExchangeAsync(mountIntent, mountIntent with { MountIdentity = "verified-mount-instance" }, default))
    throw new Exception("exact verified mount identity was not persisted");
if (mountLease.Current.Intent.MountIdentity != "verified-mount-instance") throw new Exception("mount identity was not persisted");
var changedAdmissionJob = leasedJob with { MaintenanceAdmission = admission with { RemoteName = "foreign-remote" } };
var changedAdmissionHost = new FakeHost();
var changedAdmissionAdapter = new LeasedOperations(new FakeLease(new(changedAdmissionJob, "rev1")), changedAdmissionHost);
try { await changedAdmissionAdapter.ReadAsync(jobId, default); throw new Exception("changed admitted binding reached the host adapter"); }
catch (InvalidOperationException ex) when (ex.Message == "maintenance-job-binding-mismatch") { }
if (changedAdmissionHost.LastJob is not null || changedAdmissionHost.Effects != 0) throw new Exception("host observed a job with changed admission bindings");
var changed = leasedIntent with { Custody = leasedIntent.Custody with { RemotePath = "another" } };
if (await adapter.CompareExchangeAsync(leasedIntent, changed, default)) throw new Exception("custody change accepted");
var root = Path.Combine(Path.GetTempPath(), "maintenance-journal-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var id = Guid.NewGuid().ToString("N");
    var heldIntent = intent with { JobId = id };
    var job = new CloudMigrationJob(id, "profile", "Music", "local-to-vfs", "failed", "error", "/music", null, null, "remote", "path", "/projection", false, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Maintenance: heldIntent);
    await using var held = await new CloudMigrationJournal(root).AcquireAsync();
    await held.CompareExchangeAsync(null, job);
    var real = new JournalLeaseAdapter(held);
    var first = await real.ReadAsync(id, default) ?? throw new Exception("missing intent");
    var updated = heldIntent with { Phase = CloudMaintenancePhase.Moved };
    var second = await real.CompareExchangeAsync(first, updated, default) ?? throw new Exception("CAS failed");
    if (second.Revision == first.Revision || (await held.ReadAsync(id))!.Job.Maintenance != updated)
        throw new Exception("CAS did not update whole-job revision");
    if (await real.CompareExchangeAsync(first, updated, default) is not null)
        throw new Exception("stale snapshot accepted");
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine("Leased adapter tests passed");

sealed class FakeLease(MaintenanceSnapshot initial) : IMaintenanceLease
{
    public MaintenanceSnapshot Current = initial;
    public int CompareExchangeCalls;
    public Task<MaintenanceSnapshot?> ReadAsync(string jobId, CancellationToken ct) => Task.FromResult<MaintenanceSnapshot?>(Current);
    public Task<MaintenanceSnapshot?> CompareExchangeAsync(MaintenanceSnapshot expected, CloudMaintenanceIntent replacement, CancellationToken ct)
    {
        CompareExchangeCalls++;
        if (Current.Revision != expected.Revision) return Task.FromResult<MaintenanceSnapshot?>(null);
        Current = new(Current.Job with { Maintenance = replacement }, Current.Revision + "-next");
        return Task.FromResult<MaintenanceSnapshot?>(Current);
    }
}
sealed class FakeHost : IMaintenanceHost
{
    public int Effects;
    public CloudMigrationJob? LastJob;
    public CloudMaintenanceAdmission? LastAdmission;
    public Action? AfterObserve;
    public CloudMaintenanceObservation Observation = new(true, true, CloudMaintenancePathState.Expected, CloudMaintenancePathState.Missing, CloudMaintenanceMountState.Missing, false, false);
    public string? ExecuteResult;
    public Task<CloudMaintenanceObservation> ObserveAsync(MaintenanceSnapshot snapshot, CancellationToken ct)
    {
        LastJob = snapshot.Job;
        LastAdmission = snapshot.Job.MaintenanceAdmission;
        AfterObserve?.Invoke();
        return Task.FromResult(Observation);
    }
    public Task<string?> ExecuteGuardedAsync(MaintenanceSnapshot snapshot, CloudMaintenanceAction action, CancellationToken ct) { Effects++; return Task.FromResult(ExecuteResult); }
}

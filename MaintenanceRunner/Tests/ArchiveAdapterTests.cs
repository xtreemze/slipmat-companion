using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Slipmat.MaintenanceRunner;
using Jellyfin.Plugin.AudioGateway.Services;

internal static class ArchiveAdapterTests
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName != "root") return;
        var root = Directory.CreateDirectory(Path.Combine("/root", "slipmat-archive-test-" + Guid.NewGuid().ToString("N")),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var python = new FileInfo("/usr/bin/python3").ResolveLinkTarget(true)?.FullName ?? "/usr/bin/python3";
            var helper = Path.Combine(root.FullName, "custody.py");
            File.Copy(Path.GetFullPath("MaintenanceRunner/custody.py"), helper);
            var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source"));
            byte[] contents = [1, 2, 3];
            var album = Directory.CreateDirectory(Path.Combine(source.FullName, "Album"));
            await File.WriteAllBytesAsync(Path.Combine(album.FullName, "Track.flac"), contents);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                fileCount = 1,
                totalBytes = contents.Length,
                entries = new object[] { new { path = "Album", kind = "directory" },
                    new { path = "Album/Track.flac", kind = "file", size = contents.Length, sha256 = Hash(contents) } },
            });
            var method = typeof(CustodyProcess).GetMethod("ArchiveAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Exception("archive adapter unavailable");
            var parentId = Identify(root.FullName);
            var binding = new CloudSourcePathRepairBinding(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), "Music",
                "profile", source.FullName, "remote", "Music", Identify(source.FullName),
                new(1, Hash(manifest), 1, contents.Length, Hash(manifest)), Hash(manifest));
            var plan = new CloudSourcePathRepairPlan(1, binding,
                [new(CloudSourcePathRepairKind.Directory, "Album", "Album-preserved", Identify(album.FullName))],
                Hash(manifest), Hash([42]), "");
            plan = plan with { Fingerprint = CloudSourcePathRepairTransaction.Fingerprint(plan) };
            var pythonHash = Hash(await File.ReadAllBytesAsync(python));
            var helperHash = Hash(await File.ReadAllBytesAsync(helper));
            Task<CloudSourcePathRepairIdentity> Invoke(CloudSourcePathRepairIdentity? archive, CloudSourcePathRepairPlan? requestedPlan)
                => (Task<CloudSourcePathRepairIdentity>)method.Invoke(null,
                    [python, pythonHash, helper, helperHash, root.FullName, "archive", parentId, archive,
                     manifest, Hash(manifest), requestedPlan,
                     CancellationToken.None])!;
            try
            {
                await Invoke(null, plan with { Moves = [plan.Moves[0] with { Destination = "Different" }] });
                throw new Exception("modified plan accepted with a stale fingerprint");
            }
            catch (InvalidOperationException ex) when (ex.Message == "source-repair-archive-plan-invalid") { }
            var reserved = await Invoke(null, plan);
            if (Directory.EnumerateFileSystemEntries(Path.Combine(root.FullName, "archive")).Any())
                throw new Exception("archive reservation copied files before its identity could be recorded");
            if (await Invoke(reserved, plan) != reserved || await Invoke(reserved, null) != reserved)
                throw new Exception("archive adapter did not preserve the reserved identity");
            var archivePath = Path.Combine(root.FullName, "archive", "Album", "Track.flac");
            if (!(await File.ReadAllBytesAsync(archivePath)).SequenceEqual(contents))
                throw new Exception("archive payload did not match source");
            await ExerciseJournalHostAsync(root.FullName, python, pythonHash, helper, helperHash, manifest, plan);
        }
        finally { root.Delete(true); }
    }

    private static async Task ExerciseJournalHostAsync(string root, string python, string pythonHash,
        string helper, string helperHash, byte[] manifest, CloudSourcePathRepairPlan plan)
    {
        const string ns = "mnt:[123]";
        var boot = Guid.NewGuid().ToString();
        var specificationPath = Path.Combine(root, "source-repair.json");
        var manifestPath = Path.Combine(root, "manifest.json");
        var specification = new SourcePathRepairHostSpecification(boot, ns, python, pythonHash, helper, helperHash,
            manifestPath, root, "journal-archive", Identify(root), new() { [""] = plan.Binding.RootIdentity });
        var specBytes = JsonSerializer.SerializeToUtf8Bytes(specification, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        LinuxTrustedFile.CreateNew(specificationPath, specBytes);
        LinuxTrustedFile.CreateNew(manifestPath, manifest);
        var binding = plan.Binding with { HostSpecificationSha256 = Hash(specBytes) };
        plan = plan with { Binding = binding };
        plan = plan with { Fingerprint = CloudSourcePathRepairTransaction.Fingerprint(plan) };
        var state = new CloudSourcePathRepairState(plan, CloudSourcePathRepairPhase.PreservationIntent);
        var job = new CloudMigrationJob(binding.JobId, binding.ProfileId, binding.LibraryName,
            CloudMigrationDirections.LocalToVfs, CloudMigrationPhases.Failed, "conflict-path-collision",
            binding.LocalRoot, null, null, binding.RemoteName, binding.RemotePath, "", false, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SourcePathRepair: state);
        var destination = Path.Combine(root, "journal-archive");
        var journal = new CloudMigrationJournal(Path.Combine(root, "journal"));
        await using (var lease = await journal.AcquireAsync())
        {
            await lease.CompareExchangeAsync(null, job);
            var adapter = new SourcePathRepairJournal(lease);
            var snapshot = await adapter.ReadAsync(binding.JobId, default) ?? throw new Exception("repair unavailable");
            var active = new LinuxMaintenanceBarrier(new FixtureBarrier(new(true, true, "active", 123, 0, [123], boot, ns, ns)));
            try
            {
                await new SourcePathRepairArchiveHost(adapter, active, specificationPath).ReserveArchiveAsync(snapshot, default);
                throw new Exception("active service permitted archive reservation");
            }
            catch (InvalidOperationException ex) when (ex.Message == "maintenance-service-barrier-failed") { }
            if (Directory.Exists(destination)) throw new Exception("failed barrier changed archive");
            var stopped = new LinuxMaintenanceBarrier(new FixtureBarrier(new(true, true, "inactive", 0, 0, [], boot, ns, ns)));
            try
            {
                await new SourcePathRepairArchiveHost(adapter, stopped, manifestPath).ReserveArchiveAsync(snapshot, default);
                throw new Exception("unbound host specification permitted reservation");
            }
            catch (InvalidOperationException ex) when (ex.Message == "source-repair-host-specification-changed") { }
            if (Directory.Exists(destination)) throw new Exception("unbound specification changed archive");
            var host = new SourcePathRepairArchiveHost(adapter, stopped, specificationPath);
            var identity = await host.ReserveArchiveAsync(snapshot, default);
            if (await adapter.CompareExchangeAsync(snapshot, state with { ReservedArchiveIdentity = identity }, default) is null)
                throw new Exception("archive reservation could not be journaled");
            if (await adapter.CompareExchangeAsync(snapshot, state, default) is not null)
                throw new Exception("stale revision replaced archive reservation");
        }
        await using (var lease = await journal.AcquireAsync())
        {
            var adapter = new SourcePathRepairJournal(lease);
            var snapshot = await adapter.ReadAsync(binding.JobId, default) ?? throw new Exception("reservation missing after reopening journal");
            var stopped = new LinuxMaintenanceBarrier(new FixtureBarrier(new(true, true, "inactive", 0, 0, [], boot, ns, ns)));
            var host = new SourcePathRepairArchiveHost(adapter, stopped, specificationPath);
            if (await host.CopyArchiveAsync(snapshot, default) != snapshot.State.ReservedArchiveIdentity
                || await host.VerifyArchiveAsync(snapshot, default) != snapshot.State.ReservedArchiveIdentity)
                throw new Exception("verified archive does not match the durable reservation");
            var current = await lease.ReadAsync(binding.JobId);
            if (current is null || current.Revision != snapshot.Revision || current.Job.ConflictRecovery != job.ConflictRecovery
                || current.Job.Phase != job.Phase)
                throw new Exception("archive copy unexpectedly changed the journal");

            var moveHost = new SourcePathRepairMoveHost(adapter, stopped, specificationPath);
            await CheckPathsAsync(moveHost, snapshot, moved: false);
            var stateBeforeMove = snapshot.State with
            {
                Phase = CloudSourcePathRepairPhase.MoveIntent,
                Preservation = new(plan.Fingerprint, plan.PreservationManifestSha256, snapshot.State.ReservedArchiveIdentity!),
            };
            var intent = await adapter.CompareExchangeAsync(snapshot, stateBeforeMove, default)
                ?? throw new Exception("move intent could not be journaled");
            var move = new CloudSourcePathRepairDecision(CloudSourcePathRepairAction.Move, "fixture", MoveIndex: 0);
            try
            {
                await moveHost.ExecuteAsync(snapshot, move, default);
                throw new Exception("stale revision authorized a move");
            }
            catch (InvalidOperationException ex) when (ex.Message == "source-repair-move-journal-changed") { }
            await moveHost.ExecuteAsync(intent, move, default);
            await CheckPathsAsync(moveHost, intent, moved: true);
            var restore = await adapter.CompareExchangeAsync(intent, intent.State with
            {
                Phase = CloudSourcePathRepairPhase.RestoreIntent, Cursor = 1, RollbackRequested = true,
            }, default) ?? throw new Exception("restore intent could not be journaled");
            await moveHost.ExecuteAsync(restore,
                new(CloudSourcePathRepairAction.Restore, "fixture", MoveIndex: 0), default);
            await CheckPathsAsync(moveHost, restore, moved: false);

        }
    }

    private static async Task CheckPathsAsync(SourcePathRepairMoveHost host, CloudSourcePathRepairSnapshot snapshot, bool moved)
    {
        var paths = await host.ObserveAsync(snapshot, default);
        if (paths.Length != 1 || paths[0] != new CloudSourcePathRepairPathObservation(
            moved ? CloudSourcePathRepairPathState.Missing : CloudSourcePathRepairPathState.Expected,
            moved ? CloudSourcePathRepairPathState.Expected : CloudSourcePathRepairPathState.Missing))
            throw new Exception("move observation did not match the physical path state");
    }

    private sealed class FixtureBarrier(LinuxBarrierSnapshot snapshot) : ILinuxBarrierQuery
    {
        public Task<LinuxBarrierSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private static string Hash(byte[] contents) => Convert.ToHexStringLower(SHA256.HashData(contents));

    private static CloudSourcePathRepairIdentity Identify(string path)
    {
        var identity = LinuxTrustedFile.InspectDirectory(path);
        return new(identity.Device, identity.Inode, identity.MountId);
    }
}

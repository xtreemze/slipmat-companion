using System.Collections.Immutable;
using System.Text.Json;
using Jellyfin.Plugin.AudioGateway.Models;
using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudSourcePathRepairTransactionTests
{
    private const string JobId = "dec77f3659fc4e54a9df2cab051a54c5";
    private static readonly CloudSourcePathRepairIdentity Root = new(5, 7, 11);

    [Fact]
    public async Task PreservesEveryVersionBeforeMovingAndVerifiesBeforeRepaired()
    {
        var host = new FakeHost(MakePlan());
        var final = await RunAsync(host);

        Assert.Equal(CloudSourcePathRepairAction.Complete, final.Action);
        var state = host.Snapshot.State;
        Assert.Equal(CloudSourcePathRepairPhase.Repaired, state.Phase);
        Assert.True(CloudSourcePathRepairTransaction.HasStableReceipt(state));
        Assert.Equal(2, state.Cursor);
        Assert.Equal(host.Snapshot.State.Plan.PreservationManifestSha256, state.Preservation!.ManifestSha256);
        Assert.Equal("verified-archive-instance", state.Preservation.ArchiveIdentity);
        Assert.Equal(CloudSourcePathRepairVerificationKind.Repaired, state.Verification!.Kind);
        Assert.Equal(new[] { "preserve", "move-0", "move-1", "verify-repaired" }, host.Effects);
    }

    [Fact]
    public async Task ReplaysAfterCrashesBeforeAndAfterEachForwardJournalAndEffectBoundary()
    {
        var reference = new FakeHost(MakePlan());
        await RunAsync(reference);

        for (var boundary = 0; boundary < reference.Boundaries; boundary++)
        {
            var host = new FakeHost(MakePlan()) { CrashAt = boundary };
            await Assert.ThrowsAsync<TestCrash>(() => RunAsync(host));
            host.FreshProcess();
            var result = await RunAsync(host);
            Assert.Equal(CloudSourcePathRepairAction.Complete, result.Action);
            Assert.Equal(CloudSourcePathRepairPhase.Repaired, host.Snapshot.State.Phase);
            Assert.Equal(new[] { "preserve", "move-0", "move-1" },
                host.Effects.Where(effect => !effect.StartsWith("verify-", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackReconcilesWhetherTheInFlightMoveHappened(bool applied)
    {
        var host = new FakeHost(MakePlan());
        host.SetMovingPrefix(1, CloudSourcePathRepairPhase.MoveIntent, applied);
        host.RequestRollback();

        var result = await RunAsync(host);
        Assert.Equal(CloudSourcePathRepairAction.Complete, result.Action);
        Assert.Equal(CloudSourcePathRepairPhase.RolledBack, host.Snapshot.State.Phase);
        Assert.True(CloudSourcePathRepairTransaction.HasStableReceipt(host.Snapshot.State));
        Assert.Equal(applied ? new[] { "restore-1", "restore-0", "verify-original" }
            : new[] { "restore-0", "verify-original" }, host.Effects);
        Assert.Equal(CloudSourcePathRepairArchiveState.Verified, host.Archive.State);
    }

    [Fact]
    public async Task ReplaysAfterCrashesBeforeAndAfterEachRollbackJournalAndEffectBoundary()
    {
        var reference = new FakeHost(MakePlan());
        reference.SetMovingPrefix(1, CloudSourcePathRepairPhase.MoveIntent, inFlightApplied: true);
        reference.RequestRollback();
        await RunAsync(reference);

        for (var boundary = 0; boundary < reference.Boundaries; boundary++)
        {
            var host = new FakeHost(MakePlan()) { CrashAt = boundary };
            host.SetMovingPrefix(1, CloudSourcePathRepairPhase.MoveIntent, inFlightApplied: true);
            host.RequestRollback();
            await Assert.ThrowsAsync<TestCrash>(() => RunAsync(host));
            host.FreshProcess();
            var result = await RunAsync(host);
            Assert.Equal(CloudSourcePathRepairAction.Complete, result.Action);
            Assert.Equal(CloudSourcePathRepairPhase.RolledBack, host.Snapshot.State.Phase);
            Assert.Equal(new[] { "restore-1", "restore-0" },
                host.Effects.Where(effect => !effect.StartsWith("verify-", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task RollbackBeforePreservationDoesNotCreateAnArchiveOrMoveAnything()
    {
        var host = new FakeHost(MakePlan());
        host.RequestRollback();
        Assert.Equal(CloudSourcePathRepairAction.Complete, (await RunAsync(host)).Action);
        Assert.Equal(new[] { "verify-original" }, host.Effects);
        Assert.Equal(CloudSourcePathRepairPhase.RolledBack, host.Snapshot.State.Phase);
    }

    [Theory]
    [InlineData(CloudSourcePathRepairPathState.Foreign, CloudSourcePathRepairPathState.Missing)]
    [InlineData(CloudSourcePathRepairPathState.Expected, CloudSourcePathRepairPathState.Expected)]
    [InlineData(CloudSourcePathRepairPathState.Missing, CloudSourcePathRepairPathState.Missing)]
    [InlineData((CloudSourcePathRepairPathState)99, CloudSourcePathRepairPathState.Missing)]
    public async Task AmbiguousOrForeignPathsBlockEvenDuringRollback(CloudSourcePathRepairPathState source, CloudSourcePathRepairPathState destination)
    {
        var host = new FakeHost(MakePlan());
        host.SetMovingPrefix(0, CloudSourcePathRepairPhase.MoveIntent, inFlightApplied: false);
        host.RequestRollback();
        host.Transform = observation => observation with { Paths = observation.Paths.SetItem(0, new(source, destination)) };
        Assert.Equal(CloudSourcePathRepairAction.Blocked, (await RunAsync(host)).Action);
        Assert.Empty(host.Effects);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("barrier")]
    [InlineData("noreplace")]
    [InlineData("binding")]
    [InlineData("revision")]
    [InlineData("fingerprint")]
    [InlineData("foreign-archive")]
    [InlineData("replaced-archive")]
    public async Task LosingAnyCustodyFactBlocksWithoutARename(string missing)
    {
        var host = new FakeHost(MakePlan());
        host.SetMovingPrefix(0, CloudSourcePathRepairPhase.MoveIntent, inFlightApplied: false);
        host.Transform = observation => missing switch
        {
            "lease" => observation with { LeaseHeld = false },
            "barrier" => observation with { StartupBarrierHeld = false },
            "noreplace" => observation with { NoReplaceSupported = false },
            "binding" => observation with { BindingsMatch = false },
            "revision" => observation with { JobRevision = "stale" },
            "fingerprint" => observation with { PlanFingerprint = Digest('f') },
            "foreign-archive" => observation with { Archive = new(CloudSourcePathRepairArchiveState.Foreign) },
            _ => observation with { Archive = observation.Archive with { Identity = "another-archive" } },
        };
        Assert.Equal(CloudSourcePathRepairAction.Blocked, (await RunAsync(host)).Action);
        Assert.Empty(host.Effects);
    }

    [Fact]
    public async Task LosingWholeJobCasCannotStartAnEffect()
    {
        var host = new FakeHost(MakePlan()) { RejectCas = true };
        Assert.Equal(CloudSourcePathRepairAction.Blocked, (await RunAsync(host)).Action);
        Assert.Empty(host.Effects);
    }

    [Fact]
    public async Task InterruptedImmutablePreservationCanResumeBeforeMoving()
    {
        var host = new FakeHost(MakePlan());
        host.State(state => state with { Phase = CloudSourcePathRepairPhase.PreservationIntent });
        host.Archive = new(CloudSourcePathRepairArchiveState.Resumable, "incomplete-owned-archive");
        Assert.Equal(CloudSourcePathRepairAction.Complete, (await RunAsync(host)).Action);
        Assert.Equal(new[] { "preserve", "move-0", "move-1", "verify-repaired" }, host.Effects);
    }

    [Fact]
    public async Task StartupOwnershipFailsClosedForUnknownTamperedAndUnverifiedTerminalState()
    {
        var host = new FakeHost(MakePlan());
        var initial = host.Snapshot.State;
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt(initial));
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((initial with { Plan = null! })));
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((initial with { Plan = initial.Plan with { SchemaVersion = 99 } })));
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((initial with { Phase = (CloudSourcePathRepairPhase)99 })));
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((initial with { Phase = CloudSourcePathRepairPhase.Repaired })));
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((initial with { Cursor = -1 })));
        Assert.Equal(CloudSourcePathRepairAction.Complete, (await RunAsync(host)).Action);
        var done = host.Snapshot.State;
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((done with { RollbackRequested = true })));
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((done with { Plan = done.Plan with { TargetNamespaceSha256 = Digest('f') } })));
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt((done with { Verification = done.Verification! with { NamespaceSha256 = Digest('f') } })));
        var json = JsonSerializer.Serialize(done);
        var roundTrip = JsonSerializer.Deserialize<CloudSourcePathRepairState>(json);
        Assert.True(CloudSourcePathRepairTransaction.HasStableReceipt(roundTrip!));
    }

    [Fact]
    public async Task FullVerificationMustMatchTheAdmittedRepairedNamespace()
    {
        var host = new FakeHost(MakePlan());
        host.Transform = observation => observation.Verification is { } receipt
            ? observation with { Verification = receipt with { NamespaceSha256 = Digest('f') } } : observation;
        var result = await RunAsync(host);
        Assert.Equal(CloudSourcePathRepairAction.Blocked, result.Action);
        Assert.Equal(CloudSourcePathRepairPhase.VerifyRepairedIntent, host.Snapshot.State.Phase);
        Assert.False(CloudSourcePathRepairTransaction.HasStableReceipt(host.Snapshot.State));
        Assert.Equal("verify-repaired", host.Effects[^1]);
    }

    [Theory]
    [InlineData("A/first", "Elsewhere/new")]
    [InlineData("A/first", "A/FIRST")]
    [InlineData("A/first", "A/../new")]
    [InlineData("A/first", "A/second")]
    [InlineData("A/first/sub", "A/first/renamed")]
    public async Task InvalidOrOverlappingMovesBlockEvenWithACorrectFingerprint(string first, string target)
    {
        var plan = MakePlan();
        var suspect = plan.Moves.SetItem(0, plan.Moves[0] with { Source = first, Destination = target });
        if (first == "A/first/sub")
            suspect = suspect.SetItem(1, suspect[1] with { Source = "A/first", Destination = "A/renamed" });
        var host = new FakeHost(Seal(plan with { Moves = suspect }));
        Assert.Equal(CloudSourcePathRepairAction.Blocked, (await RunAsync(host)).Action);
        Assert.Empty(host.Effects);
    }

    private static Task<CloudSourcePathRepairDecision> RunAsync(FakeHost host)
        => CloudSourcePathRepairTransaction.RunAsync(JobId, host);

    private static string Digest(char value) => new(value, 64);

    private static CloudSourcePathRepairPlan MakePlan() => Seal(new(1,
        new(JobId, Guid.NewGuid(), Guid.NewGuid(), "Synthetic library", "music", "/mnt/source",
            "cloud", "music", Root, new(1, Digest('a'), 12, 99, Digest('b')), Digest('c')),
        [
            new(CloudSourcePathRepairKind.Directory, "A/first", "A/preserved-first", Root with { Inode = 12 }),
            new(CloudSourcePathRepairKind.File, "A/second", "A/preserved-second", Root with { Inode = 13 }),
        ],
        Digest('d'), Digest('e'), ""));

    private static CloudSourcePathRepairPlan Seal(CloudSourcePathRepairPlan plan)
        => plan with { Fingerprint = CloudSourcePathRepairTransaction.Fingerprint(plan) };

    private sealed class TestCrash : Exception;

    private sealed class FakeHost(CloudSourcePathRepairPlan plan) : ICloudSourcePathRepairOperations
    {
        private int _revision;
        private readonly bool[] _moved = new bool[plan.Moves.Length];
        private CloudSourcePathRepairVerification? _verification;
        public CloudSourcePathRepairSnapshot Snapshot { get; private set; } = new("revision-0", new(plan, CloudSourcePathRepairPhase.Prepared));
        public CloudSourcePathRepairArchiveObservation Archive { get; set; } = new(CloudSourcePathRepairArchiveState.Missing);
        public List<string> Effects { get; } = [];
        public int? CrashAt { get; set; }
        public int Boundaries { get; private set; }
        public bool RejectCas { get; init; }
        public Func<CloudSourcePathRepairObservation, CloudSourcePathRepairObservation>? Transform { get; set; }

        public void FreshProcess() { CrashAt = null; _verification = null; }
        public void State(Func<CloudSourcePathRepairState, CloudSourcePathRepairState> update)
            => Snapshot = new("revision-" + (++_revision), update(Snapshot.State));
        public void RequestRollback() => State(state => state with { RollbackRequested = true });

        public void SetMovingPrefix(int prefix, CloudSourcePathRepairPhase phase, bool inFlightApplied)
        {
            Archive = new(CloudSourcePathRepairArchiveState.Verified, "verified-archive-instance", plan.PreservationManifestSha256);
            State(state => state with { Phase = phase, Cursor = prefix,
                Preservation = new(plan.Fingerprint, plan.PreservationManifestSha256, Archive.Identity!) });
            for (var i = 0; i < prefix + (inFlightApplied ? 1 : 0); i++) _moved[i] = true;
        }

        public Task<CloudSourcePathRepairSnapshot?> ReadAsync(string jobId, CancellationToken ct)
            => Task.FromResult<CloudSourcePathRepairSnapshot?>(Snapshot);

        public Task<CloudSourcePathRepairObservation> ObserveAsync(CloudSourcePathRepairSnapshot snapshot, CancellationToken ct)
        {
            var observation = new CloudSourcePathRepairObservation(snapshot.Revision, plan.Fingerprint, true, true, true, true,
                _moved.Select(value => value
                    ? new CloudSourcePathRepairPathObservation(CloudSourcePathRepairPathState.Missing, CloudSourcePathRepairPathState.Expected)
                    : new CloudSourcePathRepairPathObservation(CloudSourcePathRepairPathState.Expected, CloudSourcePathRepairPathState.Missing))
                    .ToImmutableArray(), Archive, _verification);
            return Task.FromResult(Transform?.Invoke(observation) ?? observation);
        }

        public Task<CloudSourcePathRepairSnapshot?> CompareExchangeAsync(CloudSourcePathRepairSnapshot expected, CloudSourcePathRepairState replacement, CancellationToken ct)
        {
            Boundary();
            if (RejectCas || expected != Snapshot) return Task.FromResult<CloudSourcePathRepairSnapshot?>(null);
            State(_ => replacement);
            Boundary();
            return Task.FromResult<CloudSourcePathRepairSnapshot?>(Snapshot);
        }

        public Task ExecuteAsync(CloudSourcePathRepairSnapshot expected, CloudSourcePathRepairDecision decision, CancellationToken ct)
        {
            Boundary();
            Assert.Equal(Snapshot, expected);
            switch (decision.Action)
            {
                case CloudSourcePathRepairAction.Preserve:
                    Assert.Equal(CloudSourcePathRepairPhase.PreservationIntent, Snapshot.State.Phase);
                    Assert.DoesNotContain(true, _moved);
                    Archive = new(CloudSourcePathRepairArchiveState.Verified, "verified-archive-instance", plan.PreservationManifestSha256);
                    Effects.Add("preserve");
                    break;
                case CloudSourcePathRepairAction.Move:
                    Assert.Equal(CloudSourcePathRepairPhase.MoveIntent, Snapshot.State.Phase);
                    Assert.NotNull(Snapshot.State.Preservation);
                    Assert.Equal(CloudSourcePathRepairArchiveState.Verified, Archive.State);
                    Assert.False(_moved[decision.MoveIndex!.Value]);
                    _moved[decision.MoveIndex.Value] = true;
                    Effects.Add("move-" + decision.MoveIndex);
                    break;
                case CloudSourcePathRepairAction.Restore:
                    Assert.Equal(CloudSourcePathRepairPhase.RestoreIntent, Snapshot.State.Phase);
                    Assert.True(_moved[decision.MoveIndex!.Value]);
                    _moved[decision.MoveIndex.Value] = false;
                    Effects.Add("restore-" + decision.MoveIndex);
                    break;
                case CloudSourcePathRepairAction.VerifyRepaired:
                    Assert.All(_moved, moved => Assert.True(moved));
                    _verification = new(plan.Fingerprint, CloudSourcePathRepairVerificationKind.Repaired,
                        plan.TargetNamespaceSha256, plan.PreservationManifestSha256);
                    Effects.Add("verify-repaired");
                    break;
                case CloudSourcePathRepairAction.VerifyOriginal:
                    Assert.DoesNotContain(true, _moved);
                    _verification = new(plan.Fingerprint, CloudSourcePathRepairVerificationKind.Original,
                        plan.Binding.Source.NamespaceSha256, plan.PreservationManifestSha256);
                    Effects.Add("verify-original");
                    break;
                default: throw new InvalidOperationException();
            }
            Boundary();
            return Task.CompletedTask;
        }

        private void Boundary()
        {
            if (Boundaries++ == CrashAt) throw new TestCrash();
        }
    }
}

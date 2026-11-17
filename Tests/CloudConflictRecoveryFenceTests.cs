using Jellyfin.Plugin.AudioGateway.Services;
using Xunit;

namespace Jellyfin.Plugin.AudioGateway.Tests;

public sealed class CloudConflictRecoveryFenceTests
{
    private const string JobId = "fa72db0c54b74718af63bf4b60c0d7fb";
    private const string PlanDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Acquire_ClosesAdmissionAndDrainsExistingCommands()
    {
        var fence = new CloudConflictRecoveryFence();
        Assert.True(fence.TryEnterCommand(out var ordinary));

        var acquire = fence.AcquireAsync(JobId, PlanDigest);
        Assert.False(acquire.IsCompleted);
        Assert.False(fence.TryEnterCommand(out _));

        ordinary!.Dispose();
        await using var lease = await acquire;

        Assert.False(fence.TryEnterCommand(out _));
        var ownerCommandStarted = false;
        await lease.RunAsync(() =>
        {
            Assert.True(fence.TryEnterCommand(out var command));
            using (command)
            {
                ownerCommandStarted = true;
                return Task.FromResult(true);
            }
        });
        Assert.True(ownerCommandStarted);
    }

    [Fact]
    public async Task Dispose_DrainsStartedOwnerCommandBeforeReopeningAdmission()
    {
        var fence = new CloudConflictRecoveryFence();
        var lease = await fence.AcquireAsync(JobId, PlanDigest);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var ownerAction = lease.RunAsync(async () =>
        {
            Assert.True(fence.TryEnterCommand(out var command));
            using (command)
            {
                entered.SetResult();
                await release.Task;
                return true;
            }
        });
        await entered.Task;

        var dispose = lease.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.False(fence.TryEnterCommand(out _));

        release.SetResult();
        await ownerAction;
        await dispose;
        Assert.True(fence.TryEnterCommand(out var reopened));
        reopened!.Dispose();
    }

    [Fact]
    public async Task CancelledAcquire_ReopensOrdinaryAdmissionWithoutDroppingExistingPermit()
    {
        var fence = new CloudConflictRecoveryFence();
        Assert.True(fence.TryEnterCommand(out var existing));
        using var cancellation = new CancellationTokenSource();
        var acquire = fence.AcquireAsync(JobId, PlanDigest, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
        Assert.True(fence.TryEnterCommand(out var concurrent));
        concurrent!.Dispose();
        existing!.Dispose();
    }

    [Fact]
    public async Task DrainAsync_WaitsForOwnerCommandAndHonorsCancellation()
    {
        var fence = new CloudConflictRecoveryFence();
        await using var lease = await fence.AcquireAsync(JobId, PlanDigest);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerAction = lease.RunAsync(async () =>
        {
            Assert.True(fence.TryEnterCommand(out var command));
            using (command)
            {
                entered.SetResult();
                await release.Task;
                return true;
            }
        });
        await entered.Task;

        using var cancellation = new CancellationTokenSource();
        var drain = lease.DrainAsync(cancellation.Token);
        Assert.False(drain.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);

        release.SetResult();
        await ownerAction;
        await lease.DrainAsync();
    }

    [Fact]
    public async Task InheritedStaleOwnerContext_CannotStartCommandAfterDispose()
    {
        var fence = new CloudConflictRecoveryFence();
        var lease = await fence.AcquireAsync(JobId, PlanDigest);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childAllowed = true;

        await lease.RunAsync(async () =>
        {
            _ = Task.Run(async () =>
            {
                childStarted.SetResult();
                await continueChild.Task;
                childAllowed = fence.TryEnterCommand(out var permit);
                permit?.Dispose();
                childFinished.SetResult();
            });
            await childStarted.Task;
            return true;
        });

        await lease.DisposeAsync();
        await using var replacement = await fence.AcquireAsync(JobId, PlanDigest);
        Assert.NotEqual(lease.SessionId, replacement.SessionId);
        continueChild.SetResult();
        // The inherited execution context is deliberately stale.
        await childFinished.Task;
        Assert.False(childAllowed);
    }

    [Fact]
    public async Task Lease_BindsCanonicalJobPlanAndRejectsStaleOwnerCalls()
    {
        var fence = new CloudConflictRecoveryFence();
        var lease = await fence.AcquireAsync(JobId, PlanDigest);
        Assert.Equal(JobId, lease.JobId);
        Assert.Equal(PlanDigest, lease.PlanBindingSha256);
        Assert.NotEqual(Guid.Empty, lease.SessionId);
        Assert.True(lease.Generation > 0);

        await lease.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lease.RunAsync(() => Task.FromResult(true)));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fence.AcquireAsync("not-a-job", PlanDigest));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fence.AcquireAsync(JobId, "not-a-digest"));
    }
}

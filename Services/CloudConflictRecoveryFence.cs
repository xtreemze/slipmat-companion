namespace Jellyfin.Plugin.AudioGateway.Services;

/// <summary>
/// Process-wide cooperative exclusion for Companion-owned rclone commands while a
/// cloud conflict-recovery transaction is active. It does not exclude external writers.
/// </summary>
public sealed class CloudConflictRecoveryFence
{
    private static readonly AsyncLocal<RecoveryLease?> AmbientLease = new();

    /// <summary>The shared fence used by normal production command hosts.</summary>
    public static CloudConflictRecoveryFence Shared { get; } = new();

    private readonly object _sync = new();
    private bool _admissionClosed;
    private bool _acquirePending;
    private long _generation;
    private int _inFlight;
    private RecoveryLease? _owner;
    private TaskCompletionSource _drained = CompletedSource();

    /// <summary>
    /// Closes ordinary command admission, drains all already-started Companion
    /// commands, then returns a server-created owner lease bound to this job and plan.
    /// </summary>
    public async Task<RecoveryLease> AcquireAsync(
        string jobId,
        string planBindingSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateBinding(jobId, planBindingSha256);

        Task drain;
        lock (_sync)
        {
            if (_admissionClosed || _owner is not null || _acquirePending)
                throw new InvalidOperationException("cloud-recovery-fence-busy");

            _admissionClosed = true;
            _acquirePending = true;
            drain = _inFlight == 0 ? Task.CompletedTask : _drained.Task;
        }

        try
        {
            await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                _acquirePending = false;
                _admissionClosed = false;
            }
            throw;
        }

        lock (_sync)
        {
            if (!_acquirePending || _inFlight != 0 || _owner is not null)
            {
                _acquirePending = false;
                _admissionClosed = false;
                throw new InvalidOperationException("cloud-recovery-fence-drain-lost");
            }

            var lease = new RecoveryLease(
                this,
                jobId,
                planBindingSha256.ToLowerInvariant(),
                Guid.NewGuid(),
                checked(++_generation));
            _owner = lease;
            _acquirePending = false;
            return lease;
        }
    }

    /// <summary>
    /// Attempts to enter a command. Normal callers are rejected while recovery owns
    /// the fence or while acquisition is draining. Recovery commands require its
    /// inherited server-side lease context.
    /// </summary>
    public bool TryEnterCommand(out CommandPermit? permit)
    {
        lock (_sync)
        {
            var ambient = AmbientLease.Value;
            if (_owner is not null)
            {
                if (!ReferenceEquals(ambient, _owner) || !_owner.CanRunCommands)
                {
                    permit = null;
                    return false;
                }
                permit = AddPermit(_owner);
                return true;
            }

            // A propagated lease context is stale once its owner has gone away.
            if (_admissionClosed || ambient is not null)
            {
                permit = null;
                return false;
            }

            permit = AddPermit(null);
            return true;
        }
    }

    private CommandPermit AddPermit(RecoveryLease? owner)
    {
        if (_inFlight == 0)
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        checked { _inFlight++; }
        owner?.AddInFlight();
        return new CommandPermit(this, owner);
    }

    private void ReleasePermit(RecoveryLease? owner)
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (_inFlight <= 0 || owner is not null && owner.InFlight <= 0)
                throw new InvalidOperationException("cloud-recovery-fence-permit-underflow");
            _inFlight--;
            owner?.RemoveInFlight();
            if (_inFlight == 0) drained = _drained;
            owner?.SignalIfDrained();
        }
        drained?.TrySetResult();
    }

    private async ValueTask ReleaseOwnerAsync(RecoveryLease lease)
    {
        Task drain;
        lock (_sync)
        {
            if (!ReferenceEquals(_owner, lease))
            {
                lease.MarkDisposed();
                return;
            }
            lease.BeginDispose();
            drain = lease.InFlight == 0 ? Task.CompletedTask : lease.Drained.Task;
        }

        await drain.ConfigureAwait(false);

        lock (_sync)
        {
            if (!ReferenceEquals(_owner, lease) || lease.InFlight != 0)
                throw new InvalidOperationException("cloud-recovery-fence-owner-changed");
            _owner = null;
            _admissionClosed = false;
            lease.MarkDisposed();
        }
    }

    private async Task<T> RunOwnedAsync<T>(RecoveryLease lease, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        CommandPermit runPermit;
        lock (_sync)
        {
            if (!ReferenceEquals(_owner, lease) || !lease.CanRunCommands)
                throw new InvalidOperationException("cloud-recovery-fence-owner-stale");
            runPermit = AddPermit(lease);
        }

        var prior = AmbientLease.Value;
        AmbientLease.Value = lease;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            AmbientLease.Value = prior;
            runPermit.Dispose();
        }
    }

    private async Task DrainOwnedAsync(RecoveryLease lease, CancellationToken cancellationToken)
    {
        Task drain;
        lock (_sync)
        {
            if (!ReferenceEquals(_owner, lease) || lease.IsDisposed)
                throw new InvalidOperationException("cloud-recovery-fence-owner-stale");
            drain = lease.InFlight == 0 ? Task.CompletedTask : lease.Drained.Task;
        }
        await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateBinding(string jobId, string planBindingSha256)
    {
        if (!Guid.TryParseExact(jobId, "N", out _))
            throw new ArgumentException("cloud-recovery-fence-job-id-invalid", nameof(jobId));
        if (planBindingSha256.Length != 64 || planBindingSha256.Any(ch => !Uri.IsHexDigit(ch)))
            throw new ArgumentException("cloud-recovery-fence-plan-binding-invalid", nameof(planBindingSha256));
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    /// <summary>One active recovery owner; constructed only by the fence.</summary>
    public sealed class RecoveryLease : IAsyncDisposable
    {
        private readonly CloudConflictRecoveryFence _fence;
        private bool _closing;
        private bool _disposed;
        private int _inFlight;
        private TaskCompletionSource _drained = CompletedSource();

        internal RecoveryLease(
            CloudConflictRecoveryFence fence,
            string jobId,
            string planBindingSha256,
            Guid sessionId,
            long generation)
        {
            _fence = fence;
            JobId = jobId;
            PlanBindingSha256 = planBindingSha256;
            SessionId = sessionId;
            Generation = generation;
        }

        /// <summary>Canonical durable job identifier bound at acquisition.</summary>
        public string JobId { get; }

        /// <summary>SHA-256 of the reviewed server-side plan binding.</summary>
        public string PlanBindingSha256 { get; }

        /// <summary>Unpredictable server-created session identity.</summary>
        public Guid SessionId { get; }

        /// <summary>Monotonic in-process generation that rejects stale owners.</summary>
        public long Generation { get; }

        internal bool CanRunCommands => !_closing && !_disposed;
        internal bool IsDisposed => _disposed;
        internal int InFlight => _inFlight;
        internal TaskCompletionSource Drained => _drained;

        /// <summary>Runs recovery work in the unforgeable owner context.</summary>
        public Task<T> RunAsync<T>(Func<Task<T>> action) => _fence.RunOwnedAsync(this, action);

        /// <summary>Waits until every started recovery command has settled.</summary>
        public Task DrainAsync(CancellationToken cancellationToken = default)
            => _fence.DrainOwnedAsync(this, cancellationToken);

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _fence.ReleaseOwnerAsync(this);

        internal void BeginDispose() => _closing = true;
        internal void MarkDisposed() { _closing = true; _disposed = true; }

        internal void AddInFlight()
        {
            if (_inFlight == 0)
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            checked { _inFlight++; }
        }

        internal void RemoveInFlight()
        {
            if (_inFlight <= 0) throw new InvalidOperationException("cloud-recovery-fence-owner-permit-underflow");
            _inFlight--;
        }

        internal void SignalIfDrained()
        {
            if (_inFlight == 0) _drained.TrySetResult();
        }
    }

    /// <summary>Tracks one child command through process/task settlement.</summary>
    public sealed class CommandPermit : IDisposable
    {
        private CloudConflictRecoveryFence? _fence;
        private readonly RecoveryLease? _owner;

        internal CommandPermit(CloudConflictRecoveryFence fence, RecoveryLease? owner)
        {
            _fence = fence;
            _owner = owner;
        }

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _fence, null)?.ReleasePermit(_owner);
    }
}

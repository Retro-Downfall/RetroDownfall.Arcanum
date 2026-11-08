using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>
/// One host's retained installation lock. Closing stops admission immediately; in-process mutation
/// pins retain the OS lock until their I/O drains. This is never evidence of child-process extinction.
/// </summary>
internal sealed class GuildWorkspaceLeaseHostLock : IDisposable
{
    private readonly object _sync = new();

    private readonly FileHandleIdentity _identity;

    private RetainedExclusiveFileLock? _held;

    private bool _closing;

    private int _pins;

    private GuildWorkspaceLeaseHostLock(RetainedExclusiveFileLock held, FileHandleIdentity identity)
    {
        _held = held;

        _identity = identity;
    }

    internal Guid HostInstanceId { get; } = Guid.NewGuid();

    internal static Result<GuildWorkspaceLeaseHostLock> TryAcquire(string path)
    {
        try
        {
            RetainedExclusiveFileLockAcquisitionResult acquired = RetainedExclusiveFileLock.Acquire(path);

            if (acquired.Disposition != RetainedExclusiveFileLockAcquisitionDisposition.Acquired || acquired.Lock is null)
            {
                return acquired.Disposition == RetainedExclusiveFileLockAcquisitionDisposition.Contended
                    ? new Error(ErrorCodes.Guild.LeaseConflict, "Another host holds the Guild installation mutation lock.")
                    : WorkspaceLeaseIdentityResolver.Unavailable;
            }

            if (!acquired.Lock.ValidateCurrentIdentity(null, out FileHandleIdentity identity))
            {
                acquired.Lock.Dispose();

                return WorkspaceLeaseIdentityResolver.Unavailable;
            }

            return new GuildWorkspaceLeaseHostLock(acquired.Lock, identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return WorkspaceLeaseIdentityResolver.Unavailable;
        }
    }

    internal Result Validate()
    {
        lock (_sync)
        {
            return ValidateUnderGate();
        }
    }

    internal Result<IDisposable> Pin()
    {
        lock (_sync)
        {
            Result valid = ValidateUnderGate();

            if (valid.IsFailure || _pins == int.MaxValue)
            {
                return WorkspaceLeaseIdentityResolver.Unavailable;
            }

            _pins++;

            return Result<IDisposable>.Success(new MutationPin(this));
        }
    }

    internal Result<ManagedHostStopProof> ProvePriorManagedHostStopped() => ManagedHostStopProof.Create(this);

    public void Dispose()
    {
        lock (_sync)
        {
            _closing = true;

            ReleaseWhenDrained();
        }
    }

    private Result ValidateUnderGate() => !_closing && _held is not null && _held.ValidateCurrentIdentity(_identity, out _)
        ? Result.Success()
        : Result.Failure(WorkspaceLeaseIdentityResolver.Unavailable);

    private void ReleasePin()
    {
        lock (_sync)
        {
            _pins--;

            ReleaseWhenDrained();
        }
    }

    private void ReleaseWhenDrained()
    {
        if (_closing && _pins == 0)
        {
            _held?.Dispose();

            _held = null;
        }
    }

    private sealed class MutationPin(GuildWorkspaceLeaseHostLock owner) : IDisposable
    {
        private GuildWorkspaceLeaseHostLock? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleasePin();
    }

    /// <summary>
    /// A capability tied to the same still-valid acquired lock. It proves only that a prior managed
    /// host cannot retain this installation lock; no process-tree or arbitrary OS writer claim follows.
    /// </summary>
    internal sealed class ManagedHostStopProof
    {
        private readonly GuildWorkspaceLeaseHostLock _owner;

        private ManagedHostStopProof(GuildWorkspaceLeaseHostLock owner) => _owner = owner;

        internal Guid HostInstanceId => _owner.HostInstanceId;

        internal Result Validate() => _owner.Validate();

        internal Result<IDisposable> Pin() => _owner.Pin();

        internal static Result<ManagedHostStopProof> Create(GuildWorkspaceLeaseHostLock owner)
        {
            lock (owner._sync)
            {
                Result valid = owner.ValidateUnderGate();

                return valid.IsSuccess
                    ? Result<ManagedHostStopProof>.Success(new ManagedHostStopProof(owner))
                    : Result<ManagedHostStopProof>.Failure(valid.Error);
            }
        }
    }
}

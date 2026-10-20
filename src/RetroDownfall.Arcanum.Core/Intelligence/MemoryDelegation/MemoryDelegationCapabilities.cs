using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

/// <summary>Request-owned identity. No serialized coordinate can recreate this registration.</summary>
public sealed class MemoryDelegationRoot : IAsyncDisposable
{
    internal MemoryDelegationRoot(MemoryDelegationCoordinator owner, MemoryDelegationCoordinator.Registration registration)
    {
        Owner = owner;

        Registration = registration;
    }

    internal MemoryDelegationCoordinator Owner { get; }

    internal MemoryDelegationCoordinator.Registration Registration { get; }

    public CancellationToken Revocation => Registration.Cancellation.Token;

    public bool IsConsumed => Volatile.Read(ref Registration.Consumed);

    public DateTimeOffset ExpiresAtUtc => Registration.ExpiresAtUtc;

    public ValueTask DisposeAsync() => Owner.CompleteAsync(this);
}

/// <summary>One host-minted child, bound to the immutable executed task digest.</summary>
public sealed class MemoryDelegationChild : IAsyncDisposable
{
    internal MemoryDelegationChild(MemoryDelegationRoot root, Guid childId, CovenantDigest taskDigest)
    {
        Root = root;

        ChildId = childId;

        TaskDigest = taskDigest;

        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(root.Revocation);

        Revocation = Cancellation.Token;
    }

    public ValueTask DisposeAsync() => Root.Owner.CompleteChildAsync(this);

    internal CancellationTokenSource Cancellation { get; }

    internal bool Completed;

    internal int Effects;

    internal TaskCompletionSource Drained = CompletedSignal();

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        signal.SetResult();

        return signal;
    }

    internal MemoryDelegationRoot Root { get; }

    public Guid ChildId { get; }

    public CovenantDigest TaskDigest { get; }

    public CancellationToken Revocation { get; }

    public MemoryEvaluationSourceSnapshot Sources => (Volatile.Read(ref Completed) ? null : Root.Registration.Snapshot) ?? throw new InvalidOperationException("The memory delegation source has been released.");
}

/// <summary>Retain through physical send/enumeration/disposal or complete protected serialization.</summary>
public sealed class MemoryDelegationEffect : IAsyncDisposable
{
    private MemoryDelegationRoot? _root;

    private readonly MemoryDelegationChild? _child;

    internal MemoryDelegationEffect(MemoryDelegationRoot root, MemoryDelegationChild? child = null)
    {
        _root = root;

        _child = child;
    }

    public CancellationToken Revocation
    {
        get
        {
            MemoryDelegationRoot? root = Volatile.Read(ref _root);

            return root is null ? new CancellationToken(true) : _child?.Revocation ?? root.Revocation;
        }
    }

    public ValueTask DisposeAsync()
    {
        MemoryDelegationRoot? root = Interlocked.Exchange(ref _root, null);

        root?.Owner.ReleaseEffect(root, _child);

        return ValueTask.CompletedTask;
    }
}

namespace RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

public interface IMemoryDelegationMutationBarrier
{
    ValueTask<MemoryDelegationMutation> BeginMutationAsync(CancellationToken ct = default);
}

/// <summary>Exclusive mutation ownership, retained until commit or rollback. Nested writers borrow this exact token.</summary>
public sealed class MemoryDelegationMutation : IAsyncDisposable
{
    private MemoryDelegationCoordinator? _owner;

    internal MemoryDelegationMutation(MemoryDelegationCoordinator owner) => _owner = owner;

    public bool IsOwnedBy(IMemoryDelegationMutationBarrier owner) => ReferenceEquals(Volatile.Read(ref _owner), owner);

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _owner, null)?.EndMutation();

        return ValueTask.CompletedTask;
    }
}

public static class MemoryDelegationMutationBarrierExtensions
{
    public static async ValueTask<MemoryDelegationMutation?> BeginOptionalAsync(this IMemoryDelegationMutationBarrier? barrier, CancellationToken ct = default) =>
        barrier is null ? null : await barrier.BeginMutationAsync(ct).ConfigureAwait(false);
}

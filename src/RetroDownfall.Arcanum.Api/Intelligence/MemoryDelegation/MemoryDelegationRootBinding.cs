using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

internal sealed class MemoryDelegationRootBinding(PingRequest request, ArcanumInvocationContext invocation,
    Guid rootId, MemoryDelegationRoot root, MemoryDelegationCoordinator coordinator,
    Func<CancellationToken, ValueTask<Result>>? revalidate = null)
{
    private static readonly AsyncLocal<MemoryDelegationRootBinding?> Slot = new();

    private readonly object _sensitivityLock = new();

    private int _captured;

    private readonly PingRequest _request = request;

    private readonly ArcanumInvocationContext _invocation = invocation;

    internal MemoryDelegationRoot Root { get; } = root;

    internal static IDisposable Enter(MemoryDelegationRootBinding? binding)
    {
        var previous = Slot.Value;

        Slot.Value = binding;

        return new Scope(previous);
    }

    internal static MemoryDelegationRootBinding? TryCapture(PingRequest request, ArcanumInvocationContext invocation)
    {
        var binding = Slot.Value;

        return binding is not null && ReferenceEquals(request, binding._request) && ReferenceEquals(invocation, binding._invocation)
            && Interlocked.CompareExchange(ref binding._captured, 1, 0) == 0 ? binding : null;
    }

    internal async Task<Result<MemoryDelegationChild>> ConsumeAsync(Guid childId, CovenantDigest digest, CancellationToken ct)
    {
        if (revalidate is not null)
        {
            Result valid = await revalidate(ct).ConfigureAwait(false);

            if (valid.IsFailure) return valid.Error;
        }

        return await coordinator.ConsumeAsync(Root, rootId, childId, digest, ct).ConfigureAwait(false);
    }

    private ProviderCallSensitivity? _derivedSensitivity;

    internal void RecordDerived(ProviderCallSensitivity sensitivity)
    {
        lock (_sensitivityLock) _derivedSensitivity = ProviderCallSensitivities.Merge(_derivedSensitivity, sensitivity);
    }

    internal MemoryDelegationProtection? CaptureOutputProtection()
    {
        if (!Root.IsConsumed) return null;

        ProviderCallSensitivity sensitivity;

        lock (_sensitivityLock) sensitivity = ProviderCallSensitivities.Merge(_derivedSensitivity, Root.Sensitivity);

        return new(sensitivity, Root.Revocation, AdmitOutputAsync);
    }

    private async Task<Result<MemoryDelegationEffect>> AdmitOutputAsync(CancellationToken ct)
    {
        if (revalidate is not null)
        {
            Result valid = await revalidate(ct).ConfigureAwait(false);

            if (valid.IsFailure) return valid.Error;
        }

        return await coordinator.AdmitEffectAsync(Root, ct).ConfigureAwait(false);
    }

    private sealed class Scope(MemoryDelegationRootBinding? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Slot.Value = previous;
        }
    }
}

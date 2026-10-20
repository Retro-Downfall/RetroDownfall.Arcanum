using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

/// <summary>One local publication's counted effect, released after its writer and events finish.</summary>
internal sealed class MemoryDelegationPublication(CancellationToken cancellationToken) : IAsyncDisposable
{
    private MemoryDelegationEffect? _effect;

    private CancellationTokenSource? _cancellation;

    internal CancellationToken Token => _cancellation?.Token ?? cancellationToken;

    internal async Task<Result> AdmitAsync(MemoryDelegationProtection protection)
    {
        var admitted = await protection.AdmitAsync(cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted.Error;

        _effect = admitted.Value;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _effect.Revocation);

        Token.ThrowIfCancellationRequested();

        return Result.Success();
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Dispose();

        if (_effect is not null) await _effect.DisposeAsync().ConfigureAwait(false);
    }
}

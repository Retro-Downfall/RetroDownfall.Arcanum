using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelCallExecutor
{
    private ValueTask<IDisposable?> EnterProviderWorkAsync(IChatClient client, ProviderSettings? provider, CancellationToken cancellationToken) =>
        EnterProviderWorkAsync(ProviderWorkClientIdentity.Lookup(client), provider, cancellationToken);

    private ValueTask<IDisposable?> EnterProviderWorkAsync(IEmbeddingGenerator<string, Embedding<float>> client, ProviderSettings? provider, CancellationToken cancellationToken) =>
        EnterProviderWorkAsync(ProviderWorkClientIdentity.Lookup(client), provider, cancellationToken);

    private async ValueTask<IDisposable?> EnterProviderWorkAsync(ProviderWorkClientIdentity? identity, ProviderSettings? provider, CancellationToken cancellationToken)
    {
        ProviderResourceKey? resource = identity is not null ? identity.Resource
            : provider is not null ? ProviderResourceKey.FromProvider(provider, _settings?.CurrentValue) : null;

        if (resource is null)
        {
            if (identity is not null && !identity.IsCurrent(_settings?.CurrentValue))
                throw new InvalidOperationException("The configured provider route changed before physical dispatch.");

            return null;
        }

        IDisposable? lease = WorkCoordinator is not null
            ? await WorkCoordinator.EnterCallAsync(resource.Value,
                ProviderWorkAmbient.Owner as ProviderWorkCoordinator.ForegroundTurn, cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (identity is not null && !identity.IsCurrent(_settings?.CurrentValue))
                throw new InvalidOperationException("The configured provider route changed before physical dispatch.");

            return lease;
        }
        catch
        {
            lease?.Dispose();

            throw;
        }
    }
}

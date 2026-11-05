using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>One root ordering point shared by final effect admission, revocation and owner sealing.</summary>
internal static class GuildSignetRootGate
{
    private static readonly ConditionalWeakTable<IGrimoireConnectionAdmissionGate, RootGates> Gates = new();

    private static readonly AsyncLocal<Guid?> HeldRoot = new();

    internal static async Task<Result<T>> RunAsync<T>(IGrimoireConnectionAdmissionGate installationGate, Guid root,
        Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        if (root == Guid.Empty || HeldRoot.Value is not null)
        {
            return GuildSignetStore.Unavailable;
        }

        SemaphoreSlim rootGate = Gates.GetValue(installationGate, static _ => new()).ByRoot.GetOrAdd(root, static _ => new(1, 1));

        await rootGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            HeldRoot.Value = root;

            ct.ThrowIfCancellationRequested();

            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            HeldRoot.Value = null;

            rootGate.Release();
        }
    }

    private sealed class RootGates
    {
        internal ConcurrentDictionary<Guid, SemaphoreSlim> ByRoot { get; } = new();
    }
}

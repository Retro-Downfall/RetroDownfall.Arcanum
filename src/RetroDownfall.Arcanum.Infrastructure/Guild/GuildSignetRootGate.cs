using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>One root ordering point shared by final effect admission, revocation and owner sealing.</summary>
internal static class GuildSignetRootGate
{
    private static readonly ConditionalWeakTable<IGrimoireConnectionAdmissionGate, RootGates> Gates = new();

    private static readonly AsyncLocal<object?> HeldRoot = new();

    internal static int RetainedRootCount(IGrimoireConnectionAdmissionGate installationGate) =>
        Gates.TryGetValue(installationGate, out RootGates? roots) ? roots.Count : 0;

    /// <summary>Acquires ordering only. Enter must bind the held-root marker synchronously in the caller's context.</summary>
    internal static async Task<Result<RootLease>> AcquireAsync(IGrimoireConnectionAdmissionGate installationGate,
        Guid root, CancellationToken ct)
    {
        if (root == Guid.Empty || HeldRoot.Value is not null)
            return GuildSignetStore.Unavailable;

        RootGates roots = Gates.GetValue(installationGate, static _ => new());

        RootEntry entry = roots.Rent(root);

        bool acquired = false;

        bool transferred = false;

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);

            acquired = true;

            ct.ThrowIfCancellationRequested();

            RootLease lease = new(root, roots, entry);

            transferred = true;

            return lease;
        }
        finally
        {
            if (!transferred)
            {
                if (acquired)
                    entry.Gate.Release();

                roots.Return(root, entry);
            }
        }
    }

    internal sealed class RootLease(Guid root, RootGates roots, RootEntry entry) : IDisposable
    {
        private readonly object _marker = new();

        private int _disposed;

        internal bool Enter()
        {
            if (Volatile.Read(ref _disposed) != 0 || HeldRoot.Value is not null)
                return false;

            HeldRoot.Value = _marker;

            return true;
        }

        public void Dispose()
        {
            // A helper can release the resource in a child execution context. Every caller still
            // clears its own exact marker; a stale lease can never clear a later scope's marker.
            if (ReferenceEquals(HeldRoot.Value, _marker))
                HeldRoot.Value = null;

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            entry.Gate.Release();

            roots.Return(root, entry);
        }
    }

    internal static async Task<Result<T>> RunAsync<T>(IGrimoireConnectionAdmissionGate installationGate, Guid root,
        Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        if (root == Guid.Empty || HeldRoot.Value is not null)
        {
            return GuildSignetStore.Unavailable;
        }

        RootGates roots = Gates.GetValue(installationGate, static _ => new());

        RootEntry entry = roots.Rent(root);

        bool acquired = false;

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);

            acquired = true;

            HeldRoot.Value = root;

            ct.ThrowIfCancellationRequested();

            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                HeldRoot.Value = null;

                entry.Gate.Release();
            }

            // Waiting callers own a reference too, including a caller whose wait was canceled.
            // The last reference disappears only after every holder and waiter has released it.
            roots.Return(root, entry);
        }
    }

    internal sealed class RootEntry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal int Users { get; set; }
    }

    internal sealed class RootGates
    {
        private readonly object _sync = new();

        private readonly Dictionary<Guid, RootEntry> _entries = [];

        internal int Count
        {
            get
            {
                lock (_sync)
                {
                    return _entries.Count;
                }
            }
        }

        internal RootEntry Rent(Guid root)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(root, out RootEntry? entry))
                {
                    entry = new();

                    _entries.Add(root, entry);
                }

                entry.Users = checked(entry.Users + 1);

                return entry;
            }
        }

        internal void Return(Guid root, RootEntry entry)
        {
            lock (_sync)
            {
                entry.Users--;

                if (entry.Users == 0)
                {
                    _entries.Remove(root);

                    entry.Gate.Dispose();
                }
            }
        }
    }
}

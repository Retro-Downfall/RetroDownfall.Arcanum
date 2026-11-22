using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Ambient turn accounting for nested billable work (batch lines, in-turn embeddings) that must
/// ledger against a parent run.
/// </summary>
internal static class TurnAccountingAmbient
{
    private static readonly AsyncLocal<TurnAccountingHandle?> CurrentLocal = new();

    private static readonly AsyncLocal<ITurnRunWriter?> WriterLocal = new();

    /// <summary>
    /// The turn accounting nested work ledgers against, or <see langword="null"/> when there is none
    /// or its run has already settled.
    /// </summary>
    /// <remarks>
    /// A tool task can outlive the parent and retain its handle in its async flow. Once the parent
    /// settles, a later operation must record its usage under its own run.
    /// </remarks>
    public static TurnAccountingHandle? Current
    {
        get => CurrentLocal.Value is { IsSettled: false } handle ? handle : null;
        set => CurrentLocal.Value = value;
    }

    /// <summary>The writer paired with <see cref="Current"/>, hidden with it once the run settles.</summary>
    public static ITurnRunWriter? Writer
    {
        get => CurrentLocal.Value is { IsSettled: true } ? null : WriterLocal.Value;
        set => WriterLocal.Value = value;
    }

    public static void Publish(TurnAccountingHandle handle, ITurnRunWriter? writer)
    {
        Current = handle;
        Writer = writer;

        DelegatedSpendAttribution.BudgetReservationId = null;
    }

    public static IDisposable Push(TurnAccountingHandle handle, ITurnRunWriter? writer)
    {
        RestorationScope scope = new(Current, Writer);
        Publish(handle, writer);
        return scope;
    }

    /// <summary>
    /// Hides the current turn's accounting from work that must account for itself, then restores it.
    /// </summary>
    /// <remarks>
    /// A delegated child turn needs its own run. Without suspension it would adopt and
    /// settle the parent run while the parent is still active.
    /// </remarks>
    public static IDisposable Suspend()
    {
        RestorationScope scope = new(Current, Writer);
        Clear();
        return scope;
    }

    public static void Clear()
    {
        Current = null;
        Writer = null;
        DelegatedSpendAttribution.BudgetReservationId = null;
    }

    private sealed class RestorationScope(
        TurnAccountingHandle? previousHandle,
        ITurnRunWriter? previousWriter) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (previousHandle is null)
            {
                Clear();
                return;
            }

            Publish(previousHandle, previousWriter);
        }
    }
}

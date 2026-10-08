using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Ambient turn accounting for nested billable work (batch lines, in-turn embeddings) that must
/// ledger against a parent run without starting a second reservation.
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
    /// A settled run reads as no run. A tool task abandoned past the turn's grace keeps running in a
    /// flow that still holds the turn's handle; once that run has written its status and reconciled
    /// or released its reservation, work there must account for itself rather than ledger against a
    /// run whose totals are already final.
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

        // Work this turn delegates outside the process — an outbound A2A Sending — records which
        // reservation paid for the turn it came from, so a delegated cost is traceable to a budget
        // rather than floating unattached (issue #69).
        DelegatedSpendAttribution.BudgetReservationId = handle.ReservationId;
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
    /// A delegated child turn begins its own run and reservation. Seeing the parent's handle it would
    /// adopt it instead, and on completion settle the parent's run and reservation while the parent is
    /// still mid-turn.
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

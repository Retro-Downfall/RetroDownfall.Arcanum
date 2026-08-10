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

    public static TurnAccountingHandle? Current
    {
        get => CurrentLocal.Value;
        set => CurrentLocal.Value = value;
    }

    public static ITurnRunWriter? Writer
    {
        get => WriterLocal.Value;
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

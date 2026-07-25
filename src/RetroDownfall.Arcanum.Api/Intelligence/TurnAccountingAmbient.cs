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

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Per-inference-turn inject-once tracker for user and model-requested attachment materialization.
/// </summary>
/// <remarks>
/// <see cref="BeginTurn"/> returns the turn's tracker so the turn loop can re-establish it with
/// <see cref="Enter"/> before each tool call: an <c>AsyncLocal</c> written inside an async iterator does
/// not survive the iterator's next <c>yield return</c>. With no tracker ambient,
/// <see cref="TryMarkInjected"/> answers <see langword="true"/>, so a lost tracker means repeated
/// injection rather than a refusal.
/// </remarks>
public static class SessionAttachmentTurnBudget
{
    private static readonly AsyncLocal<Turn?> Current = new();

    public static Turn BeginTurn()
    {
        Turn turn = new();

        Current.Value = turn;

        return turn;
    }

    /// <summary>Makes an already-begun turn's tracker the ambient one again, without resetting it.</summary>
    public static void Enter(Turn? turn) => Current.Value = turn;

    public static void EndTurn() => Current.Value = null;

    /// <summary>
    /// Marks an attachment as injected for this turn. Returns <c>false</c> if already injected
    /// (content must be consumed once — do not repeat on subsequent tool rounds).
    /// </summary>
    public static bool TryMarkInjected(string logicalKey, int version)
    {
        Turn? turn = Current.Value;

        if (turn is null)
        {
            return true;
        }

        string key = InjectedKey(logicalKey, version);

        return turn.Injected.Add(key);
    }

    private static string InjectedKey(string logicalKey, int version) =>
        logicalKey + "\u001f" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One turn's inject-once record.</summary>
    public sealed class Turn
    {
        internal Turn()
        {
        }

        internal HashSet<string> Injected { get; } = new(StringComparer.Ordinal);
    }
}

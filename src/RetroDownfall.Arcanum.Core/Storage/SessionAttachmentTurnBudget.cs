namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Per-inference-turn inject-once tracker for user and model-requested attachment materialization.
/// </summary>
public static class SessionAttachmentTurnBudget
{

    private static readonly AsyncLocal<State?> Current = new();

    public static void BeginTurn()
    {

        Current.Value = new State();

    }

    public static void EndTurn() => Current.Value = null;

    /// <summary>
    /// Marks an attachment as injected for this turn. Returns <c>false</c> if already injected
    /// (content must be consumed once — do not repeat on subsequent tool rounds).
    /// </summary>
    public static bool TryMarkInjected(string logicalKey, int version)
    {

        State? state = Current.Value;

        if (state is null)
        {
            return true;
        }

        string key = InjectedKey(logicalKey, version);

        return state.Injected.Add(key);

    }

    private static string InjectedKey(string logicalKey, int version) =>
        logicalKey + "\u001f" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed class State
    {

        public HashSet<string> Injected { get; } = new(StringComparer.Ordinal);

    }

}

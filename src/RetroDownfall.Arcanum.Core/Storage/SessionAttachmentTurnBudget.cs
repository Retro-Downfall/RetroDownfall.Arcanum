namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Per-inference-turn combined budget for user <c>AttachmentReferences</c> and model
/// <c>attach_session_file</c> injections (<c>AttachmentsSettings.MaxReferencesPerTurn</c>).
/// Also tracks which logical keys were already injected so content is consumed once.
/// </summary>
public static class SessionAttachmentTurnBudget
{

    private static readonly AsyncLocal<State?> Current = new();

    public static void BeginTurn(int maxReferences, int initialConsumed)
    {

        int max = Math.Max(0, maxReferences);

        int used = Math.Clamp(initialConsumed, 0, max);

        Current.Value = new State(max, used);

    }

    public static void EndTurn() => Current.Value = null;

    /// <summary>
    /// Tries to consume one reference slot. Returns <c>false</c> when the combined turn budget is exhausted.
    /// When no turn budget was begun, returns <c>true</c> (call sites without a Wizard turn are unconstrained here).
    /// </summary>
    public static bool TryConsume()
    {

        State? state = Current.Value;

        if (state is null)
        {
            return true;
        }

        if (state.Used >= state.Max)
        {
            return false;
        }

        state.Used++;

        return true;

    }

    public static int Remaining
    {
        get
        {
            State? state = Current.Value;

            if (state is null)
            {
                return int.MaxValue;
            }

            return Math.Max(0, state.Max - state.Used);
        }
    }

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

    /// <summary>
    /// Atomically consumes one budget slot and marks inject-once. On failure neither side effect
    /// applies (failed validation must call this only after materialization succeeds).
    /// </summary>
    public static bool TryConsumeAndMarkInjected(string logicalKey, int version)
    {

        State? state = Current.Value;

        if (state is null)
        {
            return true;
        }

        string key = InjectedKey(logicalKey, version);

        if (state.Injected.Contains(key))
        {
            return false;
        }

        if (state.Used >= state.Max)
        {
            return false;
        }

        state.Used++;

        state.Injected.Add(key);

        return true;

    }

    private static string InjectedKey(string logicalKey, int version) =>
        logicalKey + "\u001f" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed class State
    {

        public State(int max, int used)
        {
            Max = max;
            Used = used;
        }

        public int Max { get; }

        public int Used;

        public HashSet<string> Injected { get; } = new(StringComparer.Ordinal);

    }

}

using System.Collections.ObjectModel;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Applies a freshly computed line list to the <see cref="ObservableCollection{T}"/> bound to a
/// Terminal.Gui list view as a minimal tail edit.
/// The bound list wrapper rescans the whole collection on every change notification (longest-item and
/// mark-array recomputation), so <c>Clear()</c> followed by one <c>Add</c> per line costs O(n²) for an
/// n-line pane — paid on every 50 ms streaming flush. Replacing only the tail that actually differs
/// makes a streaming append cost work proportional to the appended text instead of the transcript.
/// </summary>
internal static class CommandCenterLineSync
{
    /// <summary>
    /// Mutates <paramref name="target"/> so it equals <paramref name="lines"/>, touching only the
    /// suffix that differs. Returns the number of change notifications raised.
    /// </summary>
    public static int ApplyTailEdit(ObservableCollection<string> target, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lines);

        int common = 0;
        while (common < target.Count
            && common < lines.Count
            && string.Equals(target[common], lines[common], StringComparison.Ordinal))
        {
            common++;
        }

        int mutations = 0;
        for (int i = target.Count - 1; i >= common; i--)
        {
            target.RemoveAt(i);
            mutations++;
        }

        for (int i = common; i < lines.Count; i++)
        {
            target.Add(lines[i]);
            mutations++;
        }

        return mutations;
    }
}

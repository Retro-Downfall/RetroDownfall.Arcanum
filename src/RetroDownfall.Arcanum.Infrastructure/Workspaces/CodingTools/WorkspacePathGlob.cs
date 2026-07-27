using System.Diagnostics.CodeAnalysis;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

/// <summary>
/// Small in-process matcher for normalized workspace-relative glob patterns.
/// Supports <c>*</c>, <c>?</c>, and recursive <c>**</c>; path separators are always <c>/</c>.
/// </summary>
internal sealed class WorkspacePathGlob
{

    private readonly string[] _segments;

    private WorkspacePathGlob(string pattern)
    {

        _segments = pattern.Split('/');

    }

    internal static bool TryCreate(
        string? pattern,
        [NotNullWhen(true)] out WorkspacePathGlob? glob)
    {

        glob = null;

        if (!WorkspaceRelativePath.TryNormalize(pattern, out string? normalized))
        {

            return false;

        }

        glob = new WorkspacePathGlob(normalized);

        return true;

    }

    internal bool IsMatch(
        string normalizedRelativePath,
        Action? checkpoint = null)
    {

        string[] pathSegments = normalizedRelativePath.Split('/');
        bool[] current = new bool[pathSegments.Length + 1];
        current[0] = true;

        foreach (string patternSegment in _segments)
        {

            checkpoint?.Invoke();

            bool[] next = new bool[pathSegments.Length + 1];

            if (string.Equals(patternSegment, "**", StringComparison.Ordinal))
            {

                next[0] = current[0];

                for (int pathIndex = 1; pathIndex <= pathSegments.Length; pathIndex++)
                {

                    checkpoint?.Invoke();
                    next[pathIndex] = current[pathIndex] || next[pathIndex - 1];

                }

            }
            else
            {

                for (int pathIndex = 1; pathIndex <= pathSegments.Length; pathIndex++)
                {

                    checkpoint?.Invoke();
                    next[pathIndex] = current[pathIndex - 1]
                        && SegmentMatches(
                            patternSegment,
                            pathSegments[pathIndex - 1],
                            checkpoint);

                }

            }

            current = next;

        }

        return current[pathSegments.Length];

    }

    internal bool CanMatchDescendant(
        string normalizedRelativeDirectory,
        Action? checkpoint = null)
    {

        string[] pathSegments = normalizedRelativeDirectory.Split('/');

        HashSet<int> states = EpsilonClosure([0], checkpoint);

        foreach (string pathSegment in pathSegments)
        {

            checkpoint?.Invoke();

            HashSet<int> next = [];

            foreach (int state in states)
            {

                if (state >= _segments.Length)
                {

                    continue;

                }

                string patternSegment = _segments[state];

                if (string.Equals(
                        patternSegment,
                        "**",
                        StringComparison.Ordinal))
                {

                    _ = next.Add(state);

                }
                else if (SegmentMatches(
                    patternSegment,
                    pathSegment,
                    checkpoint))
                {

                    _ = next.Add(state + 1);

                }

            }

            states = EpsilonClosure(next, checkpoint);

            if (states.Count == 0)
            {

                return false;

            }

        }

        return states.Any(state => state < _segments.Length);

    }

    private HashSet<int> EpsilonClosure(
        IEnumerable<int> source,
        Action? checkpoint)
    {

        HashSet<int> closure = [.. source];

        Stack<int> pending = new(closure);

        while (pending.Count > 0)
        {

            checkpoint?.Invoke();

            int state = pending.Pop();

            if (state < _segments.Length
                && string.Equals(
                    _segments[state],
                    "**",
                    StringComparison.Ordinal)
                && closure.Add(state + 1))
            {

                pending.Push(state + 1);

            }

        }

        return closure;

    }

    private static bool SegmentMatches(
        string pattern,
        string value,
        Action? checkpoint)
    {

        bool[] current = new bool[value.Length + 1];
        current[0] = true;

        foreach (char patternCharacter in pattern)
        {

            checkpoint?.Invoke();

            bool[] next = new bool[value.Length + 1];

            if (patternCharacter == '*')
            {

                next[0] = current[0];

                for (int valueIndex = 1; valueIndex <= value.Length; valueIndex++)
                {

                    checkpoint?.Invoke();
                    next[valueIndex] =
                        current[valueIndex] || next[valueIndex - 1];

                }

            }
            else
            {

                for (int valueIndex = 1; valueIndex <= value.Length; valueIndex++)
                {

                    checkpoint?.Invoke();
                    next[valueIndex] = current[valueIndex - 1]
                        && (patternCharacter == '?'
                            || CharactersEqual(
                                patternCharacter,
                                value[valueIndex - 1]));

                }

            }

            current = next;

        }

        return current[value.Length];

    }

    private static bool CharactersEqual(char left, char right)
    {

        if (left == right)
        {

            return true;

        }

        return WorkspaceRelativePath.Comparison == StringComparison.OrdinalIgnoreCase
            && char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

    }

}

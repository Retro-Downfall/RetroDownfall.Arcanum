namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// The rules of one <c>.gitignore</c> file, evaluated against paths relative to the directory that holds
/// it. It covers the part of the gitignore grammar that decides which files a directory listing leaves
/// out: comments, negation, anchoring, directory-only rules, <c>*</c>, <c>?</c>, bracket classes and
/// <c>**</c>. The last rule that matches decides, as it does for git.
/// </summary>
/// <remarks>
/// A rule line longer than <see cref="MaxPatternLength"/>, or one that comes after
/// <see cref="MaxRules"/> earlier rules, is dropped, which lists more files rather than fewer. A rule is
/// matched against a path in time proportional to the product of their segment counts.
/// </remarks>
internal sealed class GitIgnoreRules
{
    /// <summary>Rules read from one file; later lines are dropped.</summary>
    internal const int MaxRules = 1_000;

    /// <summary>Longest rule line taken into account.</summary>
    internal const int MaxPatternLength = 512;

    private readonly Rule[] _rules;

    private GitIgnoreRules(Rule[] rules)
    {
        _rules = rules;
    }

    internal static GitIgnoreRules Empty { get; } = new([]);

    internal bool IsEmpty => _rules.Length == 0;

    internal static GitIgnoreRules Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<Rule> rules = [];

        foreach (string rawLine in text.Split('\n'))
        {
            if (rules.Count >= MaxRules)
            {
                break;
            }

            if (TryParseRule(rawLine, out Rule rule))
            {
                rules.Add(rule);
            }
        }

        return rules.Count == 0
            ? Empty
            : new GitIgnoreRules([.. rules]);
    }

    /// <summary>
    /// Decides one path. <see langword="true"/> means a rule excludes it, <see langword="false"/> that a
    /// negated rule re-includes it, and <see langword="null"/> that no rule speaks to it.
    /// </summary>
    /// <param name="relativePath">The path below the directory holding the rules, using <c>/</c>.</param>
    /// <param name="isDirectory">Whether the path names a directory.</param>
    internal bool? Evaluate(string relativePath, bool isDirectory)
    {
        if (_rules.Length == 0 || string.IsNullOrEmpty(relativePath))
        {
            return null;
        }

        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        bool? decision = null;

        foreach (Rule rule in _rules)
        {
            if (rule.Matches(segments, isDirectory))
            {
                decision = !rule.Negated;
            }
        }

        return decision;
    }

    private static bool TryParseRule(string rawLine, out Rule rule)
    {
        rule = default!;

        string line = rawLine.TrimEnd('\r');

        // Trailing spaces do not count unless a backslash protects them.
        int end = line.Length;

        while (end > 0
            && line[end - 1] == ' '
            && (end < 2 || line[end - 2] != '\\'))
        {
            end--;
        }

        line = line[..end];

        if (line.Length == 0
            || line.Length > MaxPatternLength
            || line[0] == '#')
        {
            return false;
        }

        bool negated = false;

        if (line[0] == '!')
        {
            negated = true;

            line = line[1..];
        }

        bool directoryOnly = false;

        if (line.Length > 0 && line[^1] == '/')
        {
            directoryOnly = true;

            line = line[..^1];
        }

        if (line.Length == 0)
        {
            return false;
        }

        bool anchored = line.Contains('/', StringComparison.Ordinal);

        if (line[0] == '/')
        {
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return false;
        }

        List<Segment> segments = [];

        foreach (string part in line.Split('/'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (part == "**" && anchored)
            {
                // Consecutive recursive wildcards mean the same as one.
                if (segments.Count > 0 && segments[^1].IsRecursive)
                {
                    continue;
                }

                segments.Add(Segment.Recursive);

                continue;
            }

            segments.Add(Segment.FromGlob(part));
        }

        if (segments.Count == 0)
        {
            return false;
        }

        rule = new Rule([.. segments], negated, directoryOnly, anchored);

        return true;
    }

    private sealed record Rule(
        Segment[] Segments,
        bool Negated,
        bool DirectoryOnly,
        bool Anchored)
    {
        internal bool Matches(string[] path, bool isDirectory)
        {
            if (DirectoryOnly && !isDirectory)
            {
                return false;
            }

            // A rule without a slash names an entry at any depth below the file's directory.
            return Anchored
                ? MatchSegments(Segments, path)
                : Segments[0].Matches(path[^1]);
        }

        private static bool MatchSegments(Segment[] pattern, string[] path)
        {
            // reached[j] is true when the pattern segments consumed so far match the first j path segments.
            bool[] reached = new bool[path.Length + 1];

            reached[0] = true;

            for (int index = 0; index < pattern.Length; index++)
            {
                bool[] next = new bool[path.Length + 1];

                if (pattern[index].IsRecursive)
                {
                    // A recursive wildcard spans any number of segments. Trailing, it matches what is inside
                    // a directory and never the directory itself, so it spans at least one.
                    bool trailing = index == pattern.Length - 1;

                    bool spanned = false;

                    for (int length = 0; length <= path.Length; length++)
                    {
                        if (trailing)
                        {
                            next[length] = spanned;

                            spanned |= reached[length];
                        }
                        else
                        {
                            spanned |= reached[length];

                            next[length] = spanned;
                        }
                    }
                }
                else
                {
                    for (int length = 1; length <= path.Length; length++)
                    {
                        next[length] = reached[length - 1] && pattern[index].Matches(path[length - 1]);
                    }
                }

                reached = next;
            }

            return reached[path.Length];
        }
    }

    /// <summary>One path segment of a rule: a recursive wildcard, or a glob over a single name.</summary>
    private sealed class Segment
    {
        private readonly Token[] _tokens;

        private Segment(Token[] tokens, bool isRecursive)
        {
            _tokens = tokens;

            IsRecursive = isRecursive;
        }

        internal static Segment Recursive { get; } = new([], isRecursive: true);

        internal bool IsRecursive { get; }

        internal static Segment FromGlob(string glob) => new(Tokenize(glob), isRecursive: false);

        internal bool Matches(string name)
        {
            int tokenIndex = 0;

            int nameIndex = 0;

            int starToken = -1;

            int starName = 0;

            while (nameIndex < name.Length)
            {
                if (tokenIndex < _tokens.Length)
                {
                    Token token = _tokens[tokenIndex];

                    if (token.Kind == TokenKind.Star)
                    {
                        starToken = tokenIndex++;

                        starName = nameIndex;

                        continue;
                    }

                    if (token.MatchesCharacter(name[nameIndex]))
                    {
                        tokenIndex++;

                        nameIndex++;

                        continue;
                    }
                }

                if (starToken < 0)
                {
                    return false;
                }

                tokenIndex = starToken + 1;

                nameIndex = ++starName;
            }

            while (tokenIndex < _tokens.Length && _tokens[tokenIndex].Kind == TokenKind.Star)
            {
                tokenIndex++;
            }

            return tokenIndex == _tokens.Length;
        }

        private static Token[] Tokenize(string glob)
        {
            List<Token> tokens = [];

            int index = 0;

            while (index < glob.Length)
            {
                char c = glob[index];

                switch (c)
                {
                    case '*':
                        // "**" inside one name behaves as a single star.
                        while (index < glob.Length && glob[index] == '*')
                        {
                            index++;
                        }

                        if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.Star)
                        {
                            tokens.Add(new Token(TokenKind.Star));
                        }

                        continue;
                    case '?':
                        tokens.Add(new Token(TokenKind.AnyCharacter));

                        index++;

                        continue;
                    case '\\' when index + 1 < glob.Length:
                        tokens.Add(new Token(TokenKind.Literal, glob[index + 1]));

                        index += 2;

                        continue;
                    case '[' when TryReadClass(glob, index, out Token characterClass, out int next):
                        tokens.Add(characterClass);

                        index = next;

                        continue;
                    default:
                        tokens.Add(new Token(TokenKind.Literal, c));

                        index++;

                        continue;
                }
            }

            return [.. tokens];
        }

        private static bool TryReadClass(string glob, int start, out Token token, out int next)
        {
            token = default;

            next = start;

            int index = start + 1;

            bool negated = false;

            if (index < glob.Length && glob[index] is '!' or '^')
            {
                negated = true;

                index++;
            }

            List<(char Low, char High)> ranges = [];

            bool first = true;

            while (index < glob.Length)
            {
                char c = glob[index];

                if (c == ']' && !first)
                {
                    token = new Token(TokenKind.Class, default, [.. ranges], negated);

                    next = index + 1;

                    return true;
                }

                first = false;

                if (c == '\\' && index + 1 < glob.Length)
                {
                    c = glob[++index];
                }

                if (index + 2 < glob.Length
                    && glob[index + 1] == '-'
                    && glob[index + 2] != ']')
                {
                    char high = glob[index + 2];

                    ranges.Add((c, high));

                    index += 3;

                    continue;
                }

                ranges.Add((c, c));

                index++;
            }

            // No closing bracket: the opening one is an ordinary character.
            return false;
        }
    }

    private enum TokenKind
    {
        Literal,
        AnyCharacter,
        Star,
        Class,
    }

    private readonly record struct Token(
        TokenKind Kind,
        char Character = default,
        (char Low, char High)[]? Ranges = null,
        bool Negated = false)
    {
        internal bool MatchesCharacter(char value)
        {
            switch (Kind)
            {
                case TokenKind.Literal:
                    return Character == value;
                case TokenKind.AnyCharacter:
                    return true;
                case TokenKind.Class:
                    bool inClass = false;

                    foreach ((char low, char high) in Ranges!)
                    {
                        if (value >= low && value <= high)
                        {
                            inClass = true;

                            break;
                        }
                    }

                    return inClass != Negated;
                default:
                    return false;
            }
        }
    }
}

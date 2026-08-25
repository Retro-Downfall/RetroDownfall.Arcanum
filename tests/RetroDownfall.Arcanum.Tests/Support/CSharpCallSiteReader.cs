using System.Text;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// One invocation of a named member found in authored source, with the shape of its argument list.
/// </summary>
internal readonly record struct CSharpCallSite(
    string RelativePath,
    int ArgumentCount,
    IReadOnlyList<string> NamedArguments);

/// <summary>
/// Finds invocations of a named member across authored source, and counts what they pass.
/// </summary>
/// <remarks>
/// C# erases the difference between an argument a caller wrote and an argument the compiler supplied:
/// an omitted optional parameter is emitted at the call site exactly as if it had been typed, so the
/// question "does any caller ever supply this?" cannot be asked of IL or of reflection. It can only be
/// asked of the source, which is why this reader exists.
///
/// <para>It is a scanner, not a parser, and every one of its approximations is deliberately biased
/// toward over-reporting. Undercounting an argument list produces an offender a human reads and
/// dismisses; overcounting one hides a real offender for good. So a construct it cannot read — a
/// generic invocation, an argument list containing an unbalanced <c>&lt;</c> from a comparison, a call
/// written inside an interpolation hole — is either skipped or read short, never read long.</para>
/// </remarks>
internal static class CSharpCallSiteReader
{

    /// <summary>
    /// Words that may precede an invocation. Anything else immediately before the name is a type, and
    /// a type before a name is a declaration rather than a call.
    /// </summary>
    private static readonly HashSet<string> InvocationPrefixKeywords = new(StringComparer.Ordinal)
    {
        "await",

        "return",

        "yield",

        "throw",

        "new",

        "is",

        "case",

        "in",

        "out",

        "ref",

        "not",

        "and",

        "or",
    };

    internal static IReadOnlyList<CSharpCallSite> Find(
        IReadOnlyList<ProductionSource> sources,
        string memberName)
    {

        List<CSharpCallSite> sites = [];

        foreach (ProductionSource source in sources)
        {

            string text = WithoutLiterals(source.Text);

            int search = 0;

            while (true)
            {

                int start = text.IndexOf(memberName, search, StringComparison.Ordinal);

                if (start < 0)
                {

                    break;

                }

                search = start + memberName.Length;

                if (start > 0 && IsNameCharacter(text[start - 1]))
                {

                    continue;

                }

                int open = SkipSpace(text, start + memberName.Length);

                if (open >= text.Length || text[open] != '(' || !IsInvocation(text, start))
                {

                    continue;

                }

                if (TryReadArguments(text, open, out IReadOnlyList<string> arguments))
                {

                    sites.Add(new CSharpCallSite(
                        source.RelativePath,
                        arguments.Count,
                        [.. arguments.Select(NamedArgumentOf).OfType<string>()]));

                }

            }

        }

        return sites;

    }

    /// <summary>
    /// Decides whether the name at <paramref name="start"/> is being called or being declared.
    /// </summary>
    /// <remarks>
    /// A declaration always has a type immediately before the name, and a type ends in an identifier
    /// character, a <c>&gt;</c> closing a generic, or a <c>]</c> closing an array rank. A call always
    /// has punctuation or a statement keyword there. The one collision is <c>=&gt;</c>, which ends in
    /// the same <c>&gt;</c> a generic return type does and is resolved by looking one character
    /// further back.
    /// </remarks>
    private static bool IsInvocation(string text, int start)
    {

        int index = start - 1;

        while (index >= 0 && char.IsWhiteSpace(text[index]))
        {

            index--;

        }

        if (index < 0)
        {

            return false;

        }

        char previous = text[index];

        if (previous == '>')
        {

            return index > 0 && text[index - 1] == '=';

        }

        if (previous == ']')
        {

            return false;

        }

        if (!IsNameCharacter(previous))
        {

            return true;

        }

        int wordStart = index;

        while (wordStart >= 0 && IsNameCharacter(text[wordStart]))
        {

            wordStart--;

        }

        return InvocationPrefixKeywords.Contains(text[(wordStart + 1)..(index + 1)]);

    }

    /// <summary>
    /// Reads the top-level arguments of the list opening at <paramref name="open"/>.
    /// </summary>
    /// <remarks>
    /// Angle brackets are tracked so that the comma in <c>Dictionary&lt;string, int&gt;</c> is not
    /// counted as an argument separator, which would report an optional parameter as supplied when it
    /// was not. A comparison operator opens an angle depth that never closes, which swallows the
    /// commas after it and reads the list short — the harmless direction.
    /// </remarks>
    private static bool TryReadArguments(string text, int open, out IReadOnlyList<string> arguments)
    {

        List<string> read = [];

        int depth = 0;

        int angle = 0;

        int itemStart = open + 1;

        for (int index = open; index < text.Length; index++)
        {

            char character = text[index];

            if (character is '(' or '[' or '{')
            {

                depth++;

            }
            else if (character is ')' or ']' or '}')
            {

                depth--;

                if (depth == 0)
                {

                    string tail = text[itemStart..index];

                    if (read.Count > 0 || tail.Trim().Length > 0)
                    {

                        read.Add(tail);

                    }

                    arguments = read;

                    return true;

                }

            }
            else if (character == '<')
            {

                if (index + 1 >= text.Length || text[index + 1] != '=')
                {

                    angle++;

                }

            }
            else if (character == '>')
            {

                if (angle > 0 && (index == 0 || text[index - 1] != '='))
                {

                    angle--;

                }

            }
            else if (character == ',' && depth == 1 && angle == 0)
            {

                read.Add(text[itemStart..index]);

                itemStart = index + 1;

            }

        }

        arguments = [];

        return false;

    }

    private static string? NamedArgumentOf(string argument)
    {

        string trimmed = argument.TrimStart();

        int index = 0;

        while (index < trimmed.Length && IsNameCharacter(trimmed[index]))
        {

            index++;

        }

        if (index == 0)
        {

            return null;

        }

        int colon = SkipSpace(trimmed, index);

        bool named = colon < trimmed.Length
            && trimmed[colon] == ':'
            && (colon + 1 >= trimmed.Length || trimmed[colon + 1] != ':');

        return named ? trimmed[..index] : null;

    }

    /// <summary>
    /// Blanks the contents of every string and character literal, so a member name quoted in a message
    /// is not read as a call and a bracket inside a literal cannot unbalance the scan.
    /// </summary>
    private static string WithoutLiterals(string text)
    {

        StringBuilder masked = new(text);

        int index = 0;

        while (index < text.Length)
        {

            char character = text[index];

            if (character == '"')
            {

                index = MaskString(text, masked, index);

                continue;

            }

            if (character == '\'')
            {

                index = MaskCharacter(text, masked, index);

                continue;

            }

            index++;

        }

        return masked.ToString();

    }

    private static int MaskString(string text, StringBuilder masked, int start)
    {

        if (start + 2 < text.Length && text[start + 1] == '"' && text[start + 2] == '"')
        {

            int close = text.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);

            int stop = close < 0 ? text.Length : close + 3;

            Blank(masked, start, stop);

            return stop;

        }

        bool verbatim = start > 0
            && (text[start - 1] == '@'
                || (text[start - 1] == '$' && start > 1 && text[start - 2] == '@'));

        int index = start + 1;

        while (index < text.Length)
        {

            if (!verbatim && text[index] == '\\')
            {

                index += 2;

                continue;

            }

            if (!verbatim && text[index] == '\n')
            {

                break;

            }

            if (text[index] == '"')
            {

                if (verbatim && index + 1 < text.Length && text[index + 1] == '"')
                {

                    index += 2;

                    continue;

                }

                index++;

                break;

            }

            index++;

        }

        int end = Math.Min(index, text.Length);

        Blank(masked, start, end);

        return end;

    }

    private static int MaskCharacter(string text, StringBuilder masked, int start)
    {

        int index = start + 1;

        while (index < text.Length && text[index] != '\'' && text[index] != '\n')
        {

            index += text[index] == '\\' ? 2 : 1;

        }

        int end = Math.Min(index + 1, text.Length);

        Blank(masked, start, end);

        return end;

    }

    private static void Blank(StringBuilder masked, int start, int end)
    {

        for (int index = start; index < end && index < masked.Length; index++)
        {

            if (masked[index] != '\n')
            {

                masked[index] = ' ';

            }

        }

    }

    private static int SkipSpace(string text, int index)
    {

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {

            index++;

        }

        return index;

    }

    private static bool IsNameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '@';

}

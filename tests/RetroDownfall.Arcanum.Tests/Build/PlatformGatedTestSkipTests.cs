using System.Text;

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A <c>[Fact]</c>, <c>[Theory]</c>, <c>[SkippableFact]</c> or <c>[SkippableTheory]</c> method that
/// returns early on an <c>OperatingSystem.Is*</c> or <c>RuntimeInformation</c> condition, instead of
/// calling <c>Skip.If</c>/<c>Skip.IfNot</c> for that condition, reports Passed having asserted nothing
/// on the platform it did not run on. The suite's skipped count never moves, so the lost coverage is
/// invisible in the run summary — indistinguishable from the suite not having run there at all. This
/// inventory finds every such site under this project's own test tree and keeps new ones from
/// reappearing once a packet has converted the ones that existed when this file was written.
/// </summary>
/// <remarks>
/// Scoped to the whole <c>tests/</c> tree that <see cref="ProductionSourceInventory.TestSuiteSources"/>
/// covers, sibling desktop projects included: a test that reports Passed without asserting anything is
/// the same lie whichever project it lives in, and all three now reference <c>Xunit.SkippableFact</c>,
/// so all three can say so honestly.
///
/// <para>An already-<c>Skippable</c> method is in scope, not exempt by attribute alone: a method can
/// legitimately call <c>Skip.IfNot</c> for one reason and still silently return early on an unrelated
/// platform condition a few lines later, which is the same bug wearing the compliant attribute.</para>
/// </remarks>
public sealed class PlatformGatedTestSkipTests
{

    [Fact]
    public void Test_methods_skip_a_platform_gate_instead_of_returning_early()
    {

        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.TestSuiteSources())
        {

            offenders.AddRange(PlatformGatedEarlyReturnScan.FindOffenders(source));

        }

        // Named rather than counted, de-duplicated, and joined without Assert.Empty's five-entry
        // truncation: a packet fixing this needs the whole list in the failure output, not a sample
        // of it, and a method can carry more than one offending guard.
        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));

    }

    /// <summary>
    /// A method that calls <c>Skip.</c> under a plain <c>[Fact]</c> or <c>[Theory]</c> does not skip.
    /// </summary>
    /// <remarks>
    /// <c>Skip.If</c> and its siblings work by throwing, and only <c>[SkippableFact]</c> /
    /// <c>[SkippableTheory]</c> install the discoverer that recognizes that throw as a skip. Under a
    /// plain attribute the same call is an unhandled exception, so the runner reports a failure —
    /// the opposite of the silent pass the rule above catches, and just as wrong, because the source
    /// reads identically either way. An author who wrote a skip has to get one.
    /// </remarks>
    [Fact]
    public void Test_methods_that_call_Skip_carry_a_skippable_attribute()
    {

        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.TestSuiteSources())
        {

            offenders.AddRange(PlatformGatedEarlyReturnScan.FindUnskippableSkipCallers(source));

        }

        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));

    }

}

/// <summary>
/// Finds <c>[Fact]</c>/<c>[Theory]</c>/<c>[SkippableFact]</c>/<c>[SkippableTheory]</c> method bodies
/// that return early on a platform condition instead of skipping on it.
/// </summary>
/// <remarks>
/// A scanner, not a parser, in the same spirit as <c>CSharpCallSiteReader</c>: every approximation
/// here is biased toward finding a site rather than missing one, because the failure this guards
/// against is silent — a missed offender reports green forever, while a false positive is caught the
/// first time this inventory runs and is cheap for a human to dismiss.
///
/// <para>Literal-masked before any brace or paren is counted, unlike the plainer scanners elsewhere in
/// this project: a fixture string such as <c>"{\"salt\":\"not-base64!!\""</c> — deliberately unbalanced
/// JSON, because the test it lives in asserts on a parse failure — otherwise reads as an opening brace
/// with no matching close, and every method after it is misattributed to whichever later <c>}</c>
/// happens to balance the count.</para>
///
/// <para>A <c>[SkippableFact]</c>/<c>[SkippableTheory]</c> method may legitimately keep a dead guard
/// shaped exactly like the offending pattern — <c>Skip.If(cond, …)</c> immediately followed by
/// <c>if (cond) { return; }</c> — because the platform-compatibility analyzer recognizes the early
/// return as a guard clause and does not understand that <c>Skip.If</c> already exited the method on
/// the same condition; without it, a call to a platform-annotated API later in the body is a build
/// warning (an error, on this tree). This scan exempts exactly that shape: a guard is compliant only
/// when a <c>Skip.If</c>/<c>Skip.IfNot</c> call earlier in the same method, written as an unconditional
/// top-level statement of the method body (brace depth 0 — not nested inside any further <c>if</c>,
/// <c>try</c>, <c>using</c>, lambda, or other block), carries the same condition (for <c>Skip.IfNot</c>,
/// the guard must carry its logical negation, since <c>IfNot</c> skips when its argument is false and a
/// dead guard reads true in exactly the case the skip already covered). The match is textual, not
/// semantic, so the dead guard must repeat the skip's condition verbatim (with the single leading
/// <c>!</c> for the <c>IfNot</c> case) — a differently-worded but equivalent condition is not
/// recognized and is reported as a fresh offender.</para>
///
/// <para>The top-level requirement exists because the exemption is otherwise a bare textual
/// position-plus-condition match with no notion of whether the preceding <c>Skip.</c> call is actually
/// reached: a <c>Skip.If</c> nested inside an always-false <c>if</c> — dead itself, never runs — would
/// still exempt a later, genuinely-reachable early return elsewhere in the method that shares its
/// condition text, silently passing the exact bug this scanner exists to catch. Requiring brace depth 0
/// closes that gap while still exempting every dead guard this tree actually pairs with a <c>Skip.</c>
/// call, since every such pairing keeps the <c>Skip.</c> call itself at the method's own top level —
/// including the one case, <c>MultiFileCommitCoordinatorTests</c>, where the dead guard it protects
/// sits inside a nested lambda: only the guard is nested there, not the skip. Depth is brace-counted,
/// not fully parsed: a <c>Skip.</c> call nested under a braceless single-statement <c>if</c>/<c>for</c>/
/// <c>else</c> (no <c>{ }</c> at all) would still read as depth 0, since brace depth is the only
/// nesting signal tracked. No site in this tree writes a block without braces, so this is a known,
/// deliberately unaddressed gap rather than a parser upgrade worth its complexity here.</para>
/// </remarks>
internal static class PlatformGatedEarlyReturnScan
{

    private static readonly Regex TestAttribute = new(
        @"\[(?:Fact|Theory|SkippableFact|SkippableTheory)\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex MethodName = new(
        @"\b(\w+)\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    // Up to two levels of paren nesting: a bare call (`OperatingSystem.IsMacOS()`) is one level, and
    // a negated compound guard (`!(OperatingSystem.IsMacOS() && IsMacOsSandboxExecRunnable())`) is
    // two - the outer `(...)` around the compound, the inner `()` on each call inside it. One level
    // reads that shape's `if` as never opening (the inner `(` has no matching `)` before the next
    // `(`), so the guard is invisible to the scan rather than evaluated and exempted or flagged.
    private const string NestedParens =
        @"(?:[^()]|\((?:[^()]|\([^()]*\))*\))*";

    private static readonly Regex SkippableAttribute = new(
        @"\[(?:SkippableFact|SkippableTheory)\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    // Deliberately looser than SkipCall below, which needs the condition and so needs the comma that
    // separates it from the message: Skip.IfNot(cond) with no message is a real call this rule has to
    // see, and so is any future Skip. member.
    private static readonly Regex AnySkipCall = new(
        @"\bSkip\.\w+\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex SkipCall = new(
        @"\bSkip\.(If|IfNot)\s*\(\s*(" + NestedParens + @")\s*,",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex EarlyReturnGuard = new(
        @"if\s*\((" + NestedParens + @")\)\s*(?:\{\s*return\s*;\s*\}|return\s*;)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex WhitespaceRun = new(
        @"\s+",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    internal static IReadOnlyList<string> FindOffenders(ProductionSource source)
    {

        List<string> offenders = [];

        string text = WithoutLiterals(source.Text);

        foreach (Match attribute in TestAttribute.Matches(text))
        {

            if (!TryReadMethod(text, attribute.Index, out string name, out string body))
            {

                continue;

            }

            int[] depths = ComputeBraceDepths(body);

            List<(int Start, int Depth, string Method, string Condition)> skipCalls = [];

            foreach (Match skip in SkipCall.Matches(body))
            {

                skipCalls.Add(
                    (skip.Index, depths[skip.Index], skip.Groups[1].Value, Normalize(skip.Groups[2].Value)));

            }

            foreach (Match guard in EarlyReturnGuard.Matches(body))
            {

                string rawCondition = guard.Groups[1].Value;

                bool namesAPlatformCheck =
                    rawCondition.Contains("OperatingSystem.Is", StringComparison.Ordinal)
                    || rawCondition.Contains("RuntimeInformation", StringComparison.Ordinal);

                if (!namesAPlatformCheck)
                {

                    continue;

                }

                string normalizedGuard = Normalize(rawCondition);

                bool matchedByAPrecedingSkip = skipCalls.Any(
                    call => call.Start < guard.Index
                        && call.Depth == 0
                        && IsSatisfiedBy(normalizedGuard, call.Method, call.Condition));

                if (matchedByAPrecedingSkip)
                {

                    continue;

                }

                offenders.Add(
                    $"{source.RelativePath} :: {name} returns early on `{normalizedGuard}` "
                    + "instead of calling Skip.If/Skip.IfNot");

            }

        }

        return offenders;

    }

    /// <summary>
    /// Test methods that call <c>Skip.</c> without carrying a skippable attribute.
    /// </summary>
    /// <remarks>
    /// The attribute the method actually carries is read from the match that found it, not searched
    /// for near it: a <c>[SkippableFact]</c> a few lines above an unconverted <c>[Fact]</c> would
    /// otherwise exempt its neighbour, which is exactly the shape a partly converted file has.
    /// </remarks>
    internal static IReadOnlyList<string> FindUnskippableSkipCallers(ProductionSource source)
    {

        List<string> offenders = [];

        string text = WithoutLiterals(source.Text);

        foreach (Match attribute in TestAttribute.Matches(text))
        {

            if (SkippableAttribute.IsMatch(attribute.Value))
            {

                continue;

            }

            if (!TryReadMethod(text, attribute.Index, out string name, out string body)
                || !AnySkipCall.IsMatch(body))
            {

                continue;

            }

            offenders.Add(
                $"{source.RelativePath} :: {name} calls Skip. under {attribute.Value}] — a "
                + "SkipException is a failure, not a skip, without [SkippableFact]/[SkippableTheory]");

        }

        return offenders;

    }

    /// <summary>
    /// Whether a raw guard's condition is exactly the dead code a preceding <c>Skip.</c> call would
    /// leave behind: <c>Skip.If(X, …)</c> exits when <c>X</c>, so <c>if (X) { return; }</c> after it
    /// is dead; <c>Skip.IfNot(X, …)</c> exits when <c>!X</c>, so it is <c>if (!X) { return; }</c> — the
    /// single negation, not <c>X</c> itself — that is dead. Matching <c>Skip.IfNot</c>'s condition
    /// against the guard's un-negated text would exempt the opposite bug: a method that skips on one
    /// condition and silently returns early on that condition's negation, which is a live offender, not
    /// a dead guard.
    /// </summary>
    private static bool IsSatisfiedBy(string guardCondition, string skipMethod, string skipCondition) =>
        skipMethod == "If"
            ? guardCondition == skipCondition
            : guardCondition == $"!{skipCondition}" || guardCondition == $"!({skipCondition})";

    private static string Normalize(string condition) =>
        WhitespaceRun.Replace(condition, " ").Trim();

    /// <summary>
    /// Brace depth at every position in <paramref name="body"/>, relative to the method's own
    /// top-level statement list. <paramref name="body"/> starts with the method's own opening brace and
    /// ends with its matching close (see <see cref="ReadBracedRun"/>); that outer pair is the frame, not
    /// a nesting level, so a statement written directly in the method body reads depth 0, one written
    /// inside a further <c>{ }</c> — an <c>if</c>, <c>try</c>, <c>using</c>, lambda, or local-function
    /// block, any construct that opens one — reads depth 1, and so on.
    /// </summary>
    private static int[] ComputeBraceDepths(string body)
    {

        int[] depths = new int[body.Length];

        int depth = -1;

        for (int index = 0; index < body.Length; index++)
        {

            char current = body[index];

            if (current == '{')
            {

                depths[index] = depth;

                depth++;

            }
            else if (current == '}')
            {

                depth--;

                depths[index] = depth;

            }
            else
            {

                depths[index] = depth;

            }

        }

        return depths;

    }

    /// <summary>
    /// Reads the method whose attribute list contains the one found at <paramref name="attributeStart"/>:
    /// its name, and its block body if it has one.
    /// </summary>
    /// <remarks>
    /// Fails closed on an expression-bodied member (<c>=&gt;</c>) or one with no body at all (an
    /// abstract or partial declaration ending in <c>;</c>): stopping at the first of <c>{</c>, <c>;</c>
    /// or <c>=&gt;</c> after the attribute list, rather than searching only for <c>{</c>, keeps a
    /// converted <c>[SkippableFact] B()</c> a few lines below an expression-bodied
    /// <c>[Fact] A() =&gt; …;</c> from being misattributed to <c>A</c>.
    /// </remarks>
    private static bool TryReadMethod(string text, int attributeStart, out string name, out string body)
    {

        name = "";

        body = "";

        int position = SkipBracketSection(text, attributeStart);

        while (true)
        {

            int afterSpace = SkipWhitespace(text, position);

            if (afterSpace >= text.Length || text[afterSpace] != '[')
            {

                position = afterSpace;

                break;

            }

            position = SkipBracketSection(text, afterSpace);

        }

        if (position >= text.Length)
        {

            return false;

        }

        Match nameMatch = MethodName.Match(text, position, Math.Min(300, text.Length - position));

        if (!nameMatch.Success)
        {

            return false;

        }

        name = nameMatch.Groups[1].Value;

        int bodyStart = FindBlockBodyStart(text, position);

        if (bodyStart < 0)
        {

            return false;

        }

        body = ReadBracedRun(text, bodyStart);

        return true;

    }

    private static int SkipBracketSection(string text, int openBracketIndex)
    {

        int depth = 0;

        int index = openBracketIndex;

        while (index < text.Length)
        {

            if (text[index] == '[')
            {

                depth++;

            }
            else if (text[index] == ']')
            {

                depth--;

                if (depth == 0)
                {

                    return index + 1;

                }

            }

            index++;

        }

        return text.Length;

    }

    private static int SkipWhitespace(string text, int index)
    {

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {

            index++;

        }

        return index;

    }

    private static int FindBlockBodyStart(string text, int signatureStart)
    {

        int index = signatureStart;

        while (index < text.Length)
        {

            char current = text[index];

            if (current == '{')
            {

                return index;

            }

            if (current == ';')
            {

                return -1;

            }

            if (current == '=' && index + 1 < text.Length && text[index + 1] == '>')
            {

                return -1;

            }

            index++;

        }

        return -1;

    }

    /// <summary>
    /// The text from an opening brace to the one that closes it, depth-counted so a nested block —
    /// including a lambda or local function defined inside the method — cannot cut the run short.
    /// </summary>
    private static string ReadBracedRun(string text, int openBraceIndex)
    {

        StringBuilder run = new();

        int depth = 0;

        for (int index = openBraceIndex; index < text.Length; index++)
        {

            _ = run.Append(text[index]);

            if (text[index] == '{')
            {

                depth++;

            }
            else if (text[index] == '}')
            {

                depth--;

                if (depth == 0)
                {

                    break;

                }

            }

        }

        return run.ToString();

    }

    /// <summary>
    /// Blanks the contents of every string and character literal, preserving length and line breaks,
    /// so a brace or paren quoted inside a literal cannot unbalance the depth-matching this scan relies
    /// on. Duplicated from the same idea in <c>CSharpCallSiteReader</c> rather than shared, since that
    /// helper is private to its own scan.
    /// </summary>
    private static string WithoutLiterals(string text)
    {

        StringBuilder masked = new(text);

        int index = 0;

        while (index < text.Length)
        {

            char current = text[index];

            if (current == '"')
            {

                index = MaskString(text, masked, index);

                continue;

            }

            if (current == '\'')
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

}

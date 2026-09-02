using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Reduces a DDL statement to the form both a source resource and an installed
/// <c>sqlite_master</c> row agree on, so drift detection compares meaning rather than formatting.
/// </summary>
/// <remarks>
/// SQLite does not store what was typed. It strips <c>IF NOT EXISTS</c> and the terminating
/// semicolon, drops anything before the <c>CREATE</c> keyword, and keeps everything else verbatim -
/// including inline comments and the author's line breaks. Comparing raw text would therefore report
/// drift for a reindented file and miss nothing real, so both sides pass through here first.
///
/// <para>Quoted strings and quoted identifiers are preserved byte for byte. That matters:
/// <c>RAISE(ABORT, 'two  spaces')</c> is a different message from <c>RAISE(ABORT, 'two spaces')</c>,
/// and a normalizer that collapsed inside quotes would call a changed abort message identical.</para>
///
/// <para>Comments are removed rather than collapsed. Collapsing a line comment's trailing newline
/// into a space would swallow the rest of the statement into it - the normalized text would still
/// compare equal on both sides, but it would no longer be SQL a reader could trust in a
/// diagnostic.</para>
///
/// <para><b>Changing what this returns is a Core schema version step.</b> Since Core version 6 the
/// tier's published source-definition fingerprint is computed over this function's output rather than
/// over the file's bytes (see <see cref="GrimoireSchemaCatalog.ComputeSourceFingerprint"/>), so this
/// return value is Core's durable identity in <c>grimoire_feature_schemas</c> - for every object at
/// once, rather than for the one file an ordinary schema edit touches. A change here that looks purely
/// cosmetic, such as also collapsing the space around a comma, moves that identity, and because Core is
/// the tier whose refusal aborts startup the first symptom is every installation at the current version
/// refusing to open with <c>SourceDefinitionMismatch</c>. So it is authored the way any object change
/// is: raise the version, pin the currently published fingerprint for the version the step leaves
/// before editing anything, and add the fixture that reconstructs it.
/// <c>GrimoireSqlNormalizerTests</c> pins the exact output against a fixed input and fails with that
/// instruction, because nothing else in the tree can - head fingerprints are never pinned by design and
/// every version fixture hashes the raw bytes instead.</para>
/// </remarks>
internal static class GrimoireSqlNormalizer
{

    public static string Normalize(string sql)
    {

        ArgumentNullException.ThrowIfNull(sql);

        StringBuilder builder = new(sql.Length);

        int index = 0;

        bool pendingSpace = false;

        while (index < sql.Length)
        {

            char current = sql[index];

            if (current == '\'' || current == '"' || current == '`')
            {

                index = CopyQuoted(sql, index, builder, ref pendingSpace);

                continue;

            }

            if (current == '[')
            {

                index = CopyBracketed(sql, index, builder, ref pendingSpace);

                continue;

            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {

                index = SkipLineComment(sql, index);

                pendingSpace = builder.Length > 0;

                continue;

            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {

                index = SkipBlockComment(sql, index);

                pendingSpace = builder.Length > 0;

                continue;

            }

            if (IsAsciiWhitespace(current))
            {

                pendingSpace = builder.Length > 0;

                index++;

                continue;

            }

            if (TryMatchIfNotExists(sql, index, out int afterToken))
            {

                index = afterToken;

                pendingSpace = builder.Length > 0;

                continue;

            }

            if (pendingSpace)
            {

                _ = builder.Append(' ');

                pendingSpace = false;

            }

            _ = builder.Append(current);

            index++;

        }

        // One terminal semicolon only: SQLite stores the statement without it, and a source file
        // that omitted it must not read as different from one that included it.
        while (builder.Length > 0 && (builder[^1] == ';' || builder[^1] == ' '))
        {

            builder.Length--;

        }

        return builder.ToString();

    }

    private static int CopyQuoted(string sql, int index, StringBuilder builder, ref bool pendingSpace)
    {

        char quote = sql[index];

        if (pendingSpace)
        {

            _ = builder.Append(' ');

            pendingSpace = false;

        }

        _ = builder.Append(quote);

        index++;

        while (index < sql.Length)
        {

            char current = sql[index];

            _ = builder.Append(current);

            index++;

            if (current != quote)
            {

                continue;

            }

            // A doubled quote is an escaped quote inside the literal, not the end of it.
            if (index < sql.Length && sql[index] == quote)
            {

                _ = builder.Append(quote);

                index++;

                continue;

            }

            break;

        }

        return index;

    }

    private static int CopyBracketed(string sql, int index, StringBuilder builder, ref bool pendingSpace)
    {

        if (pendingSpace)
        {

            _ = builder.Append(' ');

            pendingSpace = false;

        }

        while (index < sql.Length)
        {

            char current = sql[index];

            _ = builder.Append(current);

            index++;

            if (current == ']')
            {

                break;

            }

        }

        return index;

    }

    private static int SkipLineComment(string sql, int index)
    {

        while (index < sql.Length && sql[index] != '\n')
        {

            index++;

        }

        return index;

    }

    private static int SkipBlockComment(string sql, int index)
    {

        index += 2;

        while (index + 1 < sql.Length && !(sql[index] == '*' && sql[index + 1] == '/'))
        {

            index++;

        }

        return index + 1 < sql.Length ? index + 2 : sql.Length;

    }

    private static bool TryMatchIfNotExists(string sql, int index, out int afterToken)
    {

        afterToken = index;

        if (!MatchesKeyword(sql, index, "IF", out int afterIf))
        {

            return false;

        }

        if (!MatchesKeyword(sql, SkipWhitespace(sql, afterIf), "NOT", out int afterNot))
        {

            return false;

        }

        if (!MatchesKeyword(sql, SkipWhitespace(sql, afterNot), "EXISTS", out int afterExists))
        {

            return false;

        }

        afterToken = afterExists;

        return true;

    }

    private static bool MatchesKeyword(string sql, int index, string keyword, out int afterKeyword)
    {

        afterKeyword = index;

        if (index + keyword.Length > sql.Length)
        {

            return false;

        }

        // Invariant ASCII: SQL keywords are ASCII by definition, and culture-aware casing would make
        // the same schema normalize differently under a Turkish locale.
        if (!sql.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        int next = index + keyword.Length;

        if (next < sql.Length && (char.IsLetterOrDigit(sql[next]) || sql[next] == '_'))
        {

            return false;

        }

        afterKeyword = next;

        return true;

    }

    private static int SkipWhitespace(string sql, int index)
    {

        while (index < sql.Length && IsAsciiWhitespace(sql[index]))
        {

            index++;

        }

        return index;

    }

    private static bool IsAsciiWhitespace(char value) =>
        value is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

}

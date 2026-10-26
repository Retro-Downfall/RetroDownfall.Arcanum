using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>Delegates query parsing to the admitted native FTS5 index without reading transcript content.</summary>
internal static class Fts5QueryValidation
{
    internal static Error InvalidQuery { get; } = new(
        ErrorCodes.Validation.InvalidQuery,
        "Invalid FTS5 query. Use words, quoted phrases, AND, OR, NOT, prefixes or NEAR; quote literal punctuation and double embedded quotation marks.");

    internal static Result<string> Normalize(string? query, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return InvalidQuery;
        }

        string expression = query.Trim();

        return expression.Length > maxLength
            ? new Error(ErrorCodes.Validation.InvalidQuery, "FTS5 query is too long. Shorten the complete expression and retry.")
            : expression;
    }

    internal static async Task<Result> ValidateAsync(ArcanumDbContext db, string expression, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An independent, bounded MATCH forces native parsing even when the session table is empty
        // or a title match short-circuits its transcript predicate. Never scan all matching rows.
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            db, "SELECT rowid FROM \"Entries_fts\" WHERE \"Entries_fts\" MATCH $query LIMIT 1", cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$query", expression);

        try
        {
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }
        catch (SqliteException exception) when (IsQuerySyntaxError(exception))
        {
            // Native diagnostics may contain query fragments. Neither expose nor log them.
            return Result.Failure(InvalidQuery);
        }
    }

    private static bool IsQuerySyntaxError(SqliteException exception) =>
        exception.SqliteErrorCode == 1
        && (exception.Message.StartsWith("SQLite Error 1: 'fts5: syntax error", StringComparison.Ordinal)
            || exception.Message.StartsWith("SQLite Error 1: 'unterminated string", StringComparison.Ordinal)
            || exception.Message.StartsWith("SQLite Error 1: 'expected integer, got ", StringComparison.Ordinal)
            || exception.Message.StartsWith("SQLite Error 1: 'no such column: ", StringComparison.Ordinal));
}

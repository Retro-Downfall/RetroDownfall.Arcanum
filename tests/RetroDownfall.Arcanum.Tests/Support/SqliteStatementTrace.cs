using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Every statement SQLite starts on one connection while the scope is open - the top-level statements the code under test issued,
/// not the statements a trigger runs, which SQLite reports as <c>--</c> comment lines.
/// </summary>
/// <remarks>
/// The trace is the only counter that sees every statement, including the ones issued through helpers that bypass the stores' own
/// command wrappers, which is what makes "the number of statements does not depend on the size of the corpus" a checkable claim.
/// </remarks>
internal sealed class SqliteStatementTrace : IDisposable
{
    private readonly SqliteConnection _connection;

    private readonly List<string> _statements = [];

    private readonly object _gate = new();

    internal SqliteStatementTrace(SqliteConnection connection)
    {
        _connection = connection;

        raw.sqlite3_trace(
            connection.Handle,
            (strdelegate_trace)((_, statement) =>
            {
                if (statement.StartsWith("--", StringComparison.Ordinal))
                {
                    return;
                }

                lock (_gate)
                {
                    _statements.Add(statement);
                }
            }),
            null);
    }

    /// <summary>The statements started so far, in order.</summary>
    internal IReadOnlyList<string> Statements
    {
        get
        {
            lock (_gate)
            {
                return [.. _statements];
            }
        }
    }

    internal int Count => Statements.Count;

    /// <summary>
    /// How many times each distinct statement ran. SQLite reports a statement with its parameters filled in, so two runs of one query for
    /// two leaves are two texts; a statement is therefore grouped by its shape, with every string, blob and number literal reduced to a mark.
    /// </summary>
    internal Dictionary<string, int> ByText() => Statements.GroupBy(static text => Shape(text), StringComparer.Ordinal)
        .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

    internal static string Shape(string statement) => Regex.Replace(
        Regex.Replace(statement, @"'(?:[^']|'')*'", "?"),
        @"(?<![\w.])-?\d+(?:\.\d+)?(?![\w])",
        "?");

    /// <summary>How many transactions took the writer lock up front (<c>BEGIN IMMEDIATE</c>).</summary>
    internal int ImmediateTransactions => Statements.Count(static statement => statement.StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase));

    /// <summary>How many transactions began deferred, which is how the provider spells a plain <c>BEGIN</c>: no lock until the first statement needs one.</summary>
    internal int DeferredTransactions => Statements.Count(static statement => string.Equals(statement.Trim(), "BEGIN;", StringComparison.OrdinalIgnoreCase)
        || statement.StartsWith("BEGIN DEFERRED", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => raw.sqlite3_trace(_connection.Handle, (strdelegate_trace)null!, null);
}

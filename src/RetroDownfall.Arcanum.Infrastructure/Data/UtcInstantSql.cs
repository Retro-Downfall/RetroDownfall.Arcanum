using Microsoft.Data.Sqlite;

using System.Data.Common;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Makes canonical UTC rendering part of the direct-SQL parameter boundary rather than a promise
/// made by an earlier reader or caller.
/// </summary>
internal static class UtcInstantSql
{
    /// <summary>
    /// Formats a typed instant and binds only its fixed-width UTC representation.
    /// </summary>
    internal static SqliteParameter AddParameter(
        SqliteCommand command,
        string name,
        DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return command.Parameters.AddWithValue(name, UtcInstantText.Format(value));
    }

    /// <summary>
    /// Formats and binds a typed instant when the direct-SQL owner intentionally uses the
    /// provider-neutral ADO.NET command surface.
    /// </summary>
    internal static DbParameter AddParameter(
        DbCommand command,
        string name,
        DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = UtcInstantText.Format(value);
        _ = command.Parameters.Add(parameter);

        return parameter;
    }

    /// <summary>
    /// Revalidates text crossing from a foreign or historical database before binding it to a
    /// destination instant column. Database null remains database null; every other shape fails.
    /// </summary>
    internal static SqliteParameter AddStoredParameter(
        SqliteCommand command,
        string name,
        object? storedValue)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        object canonical = storedValue switch
        {
            null or DBNull => DBNull.Value,
            string text => UtcInstantText.Normalize(text),
            _ => throw new ArgumentException(
                "A stored UTC instant parameter must be timestamp text or database null.",
                nameof(storedValue)),
        };

        return command.Parameters.AddWithValue(name, canonical);
    }
}

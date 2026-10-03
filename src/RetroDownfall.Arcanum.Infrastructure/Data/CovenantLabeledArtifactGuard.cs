using System.Data.Common;

using Microsoft.Data.Sqlite;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Issue #117 — reads the label table so a legacy raw delete can refuse an artifact it cannot erase
/// correctly.
/// </summary>
/// <remarks>
/// A reader and nothing else. It resolves no policy, acquires no lease, and removes nothing: the only
/// question it answers is whether a live label exists, and the only thing a caller may do with the
/// answer is stop.
///
/// <para>It fails closed. A label table that cannot be read is not "nothing is protected here": the
/// table is a Core object at every schema version, so on any installation that got as far as opening
/// its Grimoire it exists, and a read that fails is a Grimoire whose protection cannot be checked. The
/// answer is a refusal, and the delete does not go ahead on a guess.</para>
///
/// <para>The transaction forms read through the caller's own transaction on its own connection. A delete
/// that asks inside the transaction it deletes in has one moment for the answer and the delete, which a
/// check made beforehand cannot give it.</para>
/// </remarks>
internal sealed class CovenantLabeledArtifactGuard(
    IArtifactSensitivityLedger labels,
    ILogger<CovenantLabeledArtifactGuard> logger) : ICovenantLabeledArtifactTransactionGuard
{

    public async ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {

        try
        {

            return Verdict(
                kind,
                await labels
                    .TryReadLabelAsync(kind, artifactId, cancellationToken)
                    .ConfigureAwait(false));

        }
        catch (SqliteException exception)
        {

            return Unreadable(kind, exception);

        }

    }

    public async ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {

        RequireTransactionOn(connection, transaction);

        try
        {

            return Verdict(
                kind,
                await ArtifactSensitivityLedger
                    .ReadLabelThroughAsync(connection, transaction, kind, artifactId, cancellationToken)
                    .ConfigureAwait(false));

        }
        catch (SqliteException exception)
        {

            return Unreadable(kind, exception);

        }

    }

    public async ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {

        RequireTransactionOn(connection, transaction);

        try
        {

            return await AnyLabeledAsync(connection, transaction, kind, cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException exception)
        {

            return Unreadable(kind, exception);

        }

    }

    private static Result Verdict(SensitiveArtifactKind kind, Result<ArtifactSensitivityLabel?> label)
    {

        if (label.IsFailure)
        {

            return label.Error;

        }

        return label.Value is null
            ? Result.Success()
            : Refusal(kind);

    }

    private static async Task<Result> AnyLabeledAsync(
        DbConnection connection,
        DbTransaction transaction,
        SensitiveArtifactKind kind,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM artifact_sensitivity WHERE ArtifactKindCode = $kind);
            """;

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "$kind";

        parameter.Value = (long)kind;

        _ = command.Parameters.Add(parameter);

        object? any = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return any is 0L or null or DBNull
            ? Result.Success()
            : Refusal(kind);

    }

    private static void RequireTransactionOn(DbConnection connection, DbTransaction transaction)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        if (!ReferenceEquals(transaction.Connection, connection))
        {

            throw new ArgumentException(
                "The labelled-artifact guard reads through a transaction on the connection it is handed.",
                nameof(transaction));

        }

    }

    /// <summary>
    /// The refusal for a label table that could not be read.
    /// </summary>
    /// <remarks>
    /// <c>Covenant.Unavailable</c>, the 503, rather than the <c>Covenant.ForbiddenAuthority</c> a labelled
    /// artifact is refused with: nothing was found to be protected, so there is no authority to lack. The
    /// protection could not be checked, which is the Grimoire's condition and not the caller's, and the
    /// operator's action is to repair it.
    ///
    /// <para>The log line carries the kind and the provider's result codes and nothing else, so it names no
    /// artifact and copies no content into application logs.</para>
    /// </remarks>
    private Error Unreadable(SensitiveArtifactKind kind, SqliteException exception)
    {

        logger.LogWarning(
            "The sensitivity label table could not be read for a {Kind} delete (SQLite result {SqliteErrorCode}, extended {SqliteExtendedErrorCode}); the delete is refused.",
            kind,
            exception.SqliteErrorCode,
            exception.SqliteExtendedErrorCode);

        return new Error(
            ErrorCodes.Covenant.Unavailable,
            $"The sensitivity labels for {kind} artifacts could not be read, so a raw delete cannot be "
                + "shown to leave no labelled artifact behind and was refused.");

    }

    private static Error Refusal(SensitiveArtifactKind kind) =>
        new(
            ErrorCodes.Covenant.ForbiddenAuthority,
            $"A labelled {kind} artifact cannot be removed through a raw delete; it must be dispatched "
                + "through the sensitivity purge boundary so its evidence is preserved.");

}

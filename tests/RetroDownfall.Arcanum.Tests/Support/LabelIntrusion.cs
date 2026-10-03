using System.Data.Common;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A second writer that tries to label an artifact on its own connection, which is how a label arrives
/// between a delete's check and the delete.
/// </summary>
/// <remarks>
/// It writes the label row directly, because every writer of that table, the production ledger included,
/// needs the one write lock a delete's transaction holds. It waits only a moment for it: an attempt that
/// cannot get the lock in that time was blocked, and one that commits was not.
/// </remarks>
internal sealed class LabelIntruder(
    Func<CancellationToken, Task<SqliteConnection>> openConnection,
    SensitiveArtifactKind kind)
{

    /// <summary>The artifact the intruder labels, or a fresh identity when none is set.</summary>
    internal Guid? ArtifactId { get; set; }

    /// <summary>How many times the intruder tried to write its label.</summary>
    internal int Attempts => AskedInsideTransaction + AskedOutsideTransaction;

    /// <summary>How many of the guard's questions were asked with a transaction.</summary>
    internal int AskedInsideTransaction { get; private set; }

    /// <summary>How many of the guard's questions were asked without one.</summary>
    internal int AskedOutsideTransaction { get; private set; }

    /// <summary>How many of those attempts could not get the write lock.</summary>
    internal int Blocked { get; private set; }

    /// <summary>How many of those attempts committed a label.</summary>
    internal int Committed => Attempts - Blocked;

    internal async Task IntrudeAsync(bool askedInsideTransaction, CancellationToken cancellationToken)
    {

        if (askedInsideTransaction)
        {

            AskedInsideTransaction++;

        }
        else
        {

            AskedOutsideTransaction++;

        }

        SqliteConnection connection = await openConnection(cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand pragma = connection.CreateCommand())
        {

            pragma.CommandText = "PRAGMA busy_timeout = 50;";

            _ = await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandTimeout = 1;

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, $kind, $artifact, 1, 1, $generations, NULL, NULL, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), $now);
            """;

        _ = command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$kind", (int)kind);

        _ = command.Parameters.AddWithValue("$artifact", (ArtifactId ?? Guid.NewGuid()).ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        _ = command.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000Z");

        try
        {

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException)
        {

            Blocked++;

        }

    }

}

/// <summary>
/// The real labelled-artifact guard, followed by a <see cref="LabelIntruder"/> attempt after every
/// answer, which also counts whether the caller asked inside a transaction or outside one.
/// </summary>
/// <remarks>
/// The attempt comes after the real answer, so it lands exactly where a label written between the
/// check and the delete would. A delete that asked outside its transaction leaves the intruder free to
/// commit; one that asked inside it holds the write lock, and the intruder is blocked.
/// </remarks>
internal sealed class LabelIntrusionGuard(ICovenantLabeledArtifactTransactionGuard inner, LabelIntruder intruder)
    : ICovenantLabeledArtifactTransactionGuard
{

    public async ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {

        Result answer = await inner.EnsureUnlabeledAsync(kind, artifactId, cancellationToken).ConfigureAwait(false);

        await intruder.IntrudeAsync(askedInsideTransaction: false, cancellationToken).ConfigureAwait(false);

        return answer;

    }

    public async ValueTask<Result> EnsureUnlabeledAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {

        Result answer = await inner.EnsureUnlabeledAsync(kind, artifactId, connection, transaction, cancellationToken).ConfigureAwait(false);

        await intruder.IntrudeAsync(askedInsideTransaction: true, cancellationToken).ConfigureAwait(false);

        return answer;

    }

    public async ValueTask<Result> EnsureNoneLabeledAsync(
        SensitiveArtifactKind kind,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {

        Result answer = await inner.EnsureNoneLabeledAsync(kind, connection, transaction, cancellationToken).ConfigureAwait(false);

        await intruder.IntrudeAsync(askedInsideTransaction: true, cancellationToken).ConfigureAwait(false);

        return answer;

    }

}

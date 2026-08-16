using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Reads one assistant entry and its label in a single snapshot, or refuses.
/// </summary>
/// <remarks>
/// The single snapshot is the contract. A label written between the artifact read and the label read
/// would produce a row that looked clean, and that is exactly the window a tainted response is
/// finalized in. One deferred transaction over both reads closes it (§10.12).
///
/// <para>The lease is revalidated on entry and again after the reads, before the value is handed
/// back, so a reset or an erasure that lands mid-read cannot be beaten by a fast query. It is the
/// gate's own generation-bound lease rather than a private one, because the generation it carries is
/// exactly what those operations advance.</para>
/// </remarks>
internal sealed class ProtectedAssistantArtifactReader(ICovenantConnectionSource connections)
    : IProtectedAssistantArtifactReader
{

    public async Task<Result<ProtectedAssistantArtifact>> ReadAsync(
        Guid sessionId,
        Guid assistantEntryId,
        ICovenantSnapshotReadLease lease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(lease);

        Result entering = await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (entering.IsFailure)
        {

            return entering.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);

        (string? content, Guid? owningSessionId) = await ReadEntryAsync(
            connection,
            snapshot,
            assistantEntryId,
            cancellationToken).ConfigureAwait(false);

        if (content is null || owningSessionId != sessionId)
        {

            return new Error(
                ErrorCodes.Covenant.NotFound,
                "No assistant artifact with that identity belongs to this Session.");

        }

        Result<ArtifactSensitivityLabel?> label = await ArtifactSensitivityLedger.ReadLabelWithinAsync(
            connection,
            snapshot,
            SensitiveArtifactKind.AssistantEntry,
            assistantEntryId,
            cancellationToken).ConfigureAwait(false);

        if (label.IsFailure)
        {

            return label.Error;

        }

        if (label.Value is { } evidence)
        {

            Result verified = Verify(evidence, sessionId, content);

            if (verified.IsFailure)
            {

                return verified.Error;

            }

        }

        Result leaving = await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        return leaving.IsFailure
            ? leaving.Error
            : Result<ProtectedAssistantArtifact>.Success(new ProtectedAssistantArtifact(
                sessionId,
                assistantEntryId,
                content,
                label.Value?.Sensitivity ?? ContentSensitivity.None,
                label.Value));

    }

    /// <summary>
    /// Proves the label describes exactly these bytes for exactly this Session.
    /// </summary>
    /// <remarks>
    /// Mismatched label evidence fails closed rather than downgrading to an unlabelled read. A label
    /// whose content digest disagrees is evidence that either the artifact or the label was replaced
    /// without the other, and neither of those is a state a protected read may resolve.
    /// </remarks>
    private static Result Verify(ArtifactSensitivityLabel label, Guid sessionId, string content)
    {

        if (label.SessionId is { } owner && owner != sessionId)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This artifact's sensitivity label belongs to a different Session.");

        }

        return label.ArtifactContentDigest == DerivedArtifactContentDigest.ForText(content)
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This artifact's sensitivity label does not describe its current content.");

    }

    private static async Task<(string? Content, Guid? SessionId)> ReadEntryAsync(
        SqliteConnection connection,
        SqliteTransaction snapshot,
        Guid assistantEntryId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = snapshot;

        command.CommandText = """
            SELECT "Content", "SessionId" FROM "Entries" WHERE "Id" = $entryId;
            """;

        _ = command.Parameters.AddWithValue("$entryId", assistantEntryId.ToString().ToUpperInvariant());

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return (null, null);

        }

        return (
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Guid.TryParse(reader.GetString(1), out Guid owner) ? owner : null);

    }

}

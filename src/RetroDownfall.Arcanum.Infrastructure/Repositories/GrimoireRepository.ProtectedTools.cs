using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed partial class GrimoireRepository
{
    public async Task<Result<ProtectedToolInteractionReceipt>> AppendProtectedToolInteractionAsync(
        ProtectedToolInteraction request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var claim = request.ClaimLease;

        if (claim.ExecutorId is null || claim.OwnerBootId is null) return ProtectedToolRefused();

        Guid sessionId = claim.Claim.SessionId;

        using var writeLock = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (_db.Database.GetDbConnection() is not SqliteConnection connection) return ProtectedToolRefused();

        var admitted = await _connections.AcquireScopedAsync(connection, CovenantSqliteConnectionMode.ReadWrite, cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted.Error;

        await using var admission = admitted.Value;

        await using var transaction = admission.Connection.BeginTransaction(deferred: false);

        await using IDbContextTransaction attached = await _db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The protected tool transaction could not be attached.");

        try
        {
            long? expected = await ReadLiveClaimExpectationAsync(claim, cancellationToken, begun: true).ConfigureAwait(false);

            var input = await ReadClaimInputWithinAsync(sessionId, cancellationToken).ConfigureAwait(false);

            if (expected is null || input is null || input.SensitivityRevision != expected.Value) return ProtectedToolRefused();

            Guid callId = Guid.NewGuid(), resultId = Guid.NewGuid();

            long through = await WriteToolPairWithinAsync(sessionId, request.ToolName, request.Arguments, request.Result,
                request.Model, DateTimeOffset.UtcNow, callId, resultId, cancellationToken).ConfigureAwait(false);

            foreach (var artifact in new[] { (callId, $"[ToolCall: {request.ToolName}({request.Arguments})]"), (resultId, $"[ToolResult: {request.Result}]") })
            {
                var labelled = await ArtifactSensitivityLedger.WriteWithinAsync(admission.Connection, transaction,
                    new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry, artifact.Item1, sessionId, null,
                        claim.Claim.ClaimId, 1, DerivedArtifactContentDigest.ForText(artifact.Item2),
                        request.Sensitivity.Level, request.Sensitivity.Provenance), cancellationToken).ConfigureAwait(false);

                if (labelled.IsFailure) return labelled.Error;
            }

            await using var advance = admission.Connection.CreateCommand();

            advance.Transaction = transaction;

            advance.CommandText = """
                UPDATE session_turn_claims
                SET ExpectedCurrentSensitivityRevision = COALESCE((SELECT Revision FROM session_sensitivity_state WHERE SessionId = $session), 0)
                WHERE ClaimId = $claim AND SessionId = $session AND StateCode = 2
                  AND ExecutorId = $executor AND OwnerBootId = $boot AND LeaseDeadlineUtc > $now
                  AND AssistantEntryId = $assistant AND ExpectedCurrentSensitivityRevision = $expected;
                """;

            advance.Parameters.AddWithValue("$session", ClaimGuid(sessionId));
            advance.Parameters.AddWithValue("$claim", ClaimGuid(claim.Claim.ClaimId));
            advance.Parameters.AddWithValue("$executor", ClaimGuid(claim.ExecutorId.Value));
            advance.Parameters.AddWithValue("$boot", ClaimGuid(claim.OwnerBootId.Value));
            advance.Parameters.AddWithValue("$assistant", ClaimGuid(claim.FutureAssistantEntryId));
            advance.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));
            advance.Parameters.AddWithValue("$expected", expected.Value);

            if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return ProtectedToolRefused();

            await attached.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new ProtectedToolInteractionReceipt(callId, resultId, through);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No raw SQLite/provider error: a rejected value can include protected tool bytes.
            return ProtectedToolRefused();
        }
    }

    private static Error ProtectedToolRefused() => new(ErrorCodes.Grimoire.WriteFailed, "The protected tool interaction could not be committed.");
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// The narrow turn-begin port: create a bound Session, or append one turn to an existing one.
/// </summary>
/// <remarks>
/// Two operations that used to be one. The legacy
/// <c>BeginAssistantReplyAsync(Guid?, string, string, CancellationToken)</c> created a Session whenever
/// the supplied ID did not resolve, which meant a stale or mistyped Session ID silently produced a new
/// conversation, and — once Campaign binding exists — a Campaign binding nobody resolved (§10.12).
///
/// <para>Here, begin requires an existing Session and rechecks Campaign existence and the immutable
/// binding <em>inside</em> the transaction that inserts the rows. Resolution happened earlier, so a
/// Campaign deleted in between must abort with zero Entry side effects rather than leave a placeholder
/// attached to something that no longer exists.</para>
/// </remarks>
public sealed partial class GrimoireRepository : ISessionTurnBeginStore
{

    public async ValueTask<Result<Guid>> CreateBoundSessionAsync(
        CanonicalCampaignContext campaign,
        string title,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(title);

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using IDbContextTransaction transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {

            if (campaign.IsCampaignBound)
            {

                Result exists = await RequireCampaignAsync(campaign.CampaignId!.Value, cancellationToken)
                    .ConfigureAwait(false);

                if (exists.IsFailure)
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return exists.Error;

                }

            }

            _ = _db.Sessions.Add(new Session
            {
                Id = sessionId,
                CampaignId = campaign.CampaignId,
                CreatedAt = now,
                UpdatedAt = now,
                Status = "active",
                Title = TruncateTitle(title),
                UnsummarizedEntryCount = 0,
            });

            await _entryPersistence.SaveChangesWithRetryAsync(cancellationToken).ConfigureAwait(false);

            // The binding row and its Session commit together. A Session that briefly existed without
            // one would be readable by a concurrent turn as an integrity failure, and by the backfill
            // as a legacy Session needing resolution.
            await InsertBindingAsync(sessionId, campaign, now, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<Guid>.Success(sessionId);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(exception, "A bound Session could not be created.");

            return new Error(ErrorCodes.Grimoire.WriteFailed, "The session could not be created.");

        }

    }

    public async ValueTask<Result<AssistantReplyBeginReceipt>> BeginAssistantReplyAsync(
        Guid existingSessionId,
        CanonicalCampaignContext campaign,
        string prompt,
        string model,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(prompt);

        ArgumentNullException.ThrowIfNull(model);

        if (existingSessionId == Guid.Empty)
        {
            return new Error(ErrorCodes.Session.NotFound, "Session not found.");
        }

        using IDisposable writeLock = await SessionEntryPersistence
            .AcquireWriteLockAsync(existingSessionId, cancellationToken)
            .ConfigureAwait(false);

        await using IDbContextTransaction transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {

            Result<SessionCampaignBinding> binding = await RequireMatchingBindingAsync(
                existingSessionId,
                campaign,
                cancellationToken).ConfigureAwait(false);

            if (binding.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return binding.Error;

            }

            int entryCount = await _entryPersistence
                .GetEntryCountAsync(existingSessionId, cancellationToken)
                .ConfigureAwait(false);

            Error? limit = SessionEntryPersistence.CheckEntryLimits(
                entryCount,
                entriesToAdd: 2,
                GetSessionSettings(),
                prompt,
                string.Empty);

            if (limit is { } exceeded)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return exceeded;

            }

            Guid userEntryId = Guid.NewGuid();

            Guid assistantEntryId = Guid.NewGuid();

            DateTimeOffset now = DateTimeOffset.UtcNow;

            long turnSequence = await _entryPersistence
                .ReserveSequenceRangeAsync(existingSessionId, count: 2, cancellationToken)
                .ConfigureAwait(false);

            _ = _db.Entries.Add(new Entry
            {
                Id = userEntryId,
                SessionId = existingSessionId,
                Role = MessageRole.User,
                Content = prompt,
                ModelUsed = model,
                CreatedAt = now,
                Sequence = turnSequence,
            });

            _ = _db.Entries.Add(new Entry
            {
                Id = assistantEntryId,
                SessionId = existingSessionId,
                Role = MessageRole.Assistant,
                Content = string.Empty,
                ModelUsed = model,
                CreatedAt = now,
                Sequence = turnSequence + 1L,
            });

            await _entryPersistence
                .BumpSessionUpdatedAtAsync(existingSessionId, now, cancellationToken)
                .ConfigureAwait(false);

            await _entryPersistence.SaveChangesWithRetryAsync(cancellationToken).ConfigureAwait(false);

            await _entryPersistence
                .IncrementUnsummarizedEntryCountIfKnownAsync(existingSessionId, 2, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<AssistantReplyBeginReceipt>.Success(
                new AssistantReplyBeginReceipt(
                    existingSessionId,
                    userEntryId,
                    assistantEntryId,
                    new SessionTurnInputPreflight(
                        existingSessionId,
                        binding.Value,
                        // The watermark the turn planned against. Inserting the two rows above advances
                        // the live count immediately, so this echoed value is the only record of it.
                        turnSequence - 1,
                        TaintedArtifactCount: 0)));

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                exception,
                "Assistant begin failed for session {SessionId}; no entries were written.",
                existingSessionId);

            return new Error(ErrorCodes.Grimoire.WriteFailed, "The conversation turn could not be started.");

        }

    }

    /// <summary>
    /// Proves the Session exists, its binding matches the canonical context, and the Campaign is live.
    /// </summary>
    private async Task<Result<SessionCampaignBinding>> RequireMatchingBindingAsync(
        Guid sessionId,
        CanonicalCampaignContext campaign,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = CreateSqliteCommand();

        command.CommandText = """
            SELECT b.BindingKindCode,
                   b.CampaignId
            FROM session_campaign_bindings AS b
            WHERE b.SessionId = $sessionId;
            """;

        _ = command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        SessionCampaignBinding stored;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                bool exists = await _db.Sessions
                    .AsNoTracking()
                    .AnyAsync(session => session.Id == sessionId, cancellationToken)
                    .ConfigureAwait(false);

                return exists
                    ? new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "This Session has no immutable Campaign binding.")
                    : new Error(ErrorCodes.Session.NotFound, "Session not found.");

            }

            long kindCode = reader.GetInt64(0);

            if (kindCode is < 1 or > 3)
            {
                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "This Session's Campaign binding is malformed.");
            }

            stored = SessionCampaignBinding.Create(
                (SessionCampaignBindingKind)kindCode,
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)));

        }

        if (stored.Kind is SessionCampaignBindingKind.LegacyUnresolved)
        {
            return new Error(
                ErrorCodes.Session.CampaignBindingRequired,
                "This Session predates immutable Campaign binding and must be resolved before it can be used.");
        }

        if (stored.Kind != campaign.Binding.Kind || stored.CampaignId != campaign.CampaignId)
        {
            return new Error(
                ErrorCodes.Covenant.CampaignBindingConflict,
                "This Session's immutable Campaign binding does not match the resolved Campaign.");
        }

        if (campaign.IsCampaignBound)
        {

            Result live = await RequireCampaignAsync(campaign.CampaignId!.Value, cancellationToken)
                .ConfigureAwait(false);

            if (live.IsFailure)
            {
                return live.Error;
            }

        }

        return Result<SessionCampaignBinding>.Success(stored);

    }

    private async Task<Result> RequireCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
    {

        bool live = await _db.Campaigns
            .AsNoTracking()
            .AnyAsync(campaign => campaign.Id == campaignId, cancellationToken)
            .ConfigureAwait(false);

        return live
            ? Result.Success()
            : Result.Failure(
                new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier."));

    }

    private async Task InsertBindingAsync(
        Guid sessionId,
        CanonicalCampaignContext campaign,
        DateTimeOffset boundAtUtc,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = CreateSqliteCommand();

        command.CommandText = """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($sessionId, $kindCode, $campaignId, $boundAtUtc);
            """;

        _ = command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        _ = command.Parameters.AddWithValue("$kindCode", (long)campaign.Binding.Kind);

        _ = command.Parameters.AddWithValue(
            "$campaignId",
            campaign.CampaignId is { } id ? id.ToString() : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$boundAtUtc",
            boundAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// A command on the context's own connection and current transaction.
    /// </summary>
    /// <remarks>
    /// Raw SQL because the binding table has no EF entity by design. Enlisting the ambient transaction
    /// explicitly is what keeps the binding insert and the Session insert in one commit.
    /// </remarks>
    private SqliteCommand CreateSqliteCommand()
    {

        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        SqliteCommand command = connection.CreateCommand();

        if (_db.Database.CurrentTransaction?.GetDbTransaction() is SqliteTransaction transaction)
        {
            command.Transaction = transaction;
        }

        return command;

    }

}

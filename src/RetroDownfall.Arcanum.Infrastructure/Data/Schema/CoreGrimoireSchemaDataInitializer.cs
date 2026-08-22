using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Seeds and converges the always-present core rows inside the Core install transaction: the
/// Campaign registry epoch, the installation authority row, one Campaign binding per Session, and
/// the Covenant family's deletion-cleanup cursor.
/// </summary>
/// <remarks>
/// Everything here is convergent rather than incremental, because the same code runs on a database
/// this build has never touched and on one it wrote a moment ago. A singleton that is missing is
/// seeded; a singleton that is present is verified and left alone. Nothing is overwritten on the
/// strength of "this is what a fresh install would look like".
///
/// <para>The authority row is the sharpest case. It carries the durable record that host tooling
/// once had access to this installation's credentials, and that record survives key rotation,
/// reinstall, and restart on purpose. So this initializer may advance the authority counters, but it
/// may never write the host-tools state, the taint fingerprint, or the taint-time master version:
/// the one way a startup path could erase that evidence is by "reseeding" it, and the way to make
/// that impossible is to leave those columns out of every statement here.</para>
///
/// <para>The Session binding backfill is deliberately pessimistic. A Session whose legacy
/// <c>CampaignId</c> is null is recorded as unresolved rather than Global, because a null column
/// cannot distinguish "created outside any Campaign" from "created inside a Campaign that has since
/// been deleted", and the second reading must never be laundered into durable Global authority.</para>
/// </remarks>
internal sealed class CoreGrimoireSchemaDataInitializer : IGrimoireSchemaDataInitializer
{

    /// <summary><c>CovenantHostToolsState.Clean</c>, the only state a fresh installation may claim.</summary>
    private const long CleanHostToolsStateCode = 1;

    /// <summary><c>SessionCampaignBindingKind.Campaign</c>: bound to exactly one Campaign.</summary>
    private const long CampaignBindingKindCode = 2;

    /// <summary><c>SessionCampaignBindingKind.LegacyUnresolved</c>: carries no authority at all.</summary>
    private const long LegacyUnresolvedBindingKindCode = 3;

    /// <summary>
    /// The master key version is an unsigned counter in the domain and lives in checked signed
    /// storage because SQLite has no unsigned integer, so the domain maximum is the real ceiling.
    /// </summary>
    private const long MaximumMasterKeyVersion = uint.MaxValue;

    public GrimoireSchemaTransactionTier TransactionTier => GrimoireSchemaTransactionTier.Core;

    public async Task InitializeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(context);

        RequireUsableContext(context);

        string installedAtUtc = CovenantCanonicalSchemaDataInitializer.FormatTimestamp(context.InstalledAtUtc);

        await SeedRegistryStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await SeedInstallationTurnQuotaStateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        await SeedOrConvergeAuthorityStateAsync(connection, transaction, context, installedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        await BackfillSessionBindingsAsync(connection, transaction, installedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        await SeedCovenantCleanupCursorAsync(connection, transaction, installedAtUtc, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Starts the Campaign registry at epoch one when no row exists.
    /// </summary>
    /// <remarks>
    /// Epoch one rather than zero: the column excludes zero so that an unseeded row can never
    /// compare equal to a real observation, and a preflight holding a cached Campaign view must
    /// treat "I have never seen this registry" as a miss rather than as a match.
    ///
    /// <para>The epoch counts Campaign changes, not Campaigns, so seeding it at one on a database
    /// that already holds Campaigns is correct: the first change after this install still advances
    /// it past every cached value.</para>
    /// </remarks>
    private static async Task SeedRegistryStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO campaign_registry_state (StateKey, RegistryEpoch)
            VALUES (1, 1)
            ON CONFLICT (StateKey) DO NOTHING;
            """;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Seeds the exactly zeroed installation turn-capacity singleton.
    /// </summary>
    /// <remarks>
    /// The counter moves are updates, not upserts, so an installation with no singleton cannot
    /// finalize a single turn: the guard's capacity move reports a missing row and the whole
    /// finalization rolls back. Zeroed is the one shape the table's insert trigger accepts without a
    /// turn-capacity authorization, because it creates an owner and grants no capacity.
    /// </remarks>
    private static async Task SeedInstallationTurnQuotaStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO installation_turn_quota_state (
                StateKey,
                ClaimCount,
                ReservedFinalizationCount,
                ConsumedFinalizationCount)
            VALUES (1, 0, 0, 0)
            ON CONFLICT (StateKey) DO NOTHING;
            """;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Seeds the installation authority row, or converges an existing one onto the master key this
    /// startup actually holds.
    /// </summary>
    /// <remarks>
    /// A changed fingerprint means the master key changed, and every counter advances by one from
    /// the value already stored rather than from anything the caller supplied. Advancing from the
    /// durable row is what makes a rotate-back visible: returning to a previously used key produces
    /// a new, higher version and epoch instead of restoring the old pair, so no reader can mistake
    /// the round trip for "nothing happened".
    ///
    /// <para>The statement lists only the four key-derived columns and the timestamp. The
    /// installation identity, the host-tools state, and both taint columns are absent by design, so
    /// no reachable path through this method can downgrade a tainted installation to clean or clear
    /// the evidence describing the escape.</para>
    /// </remarks>
    private static async Task SeedOrConvergeAuthorityStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        string installedAtUtc,
        CancellationToken cancellationToken)
    {

        AuthorityStateRow? existing = await ReadAuthorityStateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {

            await InsertAuthorityStateAsync(connection, transaction, context, installedAtUtc, cancellationToken)
                .ConfigureAwait(false);

            return;

        }

        RequireWellFormed(existing);

        if (CryptographicOperations.FixedTimeEquals(
            existing.CurrentMasterKeyFingerprint,
            context.MasterKeyFingerprint))
        {

            return;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE covenant_authority_state
            SET CurrentMasterKeyVersion = $currentMasterKeyVersion,
                CurrentMasterKeyFingerprint = $currentMasterKeyFingerprint,
                AuthorityEpoch = $authorityEpoch,
                RecoveryEnvelopeEpoch = $recoveryEnvelopeEpoch,
                UpdatedAtUtc = $updatedAtUtc
            WHERE StateKey = 1;
            """;

        _ = command.Parameters.AddWithValue(
            "$currentMasterKeyVersion",
            Advance(existing.CurrentMasterKeyVersion, MaximumMasterKeyVersion, "master key version"));

        _ = command.Parameters.AddWithValue("$currentMasterKeyFingerprint", context.MasterKeyFingerprint);

        _ = command.Parameters.AddWithValue(
            "$authorityEpoch",
            Advance(existing.AuthorityEpoch, long.MaxValue, "epoch"));

        _ = command.Parameters.AddWithValue(
            "$recoveryEnvelopeEpoch",
            Advance(existing.RecoveryEnvelopeEpoch, long.MaxValue, "recovery envelope epoch"));

        _ = command.Parameters.AddWithValue("$updatedAtUtc", installedAtUtc);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task InsertAuthorityStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GrimoireSchemaInitializationContext context,
        string installedAtUtc,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO covenant_authority_state (
                StateKey,
                InstallationIdentity,
                AuthorityEpoch,
                CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint,
                RecoveryEnvelopeEpoch,
                HostToolsStateCode,
                TaintTimeMasterVersion,
                TaintFingerprint,
                TransitionId,
                UpdatedAtUtc)
            VALUES (
                1,
                $installationIdentity,
                $authorityEpoch,
                $currentMasterKeyVersion,
                $currentMasterKeyFingerprint,
                $recoveryEnvelopeEpoch,
                $hostToolsStateCode,
                NULL,
                NULL,
                NULL,
                $updatedAtUtc);
            """;

        _ = command.Parameters.AddWithValue("$installationIdentity", context.InstallationIdentity);

        _ = command.Parameters.AddWithValue("$authorityEpoch", context.AuthorityEpoch);

        // Stored in checked signed storage: the version is an unsigned counter in the domain, and
        // SQLite has no unsigned integer type.
        _ = command.Parameters.AddWithValue("$currentMasterKeyVersion", (long)context.MasterKeyVersion);

        _ = command.Parameters.AddWithValue("$currentMasterKeyFingerprint", context.MasterKeyFingerprint);

        _ = command.Parameters.AddWithValue("$recoveryEnvelopeEpoch", context.RecoveryEnvelopeEpoch);

        _ = command.Parameters.AddWithValue("$hostToolsStateCode", CleanHostToolsStateCode);

        _ = command.Parameters.AddWithValue("$updatedAtUtc", installedAtUtc);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<AuthorityStateRow?> ReadAuthorityStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT InstallationIdentity,
                   AuthorityEpoch,
                   CurrentMasterKeyVersion,
                   CurrentMasterKeyFingerprint,
                   RecoveryEnvelopeEpoch,
                   HostToolsStateCode,
                   TaintTimeMasterVersion,
                   TaintFingerprint,
                   TransitionId
            FROM covenant_authority_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        if (reader.GetValue(3) is not byte[] fingerprint)
        {

            throw MalformedAuthorityState();

        }

        if (!HostProcessToolsTaintVersionStorage.TryDecode(
            reader.GetValue(6),
            out ulong? taintTimeMasterVersion))
        {

            throw MalformedAuthorityState();

        }

        return new AuthorityStateRow(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            fingerprint,
            reader.GetInt64(4),
            reader.GetInt64(5),
            taintTimeMasterVersion,
            reader.IsDBNull(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));

    }

    /// <summary>
    /// Backfills the missing Session bindings under one Session binding write scope.
    /// </summary>
    /// <remarks>
    /// The Sessions needing a binding are read into memory before the first insert. The selecting
    /// query reads <c>session_campaign_bindings</c> in its own <c>NOT EXISTS</c> filter, and writing
    /// to a table an open cursor is still filtering on is exactly the case SQLite leaves undefined,
    /// so the read finishes before the write starts.
    ///
    /// <para>A database with nothing to backfill takes no authorization at all. The scope is the
    /// narrow, false-by-default permission that lets a writer fabricate Campaign authority for a
    /// Session, so the repeat install that has no bindings to write should not open it, and no
    /// caller should be able to read a granted scope as evidence that work happened.</para>
    /// </remarks>
    private static async Task BackfillSessionBindingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string installedAtUtc,
        CancellationToken cancellationToken)
    {

        List<UnboundSession> unbound = await ReadUnboundSessionsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (unbound.Count == 0)
        {

            return;

        }

        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($sessionId, $bindingKindCode, $campaignId, $boundAtUtc);
            """;

        SqliteParameter sessionId = command.Parameters.Add("$sessionId", SqliteType.Text);

        SqliteParameter bindingKindCode = command.Parameters.Add("$bindingKindCode", SqliteType.Integer);

        SqliteParameter campaignId = command.Parameters.Add("$campaignId", SqliteType.Text);

        SqliteParameter boundAtUtc = command.Parameters.Add("$boundAtUtc", SqliteType.Text);

        boundAtUtc.Value = installedAtUtc;

        foreach (UnboundSession session in unbound)
        {

            string? boundCampaignId = NormalizeCampaignId(session.CampaignId);

            sessionId.Value = session.SessionId;

            bindingKindCode.Value = boundCampaignId is null
                ? LegacyUnresolvedBindingKindCode
                : CampaignBindingKindCode;

            campaignId.Value = (object?)boundCampaignId ?? DBNull.Value;

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        }

    }

    private static async Task<List<UnboundSession>> ReadUnboundSessionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT session."Id", session."CampaignId"
            FROM "Sessions" AS session
            WHERE NOT EXISTS (
                SELECT 1
                FROM session_campaign_bindings AS binding
                WHERE binding.SessionId = session."Id")
            ORDER BY session."Id";
            """;

        List<UnboundSession> unbound = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            unbound.Add(new UnboundSession(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));

        }

        return unbound;

    }

    /// <summary>
    /// Gives the Covenant family a cleanup cursor when it does not already have one.
    /// </summary>
    /// <remarks>
    /// A new cursor starts at sequence zero, which claims the family has applied nothing and
    /// therefore still owes cleanup for every event already in the core journal. That is the safe
    /// direction to be wrong in: replaying an owner deletion that was already handled is idempotent,
    /// while starting at the journal head would silently forgive cleanup this family actually owes.
    /// </remarks>
    private static async Task SeedCovenantCleanupCursorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string installedAtUtc,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO capability_cleanup_state (
                CapabilityFamilyCode,
                AppliedCampaignSequence,
                AppliedSessionSequence,
                FullSweepRequired,
                UpdatedAtUtc)
            VALUES ($capabilityFamilyCode, 0, 0, 0, $updatedAtUtc)
            ON CONFLICT (CapabilityFamilyCode) DO NOTHING;
            """;

        _ = command.Parameters.AddWithValue("$capabilityFamilyCode", (long)GrimoireSchemaFamily.Covenant);

        _ = command.Parameters.AddWithValue("$updatedAtUtc", installedAtUtc);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Rejects a context that could only produce a row the schema will refuse, so the failure names
    /// the caller's bad input instead of surfacing as an opaque constraint violation that rolls the
    /// whole Core tier back.
    /// </summary>
    private static void RequireUsableContext(GrimoireSchemaInitializationContext context)
    {

        if (string.IsNullOrWhiteSpace(context.InstallationIdentity)
            || context.InstallationIdentity.Length > 128
            || context.AuthorityEpoch <= 0
            || context.MasterKeyVersion == 0
            || context.MasterKeyFingerprint is not { Length: 32 }
            || context.RecoveryEnvelopeEpoch <= 0)
        {

            throw new InvalidOperationException(
                "The installation context supplied to Core schema initialization does not describe a "
                + "usable authority identity, so no authority row is written.");

        }

    }

    /// <summary>
    /// Fails closed on an authority row this build cannot read as authoritative.
    /// </summary>
    /// <remarks>
    /// The table's own checks already reject these shapes at write time, so a row that fails here
    /// predates them or was written around them. Reseeding it would be the worst available answer:
    /// it would replace unexplained evidence with a clean-looking row.
    /// </remarks>
    private static void RequireWellFormed(AuthorityStateRow row)
    {

        bool wellFormed = row.InstallationIdentity.Length is > 0 and <= 128
            && row.AuthorityEpoch > 0
            && row.CurrentMasterKeyVersion is > 0 and <= MaximumMasterKeyVersion
            && row.CurrentMasterKeyFingerprint.Length == 32
            && row.RecoveryEnvelopeEpoch > 0
            && row.HostToolsStateCode is >= 1 and <= 3
            && HasClosedTaintShape(row);

        if (!wellFormed)
        {

            throw MalformedAuthorityState();

        }

    }

    /// <summary>
    /// A clean installation carries no taint evidence, and a pending or tainted one cannot exist
    /// without the transition identity and taint-time version that make it actionable.
    /// </summary>
    private static bool HasClosedTaintShape(AuthorityStateRow row) =>
        row.HostToolsStateCode == CleanHostToolsStateCode
            ? row.TaintTimeMasterVersion is null && row.TaintFingerprintIsNull && row.TransitionId is null
            : row.TaintTimeMasterVersion > 0 && !string.IsNullOrEmpty(row.TransitionId);

    /// <summary>
    /// Produces the next value of a monotonic authority counter, refusing at its ceiling rather than
    /// wrapping into a value a reader would compare as an older generation.
    /// </summary>
    private static long Advance(long current, long ceiling, string counter)
    {

        if (current >= ceiling)
        {

            throw new InvalidOperationException(
                $"The installation authority {counter} is at its maximum and cannot advance, so this "
                + "master key change cannot be recorded.");

        }

        return current + 1;

    }

    /// <summary>
    /// Puts a Campaign identity into the uppercase hyphenated form the rest of the Grimoire stores,
    /// so a binding written here compares equal to the same Campaign written by anything else.
    /// </summary>
    /// <remarks>
    /// A value that is not a recognizable identity is kept exactly as it was found. It is still a
    /// durable fact that this Session belonged to that Campaign, and the binding column holds
    /// historical identity with no foreign key precisely so such a fact survives; discarding it
    /// would invent an unresolved Session out of a resolved one.
    /// </remarks>
    private static string? NormalizeCampaignId(string? campaignId)
    {

        if (string.IsNullOrWhiteSpace(campaignId))
        {

            return null;

        }

        return Guid.TryParse(campaignId, out Guid parsed)
            ? parsed.ToString("D").ToUpperInvariant()
            : campaignId;

    }

    private static InvalidOperationException MalformedAuthorityState() =>
        new("The installation authority row is present but malformed; Arcanum fails closed rather "
            + "than reseeding evidence it cannot read.");

    private sealed record AuthorityStateRow(
        string InstallationIdentity,
        long AuthorityEpoch,
        long CurrentMasterKeyVersion,
        byte[] CurrentMasterKeyFingerprint,
        long RecoveryEnvelopeEpoch,
        long HostToolsStateCode,
        ulong? TaintTimeMasterVersion,
        bool TaintFingerprintIsNull,
        string? TransitionId);

    private sealed record UnboundSession(string SessionId, string? CampaignId);

}

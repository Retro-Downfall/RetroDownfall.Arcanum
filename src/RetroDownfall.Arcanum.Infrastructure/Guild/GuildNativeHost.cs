using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Native operator ingress uses the same host-issued caller objects as the local execution graph.</summary>
internal sealed class GuildNativeHost(GuildLocalHostPolicy policy, IGuildLocalHostReadiness readiness,
    IGuildLocalQuestScheduler scheduler, IOptionsMonitor<ArcanumSettings> settings,
    IGrimoireOrdinaryConnectionFactory connections)
{
    internal Task<Result<GuildCallerContext>> CreateCallerAsync(Guid invocation, CancellationToken ct) =>
        policy.CreateNativeOperatorAsync(invocation, ct);

    internal Result RequireExecutionReady() => readiness.RequireExecutionReady();

    internal async Task<Result<GuildStatusDto>> GetStatusAsync(CancellationToken ct)
    {
        bool enabled = settings.CurrentValue.Features.Guild;

        Result<IGrimoireOrdinaryConnectionLease> opened = await connections.OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using IGrimoireOrdinaryConnectionLease held = opened.Value;

        await using SqliteTransaction transaction = held.Connection.BeginTransaction(deferred: true);

        await using SqliteCommand command = held.Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='guild_local_executions')";

        bool installed = Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));

        command.CommandText = installed
            ? """
                SELECT count(*) FROM Apprentices a WHERE a.Status IN('Idle','Planning','Running','Paused','Escalated')
                    AND NOT EXISTS(SELECT 1 FROM guild_local_executions b WHERE b.ApprenticeId=a.Id)
                """
            : "SELECT count(*) FROM Apprentices WHERE Status IN('Idle','Planning','Running','Paused','Escalated')";

        long legacy = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));

        bool schemaReady = installed && (await GuildStorageAvailability.CheckAsync(held.Connection, transaction, ct).ConfigureAwait(false)).IsSuccess;

        bool available = GuildLocalExecutionAdmission.IsAvailable(settings.CurrentValue) && schemaReady
            && scheduler.IsAvailable && readiness.RequireExecutionReady().IsSuccess;

        return new GuildStatusDto(enabled, available, available || !enabled ? [] : [ErrorCodes.Guild.Unavailable],
            schemaReady ? GrimoireSchemaVersionChains.GuildCanonicalSchemaVersion : 0, legacy);
    }
}

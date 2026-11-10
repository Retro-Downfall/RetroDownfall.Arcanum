using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalHostPolicy
{
    private readonly ConditionalWeakTable<GuildCallerContext, RemoteCleanupCredential> _remoteCleanups = new();

    internal async Task<Result<GuildQuestDriver>> CreateRemoteCleanupWithinAsync(GuildCallerContext owner, GuildQuestStored row,
        LongRunningOperation operation, SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (owner.Kind != GuildCallerKind.Operator || !Authenticates(owner) || !row.Quest.CancellationRequested
            || row.Quest.ResultReceiptId is not null || row.Retired || operation.Id != row.Quest.OperationId) return Forbidden;

        Result located = await AuthorizeLocateAsync(owner, connection, transaction, row.Quest.Id, row.Quest.SessionId,
            row.Quest.CampaignId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        var signet = await GuildSignetStore.ReadAsync(connection, transaction, row.Quest.SignetId, false, ct).ConfigureAwait(false);

        if (signet.IsFailure) return signet.Error;

        var epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure || signet.Value.AuthorityEpoch != epoch.Value) return Forbidden;

        var binding = signet.Value.Binding;

        GuildExecutionHandle execution = new(binding.SignetId, binding.AgentId, binding.RunId, binding.QuestId, binding.RootQuestId,
            signet.Value.CredentialGeneration, signet.Value.RevocationGeneration, binding.IssuerSignetId);

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, owner.PrincipalId!.Value, Guid.NewGuid(), InstallationGeneration,
            receiptId: row.Quest.Id, receiptSource: "Guild.Remote.Cancel");

        _remoteCleanups.Add(caller, new(owner, execution, operation.Id, epoch.Value));

        GuildQuestDriver driver = new(execution, "observe", 0, caller);

        Result authorized = await AuthorizeRemoteCleanupWithinAsync(driver, row, connection, transaction, ct).ConfigureAwait(false);

        return authorized.IsFailure ? authorized.Error : driver;
    }

    internal async Task<Result> AuthorizeRemoteCleanupWithinAsync(GuildQuestDriver driver, GuildQuestStored row,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (driver.Caller is not { Kind: GuildCallerKind.HostReceipt } caller
            || !_remoteCleanups.TryGetValue(caller, out var credential) || !ReferenceEquals(credential.Execution, driver.Execution)
            || caller.ReceiptId != row.Quest.Id || caller.ReceiptSource != "Guild.Remote.Cancel"
            || credential.OperationId != row.Quest.OperationId || !row.Quest.CancellationRequested || row.Retired
            || !Authenticates(credential.Owner) || caller.InstallationGeneration != InstallationGeneration) return Forbidden;

        Result identity = await CurrentIdentityAsync(connection, transaction, ct).ConfigureAwait(false);

        if (identity.IsFailure) return identity;

        var epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure || epoch.Value != credential.AuthorityEpoch) return Forbidden;

        var own = await GuildSignetStore.ReadAsync(connection, transaction, row.Quest.SignetId, false, ct).ConfigureAwait(false);

        if (own.IsFailure || own.Value.CredentialGeneration != driver.Execution.CredentialGeneration
            || own.Value.RevocationGeneration != driver.Execution.RevocationGeneration) return Forbidden;

        var lineage = await GuildSignetStore.LineageAsync(connection, transaction, own.Value, ct).ConfigureAwait(false);

        if (lineage.IsFailure) return lineage.Error;

        foreach (GuildSignetRecord signet in lineage.Value)
        {
            if (signet.RestoredInert || signet.Retired || signet.InstallationGeneration != InstallationGeneration
                || signet.AuthorityEpoch != credential.AuthorityEpoch || signet.Binding.OriginPrincipalId != OperatorPrincipalId) return Forbidden;

            Result owners = await GuildSignetStore.OwnersAsync(connection, transaction, signet, ct).ConfigureAwait(false);

            if (owners.IsFailure) return owners;
        }

        return await AuthorizeLocateAsync(credential.Owner, connection, transaction, row.Quest.Id, row.Quest.SessionId,
            row.Quest.CampaignId, ct).ConfigureAwait(false);
    }

    private sealed record RemoteCleanupCredential(GuildCallerContext Owner, GuildExecutionHandle Execution, Guid OperationId,
        Guid AuthorityEpoch);
}

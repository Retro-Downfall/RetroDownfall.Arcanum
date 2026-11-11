using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalHostPolicy
{
    internal const string LocalCleanupReceiptSource = "Guild.Local.Cancel";

    private readonly ConditionalWeakTable<GuildCallerContext, LocalCleanupCredential> _localCleanups = new();

    internal async Task<Result<GuildQuestDriver>> CreateLocalCleanupWithinAsync(GuildCallerContext owner, GuildQuestStored row,
        LongRunningOperation operation, SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (owner.Kind != GuildCallerKind.Operator || !Authenticates(owner) || !row.Quest.CancellationRequested
            || row.Quest.ResultReceiptId is not null || operation.Id != row.Quest.OperationId) return Forbidden;

        Result located = await AuthorizeLocateAsync(owner, connection, transaction, row.Quest.Id, row.Quest.SessionId,
            row.Quest.CampaignId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        var signet = await GuildSignetStore.ReadAsync(connection, transaction, row.Quest.SignetId, false, ct).ConfigureAwait(false);

        if (signet.IsFailure) return signet.Error;

        var epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure || signet.Value.AuthorityEpoch != epoch.Value) return Forbidden;

        if (row.Issuance.InstallationGeneration != InstallationGeneration || operation.RunId != row.Quest.RunId
            || operation.SessionId != row.Quest.SessionId || operation.Kind != LongRunningOperationKinds.GuildQuest) return Forbidden;

        var binding = signet.Value.Binding;

        GuildExecutionHandle execution = new(binding.SignetId, binding.AgentId, binding.RunId, binding.QuestId, binding.RootQuestId,
            signet.Value.CredentialGeneration, signet.Value.RevocationGeneration, binding.IssuerSignetId);

        GuildCallerContext caller = new(GuildCallerKind.HostReceipt, owner.PrincipalId!.Value, Guid.NewGuid(), InstallationGeneration,
            receiptId: row.Quest.Id, receiptSource: LocalCleanupReceiptSource);

        _localCleanups.Add(caller, new(owner, execution, operation.Id, epoch.Value));

        GuildQuestDriver driver = new(execution, "observe", 0, caller);

        Result authorized = await AuthorizeLocalCleanupWithinAsync(driver, row, connection, transaction, ct).ConfigureAwait(false);

        return authorized.IsFailure ? authorized.Error : driver;
    }

    internal async Task<Result> AuthorizeLocalCleanupWithinAsync(GuildQuestDriver driver, GuildQuestStored row,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        if (driver.Caller is not { Kind: GuildCallerKind.HostReceipt } caller
            || !_localCleanups.TryGetValue(caller, out var credential) || !ReferenceEquals(credential.Execution, driver.Execution)
            || caller.ReceiptId != row.Quest.Id || caller.ReceiptSource != LocalCleanupReceiptSource
            || credential.OperationId != row.Quest.OperationId || !row.Quest.CancellationRequested
            || !Authenticates(credential.Owner) || caller.InstallationGeneration != InstallationGeneration) return Forbidden;

        Result identity = await CurrentIdentityAsync(connection, transaction, ct).ConfigureAwait(false);

        if (identity.IsFailure) return identity;

        var epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure || epoch.Value != credential.AuthorityEpoch) return Forbidden;

        var own = await GuildSignetStore.ReadAsync(connection, transaction, row.Quest.SignetId, false, ct).ConfigureAwait(false);

        if (own.IsFailure || own.Value.CredentialGeneration != driver.Execution.CredentialGeneration
            || own.Value.RevocationGeneration != driver.Execution.RevocationGeneration) return Forbidden;

        var binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false);

        if (binding.IsFailure || binding.Value.RunId != driver.Execution.RunId || binding.Value.RootQuestId != driver.Execution.RootQuestId
            || own.Value.Binding.QuestId != row.Quest.Id || own.Value.Binding.SignetId != driver.Execution.SignetId
            || own.Value.Binding.AgentId != driver.Execution.AgentId || own.Value.Binding.RunId != driver.Execution.RunId
            || own.Value.Binding.RootQuestId != driver.Execution.RootQuestId || row.Issuance.InstallationGeneration != InstallationGeneration)
            return Forbidden;

        var lineage = await GuildSignetStore.LineageAsync(connection, transaction, own.Value, ct).ConfigureAwait(false);

        if (lineage.IsFailure) return lineage.Error;

        foreach (GuildSignetRecord signet in lineage.Value)
        {
            if (signet.RestoredInert || signet.InstallationGeneration != InstallationGeneration
                || signet.AuthorityEpoch != credential.AuthorityEpoch || signet.Binding.OriginPrincipalId != OperatorPrincipalId) return Forbidden;

            // Cleanup consumes immutable local custody, never the deleted owner's authority.
            // Retirement and missing owners cannot revive Run access, but must not prevent stop evidence.
        }

        return await AuthorizeLocateAsync(credential.Owner, connection, transaction, row.Quest.Id, row.Quest.SessionId,
            row.Quest.CampaignId, ct).ConfigureAwait(false);
    }

    private sealed record LocalCleanupCredential(GuildCallerContext Owner, GuildExecutionHandle Execution, Guid OperationId,
        Guid AuthorityEpoch);
}

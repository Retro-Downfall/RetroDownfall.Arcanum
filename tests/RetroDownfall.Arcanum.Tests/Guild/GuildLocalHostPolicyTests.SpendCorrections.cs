using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Native_operator_can_correct_after_revocation_but_spoofed_caller_cannot()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);
        GuildCallerContext owner = await f.OperatorAsync();
        var issued = await f.IssueAsync(owner);
        Assert.True(issued.IsSuccess, issued.Error.Message);
        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, issued.Value);
        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);
        Assert.True(opened.IsSuccess, opened.Error.Message);
        await using var lease = opened.Value;
        await using ArcanumDbContext db = GrimoireFixture.CreateContext(lease.Connection);
        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()));
        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), issued.Value.Quest.Id, execution.Dispatch.DispatchId,
            issued.Value.Quest.RunId, GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);
        Assert.True((await f.Store.ExecuteProviderEffectAsync(execution, daily, effect,
            _ => Task.FromResult(Result<string>.Success("answer")), default)).IsSuccess);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BillableOperationRecord bill = new(issued.Value.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
            now, now, 100, 20, 5, 40, "{}", .25m, BillableOperationStatus.Completed, "request-id");
        var initial = await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default);
        Assert.True(initial.IsSuccess, initial.Error.Message);
        GuildSpendCorrectionRequest request = new(Guid.NewGuid(), initial.Value.Revision, GuildSpendOrigin.OperatorAttested,
            new(110, 25, 45, 6, .30m), new string('A', 64));
        GuildCallerContext spoof = new(GuildCallerKind.Operator, owner.PrincipalId!.Value, Guid.NewGuid(), owner.InstallationGeneration);
        Assert.True((await f.Store.CorrectOperatorSpendAsync(spoof, issued.Value.Quest.Id, effect.EffectId, request, default)).IsFailure);
        var current = await f.Store.GetAsync(owner, issued.Value.Quest.Id, default);
        Assert.True(current.IsSuccess, current.Error.Message);
        Guid key = Guid.NewGuid();
        var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('B', 64)), current.Value.Quest.SignetId,
            new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);
        Assert.True(revoked.IsSuccess, revoked.Error.Message);
        var corrected = await f.Store.CorrectOperatorSpendAsync(owner, issued.Value.Quest.Id, effect.EffectId, request, default);
        Assert.True(corrected.IsSuccess, corrected.Error.Message);
        Assert.Equal(.30m, corrected.Value.Receipt.Facts.KnownUsd);
        await using var cleanup = lease.Connection.CreateCommand();
        cleanup.CommandText = """
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,EventSequence,PhaseCode,CreatedAtUtc,UpdatedAtUtc)
            VALUES(2,$session,NULL,0,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')
            """;
        cleanup.Parameters.AddWithValue("$session", issued.Value.Quest.SessionId!.Value.ToString("D").ToUpperInvariant());
        Assert.Equal(1, await cleanup.ExecuteNonQueryAsync());
        Assert.True((await f.Store.CorrectOperatorSpendAsync(owner, issued.Value.Quest.Id, effect.EffectId, request, default)).IsFailure);
        Assert.True((await f.Store.ReadOperatorSpendReceiptAsync(owner, issued.Value.Quest.Id, effect.EffectId,
            corrected.Value.Receipt.ReceiptId, default)).IsFailure);
        cleanup.CommandText = "DELETE FROM guild_owner_cleanup WHERE OwnerKindCode=2 AND OwnerId=$session";
        Assert.Equal(1, await cleanup.ExecuteNonQueryAsync());
        using (RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionInitializer.Instance.Authorize(
            lease.Connection, RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteAuthorizationKind.SessionRetention))
        {
            cleanup.CommandText = "DELETE FROM Sessions WHERE Id=$session";
            Assert.Equal(1, await cleanup.ExecuteNonQueryAsync());
        }
        Assert.True((await f.Store.CorrectOperatorSpendAsync(owner, issued.Value.Quest.Id, effect.EffectId, request, default)).IsFailure);
        Assert.True((await f.Store.ReadOperatorSpendReceiptAsync(owner, issued.Value.Quest.Id, effect.EffectId,
            corrected.Value.Receipt.ReceiptId, default)).IsFailure);
    }
}

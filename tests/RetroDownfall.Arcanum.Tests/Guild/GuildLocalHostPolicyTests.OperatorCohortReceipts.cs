using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Operator_and_run_cohort_mutations_do_not_share_idempotency_scope()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner, f.Request with
        {
            Grant = f.Request.Grant with { Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Cohort] },
        });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext issuer = await BindCohortRunAsync(f, owner, root.Value);

        GuildCohortStore cohorts = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority,
            f.Store, new GuildBudgetLedger(), f.Policy, TimeProvider.System);

        Guid key = Guid.NewGuid();

        CreateCohortRequest runRequest = new(key, root.Value.Quest.Id, "Run's Cohort", new(GuildConvergenceKind.All, null), new("text/plain"), null);

        var run = await cohorts.CreateAsync(issuer, runRequest, default);

        Assert.True(run.IsSuccess, run.Error.Message);

        CreateCohortRequest operatorRequest = runRequest with { Name = "Operator's Cohort" };

        var managed = await cohorts.CreateOperatorAsync(owner, operatorRequest, default);

        Assert.True(managed.IsSuccess, managed.Error.Message);

        Assert.NotEqual(run.Value.Id, managed.Value.Id);

        var replay = await cohorts.CreateOperatorAsync(owner, operatorRequest, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(managed.Value.Id, replay.Value.Id);

        Assert.True(replay.Value.Receipt!.Replayed);
    }

    [Fact]
    public async Task Operator_cohort_receipt_storage_rejects_forged_principal_and_result_association()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, _, cohorts, cohort, _) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        Guid wrong = Guid.NewGuid();

        Guid key = Guid.NewGuid();

        string generation = f.Generation.ToString("D").ToUpperInvariant();

        string parent = cohort.Id.ToString("D").ToUpperInvariant();

        string forged = wrong.ToString("D").ToUpperInvariant();

        string receipt = Guid.NewGuid().ToString("D").ToUpperInvariant();

        string idempotency = key.ToString("D").ToUpperInvariant();

        string validPrincipal = owner.PrincipalId!.Value.ToString("D").ToUpperInvariant();

        string command(string principal, int action, string? result) => $"""
            INSERT INTO guild_operator_cohort_mutations(InstallationGeneration,PrincipalId,ActionCode,ParentId,IdempotencyKey,ReceiptId,
                AcceptedAtUtc,RequestDigest,CohortId,ResultObjectId,AcceptedRevision)
            VALUES('{generation}','{principal}',{action},'{parent}','{idempotency}','{receipt}',
                '2026-11-11T00:00:00.0000000Z',zeroblob(32),'{parent}',{result},1)
            """;

        await Assert.ThrowsAsync<SqliteException>(() => f.ExecuteAsync(command(forged, 2, "NULL")));

        await Assert.ThrowsAsync<SqliteException>(() => f.ExecuteAsync(command(validPrincipal, 3, "'" + forged + "'")));

        await Assert.ThrowsAsync<SqliteException>(() => f.ExecuteAsync(command(validPrincipal, 99, "NULL")));
    }

}

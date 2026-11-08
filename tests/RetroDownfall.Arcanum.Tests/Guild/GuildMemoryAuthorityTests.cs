using RetroDownfall.Arcanum.Core.Guild;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildMemoryAuthorityTests
{
    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("missing-schema")]
    public async Task Retained_memory_invocation_never_falls_back_after_live_authority_is_lost(string reason)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with
        {
            ExpiresAtUtc = f.Time.GetUtcNow().AddMinutes(1),

            Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))],
        };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Assert.Equal(1, await GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga, GuildMemoryOperation.Read,
            _ => Task.FromResult(1), default));

        if (reason == "expired")
        {
            f.Time.Advance(TimeSpan.FromMinutes(1));
        }
        else if (reason == "revoked")
        {
            Guid key = Guid.NewGuid();

            var revoked = await f.Authority.RevokeAsync(f.Operator, new(key, new string('A', 64)), execution.SignetId,
                new(key, 1, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }
        else
        {
            await f.ExecuteAsync("DROP TABLE guild_memory_attribution");
        }

        bool called = false;

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga,
            GuildMemoryOperation.Read, _ => { called = true; return Task.FromResult(2); }, default));

        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_does_not_return_materialized_content_after_invocation_or_policy_ends(bool endInvocation)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable scope = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string> read = Task.Run(() => GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga, GuildMemoryOperation.Read, async _ =>
        {
            entered.SetResult();

            await release.Task;

            return "materialized content";
        }, default));

        await entered.Task;

        if (endInvocation)
        {
            scope.Dispose();
        }
        else
        {
            f.Policy.HostGrant = f.Grant;
        }

        release.SetResult();

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => read);
    }

    [Fact]
    public async Task Typed_admission_cannot_lend_one_execution_authority_to_another_invocation()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle authorized = await f.RootAsync(grant);

        GuildExecutionHandle other = await f.RootAsync();

        using IDisposable scope = GuildMemoryInvocationAmbient.Begin(new(f.Authority, other));

        bool called = false;

        var result = await f.Authority.AdmitMemoryAsync(authorized, GuildMemoryStore.Saga, GuildMemoryOperation.Read,
            _ => { called = true; return Task.FromResult(17); }, default);

        Assert.True(result.IsFailure);

        Assert.False(called);
    }

    [Fact]
    public async Task Global_is_explicit_classification_before_selection_and_not_a_missing_campaign()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable scope = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        string selected = await GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga, GuildMemoryOperation.Read, async ct =>
        {
            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            string predicate = GuildMemoryAccess.SqlScope(command, GuildMemoryStore.Saga, GuildMemoryOperation.Read, "CampaignId", "ScopeKindCode");

            command.CommandText = "WITH candidates(Id,ScopeKindCode,CampaignId,Score) AS (VALUES ('unknown',0,NULL,100),('legacy',3,NULL,90),('malformed',1,'other',80),('global',1,NULL,1)) SELECT Id FROM candidates WHERE " + predicate + " ORDER BY Score DESC LIMIT 1";

            return (string)(await command.ExecuteScalarAsync(ct))!;
        }, default);

        Assert.Equal("global", selected);
    }

    [Fact]
    public async Task An_escaped_callback_cannot_reuse_the_completed_admission()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable scope = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string>? escaped = null;

        await GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga, GuildMemoryOperation.Read, _ =>
        {
            escaped = Task.Run(async () =>
            {
                await release.Task;

                using SqliteCommand command = new();

                return GuildMemoryAccess.SqlScope(command, GuildMemoryStore.Saga, GuildMemoryOperation.Read, "CampaignId", "ScopeKindCode");
            });

            return Task.FromResult(true);
        }, default);

        release.SetResult();

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => escaped!);
    }

    [Fact]
    public async Task A_live_global_read_runs_and_an_empty_grant_does_not_run_the_reader()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with { Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable scope = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        Assert.Equal(17, await GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga, GuildMemoryOperation.Read,
            _ => Task.FromResult(17), default));

        bool called = false;

        await Assert.ThrowsAsync<GuildMemoryAccessException>(() => GuildMemoryAccess.RunAsync(GuildMemoryStore.Lexicon,
            GuildMemoryOperation.Read, _ => { called = true; return Task.FromResult(42); }, default));

        Assert.False(called);
    }
}

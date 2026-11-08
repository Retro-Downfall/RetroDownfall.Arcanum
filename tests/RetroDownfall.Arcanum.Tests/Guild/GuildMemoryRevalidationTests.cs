using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildMemoryRevalidationTests
{
    [Theory]
    [InlineData("live", true)]
    [InlineData("raw-transaction", true)]
    [InlineData("policy-narrowed", false)]
    [InlineData("expired", false)]
    [InlineData("invocation-ended", false)]
    public async Task Mutation_revalidation_reads_live_authority_in_the_callers_transaction(string change, bool allowed)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with
        {
            ExpiresAtUtc = f.Time.GetUtcNow().AddMinutes(1),

            Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Global))],
        };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable scope = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        bool validated = false;

        Task<bool> operation = GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga, GuildMemoryOperation.Append, async token =>
        {
            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteTransaction? transaction = change == "raw-transaction" ? null : connection.BeginTransaction();

            if (change == "raw-transaction")
            {
                await using SqliteCommand begin = connection.CreateCommand();

                begin.CommandText = "BEGIN IMMEDIATE";

                _ = await begin.ExecuteNonQueryAsync(token);
            }

            if (change == "policy-narrowed")
            {
                f.Policy.HostGrant = f.Grant;
            }
            else if (change == "expired")
            {
                f.Time.Advance(TimeSpan.FromMinutes(1));
            }
            else if (change == "invocation-ended")
            {
                scope.Dispose();
            }

            await GuildMemoryAccess.ValidateMutationAsync(connection, transaction, GuildMemoryStore.Saga,
                GuildMemoryOperation.Append, null, explicitlyGlobal: true, token);

            validated = true;

            return true;
        }, default);

        if (allowed)
        {
            Assert.True(await operation);
        }
        else
        {
            GuildMemoryAccessException failure = await Assert.ThrowsAsync<GuildMemoryAccessException>(() => operation);

            Assert.Contains(failure.Error.Code, new[] { ErrorCodes.Guild.MemoryNotGranted, ErrorCodes.Guild.AuthorityUnavailable,
                ErrorCodes.Guild.AuthorityExpired });
        }

        Assert.Equal(allowed, validated);
    }

    [Fact]
    public async Task Mutation_revalidation_refuses_a_transaction_owned_by_another_connection()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        GuildGrant grant = f.Grant with
        {
            Memory = [new(GuildMemoryStore.Saga, GuildMemoryOperation.Append, new(GuildMemoryScopeKind.Global))],
        };

        f.Policy.HostGrant = grant;

        GuildExecutionHandle execution = await f.RootAsync(grant);

        using IDisposable scope = GuildMemoryInvocationAmbient.Begin(new(f.Authority, execution));

        GuildMemoryAccessException failure = await Assert.ThrowsAsync<GuildMemoryAccessException>(() =>
            GuildMemoryAccess.RunAsync(GuildMemoryStore.Saga, GuildMemoryOperation.Append, async token =>
            {
                await using SqliteConnection connection = await f.OpenAsync();

                await using SqliteConnection other = await f.OpenAsync();

                await using SqliteTransaction transaction = other.BeginTransaction();

                await GuildMemoryAccess.ValidateMutationAsync(connection, transaction, GuildMemoryStore.Saga,
                    GuildMemoryOperation.Append, null, explicitlyGlobal: true, token);

                return true;
            }, default));

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, failure.Error.Code);
    }
}

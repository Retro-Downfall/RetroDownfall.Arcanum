using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Native_sqlite_writer_pressure_refuses_acceptance_without_partial_rows_and_recovers()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        GuildPublishRequest request = Progress();

        long bodies = await f.CountAsync("guild_content");

        await using (SqliteConnection holder = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = holder.BeginTransaction(deferred: false);

            // Qualify the actual native failure separately from the domain mapping. This is an
            // independent admitted SQLite writer contending with a real transaction, not a trigger.
            await using (SqliteConnection contender = await f.OpenAsync())
            {
                contender.DefaultTimeout = 1;

                SqliteException error = Assert.Throws<SqliteException>(() => contender.BeginTransaction(deferred: false));

                Assert.Contains(error.SqliteErrorCode, new[] { 5, 6 });
            }

            var refused = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, request, default);

            Assert.Equal(ErrorCodes.Guild.StoragePressure, refused.Error.Code);
        }

        Assert.Equal(bodies, await f.CountAsync("guild_content"));

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));

        Assert.Equal(0L, await f.CountAsync("guild_inbox"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_streams"));

        var accepted = await f.Portal.PublishAsync(f.RunCaller(driver), root.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Equal(1L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Native_sqlite_pressure_preserves_result_outbox_and_restart_does_not_repeat_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        var driver = await f.DriverAsync(root);

        var result = PortalResult(root.Quest.Id, "committed under the original execution");

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), result.Receipt.CanonicalContentDigest),
            root.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        await using (SqliteConnection holder = await f.OpenAsync())
        {
            await using SqliteTransaction transaction = holder.BeginTransaction(deferred: false);

            var refused = await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default);

            Assert.Equal(ErrorCodes.Guild.StoragePressure, refused.Error.Code);
        }

        Assert.Equal(3L, await PortalScalarAsync(f, "SELECT COUNT(*) FROM guild_portal_outbox WHERE DeliveryStateCode=1"));

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));

        f.Restart();

        var drained = await f.Portal.DrainOutboxAsync(f.Caller, root.Quest.Id, default);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Assert.Equal(3, drained.Value);

        Assert.Equal(0L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(1L, await f.CountAsync("fixture_settlements"));

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));
    }
}

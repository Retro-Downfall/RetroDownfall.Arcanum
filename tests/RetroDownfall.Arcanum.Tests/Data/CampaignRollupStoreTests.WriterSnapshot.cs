using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Storage;

using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class CampaignRollupStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rollup_writers_reserve_the_database_before_reading_the_source_snapshot(bool publish, bool legacy)
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync(legacy);

        fixture.Connection.DefaultTimeout = 1;

        await fixture.ExecuteAsync("PRAGMA journal_mode=WAL; CREATE TABLE unrelated_writer_probe (Id INTEGER PRIMARY KEY);");

        CampaignRollupInput? input = null;

        if (publish)
        {
            var contribution = await fixture.Store.PublishContributionAsync(await fixture.PrepareAsync(), "decision", null, null, CancellationToken.None);

            Assert.True(contribution.IsSuccess, contribution.Error.Message);

            var prepared = await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            input = Assert.IsType<CampaignRollupInput>(prepared.Value);
        }
        else
        {
            Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_state;"));
        }

        await using SqliteConnection competitor = new(fixture.Connection.ConnectionString);

        await competitor.OpenAsync();

        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_busy_timeout(competitor.Handle, 0));

        int? competingWrite = null;

        // ROW is reached after SQLite establishes the real Campaign snapshot. A distinct WAL
        // connection can commit here only if the store has not already reserved its writer.
        using CampaignSnapshotTrace trace = new(fixture.Connection, () =>
            competingWrite = raw.sqlite3_exec(competitor.Handle, "INSERT INTO unrelated_writer_probe VALUES (1);"));

        if (publish)
        {
            var result = await fixture.Store.PublishRollupAsync(input!, "Campaign decision", null, null, CancellationToken.None);

            trace.AssertObservedWithoutFault();

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("Campaign decision", result.Value.Content);

            Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_artifacts;"));
        }
        else
        {
            var result = await fixture.Store.PrepareRollupAsync(Campaign, null, CancellationToken.None);

            trace.AssertObservedWithoutFault();

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Null(result.Value);
        }

        Assert.Equal(raw.SQLITE_BUSY, competingWrite);

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM campaign_rollup_state;"));

        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM unrelated_writer_probe;"));

        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_exec(competitor.Handle, "INSERT INTO unrelated_writer_probe VALUES (1);"));

        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM unrelated_writer_probe;"));
    }

    private sealed class CampaignSnapshotTrace : IDisposable
    {
        private readonly SqliteConnection _connection;

        private readonly Action _onSnapshot;

        private readonly NativeTraceCallback _callback;

        private nint _campaignStatement;

        private bool _observed;

        private Exception? _callbackFault;

        internal CampaignSnapshotTrace(SqliteConnection connection, Action onSnapshot)
        {
            _connection = connection;

            _onSnapshot = onSnapshot;

            _callback = Observe;

            Assert.Equal(raw.SQLITE_OK, TraceNative(connection.Handle!.DangerousGetHandle(), 5, _callback, 0));
        }

        private int Observe(uint kind, nint context, nint statement, nint data)
        {
            try
            {
                if (!_observed && kind == 1 && (Marshal.PtrToStringUTF8(data) ?? string.Empty).Contains("FROM \"Campaigns\"", StringComparison.Ordinal))
                {
                    _campaignStatement = statement;
                }
                else if (!_observed && kind == 4 && statement == _campaignStatement)
                {
                    _observed = true;

                    _onSnapshot();
                }
            }
            catch (Exception exception)
            {
                _callbackFault ??= exception;
            }

            return 0;
        }

        internal void AssertObservedWithoutFault()
        {
            Assert.Null(_callbackFault);

            Assert.True(_observed);
        }

        public void Dispose() => Assert.Equal(raw.SQLITE_OK, TraceNative(_connection.Handle!.DangerousGetHandle(), 0, null, 0));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeTraceCallback(uint kind, nint context, nint statement, nint data);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int TraceNative(nint connection, uint mask, NativeTraceCallback? callback, nint context);
    }
}

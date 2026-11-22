using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class BudgetReservationServiceTests
{
    [SkippableFact]
    public async Task Unchanged_native_spend_fold_characterizes_reader_cancellation_without_inventing_token_identity()
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("native-reader-cancel", now);

        await SeedSnapshotBillsAsync(run, now, 32);

        BudgetReservationService service = CreateService();

        service.UtcNowForTesting = () => now;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using CancellationTokenSource cancellation = new();

        using BudgetNativeTrace trace = new(connection);

        int done = 0;

        trace.RowObserved = statement =>
        {
            if (!statement.IsCommittedFold || Interlocked.Exchange(ref done, 1) != 0) return;

            cancellation.Cancel();

            raw.sqlite3_interrupt(connection.Handle);
        };

        Exception? fault = await Record.ExceptionAsync(() => service.GetTodayCommittedSpendAsync(cancellation.Token));

        Assert.NotNull(fault);

        Assert.True(fault is OperationCanceledException or SqliteException);

        Assert.True(cancellation.IsCancellationRequested);

        if (fault is OperationCanceledException canceled)
            _snapshotOutput.WriteLine($"unchanged spend-read driver cancellation: {fault.GetType().Name}, tokenEqual={canceled.CancellationToken == cancellation.Token}, tokenCanBeCanceled={canceled.CancellationToken.CanBeCanceled}");

        Assert.Equal(0L, await CountReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    private async Task SeedSnapshotBillsAsync(Guid run, DateTimeOffset now, int count)
    {
        await using DbCommand cmd = _db!.Database.GetDbConnection().CreateCommand();

        cmd.CommandText = """
            WITH RECURSIVE bills(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM bills WHERE n < @count)
            INSERT INTO "BillableOperations"
                ("Id", "RunId", "OperationType", "Provider", "Model", "Purpose", "StartedAt", "CompletedAt",
                 "InputTokens", "OutputTokens", "ReasoningTokens", "CachedTokens", "PricingSnapshotJson", "ActualCostUsd", "Status")
            SELECT printf('%032x', n), @run, 0, 'test', 'model', 'writer-snapshot', @now, @now,
                1, 1, 0, 0, '{}', '0.0001', 0 FROM bills;
            """;

        AddParameter(cmd, "@count", count);

        AddParameter(cmd, "@run", run.ToString("N"));

        AddParameter(cmd, "@now", UtcInstantText.Format(now));

        Assert.Equal(count, await cmd.ExecuteNonQueryAsync());
    }

    private sealed class NativeBudgetStatement(string sql, bool insideTransaction)
    {
        internal string Sql { get; } = sql;

        internal bool InsideTransaction { get; } = insideTransaction;

        internal bool IsCommittedFold => Sql.Contains("SELECT \"ActualCostUsd\"", StringComparison.Ordinal)
            && Sql.Contains("FROM \"BillableOperations\"", StringComparison.Ordinal);

        internal int Rows { get; set; }

        internal int VmSteps { get; set; }
    }

    private sealed class BudgetNativeTrace : IDisposable
    {
        private readonly SqliteConnection _connection;

        private readonly NativeTraceCallback _callback;

        private readonly Dictionary<nint, NativeBudgetStatement> _active = [];

        private Exception? _callbackFault;

        private bool _disposed;

        internal List<NativeBudgetStatement> Statements { get; } = [];

        internal Action<NativeBudgetStatement>? Started { get; set; }

        internal Action<NativeBudgetStatement>? RowObserved { get; set; }

        internal BudgetNativeTrace(SqliteConnection connection)
        {
            _connection = connection;

            _callback = Observe;

            Assert.Equal(0, TraceNative(connection.Handle!.DangerousGetHandle(), 7, _callback, 0));
        }

        private int Observe(uint kind, nint context, nint statement, nint data)
        {
            try
            {
                if (kind == 1)
                {
                    NativeBudgetStatement item = new(Marshal.PtrToStringUTF8(data) ?? string.Empty, raw.sqlite3_get_autocommit(_connection.Handle) == 0);

                    _active[statement] = item;

                    Statements.Add(item);

                    Started?.Invoke(item);
                }
                else if (_active.TryGetValue(statement, out NativeBudgetStatement? item))
                {
                    if (kind == 4)
                    {
                        item.Rows++;

                        RowObserved?.Invoke(item);
                    }

                    if (kind == 2)
                    {
                        item.VmSteps = StatementStatus(statement, raw.SQLITE_STMTSTATUS_VM_STEP, 1);

                        _active.Remove(statement);
                    }
                }
            }
            catch (Exception exception)
            {
                _callbackFault ??= exception;
            }

            return 0;
        }

        internal void AssertNoCallbackFault() => Assert.Null(_callbackFault);

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            Assert.Equal(0, TraceNative(_connection.Handle!.DangerousGetHandle(), 0, null, 0));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeTraceCallback(uint kind, nint context, nint statement, nint data);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int TraceNative(nint connection, uint mask, NativeTraceCallback? callback, nint context);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_stmt_status", CallingConvention = CallingConvention.Cdecl)]
        private static extern int StatementStatus(nint statement, int operation, int reset);
    }
}

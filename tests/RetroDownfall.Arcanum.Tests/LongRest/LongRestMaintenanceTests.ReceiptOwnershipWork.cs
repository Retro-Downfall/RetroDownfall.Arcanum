using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Subject_erasure_cleans_positive_orphan_manifest_siblings_but_preserves_unknown_historical_orphans(
        bool protectedSources, bool foreignKeys)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources);

        string selected = Guid.NewGuid().ToString("D").ToUpperInvariant();

        string unknown = Guid.NewGuid().ToString("D").ToUpperInvariant();

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await SeedReceiptOrphanInputAsync(f, selected, 1, f.Source, protectedSources);

        await SeedReceiptOrphanInputAsync(f, selected, 2, Guid.NewGuid().ToString("D"), protectedSources);

        await SeedReceiptOrphanInputAsync(f, unknown, 1, Guid.NewGuid().ToString("D"), protectedSources);

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        Assert.Equal(foreignKeys ? 1L : 0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));

        Assert.Equal(2L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId = $id", ("$id", selected)));

        Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId = $id", ("$id", unknown)));

        if (protectedSources)
        {
            await ErasePaidParticipantAsync(f, foreignKeys);
        }
        else
        {
            Assert.True(await f.H.Saga.Store.DeleteAsync(f.Source, CancellationToken.None));
        }

        Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId = $id", ("$id", selected)));

        if (protectedSources)
        {
            Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_protected_receipt_inputs", "ReceiptId = $id", ("$id", selected)));

            Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_protected_receipt_inputs", "ReceiptId = $id", ("$id", unknown)));
        }

        Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId = $id", ("$id", unknown)));

        Assert.Equal(foreignKeys ? 1L : 0L, await f.H.ScalarAsync("PRAGMA foreign_keys;"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Protected_positive_orphan_ownership_survives_missing_claim_and_fourth_GUID_spelling(bool foreignKeys, bool missingClaim)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: true);

        string receipt = Guid.NewGuid().ToString("D").ToUpperInvariant();

        string raw = Guid.Parse(f.Source).ToString("N");

        string fourth = string.Concat(raw.Select((character, index) => index % 2 == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character)));

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await SeedReceiptOrphanInputAsync(f, receipt, 1, fourth, protectedSource: true);

        if (missingClaim)
        {
            await using SqliteCommand command = f.H.Connection.CreateCommand();

            command.CommandText = "DELETE FROM annal_claims WHERE SubjectStoreCode=1 AND SubjectId=$subject;";

            command.Parameters.AddWithValue("$subject", f.Source);

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        await ErasePaidParticipantAsync(f, foreignKeys);

        Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId=$receipt", ("$receipt", receipt)));

        Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_protected_receipt_inputs", "ReceiptId=$receipt", ("$receipt", receipt)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contradictory_protected_orphan_version_is_not_independent_subject_erasure_authority(bool foreignKeys)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: true);

        string receipt = Guid.NewGuid().ToString("D").ToUpperInvariant();

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await using SqliteCommand command = f.H.Connection.CreateCommand();

        command.CommandText = "SELECT VersionId FROM annal_versions WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode=1 AND SubjectId=$subject) ORDER BY Revision DESC LIMIT 1;";

        command.Parameters.AddWithValue("$subject", f.Source);

        string version = Assert.IsType<string>(await command.ExecuteScalarAsync());

        await SeedReceiptOrphanInputAsync(f, receipt, 1, Guid.NewGuid().ToString("D"), protectedSource: true, version);

        command.CommandText = "DELETE FROM long_rest_receipt_inputs WHERE ReceiptId=$receipt;";

        command.Parameters.AddWithValue("$receipt", receipt);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        await ErasePaidParticipantAsync(f, foreignKeys);

        Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_protected_receipt_inputs", "ReceiptId=$receipt", ("$receipt", receipt)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Selected_receipt_families_cross_multiple_frozen_batches_without_losing_siblings(bool protectedSources, bool foreignKeys)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources);

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        for (int index = 0; index < 260; index++)
        {
            string receipt = "RV217-SELECTED-" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

            await SeedReceiptOrphanInputAsync(f, receipt, 1, f.Source, protectedSources);

            await SeedReceiptOrphanInputAsync(f, receipt, 2, Guid.NewGuid().ToString("D"), protectedSources);
        }

        await SeedReceiptOrphanInputAsync(f, "RV217-UNKNOWN", 1, Guid.NewGuid().ToString("D"), protectedSources);

        Assert.Equal(520L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId LIKE 'RV217-SELECTED-%'"));

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        using ReceiptDeleteTrace batches = new(f.H.Connection, static () => { });

        if (protectedSources)
        {
            await ErasePaidParticipantAsync(f, foreignKeys);
        }
        else
        {
            Assert.True(await f.H.Saga.Store.DeleteAsync(f.Source, CancellationToken.None));
        }

        Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId LIKE 'RV217-SELECTED-%'"));

        Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId='RV217-UNKNOWN'"));

        if (!protectedSources)
        {
            Assert.Null(batches.Fault);

            Assert.True(batches.BatchWidths.Count >= 3);

            Assert.All(batches.BatchWidths, static width => Assert.InRange(width, 1, 128));
        }

        if (protectedSources)
        {
            Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_protected_receipt_inputs", "ReceiptId LIKE 'RV217-SELECTED-%'"));

            Assert.Equal(1L, await CountLifecycleAsync(f, "long_rest_protected_receipt_inputs", "ReceiptId='RV217-UNKNOWN'"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Positive_protected_only_memory_coordinate_freezes_the_whole_receipt_before_its_witness_is_removed(bool foreignKeys)
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: true);

        const string receipt = "RV217-PROTECTED-WITNESS";

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await SeedReceiptOrphanInputAsync(f, receipt, 1, Guid.NewGuid().ToString("D"),
            protectedSource: true, protectedMemory: f.Source);

        await SeedReceiptOrphanInputAsync(f, receipt, 2, Guid.NewGuid().ToString("D"), protectedSource: true);

        await f.H.ExecuteAsync(foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        await ErasePaidParticipantAsync(f, foreignKeys);

        Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_protected_receipt_inputs", "ReceiptId=$receipt", ("$receipt", receipt)));

        Assert.Equal(0L, await CountLifecycleAsync(f, "long_rest_receipt_inputs", "ReceiptId=$receipt", ("$receipt", receipt)));
    }

    [Fact]
    public async Task Refused_receipt_child_delete_rolls_back_every_earlier_family_and_subject_effect()
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await SeedReceiptOrphanInputAsync(f, "RV217-ROLLBACK", 1, f.Source, protectedSource: false);

        var before = await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables));

        await f.H.ExecuteAsync("CREATE TRIGGER rv217_refuse_input BEFORE DELETE ON long_rest_receipt_inputs WHEN OLD.ReceiptId='RV217-ROLLBACK' BEGIN SELECT RAISE(ABORT,'RV217 controlled refusal'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => f.H.Saga.Store.DeleteAsync(f.Source, CancellationToken.None));

        Assert.Equal(before.ToArray(), (await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables))).ToArray());

        Assert.Equal(1L, await CountLifecycleAsync(f, "saga_memories", "Id=$memory", ("$memory", f.Source)));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT 1;"));
    }

    [Fact]
    public async Task Caller_cancellation_after_actual_receipt_input_delete_rolls_back_all_family_changes()
    {
        await using MaintenanceFixture f = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await f.H.ExecuteAsync("PRAGMA foreign_keys = OFF;");

        await SeedReceiptOrphanInputAsync(f, "RV217-CANCEL", 1, f.Source, protectedSource: false);

        var before = await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables));

        using CancellationTokenSource caller = new();

        using ReceiptDeleteTrace trace = new(f.H.Connection, caller.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.H.Saga.Store.DeleteAsync(f.Source, caller.Token));

        Assert.True(caller.IsCancellationRequested);

        Assert.True(trace.Observed);

        Assert.Null(trace.Fault);

        Assert.Equal(before.ToArray(), (await ReadLifecycleCountsAsync(f, PaidGenerationTables.Concat(PaidReceiptTables))).ToArray());

        Assert.Equal(1L, await CountLifecycleAsync(f, "saga_memories", "Id=$memory", ("$memory", f.Source)));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT 1;"));
    }

    private sealed class ReceiptDeleteTrace : IDisposable
    {
        private readonly SqliteConnection _connection;

        private readonly Action _completed;

        private readonly ReceiptTraceCallback _callback;

        private readonly Dictionary<nint, string> _active = [];

        internal bool Observed { get; private set; }

        internal Exception? Fault { get; private set; }

        internal List<int> BatchWidths { get; } = [];

        internal ReceiptDeleteTrace(SqliteConnection connection, Action completed)
        {
            _connection = connection;

            _completed = completed;

            _callback = Observe;

            Assert.Equal(0, Trace(connection.Handle!.DangerousGetHandle(), 3, _callback, 0));
        }

        private int Observe(uint kind, nint context, nint statement, nint data)
        {
            try
            {
                if (kind == 1)
                {
                    _active[statement] = Marshal.PtrToStringUTF8(data) ?? string.Empty;
                }
                else if (kind == 2 && _active.Remove(statement, out string? sql)
                    && sql.StartsWith("DELETE FROM long_rest_receipt_inputs", StringComparison.Ordinal))
                {
                    BatchWidths.Add(sql.Split("$annalsReceipt", StringSplitOptions.None).Length - 1);

                    if (!Observed)
                    {
                        Observed = true;

                        _completed();
                    }
                }
            }
            catch (Exception exception)
            {
                Fault ??= exception;
            }

            return 0;
        }

        public void Dispose() => Assert.Equal(0, Trace(_connection.Handle!.DangerousGetHandle(), 0, null, 0));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReceiptTraceCallback(uint kind, nint context, nint statement, nint data);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Trace(nint connection, uint mask, ReceiptTraceCallback? callback, nint context);
    }

    private static async Task SeedReceiptOrphanInputAsync(MaintenanceFixture f, string receipt, int ordinal,
        string memory, bool protectedSource, string? version = null, string? protectedMemory = null)
    {
        await using SqliteCommand command = f.H.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO long_rest_receipt_inputs
                (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
            SELECT $receipt, $ordinal, $memory, $version, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson
            FROM long_rest_receipt_inputs WHERE ReceiptId <> $receipt LIMIT 1;
            """;

        command.Parameters.AddWithValue("$receipt", receipt);

        command.Parameters.AddWithValue("$ordinal", ordinal);

        command.Parameters.AddWithValue("$memory", memory);

        command.Parameters.AddWithValue("$version", version ?? Guid.NewGuid().ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        if (protectedSource)
        {
            command.Parameters["$memory"].Value = protectedMemory ?? memory;

            command.CommandText = """
                INSERT INTO long_rest_protected_receipt_inputs
                    (ReceiptId, Ordinal, MemoryId, ClaimId, VersionId, LabelId, LabelDigest, SagaRevision,
                     SagaContentDigest, SensitivityDigest, LabelJson)
                SELECT $receipt, $ordinal, $memory, $claim, $version, $label, LabelDigest, SagaRevision,
                    SagaContentDigest, SensitivityDigest, '{}'
                FROM long_rest_protected_receipt_inputs WHERE ReceiptId <> $receipt LIMIT 1;
                """;

            command.Parameters.AddWithValue("$claim", Guid.NewGuid().ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D").ToUpperInvariant());

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }
}

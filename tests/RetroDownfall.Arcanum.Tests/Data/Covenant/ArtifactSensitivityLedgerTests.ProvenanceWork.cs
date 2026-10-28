using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class ArtifactSensitivityLedgerTests
{
    private const string SessionProvenanceIndex = "idx_artifact_sensitivity_session_provenance";

    private const string SessionProvenanceIndexSql = "CREATE INDEX " + SessionProvenanceIndex
        + " ON artifact_sensitivity(SessionId, ProvenanceModeCode, ExactGenerationIds, GenerationBloom);";

    [Theory]
    [InlineData(1024)]
    [InlineData(8192)]
    public async Task Actual_label_writer_does_not_revisit_repeated_live_provenance_for_its_projection(int count)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await SeedOrderedLabelsAsync(fixture, count);

        using ProvenanceNativeTrace trace = new(fixture.Connection);

        var result = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), CancellationToken.None);

        Assert.True(result.IsSuccess);

        trace.AssertNoFault();

        ProvenanceStatement[] reads = trace.Statements.Where(static item =>
            item.Sql.Contains("FROM artifact_sensitivity", StringComparison.Ordinal)).ToArray();

        int work = reads.Sum(static item => item.VmSteps);

        Assert.Equal(CovenantDigests.Sensitivity(GenerationProvenance.CreateExact([GenerationOne, GenerationTwo])
            .ToDigestInput(ContentSensitivity.CovenantDerived)),
            (await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None)).Value.GenerationProvenanceDigest);

        Console.WriteLine($"Actual production projection work: labels={count + 1}; allArtifactReadVM={work}; allArtifactReadCalls={reads.Length}; includes existing point read; fixedLowDiversityWorkBound=1000.");

        Assert.InRange(reads.Length, 1, 19);

        Assert.True(work < 1000, $"The actual writer revisited repeated Session provenance: {work} VM at {count + 1} labels.");
    }

    [Theory]
    [InlineData(32)]
    [InlineData(1024)]
    [InlineData(8192)]
    public async Task Initial_provenance_window_uses_covering_order_without_table_fetch_or_temp_distinct(int count)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await SeedOrderedLabelsAsync(fixture, count);

        using ProvenanceNativeTrace trace = new(fixture.Connection);

        var result = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        trace.AssertNoFault();

        ProvenanceStatement fold = trace.Statements.First(static item => item.Sql.Contains("AS ModeStorage", StringComparison.Ordinal));

        string[] plan = await ProvenancePlanAsync(fixture.Connection, fold.Sql, explain: false);

        string[] opcodes = await ProvenancePlanAsync(fixture.Connection, fold.Sql, explain: true);

        var projection = await fixture.Ledger.ReadSessionProjectionAsync(Session, CancellationToken.None);

        Assert.Equal(CovenantDigests.Sensitivity(GenerationProvenance.CreateExact([GenerationOne, GenerationTwo])
            .ToDigestInput(ContentSensitivity.CovenantDerived)), projection.Value.GenerationProvenanceDigest);

        Console.WriteLine($"Declared Core{GrimoireSchemaVersionChains.CoreSchemaVersion} source fingerprint: {GrimoireSchemaCatalog.CoreSchemaFingerprint}");

        Console.WriteLine($"Actual provenance fold: labels={count + 1}; VM={fold.VmSteps}; plan={string.Join("; ", plan)}; ephemeral={opcodes.Count(static opcode => opcode == "OpenEphemeral")}; openRead={opcodes.Count(static opcode => opcode == "OpenRead")}");

        Assert.Contains(plan, static row => row.Contains("USING COVERING INDEX", StringComparison.Ordinal));

        Assert.DoesNotContain(plan, static row => row.Contains("TEMP B-TREE", StringComparison.Ordinal));

        Assert.DoesNotContain("OpenEphemeral", opcodes);

        Assert.Equal(1, opcodes.Count(static opcode => opcode == "OpenRead"));
    }

    private static async Task<string[]> ProvenancePlanAsync(SqliteConnection connection, string sql, bool explain)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = (explain ? "EXPLAIN " : "EXPLAIN QUERY PLAN ") + sql;

        command.Parameters.AddWithValue("$session", Session.ToString().ToUpperInvariant());

        List<string> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(explain ? 1 : 3));
        }

        return [.. rows];
    }

    private sealed class ProvenanceStatement(string sql)
    {
        internal string Sql { get; } = sql;

        internal bool IsFold => Sql.Contains("SELECT DISTINCT ProvenanceModeCode", StringComparison.Ordinal);

        internal int VmSteps { get; set; }
    }

    private sealed class ProvenanceNativeTrace : IDisposable
    {
        private readonly SqliteConnection _connection;

        private readonly ProvenanceTraceCallback _callback;

        private readonly Dictionary<nint, ProvenanceStatement> _active = [];

        private Exception? _fault;

        internal List<ProvenanceStatement> Statements { get; } = [];

        private readonly Action<string>? _completed;

        internal ProvenanceNativeTrace(SqliteConnection connection, Action<string>? completed = null)
        {
            _completed = completed;

            _connection = connection;

            _callback = Observe;

            Assert.Equal(0, ProvenanceTrace(connection.Handle!.DangerousGetHandle(), 3, _callback, 0));
        }

        private int Observe(uint kind, nint context, nint statement, nint data)
        {
            try
            {
                if (kind == 1)
                {
                    ProvenanceStatement item = new(Marshal.PtrToStringUTF8(data) ?? string.Empty);

                    _active[statement] = item;

                    Statements.Add(item);
                }
                else if (kind == 2 && _active.Remove(statement, out ProvenanceStatement? item))
                {
                    item.VmSteps = ProvenanceStatementStatus(statement, raw.SQLITE_STMTSTATUS_VM_STEP, 1);

                    _completed?.Invoke(item.Sql);
                }
            }
            catch (Exception exception)
            {
                _fault ??= exception;
            }

            return 0;
        }

        internal void AssertNoFault() => Assert.Null(_fault);

        public void Dispose() => Assert.Equal(0, ProvenanceTrace(_connection.Handle!.DangerousGetHandle(), 0, null, 0));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProvenanceTraceCallback(uint kind, nint context, nint statement, nint data);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ProvenanceTrace(nint connection, uint mask, ProvenanceTraceCallback? callback, nint context);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_stmt_status", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ProvenanceStatementStatus(nint statement, int operation, int reset);
    }
}

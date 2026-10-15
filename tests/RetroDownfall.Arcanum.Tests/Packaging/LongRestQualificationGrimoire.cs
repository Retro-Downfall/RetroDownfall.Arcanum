using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>One provider-effect row: the durable record of one paid unit's attempts.</summary>
internal sealed record LongRestEffectRow(
    string WorkItemId,
    string GenerationId,
    string OperationId,
    int WorkKindCode,
    int PayloadKindCode,
    long PhysicalOrdinal,
    int StateCode,
    long SentCount,
    long PaidCount,
    long InvalidCount,
    long FailureCount,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    string? FailureCode);

/// <summary>One sweep row.</summary>
internal sealed record LongRestSweepRow(
    string OperationId,
    string? GenerationId,
    int PhaseCode,
    long ReadmitCount,
    string? ParkedAt);

/// <summary>One node of a completed generation's hierarchy, with its vector for the summaries.</summary>
internal sealed record LongRestNodeRow(
    string NodeId,
    int Layer,
    string? MemoryId,
    string? Summary,
    float[]? Vector);

/// <summary>One memory row as the Saga store holds it.</summary>
internal sealed record LongRestMemoryRow(
    string Id,
    string Content,
    bool Retired,
    bool Pinned);

/// <summary>
/// Read-only access to the isolated installation's Grimoire, opened the way <c>arcanum doctor</c> opens it: SQLCipher with
/// the passphrase derived from the installation's own Data Protection key ring, KDF sidecar and sealed secret. It reads
/// only files under the isolated home it is given, never the operator's installation, and never an operating-system
/// credential store, so it cannot prompt for the keychain. It exists because Long Rest's durable evidence (the provider
/// effects journal, the sweep rows, the generation's hierarchy and its search index) is not on any API surface.
/// </summary>
internal sealed class LongRestQualificationGrimoire
{
    private const string GrimoireProtectorPurpose = "Arcanum.Core.GrimoireEncryption";

    private const string DataProtectionApplicationName = "ArcanumCore";

    private readonly string _databasePath;

    private readonly string _passphrase;

    private LongRestQualificationGrimoire(string databasePath, string passphrase)
    {
        _databasePath = databasePath;

        _passphrase = passphrase;
    }

    /// <summary>Opens the Grimoire under <paramref name="configurationDirectory"/> (the isolated home's <c>.config/arcanum</c>).</summary>
    public static async Task<LongRestQualificationGrimoire> OpenAsync(string configurationDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);

        SqliteNativeRuntime.Instance.Initialize();

        string databasePath = Path.Combine(configurationDirectory, "arcanum.db");

        IDataProtectionProvider provider = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(configurationDirectory, "keys")),
            builder => builder.SetApplicationName(DataProtectionApplicationName));

        SecretStoreReadResult secret = await ProtectedCredentialFile.ReadAsync(
            Path.Combine(configurationDirectory, "grimoire-key.dat"),
            provider.CreateProtector(GrimoireProtectorPurpose),
            "grimoire-key.dat could not be decrypted with the isolated installation's key ring.",
            cancellationToken);

        if (secret.Status != SecretStoreReadStatus.Ok || string.IsNullOrEmpty(secret.Value))
        {
            throw new InvalidOperationException(
                $"The isolated Grimoire encryption secret was not readable ({secret.Status}).");
        }

        byte[] salt = GrimoireKdfSidecarFile.Read(databasePath).GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret.Value, salt);

        LongRestQualificationGrimoire grimoire = new(databasePath, passphrase);

        _ = await grimoire.ScalarAsync("SELECT count(*) FROM sqlite_master;", cancellationToken);

        return grimoire;
    }

    /// <summary>
    /// Runs one read-only <c>SELECT</c> and returns every cell as text, for failure diagnostics. The connection is opened
    /// read-only, so nothing here can change the installation; the prefix check only keeps the helper honest about its purpose.
    /// </summary>
    public async Task<IReadOnlyList<string[]>> DumpAsync(string select, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(select);

        if (!select.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only a SELECT statement may be dumped.", nameof(select));
        }

        return await QueryAsync(
            select,
            static reader =>
            {
                string[] cells = new string[reader.FieldCount];

                for (int index = 0; index < cells.Length; index++)
                {
                    cells[index] = reader.IsDBNull(index)
                        ? "NULL"
                        : reader[index] is byte[] bytes
                            ? $"blob[{bytes.Length}]"
                            : Convert.ToString(reader[index], CultureInfo.InvariantCulture) ?? string.Empty;
                }

                return cells;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<LongRestEffectRow>> ReadEffectsAsync(CancellationToken cancellationToken) =>
        await QueryAsync(
            """
            SELECT effect.WorkItemId, item.GenerationId, item.OperationId, item.KindCode, effect.PayloadKindCode,
                   effect.PhysicalOrdinal, effect.StateCode, effect.SentCount, effect.PaidCount, effect.InvalidCount,
                   effect.FailureCount, effect.InputTokens, effect.OutputTokens, effect.CachedInputTokens, effect.FailureCode
            FROM long_rest_provider_effects effect
            JOIN long_rest_work_items item ON item.WorkItemId = effect.WorkItemId
            ORDER BY effect.UpdatedAtUtc, effect.WorkItemId;
            """,
            static reader => new LongRestEffectRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                reader.GetInt32(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)),
            cancellationToken);

    public async Task<IReadOnlyList<LongRestSweepRow>> ReadSweepsAsync(CancellationToken cancellationToken) =>
        await QueryAsync(
            "SELECT OperationId, GenerationId, PhaseCode, ReadmitCount, ParkedAt FROM long_rest_sweeps ORDER BY UpdatedAtUtc;",
            static reader => new LongRestSweepRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)),
            cancellationToken);

    /// <summary>Search-index rows still held for a generation: zero once the generation completes (its index rows end with it).</summary>
    public async Task<long> CountCleanIndexRowsAsync(string generationId, CancellationToken cancellationToken) =>
        Convert.ToInt64(
            await ScalarAsync(
                "SELECT count(*) FROM long_rest_clean_fts WHERE lower(GenerationId) = lower($generation);",
                cancellationToken,
                ("$generation", generationId)),
            CultureInfo.InvariantCulture);

    public async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {
        if (table is not ("long_rest_receipts" or "long_rest_accepted_summaries" or "long_rest_adjudications"
            or "long_rest_discovery_nodes" or "long_rest_discovery_generations" or "long_rest_work_items"
            or "long_rest_suppressions"))
        {
            throw new ArgumentException("Only the Long Rest evidence tables may be counted.", nameof(table));
        }

        return Convert.ToInt64(
            await ScalarAsync($"SELECT count(*) FROM {table};", cancellationToken),
            CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How many times the named memories appear in the disclosure journal: as an input of any paid unit (<c>long_rest_work_inputs</c>)
    /// or as a captured leaf of any generation. A memory the sweep left out of its corpus appears in neither.
    /// </summary>
    public async Task<long> CountJournaledMemoriesAsync(IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);

        if (memoryIds.Count == 0)
        {
            return 0;
        }

        (string Name, object? Value)[] parameters =
        [
            .. memoryIds.Select((id, index) => ($"$id{index.ToString(CultureInfo.InvariantCulture)}", (object?)id.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant())),
        ];

        string list = string.Join(", ", parameters.Select(static parameter => parameter.Name));

        return Convert.ToInt64(
            await ScalarAsync(
                $"""
                SELECT (SELECT count(*) FROM long_rest_work_inputs WHERE lower(replace(MemoryId, '-', '')) IN ({list}))
                     + (SELECT count(*) FROM long_rest_discovery_leaves WHERE lower(replace(MemoryId, '-', '')) IN ({list}));
                """,
                cancellationToken,
                parameters),
            CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<(int Code, long Count)>> CountAdjudicationDecisionsAsync(CancellationToken cancellationToken) =>
        await QueryAsync(
            "SELECT DecisionKindCode, count(*) FROM long_rest_adjudications GROUP BY DecisionKindCode ORDER BY DecisionKindCode;",
            static reader => (reader.GetInt32(0), reader.GetInt64(1)),
            cancellationToken);

    public async Task<IReadOnlyList<string>> ReadGenerationIdsAsync(CancellationToken cancellationToken) =>
        await QueryAsync(
            "SELECT GenerationId FROM long_rest_discovery_generations ORDER BY CreatedAtUtc;",
            static reader => reader.GetString(0),
            cancellationToken);

    public async Task<IReadOnlyList<LongRestMemoryRow>> ReadMemoriesAsync(CancellationToken cancellationToken) =>
        await QueryAsync(
            "SELECT Id, Content, RetiredAtUtc IS NOT NULL, PinnedAtUtc IS NOT NULL FROM saga_memories ORDER BY Id;",
            static reader => new LongRestMemoryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)),
            cancellationToken);

    /// <summary>Every stored memory vector, keyed by memory id (the Saga store's own embedding rows).</summary>
    public async Task<IReadOnlyDictionary<string, float[]>> ReadMemoryVectorsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Id, float[] Vector)> rows = await QueryAsync(
            "SELECT MemoryId, Embedding FROM saga_memory_embeddings;",
            static reader => (reader.GetString(0), EmbeddingBlobCodec.Decode((byte[])reader[1])),
            cancellationToken);

        return rows.ToDictionary(static row => row.Id, static row => row.Vector, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The hierarchy of one generation: its nodes (summaries with their vectors) and the parent-to-child edges.</summary>
    public async Task<(IReadOnlyList<LongRestNodeRow> Nodes, IReadOnlyList<(string Parent, string Child)> Edges)> ReadHierarchyAsync(
        string generationId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LongRestNodeRow> nodes = await QueryAsync(
            """
            SELECT node.NodeId, node.Layer, node.MemoryId, node.Summary, embedding.Vector
            FROM long_rest_discovery_nodes node
            LEFT JOIN long_rest_discovery_embeddings embedding ON embedding.NodeId = node.NodeId
            WHERE node.GenerationId = $generation
            ORDER BY node.Layer, node.GroupOrdinal;
            """,
            static reader => new LongRestNodeRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : EmbeddingBlobCodec.Decode((byte[])reader[4])),
            cancellationToken,
            ("$generation", generationId));

        IReadOnlyList<(string Parent, string Child)> edges = await QueryAsync(
            "SELECT ParentNodeId, ChildNodeId FROM long_rest_discovery_edges WHERE GenerationId = $generation ORDER BY ParentNodeId, Ordinal;",
            static reader => (reader.GetString(0), reader.GetString(1)),
            cancellationToken,
            ("$generation", generationId));

        return (nodes, edges);
    }

    private async Task<object?> ScalarAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);

        await using SqliteCommand command = CreateCommand(connection, sql, parameters);

        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<SqliteDataReader, T> read,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken);

        await using SqliteCommand command = CreateCommand(connection, sql, parameters);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        List<T> rows = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            Password = _passphrase,
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken);

            // A live writer holds the database briefly; a read waits for it rather than failing the qualification.
            await using SqliteCommand busy = connection.CreateCommand();

            busy.CommandText = "PRAGMA busy_timeout = 15000;";

            _ = await busy.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string sql,
        (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}

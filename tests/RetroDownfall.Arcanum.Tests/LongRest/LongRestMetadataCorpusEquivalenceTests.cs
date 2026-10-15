using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using Mechanism = RetroDownfall.Arcanum.Tests.LongRest.LongRestMutationMatrixTests.Mechanism;
using Row = RetroDownfall.Arcanum.Tests.LongRest.LongRestMutationMatrixTests.Row;
using Scenario = RetroDownfall.Arcanum.Tests.LongRest.LongRestMutationMatrixTests.Scenario;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The set read of a generation's leaves (<see cref="LongRestDiscoveryStore.VerifyFrozenMetadataCorpusAsync"/>, what a seam runs) against the
/// per-leaf read it replaced (<see cref="LongRestDiscoveryStore.ReadFrozenMetadataLeafAsync"/> for every leaf, which a step still runs for its
/// window and a unit's inputs). The two read the same rows by different statements, so the contract is a differential: on every corpus, the
/// set read refuses exactly when the per-leaf read refuses, and refuses with the same kind of exception.
/// </summary>
/// <remarks>
/// <para>The corpora are the ones the mutation matrix builds, mutated by every one of its rows and by the stored-side mutations the matrix leaves
/// to the full recompute: a sweep over forty leaves, clean and protected, with one change made to a leaf the next unit uses and once to one it does
/// not. The matrix mutates through the triggers that watch the corpus, and a trigger that fires purges the generation, which leaves nothing for
/// either read to compare; so each case first removes those watchers, as a restore or a table rebuild does, and the mutation reaches the rows
/// the leaves froze. That is the corpus a seam exists for.</para>
/// <para>The second half mutates a plain corpus one column at a time, on either side of the comparison: the live row a snapshot is made of,
/// and the manifest the leaf stored. Each is refused by both reads or admitted by both, and a stored manifest that is merely reformatted is
/// refused, because the comparison is of the bytes.</para>
/// </remarks>
[Collection("Grimoire")]
public sealed class LongRestMetadataCorpusEquivalenceTests
{
    /// <summary>The triggers that watch the corpus and purge the generation when a source changes; a restore or a rebuild runs without them.</summary>
    private static readonly string[] Watchers =
    [
        "long_rest_attachment_delete",
        "long_rest_attachment_insert",
        "long_rest_attachment_update",
        "long_rest_dependency_delete",
        "long_rest_dependency_insert",
        "long_rest_embedding_delete",
        "long_rest_embedding_insert",
        "long_rest_embedding_update",
        "long_rest_generation_invalidate",
        "long_rest_head_delete",
        "long_rest_head_insert",
        "long_rest_head_update",
        "long_rest_label_delete",
        "long_rest_label_insert",
        "long_rest_saga_delete",
        "long_rest_saga_insert",
        "long_rest_saga_update",
        "long_rest_sources_deleted",
        "long_rest_sources_mutated",
    ];

    /// <summary>A change to a stored manifest, which the matrix leaves to the full recompute and which only a read of the leaf row sees.</summary>
    private static readonly Row[] StoredSide =
    [
        new("leaf-digest", Mechanism.Seam, static (s, m) => LeafAsync(s, "SourceDigest = zeroblob(32)", m)),
        new("leaf-embedding-digest", Mechanism.Seam, static (s, m) => LeafAsync(s, "EmbeddingDigest = zeroblob(32)", m)),
        new("leaf-snapshot-whitespace", Mechanism.Seam, static (s, m) => LeafAsync(s, "SnapshotJson = replace(SnapshotJson, '{', '{ ')", m)),
        new("leaf-snapshot-extra-key", Mechanism.Seam, static (s, m) => LeafAsync(s, "SnapshotJson = json_set(SnapshotJson, '$.unknown', 1)", m)),
        new("leaf-snapshot-flag-text", Mechanism.Seam, static (s, m) => LeafAsync(s, "SnapshotJson = replace(SnapshotJson, '\"isCurrent\":true', '\"isCurrent\": true')", m)),
        new("leaf-label-json-present", Mechanism.Seam, static (s, m) => LeafAsync(s, "LabelJson = '{}'", m), CleanOnly: true),

        // The storage class of a stored manifest. SQLite compares a BLOB with a TEXT value as unequal whatever the bytes, so the per-leaf read,
        // which compares inside SQLite, refuses a manifest stored as a BLOB; the set read decodes both sides to strings and must refuse it too.
        new("leaf-snapshot-blob", Mechanism.Seam, static (s, m) => LeafAsync(s, "SnapshotJson = CAST(SnapshotJson AS BLOB)", m)),
        new("leaf-snapshot-trailing-nul", Mechanism.Seam, static (s, m) => LeafAsync(s, "SnapshotJson = SnapshotJson || char(0)", m)),
        new("leaf-label-json-blob", Mechanism.Seam, static (s, m) => LeafAsync(s, "LabelJson = CAST(LabelJson AS BLOB)", m), ProtectedOnly: true),
        new("leaf-label-json-trailing-nul", Mechanism.Seam, static (s, m) => LeafAsync(s, "LabelJson = LabelJson || char(0)", m), ProtectedOnly: true),
        new("leaf-label-json-present-blob", Mechanism.Seam, static (s, m) => LeafAsync(s, "LabelJson = CAST('{}' AS BLOB)", m), CleanOnly: true),

        // The same for the other columns a leaf stores that the set read joins on or decodes and that a BLOB can reach. A leaf's MemoryId and
        // SourceDigest cannot be made to differ in storage class: its foreign keys and its length check refuse the change.
        new("leaf-version-blob", Mechanism.Seam, static (s, m) => LeafAsync(s, "VersionId = CAST(VersionId AS BLOB)", m)),
        new("leaf-sequence-blob", Mechanism.Seam, static (s, m) => LeafAsync(s, "InsertionSequence = CAST(InsertionSequence AS BLOB)", m)),
        new("leaf-epoch-blob", Mechanism.Seam, static (s, m) => LeafAsync(s, "MutationEpoch = CAST(MutationEpoch AS BLOB)", m)),
        new("leaf-embedding-dimensions-blob", Mechanism.Seam, static (s, m) => LeafAsync(s, "EmbeddingDimensions = CAST(EmbeddingDimensions AS BLOB)", m)),
        new("leaf-label-json-whitespace", Mechanism.Seam, static (s, m) => LeafAsync(s, "LabelJson = replace(LabelJson, '{', '{ ')", m), ProtectedOnly: true),
        new("leaf-label-json-absent", Mechanism.Seam, static (s, m) => LeafAsync(s, "LabelJson = NULL", m), ProtectedOnly: true),
        new("label-session", Mechanism.Seam, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

            await s.SqlAsync("UPDATE artifact_sensitivity SET SessionId = NULL WHERE ArtifactKindCode = 6 AND lower(replace(ArtifactId, '-', '')) = lower(replace($m, '-', ''));", ("$m", m));
        }, ProtectedOnly: true),

        // A label stored in another spelling is found by the loose comparison and not by the exact point read the leaf read makes; the set read
        // hands such a leaf to the point read, and the two must still agree.
        new("label-spelling", Mechanism.Seam, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

            await s.SqlAsync("UPDATE artifact_sensitivity SET ArtifactId = lower(ArtifactId) WHERE ArtifactKindCode = 6 AND lower(replace(ArtifactId, '-', '')) = lower(replace($m, '-', ''));", ("$m", m));
        }, ProtectedOnly: true),
    ];

    private static async Task LeafAsync(Scenario s, string assignment, string memory)
    {
        await s.SqlAsync("DROP TRIGGER long_rest_discovery_leaves_guard_update;");

        await s.SqlAsync($"UPDATE long_rest_discovery_leaves SET {assignment} WHERE GenerationId = $g AND MemoryId = $m;", ("$g", s.GenerationText), ("$m", memory));
    }

    public static IEnumerable<object[]> Cases() => LongRestMutationMatrixTests.RowCases().Concat(CasesOf(StoredSide));

    private static IEnumerable<object[]> CasesOf(Row[] rows)
    {
        foreach (Row row in rows)
        {
            foreach (bool isProtected in new[] { false, true })
            {
                if ((isProtected && row.CleanOnly) || (!isProtected && row.ProtectedOnly))
                {
                    continue;
                }

                foreach (bool input in row.EveryLeaf ? new[] { true } : new[] { true, false })
                {
                    yield return [row.Name, isProtected, input];
                }
            }
        }
    }

    private static Row Find(string name) => LongRestMutationMatrixTests.Rows.Concat(StoredSide).Single(candidate => candidate.Name == name);

    /// <summary>Every row of the matrix, on a corpus whose watchers are gone, and every stored-side change: the two reads give one verdict.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_set_read_gives_the_verdict_of_the_per_leaf_read(string name, bool isProtected, bool input)
    {
        Row row = Find(name);

        await using Scenario s = await Scenario.CreateAsync(isProtected, LongRestMutationMatrixTests.Leaves, queueUnit: true, prepare: row.Prepare);

        await BlindAsync(s);

        await row.Apply(s, input ? s.Inputs[0] : s.Bystander);

        Verdicts v = await VerdictsAsync(s);

        Assert.Equal(v.PerLeaf, v.Set);
    }

    /// <summary>
    /// The differential is not satisfied by two reads that admit everything. The classes only a read of every leaf can see, and the ones the
    /// set-based question sees, are refused by the per-leaf read and by the set read alike, on a leaf the next unit does not use.
    /// </summary>
    [Theory]
    [InlineData("hash-bypass", false)]
    [InlineData("hash-bypass", true)]
    [InlineData("leaf-snapshot", false)]
    [InlineData("leaf-snapshot", true)]
    [InlineData("leaf-digest", false)]
    [InlineData("leaf-digest", true)]
    [InlineData("leaf-snapshot-whitespace", false)]
    [InlineData("leaf-snapshot-extra-key", true)]
    [InlineData("label-content-digest", true)]
    [InlineData("leaf-label-json-whitespace", true)]
    [InlineData("leaf-label-json-absent", true)]
    [InlineData("leaf-snapshot-blob", false)]
    [InlineData("leaf-snapshot-blob", true)]
    [InlineData("leaf-snapshot-trailing-nul", false)]
    [InlineData("leaf-label-json-blob", true)]
    [InlineData("leaf-label-json-trailing-nul", true)]
    [InlineData("leaf-label-json-present-blob", false)]
    [InlineData("label-spelling", true)]
    [InlineData("label-session", true)]
    [InlineData("frozen-flag-consolidated", false)]
    [InlineData("frozen-flag-output", true)]
    [InlineData("foreign-suppression", false)]
    [InlineData("consolidated-source-delete", false)]
    [InlineData("retire", false)]
    [InlineData("pin", true)]
    [InlineData("correction", false)]
    [InlineData("embedding-delete", true)]
    [InlineData("owner-binding", false)]
    public async Task Both_reads_refuse_what_only_a_read_of_every_leaf_can_see(string name, bool isProtected)
    {
        Row mutation = Find(name);

        await using Scenario s = await Scenario.CreateAsync(isProtected, LongRestMutationMatrixTests.Leaves, queueUnit: true, prepare: mutation.Prepare);

        await BlindAsync(s);

        await mutation.Apply(s, s.Bystander);

        Verdicts v = await VerdictsAsync(s);

        Assert.Equal(nameof(InvalidDataException), v.PerLeaf);

        Assert.Equal(nameof(InvalidDataException), v.Set);
    }

    /// <summary>
    /// The qualified label columns are a constant, so that the statement is one and is not built while the process starts; they must stay the
    /// columns the decoder reads, in its order.
    /// </summary>
    [Fact]
    public void The_qualified_label_columns_are_the_decoders_columns_in_its_order()
    {
        Assert.Equal(ArtifactSensitivityLedger.LabelColumnListAsLabel, "label." + ArtifactSensitivityLedger.LabelColumnList.Replace(", ", ", label.", StringComparison.Ordinal));

        Assert.Equal(ArtifactSensitivityLedger.LabelColumnCount, ArtifactSensitivityLedger.LabelColumnList.Split(", ").Length);
    }

    /// <summary>The corpus nothing has touched is admitted by both, so a refusal above is a refusal of the mutation and not of the harness.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_reads_admit_a_corpus_nothing_has_touched(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, LongRestMutationMatrixTests.Leaves, queueUnit: false);

        Verdicts v = await VerdictsAsync(s);

        Assert.Equal("admitted", v.PerLeaf);

        Assert.Equal("admitted", v.Set);
    }

    /// <summary>
    /// A leaf the sweep consolidated, or produced, no longer has the flags it was frozen with, and the receipt that explains the difference is
    /// proved in full. The corpus is consolidated before capture, so its flags are frozen; the live flags then agree with the frozen ones and no
    /// receipt need be proved. This pins that the prepared corpus is admitted and that erasing a consolidated source is refused, by both.
    /// </summary>
    [Theory]
    [InlineData(false, "consolidated-source-delete")]
    [InlineData(true, "consolidated-source-erase")]
    public async Task Both_reads_refuse_the_loss_of_the_row_a_frozen_flag_rests_on(bool isProtected, string name)
    {
        Row mutation = Find(name);

        await using Scenario s = await Scenario.CreateAsync(isProtected, LongRestMutationMatrixTests.Leaves, queueUnit: false, prepare: mutation.Prepare);

        Verdicts before = await VerdictsAsync(s);

        Assert.Equal("admitted", before.PerLeaf);

        Assert.Equal("admitted", before.Set);

        await BlindAsync(s);

        await mutation.Apply(s, s.Bystander);

        Verdicts after = await VerdictsAsync(s);

        Assert.Equal(nameof(InvalidDataException), after.PerLeaf);

        Assert.Equal(nameof(InvalidDataException), after.Set);
    }

    /// <summary>
    /// A protected leaf is joined to its label by <c>upper(MemoryId)</c> and the point read finds the label by the uppercase D form of the Guid it
    /// parses from the memory id, which are one string only for a memory id that is a Guid in D form. Capture refuses any other, and the hash
    /// that binds a leaf refuses it too, so such a leaf is never admitted; but the two reads must agree on how they refuse it, and that must not
    /// rest on how capture behaves. Here a leaf is renamed to the spelling without hyphens everywhere the corpus names it, its label stored under
    /// that spelling and its manifest rewritten to match, so that nothing but the label's key distinguishes the reads: the point read does not find
    /// that label and refuses, and the set read must hand the leaf to it and refuse in the same way.
    /// </summary>
    [Fact]
    public async Task A_protected_leaf_whose_memory_id_is_not_in_D_form_is_refused_by_both_reads()
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected: true, LongRestMutationMatrixTests.Leaves, queueUnit: false);

        await BlindAsync(s);

        Verdicts before = await VerdictsAsync(s);

        Assert.Equal("admitted", before.PerLeaf);

        Assert.Equal("admitted", before.Set);

        string spelled = s.Bystander.Replace("-", "", StringComparison.Ordinal);

        LongRestSourceLeaf leaf;

        await using (SqliteTransaction transaction = s.F.H.Connection.BeginTransaction(deferred: true))
        {
            leaf = await LongRestDiscoveryStore.ReadFrozenMetadataLeafAsync(s.F.H.Connection, transaction, s.Generation, s.Bystander, CancellationToken.None);
        }

        LongRestSourceLeaf renamed = leaf with { Snapshot = leaf.Snapshot with { MemoryId = spelled } };

        await s.SqlAsync("PRAGMA foreign_keys = OFF;");

        foreach (string trigger in await TriggersAsync(s))
        {
            await s.SqlAsync($"DROP TRIGGER {trigger};");
        }

        foreach ((string table, string column) in new[]
        {
            ("saga_memories", "Id"), ("saga_memory_embeddings", "MemoryId"), ("annal_claims", "SubjectId"), ("long_rest_sources", "MemoryId"),
            ("long_rest_discovery_leaves", "MemoryId"),
        })
        {
            await s.SqlAsync($"UPDATE {table} SET {column} = $spelled WHERE {column} = $memory;", ("$spelled", spelled), ("$memory", s.Bystander));
        }

        await s.SqlAsync("UPDATE artifact_sensitivity SET ArtifactId = upper($spelled) WHERE ArtifactKindCode = 6 AND ArtifactId = upper($memory);",
            ("$spelled", spelled), ("$memory", s.Bystander));

        await s.SqlAsync("UPDATE long_rest_discovery_leaves SET SnapshotJson = $snapshot WHERE GenerationId = $generation AND MemoryId = $spelled;",
            ("$snapshot", JsonSerializer.Serialize(renamed.Snapshot, LongRestJsonContext.Default.LongRestSnapshot)), ("$generation", s.GenerationText), ("$spelled", spelled));

        // The set-based question every seam asks first is satisfied by the renamed corpus, so what follows is decided by the two reads of the leaves.
        await using (SqliteTransaction transaction = s.F.H.Connection.BeginTransaction(deferred: true))
        {
            Assert.True(await LongRestDiscoveryStore.FrozenSourceMetadataMatchesAsync(s.F.H.Connection, transaction, s.Generation, CancellationToken.None));
        }

        Verdicts after = await VerdictsAsync(s);

        Assert.Equal(after.PerLeaf, after.Set);

        Assert.Equal(nameof(InvalidDataException), after.Set);
    }

    private static async Task<List<string>> TriggersAsync(Scenario s)
    {
        List<string> names = [];

        await using SqliteCommand command = s.F.H.Connection.CreateCommand();

        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger';";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // The plain corpus, one column at a time.

    private const int Memories = 8;

    private static string MemoryId(int number) => new Guid("a0000000-0000-0000-0000-" + number.ToString("D12", CultureInfo.InvariantCulture)).ToString("D");

    /// <summary>
    /// Eight clean memories with a history: the third depends on the second (an outgoing dependency of one version and an incoming one of the
    /// other), and the fifth has been corrected, so its head is a second revision that names a predecessor.
    /// </summary>
    private static async Task<(LongRestSweepHarness H, LongRestDiscoveryGeneration Generation)> CorpusAsync(bool withHistory)
    {
        LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        for (int number = 1; number <= Memories; number++)
        {
            await h.SeedDistinctAsync(MemoryId(number), number);
        }

        if (withHistory)
        {
            (string dependencyVersion, long dependencySequence) = await VersionOfAsync(h, MemoryId(2));

            (string dependentVersion, long dependentSequence) = await VersionOfAsync(h, MemoryId(3));

            Assert.True(dependentSequence > dependencySequence);

            await h.ExecuteAsync("""
                INSERT INTO annal_dependencies (DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence, RelationCode, Ordinal, CreatedAtUtc)
                VALUES ($dependent, $dependentSequence, $dependency, $dependencySequence, 3, 1, '2026-10-10T00:00:00.0000000Z');
                """, ("$dependent", dependentVersion), ("$dependentSequence", dependentSequence), ("$dependency", dependencyVersion), ("$dependencySequence", dependencySequence));

            string content = (string)(await h.ScalarAsync($"SELECT Content FROM saga_memories WHERE Id = '{MemoryId(5)}';"))!;

            Assert.Equal(SagaCurationOutcomeKind.Applied, (await h.Saga.Store.CorrectAsync(MemoryId(5), AnnalContentDigest.ForSagaMemory(content),
                "a corrected observation", h.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None)).Kind);
        }

        return (h, await h.CaptureAsync());
    }

    private static async Task<(string Version, long Sequence)> VersionOfAsync(LongRestSweepHarness h, string memory)
    {
        await using SqliteCommand command = h.Connection.CreateCommand();

        command.CommandText = """
            SELECT v.VersionId, v.Sequence FROM annal_claims c JOIN annal_heads head ON head.ClaimId = c.ClaimId
            JOIN annal_versions v ON v.VersionId = head.CurrentVersionId WHERE c.SubjectStoreCode = 1 AND c.SubjectId = $memory;
            """;

        _ = command.Parameters.AddWithValue("$memory", memory);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return (reader.GetString(0), reader.GetInt64(1));
    }

    private static string Head(string memory) =>
        $"(SELECT head.CurrentVersionId FROM annal_claims c JOIN annal_heads head ON head.ClaimId = c.ClaimId WHERE c.SubjectStoreCode = 1 AND c.SubjectId = '{memory}')";

    /// <summary>Every column a snapshot is made of, changed on the live side one at a time; and every way a stored manifest can differ from the bytes the rows now describe.</summary>
    public static TheoryData<string, string> PlainMutations => new()
    {
        { "none", "SELECT 1;" },
        { "version-origin", $"UPDATE annal_versions SET OriginCode = CASE OriginCode WHEN 1 THEN 2 ELSE 1 END WHERE VersionId = {Head(MemoryId(4))};" },
        { "version-sensitivity", $"UPDATE annal_versions SET SensitivityCode = 1 WHERE VersionId = {Head(MemoryId(4))};" },
        { "version-valid-to", $"UPDATE annal_versions SET ValidToUtc = '2026-12-31T00:00:00.0000000Z' WHERE VersionId = {Head(MemoryId(4))};" },
        { "version-recorded-at", $"UPDATE annal_versions SET RecordedAtUtc = '2026-10-11T00:00:00.0000000Z' WHERE VersionId = {Head(MemoryId(4))};" },
        { "version-hash-format", $"UPDATE annal_versions SET ContentHashFormatCode = 2 WHERE VersionId = {Head(MemoryId(4))};" },
        { "version-hash", $"UPDATE annal_versions SET ContentHash = zeroblob(32) WHERE VersionId = {Head(MemoryId(4))};" },
        { "version-corrected-hash", $"UPDATE annal_versions SET ContentHash = zeroblob(32) WHERE VersionId = {Head(MemoryId(5))};" },
        { "memory-retired", "UPDATE saga_memories SET RetiredAtUtc = '2026-10-11T00:00:00.0000000Z' WHERE Id = '" + "a0000000-0000-0000-0000-000000000004" + "';" },
        { "memory-pinned", "UPDATE saga_memories SET PinnedAtUtc = '2026-10-11T00:00:00.0000000Z' WHERE Id = '" + "a0000000-0000-0000-0000-000000000004" + "';" },
        { "embedding-removed", "DELETE FROM saga_memory_embeddings WHERE MemoryId = 'a0000000-0000-0000-0000-000000000004';" },
        { "dependency-removed", "DELETE FROM annal_dependencies;" },
        { "dependency-ordinal", "UPDATE annal_dependencies SET Ordinal = 2;" },
        { "leaf-digest", "UPDATE long_rest_discovery_leaves SET SourceDigest = zeroblob(32) WHERE MemoryId = 'a0000000-0000-0000-0000-000000000004';" },
        { "leaf-snapshot-revision", "UPDATE long_rest_discovery_leaves SET SnapshotJson = json_set(SnapshotJson, '$.revision', 99) WHERE MemoryId = 'a0000000-0000-0000-0000-000000000004';" },
        { "leaf-snapshot-dependencies", "UPDATE long_rest_discovery_leaves SET SnapshotJson = json_set(SnapshotJson, '$.dependencies', json('[]')) WHERE MemoryId = 'a0000000-0000-0000-0000-000000000003';" },
        { "leaf-snapshot-incoming", "UPDATE long_rest_discovery_leaves SET SnapshotJson = json_set(SnapshotJson, '$.incomingDependencies', json('[]')) WHERE MemoryId = 'a0000000-0000-0000-0000-000000000002';" },
        { "leaf-label-json", "UPDATE long_rest_discovery_leaves SET LabelJson = '{}' WHERE MemoryId = 'a0000000-0000-0000-0000-000000000004';" },
        { "leaf-snapshot-blob", "UPDATE long_rest_discovery_leaves SET SnapshotJson = CAST(SnapshotJson AS BLOB) WHERE MemoryId = 'a0000000-0000-0000-0000-000000000004';" },
        { "leaf-label-json-blob", "UPDATE long_rest_discovery_leaves SET LabelJson = CAST('{}' AS BLOB) WHERE MemoryId = 'a0000000-0000-0000-0000-000000000004';" },
        { "leaf-embedding-digest", "UPDATE long_rest_discovery_leaves SET EmbeddingDigest = zeroblob(32) WHERE MemoryId = 'a0000000-0000-0000-0000-000000000004';" },
    };

    [Theory]
    [MemberData(nameof(PlainMutations))]
    public async Task On_a_plain_corpus_with_a_history_the_two_reads_agree_after_each_change(string name, string sql)
    {
        (LongRestSweepHarness h, LongRestDiscoveryGeneration generation) = await CorpusAsync(withHistory: true);

        await using LongRestSweepHarness harness = h;

        foreach (string trigger in Watchers.Concat(["annal_versions_guard_update", "annal_dependencies_guard_update", "long_rest_discovery_leaves_guard_update"]))
        {
            await h.ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger};");
        }

        Verdicts untouched = await VerdictsAsync(h, generation);

        Assert.Equal("admitted", untouched.PerLeaf);

        Assert.Equal("admitted", untouched.Set);

        await h.ExecuteAsync(sql);

        Verdicts changed = await VerdictsAsync(h, generation);

        Assert.Equal(changed.PerLeaf, changed.Set);

        // Every change but the untouched one makes the leaf differ from what it froze, so this is a differential over refusals and not over silence.
        Assert.Equal(name == "none" ? "admitted" : nameof(InvalidDataException), changed.Set);
    }

    /// <summary>The set read of a corpus with a history is one statement however long the history is, and a clean corpus without one is the same.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_set_read_is_one_statement_over_the_corpus_with_or_without_a_history(bool withHistory)
    {
        (LongRestSweepHarness h, LongRestDiscoveryGeneration generation) = await CorpusAsync(withHistory);

        await using LongRestSweepHarness harness = h;

        using SqliteStatementTrace trace = new(h.Connection);

        await using SqliteTransaction transaction = h.Connection.BeginTransaction(deferred: true);

        await LongRestDiscoveryStore.VerifyFrozenMetadataCorpusAsync(h.Connection, transaction, generation, CancellationToken.None);

        Assert.Single(trace.Statements, static statement => statement.Contains("json_group_array", StringComparison.Ordinal));

        Assert.Equal(1, trace.Statements.Count(static statement => statement.Contains("long_rest_discovery_leaves", StringComparison.Ordinal)));
    }

    internal sealed record Verdicts(string PerLeaf, string Set);

    private static async Task BlindAsync(Scenario s)
    {
        foreach (string trigger in Watchers)
        {
            await s.SqlAsync($"DROP TRIGGER IF EXISTS {trigger};");
        }
    }

    private static Task<Verdicts> VerdictsAsync(Scenario s) => VerdictsAsync(s.F.H.Connection, s.Generation);

    private static Task<Verdicts> VerdictsAsync(LongRestSweepHarness h, LongRestDiscoveryGeneration generation) => VerdictsAsync(h.Connection, generation);

    internal static async Task<Verdicts> VerdictsAsync(SqliteConnection connection, LongRestDiscoveryGeneration generation) =>
        new(await RunAsync(connection, generation, perLeaf: true), await RunAsync(connection, generation, perLeaf: false));

    private static async Task<string> RunAsync(SqliteConnection connection, LongRestDiscoveryGeneration generation, bool perLeaf)
    {
        try
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            if (perLeaf)
            {
                await PerLeafSeamAsync(connection, transaction, generation);
            }
            else
            {
                await LongRestDiscoveryStore.VerifyFrozenMetadataAsync(connection, transaction, generation, LongRestVerificationDepth.Seam, CancellationToken.None);
            }

            return "admitted";
        }
        catch (Exception exception)
        {
            return exception.GetType().Name;
        }
    }

    /// <summary>The seam this replaced, word for word: the set-based question, then every leaf by itself in key order.</summary>
    private static async Task PerLeafSeamAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestDiscoveryGeneration generation)
    {
        if (!await LongRestDiscoveryStore.FrozenSourceMetadataMatchesAsync(connection, transaction, generation, CancellationToken.None))
        {
            throw new InvalidDataException();
        }

        List<string> memories = [];

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            command.CommandText = "SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $generation ORDER BY MemoryId;";

            _ = command.Parameters.AddWithValue("$generation", generation.GenerationId.ToString("D").ToUpperInvariant());

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                memories.Add(reader.GetString(0));
            }
        }

        foreach (string memory in memories)
        {
            _ = await LongRestDiscoveryStore.ReadFrozenMetadataLeafAsync(connection, transaction, generation, memory, CancellationToken.None);
        }
    }
}

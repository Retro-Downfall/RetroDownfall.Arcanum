using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using System.Text;
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Closed source inventory for every authored boundary that may render persisted instants.
/// </summary>
public sealed class UtcInstantPersistenceBoundaryTests
{
    [Fact]
    public void Every_direct_SQL_instant_parameter_is_canonical_at_its_binding_site()
    {
        IReadOnlyList<InstantBindingViolation> violations = FindInstantBindingViolations(
            ProductionSourceInventory.Sources());

        if (violations.Count > 0)
        {
            Assert.Fail(string.Join(System.Environment.NewLine, violations));
        }
    }

    [Fact]
    public void Per_binding_guard_rejects_a_raw_neighbor_beside_a_legitimate_codec_call()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(
                    SqliteCommand command,
                    DateTimeOffset now,
                    string imported)
                {
                    _ = command.Parameters.AddWithValue(
                        "$updatedAt",
                        UtcInstantText.Format(now));
                    _ = command.Parameters.AddWithValue("$createdAt", imported);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$createdAt", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_rejects_a_raw_value_despite_an_unrelated_canonical_local()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(
                    SqliteCommand command,
                    DateTimeOffset now,
                    string imported)
                {
                    string canonicalButUnrelated = UtcInstantText.Format(now);
                    string rawImportedValue = imported;
                    _ = command.Parameters.AddWithValue("$createdAt", rawImportedValue);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$createdAt", violation.ParameterName);
        Assert.Equal("rawImportedValue", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_follows_the_SQL_column_when_the_parameter_name_is_generic()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, string imported)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue("$value", imported);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_recognizes_a_colon_prefixed_semantic_instant_parameter()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, string imported)
                {
                    _ = command.Parameters.AddWithValue(":createdAt", imported);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal(":createdAt", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_maps_a_colon_prefixed_generic_parameter_from_its_SQL_column()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, string imported)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES (:value);";
                    _ = command.Parameters.AddWithValue(":value", imported);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal(":value", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_does_not_share_generic_parameter_proof_between_methods()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void WriteCanonical(SqliteCommand command, DateTimeOffset now)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue("$value", UtcInstantText.Format(now));
                }

                internal static void WriteUnrecognized(SqliteCommand command, string imported)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.Add(new SqliteParameter("$value", imported));
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("<unproven binding>", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_discovers_SQL_assembled_from_multiple_string_literals()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, string imported)
                {
                    command.CommandText = "INSERT INTO Entries "
                        + "(CreatedAt) "
                        + "VALUES (:value);";
                    _ = command.Parameters.AddWithValue(":value", imported);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal(":value", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_fails_closed_when_a_generic_bind_has_ambiguous_command_provenance()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(
                    SqliteCommand first,
                    SqliteCommand second,
                    DateTimeOffset now)
                {
                    first.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    second.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    Bind("$value", UtcInstantText.Format(now));
                }

                private static void Bind(string name, object value)
                {
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("<ambiguous command provenance>", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_does_not_ignore_a_non_instant_command_when_provenance_is_ambiguous()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(
                    SqliteCommand first,
                    SqliteCommand second,
                    DateTimeOffset now)
                {
                    first.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    second.CommandText = "INSERT INTO Entries (Content) VALUES ($value);";
                    Bind("$value", UtcInstantText.Format(now));
                }

                private static void Bind(string name, object value)
                {
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("<ambiguous command provenance>", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_follows_an_insert_select_projection_to_a_generic_parameter()
    {
        const string source = """"
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, string imported)
                {
                    command.CommandText = """
                        INSERT INTO BatchLineCheckpoints (LineNumber, DispatchedAt)
                        SELECT $line, $value
                        WHERE $line > 0;
                        """;
                    _ = command.Parameters.AddWithValue("$line", 1);
                    _ = command.Parameters.AddWithValue("$value", imported);
                }
            }
            """";

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_checks_tuple_backed_direct_SQL_parameters()
    {
        const string source = """
            internal static class Fixture
            {
                internal static Task Write(string imported) => ExecuteAsync(
                    "INSERT INTO Entries (CreatedAt) VALUES ($value);",
                    ("$value", imported));
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_fails_closed_on_an_unrecognized_parameter_binding_shape()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, string imported)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    SqliteParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "$value";
                    parameter.Value = imported;
                    _ = command.Parameters.Add(parameter);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("<unproven binding>", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_does_not_hide_an_unrecognized_bind_behind_a_proven_bind()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(
                    SqliteCommand command,
                    DateTimeOffset now,
                    string imported)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue("$value", UtcInstantText.Format(now));

                    SqliteParameter parameter = command.CreateParameter();
                    parameter.ParameterName = "$value";
                    parameter.Value = imported;
                    _ = command.Parameters.Add(parameter);
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("<unproven binding>", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_checks_every_value_assigned_to_a_reused_typed_parameter()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(
                    SqliteCommand command,
                    DateTimeOffset now,
                    string imported)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue("$value", UtcInstantText.Format(now));

                    SqliteParameter reused = command.Parameters.Add("$value", SqliteType.Text);
                    reused.Value = UtcInstantText.Format(now);
                    reused.Value = imported;
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_rejects_a_non_timestamp_overload_of_a_shared_formatter()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, Guid id)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue(
                        "$value",
                        GrimoireEntitySql.Format(id));
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("GrimoireEntitySql.Format(id)", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_rejects_a_timestamp_named_string_member_passed_to_a_shared_formatter()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal sealed class ImportedRow
            {
                internal string CreatedAt { get; init; } = string.Empty;
            }

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, ImportedRow row)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue(
                        "$value",
                        GrimoireEntitySql.Format(row.CreatedAt));
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("GrimoireEntitySql.Format(row.CreatedAt)", violation.Value);
    }

    [Fact]
    public void Per_binding_guard_proves_one_canonical_local_at_each_of_its_bindings()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, DateTimeOffset installedAt)
                {
                    string installedAtUtc = CanonicalTimestamp.Format(installedAt);
                    _ = command.Parameters.AddWithValue("$createdAt", installedAtUtc);
                    _ = command.Parameters.AddWithValue("$updatedAt", installedAtUtc);
                }
            }

            internal static class CanonicalTimestamp
            {
                internal static string Format(DateTimeOffset value) =>
                    UtcInstantText.Format(value);
            }
            """;

        Assert.Empty(FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));
    }

    [Fact]
    public void Persisted_instant_writers_use_the_central_codec_and_new_writers_require_review()
    {
        string[] expectedOwners =
        [
            "src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AExternalSpendLedger.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCovenantRestoreReconciler.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStatePurger.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/CovenantRestoreStateJoiners.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/RestoreStagingManagedAuthoritySanitizationSession.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairJournal.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BatchAccountingRecoveryStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathMarkerIntentStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCurationKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureJournal.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEnvelopeStateStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIndexRebuilder.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantManagedFileErasureKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantOwnerDeletionReader.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantQuotaGuard.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchOutboxWorker.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/LocalErasureWorkItemStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ManagedFileWriteIntentRecoveryService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/SessionDerivedArtifactStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireEntitySql.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.Curation.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SagaSuppressionKeyStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SanctumBreachRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/CovenantCanonicalSchemaDataInitializer.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaTransitionJournal.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/UtcInstantCanonicalizationBackfill.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SessionContextPinStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/UnseenServantWatermarkStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/UtcInstantTypeMappings.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/UtcInstantSql.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Diagnostics/OperationDiagnostics.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.SessionTurnBegin.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedArtifactTransferStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedSessionTransferIntentStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionTurnClaimStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Security/HostProcessToolsAuthorityStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Weave/SessionAttachmentIndexRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Weave/TapestryStore.cs",
        ];

        string[] actualOwners =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.Names("UtcInstantText.Format(")
                    || source.Names("UtcInstantText.Normalize("))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expectedOwners.Order(StringComparer.Ordinal), actualOwners);
    }

    [Fact]
    public void Grimoire_entity_format_callers_are_closed_for_timestamp_overload_review()
    {
        string[] expectedOwners =
        [
            "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ApprenticeRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/CampaignRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/EntryTemporalQueries.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.SessionTurnBegin.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/PromptRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionEntryPersistence.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs",
        ];

        string[] actualOwners =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names("GrimoireEntitySql.Format("))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expectedOwners.Order(StringComparer.Ordinal), actualOwners);
    }

    [Fact]
    public void Round_trip_formatting_is_confined_to_non_database_outputs()
    {
        const string infrastructureRoot = "src/RetroDownfall.Arcanum.Infrastructure/";

        ProductionSource[] infrastructureSources =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => source.RelativePath.StartsWith(
                    infrastructureRoot,
                    StringComparison.Ordinal)),
        ];

        string[] expectedNonDatabaseOwners =
        [
            "src/RetroDownfall.Arcanum.Infrastructure/CommLink/WebhookCommLinkDispatcher.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Security/ListenAnySecurityPolicy.cs",
        ];

        string[] actual =
        [
            .. infrastructureSources.Where(static source =>
                    source.Names(".ToString(\"o\"")
                    || source.Names(".ToString(\"O\""))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expectedNonDatabaseOwners, actual);

        Assert.DoesNotContain(
            infrastructureSources,
            static source => source.Names("yyyy-MM-ddTHH:mm:ss.fffffffZ"));

        Assert.Equal(
            ["src/RetroDownfall.Arcanum.Infrastructure/Data/UtcInstantText.cs"],
            infrastructureSources
                .Where(static source => source.Names("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal)
                .ToArray());

        Assert.DoesNotContain(
            infrastructureSources,
            static source =>
                source.Names("TimestampFormat = \"o\"")
                || source.Names("TimestampFormat = \"O\""));
    }

    [Fact]
    public void Schema_side_clock_writers_use_only_the_fixed_width_UTC_expression()
    {
        const string canonicalClock = "strftime('%Y-%m-%dT%H:%M:%f', 'now') || '0000Z'";

        string[] clockOwners =
        [
            .. GrimoireSchemaCatalog.AllObjects
                .Where(static definition =>
                    definition.Sql.Contains("strftime", StringComparison.OrdinalIgnoreCase)
                    || definition.Sql.Contains("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase)
                    || definition.Sql.Contains("datetime('now'", StringComparison.OrdinalIgnoreCase))
                .Select(static definition => definition.Name)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            ["Campaigns_owner_deletion_event", "Sessions_owner_deletion_event"],
            clockOwners);

        foreach (string owner in clockOwners)
        {
            GrimoireSchemaObject definition = GrimoireSchemaCatalog.AllObjects.Single(
                candidate => candidate.Name == owner);

            Assert.Contains(canonicalClock, definition.Sql, StringComparison.Ordinal);
        }
    }

    private static IReadOnlyList<InstantBindingViolation> FindInstantBindingViolations(
        IReadOnlyList<ProductionSource> sources)
    {
        ParsedSource[] parsed =
        [
            .. sources.Select(static source => new ParsedSource(
                source,
                CSharpSyntaxTree.ParseText(
                    source.Text,
                    new CSharpParseOptions(LanguageVersion.Preview),
                    source.RelativePath))),
        ];

        Dictionary<string, List<MethodDeclarationSyntax>> methods = IndexMethods(parsed);
        Dictionary<string, HashSet<string>> memberTypes = IndexMemberTypes(parsed);
        List<InstantBindingViolation> violations = [];

        foreach (ParsedSource source in parsed)
        {
            SyntaxNode[] scopes =
            [
                .. source.Root.DescendantNodes()
                    .Where(IsExecutableScope)
                    .Where(scope => ContainsPotentialInstantBoundary(scope, source.Root)),
                .. ContainsPotentialInstantBoundary(source.Root, source.Root)
                    ? [source.Root]
                    : Array.Empty<SyntaxNode>(),
            ];

            foreach (SyntaxNode scope in scopes)
            {
                SqlWriteContract[] contracts = DiscoverSqlWriteContracts(scope, source.Root);
                HashSet<SqlParameterSlot> inspected = [];

                foreach (InvocationExpressionSyntax invocation in OwnedDescendants<InvocationExpressionSyntax>(
                             scope,
                             source.Root))
                {
                    if (!IsParameterBindingInvocation(invocation))
                    {
                        continue;
                    }

                    SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

                    for (int index = 0; index < arguments.Count - 1; index++)
                    {
                        if (!TryReadParameterName(arguments[index].Expression, out string parameterName))
                        {
                            continue;
                        }

                        InspectBinding(
                            source.Source,
                            contracts,
                            inspected,
                            parameterName,
                            arguments[index + 1].Expression,
                            invocation,
                            BindingCommandKey(invocation, index),
                            exactContractOrigin: null,
                            IsUtcInstantSqlBinder(invocation),
                            methods,
                            memberTypes,
                            violations);
                    }
                }

                foreach (TupleExpressionSyntax tuple in OwnedDescendants<TupleExpressionSyntax>(
                             scope,
                             source.Root))
                {
                    SeparatedSyntaxList<ArgumentSyntax> arguments = tuple.Arguments;

                    if (arguments.Count < 2
                        || !TryReadParameterName(arguments[0].Expression, out string parameterName))
                    {
                        continue;
                    }

                    InvocationExpressionSyntax? owner = tuple.Ancestors()
                        .OfType<InvocationExpressionSyntax>()
                        .FirstOrDefault();

                    InspectBinding(
                        source.Source,
                        contracts,
                        inspected,
                        parameterName,
                        arguments[1].Expression,
                        tuple,
                        commandKey: null,
                        owner,
                        canonicalByContract: false,
                        methods,
                        memberTypes,
                        violations);
                }

                foreach (VariableDeclaratorSyntax declaration in OwnedDescendants<VariableDeclaratorSyntax>(
                             scope,
                             source.Root))
                {
                    if (declaration.Initializer?.Value is not InvocationExpressionSyntax initializer
                        || !IsTypedParameterDeclaration(initializer)
                        || !TryReadParameterName(
                            initializer.ArgumentList.Arguments[0].Expression,
                            out string declaredParameterName))
                    {
                        continue;
                    }

                    string? commandKey = ParameterCollectionCommandKey(initializer.Expression);
                    SqlWriteContract[] commandContracts = ResolveCommandContracts(
                        contracts,
                        commandKey,
                        declaration,
                        exactContractOrigin: null);
                    SqlWriteContract[] mappedContracts =
                    [
                        .. commandContracts.Where(contract =>
                            contract.InstantParameters.Contains(declaredParameterName)),
                    ];

                    if (!IsSemanticInstantParameter(declaredParameterName)
                        && mappedContracts.Length == 0)
                    {
                        continue;
                    }

                    MarkInspected(mappedContracts, declaredParameterName, inspected);

                    AssignmentExpressionSyntax[] assignedValues =
                    [
                        .. OwnedDescendants<AssignmentExpressionSyntax>(scope, source.Root)
                        .OfType<AssignmentExpressionSyntax>()
                        .Where(assignment =>
                            assignment.SpanStart > declaration.SpanStart
                            && assignment.Left is MemberAccessExpressionSyntax
                            {
                                Expression: IdentifierNameSyntax owner,
                                Name.Identifier.ValueText: "Value",
                            }
                            && owner.Identifier.ValueText == declaration.Identifier.ValueText)
                    ];

                    if (assignedValues.Length == 0)
                    {
                        AddViolation(
                            source.Source,
                            initializer,
                            declaredParameterName,
                            "<unproven binding>",
                            violations);

                        continue;
                    }

                    foreach (AssignmentExpressionSyntax assignment in assignedValues)
                    {
                        if (IsCanonicalExpression(
                                assignment.Right,
                                assignment,
                                methods,
                                memberTypes,
                                []))
                        {
                            continue;
                        }

                        AddViolation(
                            source.Source,
                            assignment.Right,
                            declaredParameterName,
                            assignment.Right.ToString(),
                            violations);
                    }
                }

                foreach (AssignmentExpressionSyntax assignment in OwnedDescendants<AssignmentExpressionSyntax>(
                             scope,
                             source.Root))
                {
                    if (assignment.Left is MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Value",
                            Expression: ElementAccessExpressionSyntax parameterAccess,
                        }
                        && parameterAccess.ArgumentList.Arguments.Count == 1
                        && TryReadParameterName(
                            parameterAccess.ArgumentList.Arguments[0].Expression,
                            out string indexedParameterName))
                    {
                        InspectBinding(
                            source.Source,
                            contracts,
                            inspected,
                            indexedParameterName,
                            assignment.Right,
                            assignment,
                            ElementAccessCommandKey(parameterAccess),
                            exactContractOrigin: null,
                            canonicalByContract: false,
                            methods,
                            memberTypes,
                            violations);
                    }

                    if (assignment.Left is MemberAccessExpressionSyntax
                        {
                            Expression: IdentifierNameSyntax parameter,
                            Name.Identifier.ValueText: "ParameterName",
                        }
                        && TryReadParameterName(assignment.Right, out string manuallyNamedParameter))
                    {
                        string? commandKey = CreatedParameterCommandKey(
                            parameter.Identifier.ValueText,
                            assignment,
                            scope,
                            source.Root);
                        SqlWriteContract[] commandContracts = ResolveCommandContracts(
                            contracts,
                            commandKey,
                            assignment,
                            exactContractOrigin: null);
                        SqlWriteContract[] mappedContracts =
                        [
                            .. commandContracts.Where(contract =>
                                contract.InstantParameters.Contains(manuallyNamedParameter)),
                        ];

                        if (!IsSemanticInstantParameter(manuallyNamedParameter)
                            && mappedContracts.Length == 0)
                        {
                            continue;
                        }

                        MarkInspected(mappedContracts, manuallyNamedParameter, inspected);
                        AddViolation(
                            source.Source,
                            assignment.Right,
                            manuallyNamedParameter,
                            "<unproven binding>",
                            violations);
                    }
                }

                foreach (SqlWriteContract contract in contracts)
                {
                    foreach (string parameterName in contract.InstantParameters)
                    {
                        if (inspected.Contains(new SqlParameterSlot(contract.Id, parameterName)))
                        {
                            continue;
                        }

                        if (!InspectTransitiveContractBinding(
                                source.Source,
                                contract,
                                parameterName,
                                scope,
                                source.Root,
                                methods,
                                memberTypes,
                                violations))
                        {
                            AddViolation(
                                source.Source,
                                contract.Origin,
                                parameterName,
                                "<unproven binding>",
                                violations);
                        }
                    }
                }
            }
        }

        return violations;
    }

    private static void InspectBinding(
        ProductionSource source,
        IReadOnlyList<SqlWriteContract> contracts,
        HashSet<SqlParameterSlot> inspected,
        string parameterName,
        ExpressionSyntax value,
        SyntaxNode binding,
        string? commandKey,
        SyntaxNode? exactContractOrigin,
        bool canonicalByContract,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes,
        List<InstantBindingViolation> violations)
    {
        SqlWriteContract[] mapped = contracts
            .Where(contract => contract.InstantParameters.Contains(parameterName))
            .ToArray();

        SqlWriteContract[] provenance = ResolveCommandContracts(
            contracts,
            commandKey,
            binding,
            exactContractOrigin);
        SqlWriteContract[] resolved =
        [
            .. provenance.Where(contract => contract.InstantParameters.Contains(parameterName)),
        ];

        bool semantic = IsSemanticInstantParameter(parameterName);

        if (!semantic && mapped.Length == 0)
        {
            return;
        }

        if (provenance.Length > 1)
        {
            MarkInspected(mapped, parameterName, inspected);
            AddViolation(
                source,
                value,
                parameterName,
                "<ambiguous command provenance>",
                violations);

            return;
        }

        if (resolved.Length == 1)
        {
            MarkInspected(resolved, parameterName, inspected);
        }
        else if (!semantic)
        {
            return;
        }

        if (canonicalByContract
            || IsCanonicalExpression(value, binding, methods, memberTypes, []))
        {
            return;
        }

        AddViolation(source, value, parameterName, value.ToString(), violations);
    }

    private static SqlWriteContract[] ResolveCommandContracts(
        IReadOnlyList<SqlWriteContract> contracts,
        string? commandKey,
        SyntaxNode binding,
        SyntaxNode? exactContractOrigin)
    {
        if (exactContractOrigin is not null)
        {
            return
            [
                .. contracts.Where(contract => ReferenceEquals(contract.Origin, exactContractOrigin)),
            ];
        }

        if (commandKey is null)
        {
            return [.. contracts];
        }

        SqlWriteContract[] preceding =
        [
            .. contracts.Where(contract =>
                string.Equals(contract.CommandKey, commandKey, StringComparison.Ordinal)
                && contract.Origin.SpanStart <= binding.SpanStart),
        ];

        if (preceding.Length == 0)
        {
            return [];
        }

        int nearest = preceding.Max(static contract => contract.Origin.SpanStart);

        return [.. preceding.Where(contract => contract.Origin.SpanStart == nearest)];
    }

    private static void MarkInspected(
        IEnumerable<SqlWriteContract> contracts,
        string parameterName,
        HashSet<SqlParameterSlot> inspected)
    {
        foreach (SqlWriteContract contract in contracts)
        {
            _ = inspected.Add(new SqlParameterSlot(contract.Id, parameterName));
        }
    }

    private static void AddViolation(
        ProductionSource source,
        SyntaxNode locationNode,
        string parameterName,
        string value,
        List<InstantBindingViolation> violations)
    {
        FileLinePositionSpan location = locationNode.GetLocation().GetLineSpan();

        InstantBindingViolation violation = new(
            source.RelativePath,
            location.StartLinePosition.Line + 1,
            parameterName,
            value);

        if (!violations.Contains(violation))
        {
            violations.Add(violation);
        }
    }

    private static bool InspectTransitiveContractBinding(
        ProductionSource source,
        SqlWriteContract contract,
        string parameterName,
        SyntaxNode scope,
        SyntaxNode root,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes,
        List<InstantBindingViolation> violations)
    {
        List<TransitiveBindingSite> sites = [];
        bool ambiguous = false;
        HashSet<MethodTraversalKey> visited = [];

        foreach (InvocationExpressionSyntax invocation in OwnedDescendants<InvocationExpressionSyntax>(
                     scope,
                     root))
        {
            bool isContractOrigin = ReferenceEquals(invocation, contract.Origin);
            int[] commandArguments = contract.CommandKey is null
                ? []
                :
                [
                    .. invocation.ArgumentList.Arguments
                        .Select((argument, index) => new { argument, index })
                        .Where(candidate => string.Equals(
                            CommandIdentity(candidate.argument.Expression),
                            contract.CommandKey,
                            StringComparison.Ordinal))
                        .Select(static candidate => candidate.index),
                ];

            if (!isContractOrigin && commandArguments.Length == 0)
            {
                continue;
            }

            MethodDeclarationSyntax[] candidates = ResolveMethodCandidates(invocation, methods);

            if (candidates.Length == 0)
            {
                continue;
            }

            if (candidates.Length != 1)
            {
                ambiguous = true;
                continue;
            }

            MethodDeclarationSyntax method = candidates[0];
            HashSet<string> commandParameters =
            [
                .. commandArguments.Select(index =>
                    method.ParameterList.Parameters[index].Identifier.ValueText),
            ];
            string? sqlParameter = isContractOrigin
                && contract.SqlArgumentIndex is { } sqlIndex
                && sqlIndex < method.ParameterList.Parameters.Count
                    ? method.ParameterList.Parameters[sqlIndex].Identifier.ValueText
                    : null;

            CollectTransitiveBindingSites(
                method,
                parameterName,
                commandParameters,
                sqlParameter,
                methods,
                memberTypes,
                visited,
                sites,
                ref ambiguous);
        }

        if (ambiguous)
        {
            AddViolation(
                source,
                contract.Origin,
                parameterName,
                "<ambiguous command provenance>",
                violations);

            return true;
        }

        if (sites.Count == 0)
        {
            return false;
        }

        foreach (TransitiveBindingSite site in sites)
        {
            if (site.CanonicalByContract
                || IsCanonicalExpression(site.Value, site.Binding, methods, memberTypes, []))
            {
                continue;
            }

            AddViolation(source, site.Value, parameterName, site.Value.ToString(), violations);
        }

        return true;
    }

    private static void CollectTransitiveBindingSites(
        MethodDeclarationSyntax method,
        string parameterName,
        HashSet<string> commandParameters,
        string? sqlParameter,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes,
        HashSet<MethodTraversalKey> visited,
        List<TransitiveBindingSite> sites,
        ref bool ambiguous)
    {
        if (!visited.Add(new MethodTraversalKey(
                method.SyntaxTree.FilePath,
                method.SpanStart,
                parameterName,
                string.Join("\0", commandParameters.Order(StringComparer.Ordinal)),
                sqlParameter)))
        {
            return;
        }

        SyntaxNode methodRoot = method.SyntaxTree.GetRoot();
        HashSet<string> activeCommands = [.. commandParameters];

        if (sqlParameter is not null)
        {
            foreach (AssignmentExpressionSyntax assignment in OwnedDescendants<AssignmentExpressionSyntax>(
                         method,
                         methodRoot))
            {
                if (assignment.Left is MemberAccessExpressionSyntax
                    {
                        Expression: ExpressionSyntax command,
                        Name.Identifier.ValueText: "CommandText",
                    }
                    && assignment.Right is IdentifierNameSyntax sql
                    && sql.Identifier.ValueText == sqlParameter
                    && CommandIdentity(command) is { } commandKey)
                {
                    _ = activeCommands.Add(commandKey);
                }
            }

            foreach (InvocationExpressionSyntax invocation in OwnedDescendants<InvocationExpressionSyntax>(
                         method,
                         methodRoot))
            {
                if (invocation.ArgumentList.Arguments.Any(argument =>
                        argument.Expression is IdentifierNameSyntax sql
                        && sql.Identifier.ValueText == sqlParameter)
                    && InvocationSqlCommandKey(invocation) is { } commandKey)
                {
                    _ = activeCommands.Add(commandKey);
                }
            }
        }

        foreach (InvocationExpressionSyntax invocation in OwnedDescendants<InvocationExpressionSyntax>(
                     method,
                     methodRoot))
        {
            if (IsParameterBindingInvocation(invocation))
            {
                SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

                for (int index = 0; index < arguments.Count - 1; index++)
                {
                    if (!TryReadParameterName(arguments[index].Expression, out string candidate)
                        || !string.Equals(candidate, parameterName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string? bindingCommand = BindingCommandKey(invocation, index);

                    if (bindingCommand is null)
                    {
                        ambiguous = true;
                        continue;
                    }

                    if (activeCommands.Contains(bindingCommand))
                    {
                        sites.Add(new TransitiveBindingSite(
                            arguments[index + 1].Expression,
                            invocation,
                            IsUtcInstantSqlBinder(invocation)));
                    }
                }

                continue;
            }

            int[] commandArguments =
            [
                .. invocation.ArgumentList.Arguments
                    .Select((argument, index) => new { argument, index })
                    .Where(candidate => CommandIdentity(candidate.argument.Expression) is { } commandKey
                        && activeCommands.Contains(commandKey))
                    .Select(static candidate => candidate.index),
            ];

            if (commandArguments.Length == 0)
            {
                continue;
            }

            MethodDeclarationSyntax[] candidates = ResolveMethodCandidates(invocation, methods);

            if (candidates.Length == 0)
            {
                continue;
            }

            if (candidates.Length != 1)
            {
                ambiguous = true;
                continue;
            }

            MethodDeclarationSyntax called = candidates[0];
            HashSet<string> calledCommands =
            [
                .. commandArguments.Select(index =>
                    called.ParameterList.Parameters[index].Identifier.ValueText),
            ];

            CollectTransitiveBindingSites(
                called,
                parameterName,
                calledCommands,
                sqlParameter: null,
                methods,
                memberTypes,
                visited,
                sites,
                ref ambiguous);
        }

        foreach (AssignmentExpressionSyntax assignment in OwnedDescendants<AssignmentExpressionSyntax>(
                     method,
                     methodRoot))
        {
            if (assignment.Left is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Value",
                    Expression: ElementAccessExpressionSyntax parameterAccess,
                }
                && parameterAccess.ArgumentList.Arguments.Count == 1
                && TryReadParameterName(
                    parameterAccess.ArgumentList.Arguments[0].Expression,
                    out string candidate)
                && string.Equals(candidate, parameterName, StringComparison.Ordinal)
                && ElementAccessCommandKey(parameterAccess) is { } commandKey
                && activeCommands.Contains(commandKey))
            {
                sites.Add(new TransitiveBindingSite(
                    assignment.Right,
                    assignment,
                    CanonicalByContract: false));
            }
        }
    }

    private static MethodDeclarationSyntax[] ResolveMethodCandidates(
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods)
    {
        string lookupName = WrapperLookupName(invocation);

        return methods.TryGetValue(lookupName, out List<MethodDeclarationSyntax>? candidates)
            ?
            [
                .. candidates.Where(method =>
                    method.ParameterList.Parameters.Count == invocation.ArgumentList.Arguments.Count),
            ]
            : [];
    }

    private static SqlWriteContract[] DiscoverSqlWriteContracts(SyntaxNode scope, SyntaxNode root)
    {
        List<SqlWriteContract> contracts = [];

        foreach (AssignmentExpressionSyntax assignment in OwnedDescendants<AssignmentExpressionSyntax>(
                     scope,
                     root))
        {
            if (assignment.Left is not MemberAccessExpressionSyntax
                {
                    Expression: ExpressionSyntax command,
                    Name.Identifier.ValueText: "CommandText",
                }
                || !TryResolveString(assignment.Right, assignment, scope, root, [], out string sql))
            {
                continue;
            }

            AddSqlWriteContract(
                contracts,
                CommandIdentity(command),
                assignment,
                sqlArgumentIndex: null,
                sql);
        }

        foreach (InvocationExpressionSyntax invocation in OwnedDescendants<InvocationExpressionSyntax>(
                     scope,
                     root))
        {
            if (IsParameterBindingInvocation(invocation))
            {
                continue;
            }

            for (int argumentIndex = 0;
                 argumentIndex < invocation.ArgumentList.Arguments.Count;
                 argumentIndex++)
            {
                ArgumentSyntax argument = invocation.ArgumentList.Arguments[argumentIndex];

                if (!TryResolveString(argument.Expression, argument, scope, root, [], out string sql))
                {
                    continue;
                }

                AddSqlWriteContract(
                    contracts,
                    InvocationSqlCommandKey(invocation),
                    invocation,
                    argumentIndex,
                    sql);
            }
        }

        return [.. contracts];
    }

    private static void AddSqlWriteContract(
        List<SqlWriteContract> contracts,
        string? commandKey,
        SyntaxNode origin,
        int? sqlArgumentIndex,
        string sql)
    {
        if (!IsSqlWrite(sql))
        {
            return;
        }

        HashSet<string> parameters = DiscoverSqlMappedInstantParameters(sql);

        contracts.Add(new SqlWriteContract(
            contracts.Count,
            commandKey,
            origin,
            sqlArgumentIndex,
            parameters));
    }

    private static bool TryResolveString(
        ExpressionSyntax expression,
        SyntaxNode binding,
        SyntaxNode scope,
        SyntaxNode root,
        HashSet<SyntaxNode> visited,
        out string text)
    {
        expression = Unwrap(expression);

        if (!visited.Add(expression))
        {
            text = string.Empty;

            return false;
        }

        if (TryEvaluateString(expression, out text))
        {
            return true;
        }

        if (expression is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.AddExpression)
            && TryResolveString(binary.Left, binding, scope, root, visited, out string left)
            && TryResolveString(binary.Right, binding, scope, root, visited, out string right))
        {
            text = left + right;

            return true;
        }

        if (expression is IdentifierNameSyntax identifier)
        {
            ExpressionSyntax? value = OwnedDescendants<SyntaxNode>(scope, root)
                .Where(node => node.SpanStart < binding.SpanStart)
                .Select(node => node switch
                {
                    VariableDeclaratorSyntax declarator
                        when declarator.Identifier.ValueText == identifier.Identifier.ValueText
                            && declarator.Initializer is not null =>
                        declarator.Initializer.Value,
                    AssignmentExpressionSyntax assignment
                        when assignment.Left is IdentifierNameSyntax assigned
                            && assigned.Identifier.ValueText == identifier.Identifier.ValueText =>
                        assignment.Right,
                    _ => null,
                })
                .LastOrDefault(static candidate => candidate is not null);

            if (value is null)
            {
                ExpressionSyntax[] fieldValues =
                [
                    .. root.DescendantNodes()
                        .OfType<VariableDeclaratorSyntax>()
                        .Where(declarator =>
                            declarator.Identifier.ValueText == identifier.Identifier.ValueText
                            && declarator.Initializer is not null
                            && declarator.Ancestors().Any(static ancestor => ancestor is FieldDeclarationSyntax))
                        .Select(static declarator => declarator.Initializer!.Value),
                ];

                value = fieldValues.Length == 1 ? fieldValues[0] : null;
            }

            if (value is not null)
            {
                return TryResolveString(value, binding, scope, root, visited, out text);
            }
        }

        if (expression is MemberAccessExpressionSyntax member)
        {
            string owner = SimpleTypeName(member.Expression.ToString());
            ExpressionSyntax[] values =
            [
                .. root.DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(type => type.Identifier.ValueText == owner)
                    .SelectMany(static type => type.Members)
                    .SelectMany(declaration => declaration switch
                    {
                        FieldDeclarationSyntax field => field.Declaration.Variables
                            .Where(variable =>
                                variable.Identifier.ValueText == member.Name.Identifier.ValueText
                                && variable.Initializer is not null)
                            .Select(static variable => variable.Initializer!.Value),
                        PropertyDeclarationSyntax property
                            when property.Identifier.ValueText == member.Name.Identifier.ValueText
                                && property.ExpressionBody is not null =>
                            [property.ExpressionBody.Expression],
                        _ => [],
                    }),
            ];

            if (values.Length == 1)
            {
                return TryResolveString(values[0], binding, scope, root, visited, out text);
            }
        }

        text = string.Empty;

        return false;
    }

    private static bool IsExecutableScope(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax
            or AccessorDeclarationSyntax
            or LocalFunctionStatementSyntax;

    private static bool ContainsPotentialInstantBoundary(SyntaxNode scope, SyntaxNode root) =>
        OwnedDescendants<LiteralExpressionSyntax>(scope, root).Any(literal =>
            literal.IsKind(SyntaxKind.StringLiteralExpression)
            && (literal.Token.ValueText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                || literal.Token.ValueText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || TryReadParameterName(literal, out _)));

    private static IEnumerable<TNode> OwnedDescendants<TNode>(SyntaxNode scope, SyntaxNode root)
        where TNode : SyntaxNode =>
        scope.DescendantNodes()
            .OfType<TNode>()
            .Where(node => ReferenceEquals(OwningExecutableScope(node, root), scope));

    private static SyntaxNode OwningExecutableScope(SyntaxNode node, SyntaxNode root) =>
        node.Ancestors().FirstOrDefault(IsExecutableScope) ?? root;

    private static string? BindingCommandKey(
        InvocationExpressionSyntax invocation,
        int parameterArgumentIndex)
    {
        string? collectionOwner = ParameterCollectionCommandKey(invocation.Expression);

        if (collectionOwner is not null)
        {
            return collectionOwner;
        }

        return parameterArgumentIndex > 0
            ? CommandIdentity(invocation.ArgumentList.Arguments[0].Expression)
            : null;
    }

    private static string? ParameterCollectionCommandKey(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);

        return expression is MemberAccessExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Expression: ExpressionSyntax command,
                Name.Identifier.ValueText: "Parameters",
            },
        }
            ? CommandIdentity(command)
            : null;
    }

    private static string? ElementAccessCommandKey(ElementAccessExpressionSyntax access) =>
        access.Expression is MemberAccessExpressionSyntax
        {
            Expression: ExpressionSyntax command,
            Name.Identifier.ValueText: "Parameters",
        }
            ? CommandIdentity(command)
            : null;

    private static string? CreatedParameterCommandKey(
        string parameterVariable,
        SyntaxNode binding,
        SyntaxNode scope,
        SyntaxNode root)
    {
        InvocationExpressionSyntax? initializer = OwnedDescendants<VariableDeclaratorSyntax>(scope, root)
            .Where(declaration =>
                declaration.SpanStart < binding.SpanStart
                && declaration.Identifier.ValueText == parameterVariable)
            .Select(static declaration => declaration.Initializer?.Value)
            .OfType<InvocationExpressionSyntax>()
            .LastOrDefault();

        return initializer?.Expression is MemberAccessExpressionSyntax
        {
            Expression: ExpressionSyntax command,
            Name.Identifier.ValueText: "CreateParameter",
        }
            ? CommandIdentity(command)
            : null;
    }

    private static string? InvocationSqlCommandKey(InvocationExpressionSyntax invocation)
    {
        string[] lambdaParameters =
        [
            .. invocation.ArgumentList.Arguments
                .SelectMany(static argument => argument.Expression switch
                {
                    SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
                    ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters
                        .Select(static parameter => parameter.Identifier.ValueText),
                    _ => [],
                })
                .Distinct(StringComparer.Ordinal),
        ];

        if (lambdaParameters.Length == 1)
        {
            return lambdaParameters[0];
        }

        VariableDeclaratorSyntax? declaration = invocation.Ancestors()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(candidate => candidate.Initializer?.Value.Span.Contains(invocation.Span) == true);

        if (declaration is not null)
        {
            return declaration.Identifier.ValueText;
        }

        AssignmentExpressionSyntax? assignment = invocation.Ancestors()
            .OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.Right.Span.Contains(invocation.Span));

        if (assignment?.Left is ExpressionSyntax assigned
            && CommandIdentity(assigned) is { } assignedCommand)
        {
            return assignedCommand;
        }

        return invocation.Expression is MemberAccessExpressionSyntax member
            ? CommandIdentity(member.Expression)
            : null;
    }

    private static string? CommandIdentity(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);

        return expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.ToString(),
            _ => null,
        };
    }

    private static Dictionary<string, List<MethodDeclarationSyntax>> IndexMethods(
        IEnumerable<ParsedSource> sources)
    {
        Dictionary<string, List<MethodDeclarationSyntax>> methods = new(StringComparer.Ordinal);

        foreach (MethodDeclarationSyntax method in sources.SelectMany(
                     static source => source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()))
        {
            string typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText;

            AddMethod(methods, typeName + "." + method.Identifier.ValueText, method);
        }

        return methods;
    }

    private static Dictionary<string, HashSet<string>> IndexMemberTypes(
        IEnumerable<ParsedSource> sources)
    {
        Dictionary<string, HashSet<string>> memberTypes = new(StringComparer.Ordinal);

        foreach (TypeDeclarationSyntax type in sources.SelectMany(
                     static source => source.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()))
        {
            string typeName = type.Identifier.ValueText;

            foreach (PropertyDeclarationSyntax property in type.Members.OfType<PropertyDeclarationSyntax>())
            {
                AddMemberType(memberTypes, typeName, property.Identifier.ValueText, property.Type.ToString());
            }

            foreach (FieldDeclarationSyntax field in type.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                {
                    AddMemberType(
                        memberTypes,
                        typeName,
                        variable.Identifier.ValueText,
                        field.Declaration.Type.ToString());
                }
            }

            if (type is RecordDeclarationSyntax { ParameterList: { } parameters })
            {
                foreach (ParameterSyntax parameter in parameters.Parameters)
                {
                    if (parameter.Type is not null)
                    {
                        AddMemberType(
                            memberTypes,
                            typeName,
                            parameter.Identifier.ValueText,
                            parameter.Type.ToString());
                    }
                }
            }
        }

        return memberTypes;
    }

    private static void AddMemberType(
        Dictionary<string, HashSet<string>> memberTypes,
        string typeName,
        string memberName,
        string memberType)
    {
        string key = SimpleTypeName(typeName) + "." + memberName;

        if (!memberTypes.TryGetValue(key, out HashSet<string>? candidates))
        {
            candidates = new HashSet<string>(StringComparer.Ordinal);
            memberTypes.Add(key, candidates);
        }

        _ = candidates.Add(NormalizeType(memberType) ?? memberType);
    }

    private static void AddMethod(
        Dictionary<string, List<MethodDeclarationSyntax>> methods,
        string key,
        MethodDeclarationSyntax method)
    {
        if (!methods.TryGetValue(key, out List<MethodDeclarationSyntax>? overloads))
        {
            overloads = [];
            methods.Add(key, overloads);
        }

        overloads.Add(method);
    }

    private static bool TryReadParameterName(ExpressionSyntax expression, out string parameterName)
    {
        string? candidate = expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

        if (candidate is null
            || candidate.Length < 2
            || candidate[0] is not ('$' or '@' or ':'))
        {
            parameterName = string.Empty;

            return false;
        }

        parameterName = candidate;

        return true;
    }

    private static bool IsSemanticInstantParameter(string parameterName)
    {
        string name = parameterName[1..];

        return name.EndsWith("At", StringComparison.Ordinal)
            || name.EndsWith("Utc", StringComparison.Ordinal)
            || name.EndsWith("Time", StringComparison.Ordinal)
            || name.EndsWith("Date", StringComparison.Ordinal)
            || name.EndsWith("Deadline", StringComparison.Ordinal)
            || name.EndsWith("Watermark", StringComparison.Ordinal)
            || name.EndsWith("OlderThan", StringComparison.Ordinal)
            || name.EndsWith("NewerThan", StringComparison.Ordinal)
            || name == "now";
    }

    private static HashSet<string> DiscoverSqlMappedInstantParameters(string sql)
    {
        HashSet<string> instantColumns =
        [
            .. UtcInstantColumnInventory.Core.SelectMany(static table => table.Columns),
            .. UtcInstantColumnInventory.CovenantCanonical.SelectMany(static table => table.Columns),
        ];

        HashSet<string> parameters = new(StringComparer.Ordinal);

        if (!IsSqlWrite(sql))
        {
            return parameters;
        }

        foreach (Match assignment in Regex.Matches(
                     sql,
                     "(?<column>\\\"?[A-Za-z][A-Za-z0-9_]*\\\"?)\\s*=\\s*(?<parameter>[@$:][A-Za-z][A-Za-z0-9_]*)",
                     RegexOptions.CultureInvariant))
        {
            string column = assignment.Groups["column"].Value.Trim('"');

            if (instantColumns.Contains(column))
            {
                _ = parameters.Add(assignment.Groups["parameter"].Value);
            }
        }

        AddInsertParameters(sql, instantColumns, parameters);

        return parameters;
    }

    private static bool IsSqlWrite(string sql) =>
        TryFindWord(sql, "INSERT", 0, out _)
        || TryFindWord(sql, "UPDATE", 0, out _);

    private static bool TryEvaluateString(ExpressionSyntax expression, out string text)
    {
        expression = Unwrap(expression);

        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            text = literal.Token.ValueText;

            return true;
        }

        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            StringBuilder builder = new();

            foreach (InterpolatedStringContentSyntax content in interpolated.Contents)
            {
                _ = content switch
                {
                    InterpolatedStringTextSyntax textPart => builder.Append(textPart.TextToken.ValueText),
                    InterpolationSyntax => builder.Append(" expression "),
                    _ => builder,
                };
            }

            text = builder.ToString();

            return true;
        }

        if (expression is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.AddExpression)
            && TryEvaluateString(binary.Left, out string left)
            && TryEvaluateString(binary.Right, out string right))
        {
            text = left + right;

            return true;
        }

        text = string.Empty;

        return false;
    }

    private static void AddInsertParameters(
        string sql,
        IReadOnlySet<string> instantColumns,
        HashSet<string> parameters)
    {
        int searchFrom = 0;

        while (TryFindWord(sql, "INSERT", searchFrom, out int insertAt))
        {
            int columnsOpen = sql.IndexOf('(', insertAt);

            if (columnsOpen < 0
                || !TryFindClosingParenthesis(sql, columnsOpen, out int columnsClose))
            {
                return;
            }

            int valuesAt = FindTopLevelWord(sql, "VALUES", columnsClose + 1);
            int selectAt = FindTopLevelWord(sql, "SELECT", columnsClose + 1);

            string[] values;
            int statementEnd;

            if (valuesAt >= 0 && (selectAt < 0 || valuesAt < selectAt))
            {
                int valuesOpen = sql.IndexOf('(', valuesAt);

                if (valuesOpen < 0
                    || !TryFindClosingParenthesis(sql, valuesOpen, out int valuesClose))
                {
                    return;
                }

                values = SplitSqlList(sql[(valuesOpen + 1)..valuesClose]);
                statementEnd = valuesClose;
            }
            else if (selectAt >= 0)
            {
                int projectionStart = selectAt + "SELECT".Length;
                int projectionEnd = FindInsertSelectProjectionEnd(sql, projectionStart);

                values = SplitSqlList(sql[projectionStart..projectionEnd]);
                statementEnd = projectionEnd;
            }
            else
            {
                searchFrom = columnsClose + 1;
                continue;
            }

            string[] columns = SplitSqlList(sql[(columnsOpen + 1)..columnsClose]);

            for (int index = 0; index < Math.Min(columns.Length, values.Length); index++)
            {
                string column = columns[index].Trim().Trim('"');
                string value = values[index].Trim();

                if (instantColumns.Contains(column)
                    && Regex.Match(value, "^[@$:][A-Za-z][A-Za-z0-9_]*", RegexOptions.CultureInvariant)
                        is { Success: true } parameter)
                {
                    _ = parameters.Add(parameter.Value);
                }
            }

            searchFrom = statementEnd + 1;
        }
    }

    private static int FindInsertSelectProjectionEnd(string sql, int projectionStart)
    {
        int projectionEnd = sql.Length;

        foreach (string clause in new[] { "FROM", "WHERE", "RETURNING" })
        {
            int clauseAt = FindTopLevelWord(sql, clause, projectionStart);

            if (clauseAt >= 0)
            {
                projectionEnd = Math.Min(projectionEnd, clauseAt);
            }
        }

        int semicolonAt = FindTopLevelCharacter(sql, ';', projectionStart);

        return semicolonAt >= 0 ? Math.Min(projectionEnd, semicolonAt) : projectionEnd;
    }

    private static int FindTopLevelWord(string text, string word, int start)
    {
        int depth = 0;
        char quote = '\0';

        for (int index = start; index <= text.Length - word.Length; index++)
        {
            char current = text[index];

            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')')
            {
                depth--;
                continue;
            }

            if (depth == 0
                && text.AsSpan(index, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)
                && (index == 0 || !IsSqlWordCharacter(text[index - 1]))
                && (index + word.Length == text.Length
                    || !IsSqlWordCharacter(text[index + word.Length])))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindTopLevelCharacter(string text, char target, int start)
    {
        int depth = 0;
        char quote = '\0';

        for (int index = start; index < text.Length; index++)
        {
            char current = text[index];

            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
            }
            else if (depth == 0 && current == target)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSqlWordCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '_';

    private static bool TryFindWord(
        string text,
        string word,
        int start,
        out int position)
    {
        position = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);

        return position >= 0;
    }

    private static bool TryFindClosingParenthesis(
        string text,
        int opening,
        out int closing)
    {
        int depth = 0;

        for (int index = opening; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')' && --depth == 0)
            {
                closing = index;

                return true;
            }
        }

        closing = -1;

        return false;
    }

    private static string[] SplitSqlList(string list)
    {
        List<string> items = [];
        int depth = 0;
        int itemStart = 0;
        char quote = '\0';

        for (int index = 0; index < list.Length; index++)
        {
            char current = list[index];

            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < list.Length && list[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            switch (current)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    items.Add(list[itemStart..index]);
                    itemStart = index + 1;
                    break;
            }
        }

        items.Add(list[itemStart..]);

        return [.. items];
    }

    private static bool IsParameterBindingInvocation(InvocationExpressionSyntax invocation)
    {
        string name = invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty,
        };

        return name is "AddWithValue" or "AddParameter" or "AddStoredParameter" or "Bind"
            || name == "Add" && invocation.Expression is IdentifierNameSyntax;
    }

    private static bool IsTypedParameterDeclaration(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.Count >= 2
            && invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Add",
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Parameters",
                },
            };

    private static bool IsUtcInstantSqlBinder(InvocationExpressionSyntax invocation) =>
        InvocationName(invocation) is "UtcInstantSql.AddParameter" or "UtcInstantSql.AddStoredParameter";

    private static bool IsCanonicalExpression(
        ExpressionSyntax expression,
        SyntaxNode binding,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes,
        HashSet<SyntaxNode> visited)
    {
        expression = Unwrap(expression);

        if (expression is MemberAccessExpressionSyntax member
            && member.ToString() == "DBNull.Value")
        {
            return true;
        }

        if (expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return true;
        }

        if (expression is ConditionalExpressionSyntax conditional)
        {
            return IsCanonicalExpression(conditional.WhenTrue, binding, methods, memberTypes, visited)
                && IsCanonicalExpression(conditional.WhenFalse, binding, methods, memberTypes, visited);
        }

        if (expression is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.CoalesceExpression))
        {
            return IsCanonicalExpression(binary.Left, binding, methods, memberTypes, visited)
                && IsCanonicalExpression(binary.Right, binding, methods, memberTypes, visited);
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            string name = InvocationName(invocation);

            if (name is "UtcInstantText.Format" or "UtcInstantText.Normalize")
            {
                return true;
            }

            if (name == "GrimoireEntitySql.Format"
                && invocation.ArgumentList.Arguments.Count == 1
                && IsTemporalExpression(
                    invocation.ArgumentList.Arguments[0].Expression,
                    binding,
                    memberTypes))
            {
                return true;
            }

            return IsCanonicalWrapper(invocation, binding, methods, memberTypes, visited);
        }

        if (expression is IdentifierNameSyntax identifier)
        {
            return IsCanonicalLocal(identifier, binding, methods, memberTypes, visited);
        }

        return false;
    }

    private static bool IsCanonicalWrapper(
        InvocationExpressionSyntax invocation,
        SyntaxNode binding,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes,
        HashSet<SyntaxNode> visited)
    {
        string name = WrapperLookupName(invocation);

        if (!methods.TryGetValue(name, out List<MethodDeclarationSyntax>? candidates))
        {
            return false;
        }

        MethodDeclarationSyntax[] matching =
        [
            .. candidates.Where(method =>
                method.ParameterList.Parameters.Count == invocation.ArgumentList.Arguments.Count),
        ];

        for (int index = 0; index < invocation.ArgumentList.Arguments.Count; index++)
        {
            string? argumentType = InferExpressionType(
                invocation.ArgumentList.Arguments[index].Expression,
                binding,
                memberTypes);

            if (argumentType is null)
            {
                continue;
            }

            matching =
            [
                .. matching.Where(method =>
                    string.Equals(
                        NormalizeType(method.ParameterList.Parameters[index].Type?.ToString()),
                        NormalizeType(argumentType),
                        StringComparison.Ordinal)),
            ];
        }

        if (matching.Length == 0)
        {
            return false;
        }

        foreach (MethodDeclarationSyntax method in matching)
        {
            if (!visited.Add(method))
            {
                return false;
            }

            ExpressionSyntax? returned = method.ExpressionBody?.Expression
                ?? method.Body?.DescendantNodes()
                    .OfType<ReturnStatementSyntax>()
                    .Select(static statement => statement.Expression)
                    .SingleOrDefault(static returned => returned is not null);

            if (returned is null
                || !IsCanonicalExpression(returned, binding, methods, memberTypes, visited))
            {
                return false;
            }

            _ = visited.Remove(method);
        }

        return true;
    }

    private static bool IsCanonicalLocal(
        IdentifierNameSyntax identifier,
        SyntaxNode binding,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes,
        HashSet<SyntaxNode> visited)
    {
        SyntaxNode? containingMember = binding.Ancestors().FirstOrDefault(static ancestor =>
            ancestor is MethodDeclarationSyntax or LocalFunctionStatementSyntax);

        if (containingMember is null)
        {
            return false;
        }

        ExpressionSyntax[] values =
        [
            .. containingMember.DescendantNodes()
                .Where(node => node.SpanStart < binding.SpanStart)
                .SelectMany(node => node switch
                {
                    VariableDeclaratorSyntax declarator
                        when declarator.Identifier.ValueText == identifier.Identifier.ValueText
                            && declarator.Initializer is not null =>
                        [declarator.Initializer.Value],
                    AssignmentExpressionSyntax assignment
                        when assignment.Left is IdentifierNameSyntax assigned
                            && assigned.Identifier.ValueText == identifier.Identifier.ValueText =>
                        [assignment.Right],
                    _ => Array.Empty<ExpressionSyntax>(),
                }),
        ];

        return values.Length > 0
            && values.All(value => IsCanonicalExpression(value, binding, methods, memberTypes, visited));
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    continue;
                default:
                    return expression;
            }
        }
    }

    private static string? InferExpressionType(
        ExpressionSyntax expression,
        SyntaxNode binding,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes)
    {
        expression = Unwrap(expression);

        if (expression is MemberAccessExpressionSyntax member)
        {
            if (member.Expression.ToString() is "DateTimeOffset" or "DateTime")
            {
                return member.Expression.ToString();
            }

            if (member.Name.Identifier.ValueText == "GetUtcNow")
            {
                return "DateTimeOffset";
            }

            string? ownerType = InferExpressionType(member.Expression, binding, memberTypes);

            if (ownerType is not null
                && memberTypes.TryGetValue(
                    SimpleTypeName(ownerType) + "." + member.Name.Identifier.ValueText,
                    out HashSet<string>? candidates)
                && candidates.Count == 1)
            {
                return candidates.Single();
            }
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            if (InvocationName(invocation).EndsWith("GetUtcNow", StringComparison.Ordinal))
            {
                return "DateTimeOffset";
            }

            if (invocation.Expression is MemberAccessExpressionSyntax call
                && call.Name.Identifier.ValueText == "ToUniversalTime")
            {
                return InferExpressionType(call.Expression, binding, memberTypes);
            }
        }

        if (expression is not IdentifierNameSyntax identifier)
        {
            return null;
        }

        SyntaxNode? containingMember = binding.Ancestors().FirstOrDefault(static ancestor =>
            ancestor is MethodDeclarationSyntax or LocalFunctionStatementSyntax);

        if (containingMember is null)
        {
            return null;
        }

        SingleVariableDesignationSyntax? designation = containingMember.DescendantNodes()
            .OfType<SingleVariableDesignationSyntax>()
            .Where(candidate => candidate.SpanStart < identifier.SpanStart)
            .LastOrDefault(candidate =>
                candidate.Identifier.ValueText == identifier.Identifier.ValueText);

        if (designation?.Parent is DeclarationPatternSyntax declarationPattern)
        {
            return declarationPattern.Type.ToString();
        }

        IsPatternExpressionSyntax? pattern = designation?.Ancestors()
            .OfType<IsPatternExpressionSyntax>()
            .FirstOrDefault();

        if (pattern is not null)
        {
            return InferExpressionType(pattern.Expression, binding, memberTypes);
        }

        TypeSyntax? parameterType = containingMember.DescendantNodes()
            .OfType<ParameterSyntax>()
            .Where(parameter => parameter.Identifier.ValueText == identifier.Identifier.ValueText)
            .Select(static parameter => parameter.Type)
            .FirstOrDefault(static type => type is not null);

        if (parameterType is not null)
        {
            return parameterType.ToString();
        }

        return containingMember.DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .Where(declaration => declaration.Variables.Any(variable =>
                variable.Identifier.ValueText == identifier.Identifier.ValueText))
            .Select(static declaration => declaration.Type.ToString())
            .FirstOrDefault();
    }

    private static string? NormalizeType(string? type) =>
        type?.Replace("global::", string.Empty, StringComparison.Ordinal).TrimEnd('?');

    private static string SimpleTypeName(string type)
    {
        string normalized = NormalizeType(type) ?? type;
        int generic = normalized.IndexOf('<');

        if (generic >= 0)
        {
            normalized = normalized[..generic];
        }

        int qualification = normalized.LastIndexOf('.');

        return qualification >= 0 ? normalized[(qualification + 1)..] : normalized;
    }

    private static bool IsTemporalExpression(
        ExpressionSyntax expression,
        SyntaxNode binding,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes) =>
        SimpleTypeName(InferExpressionType(expression, binding, memberTypes) ?? string.Empty)
            is "DateTime" or "DateTimeOffset";

    private static string InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member when member.Expression is IdentifierNameSyntax owner =>
                owner.Identifier.ValueText + "." + member.Name.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => invocation.Expression.ToString(),
        };

    private static string WrapperLookupName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier =>
                invocation.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText
                    + "."
                    + identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member when member.Expression is IdentifierNameSyntax owner =>
                owner.Identifier.ValueText + "." + member.Name.Identifier.ValueText,
            _ => InvocationName(invocation),
        };

    private sealed record ParsedSource(ProductionSource Source, SyntaxTree Tree)
    {
        internal SyntaxNode Root { get; } = Tree.GetRoot();
    }

    private sealed record SqlWriteContract(
        int Id,
        string? CommandKey,
        SyntaxNode Origin,
        int? SqlArgumentIndex,
        HashSet<string> InstantParameters);

    private readonly record struct SqlParameterSlot(int ContractId, string ParameterName);

    private readonly record struct MethodTraversalKey(
        string Source,
        int MethodStart,
        string ParameterName,
        string Commands,
        string? SqlParameter);

    private sealed record TransitiveBindingSite(
        ExpressionSyntax Value,
        SyntaxNode Binding,
        bool CanonicalByContract);

    private sealed record InstantBindingViolation(
        string Source,
        int Line,
        string ParameterName,
        string Value);
}

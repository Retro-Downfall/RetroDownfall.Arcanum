using Xunit.Abstractions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Closed source inventory for every authored boundary that may render persisted instants.
/// </summary>
public sealed class UtcInstantPersistenceBoundaryTests(ITestOutputHelper output)
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

    [Theory]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Data.UtcInstantText.Format(now)", true)]
    [InlineData("Other.UtcInstantText.Format(now)", false)]
    public void Per_binding_guard_recognizes_only_the_exact_qualified_instant_codec(string value, bool canonical)
    {
        string source = $$"""
            using Microsoft.Data.Sqlite;
            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, DateTimeOffset now)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue("$value", {{value}});
                }
            }
            """;

        IReadOnlyList<InstantBindingViolation> violations = FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]);

        Assert.Equal(canonical, violations.Count == 0);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("command.Parameters.Clear();", false)]
    [InlineData("command.Parameters[\"$value\"].Value = imported;", false)]
    public void Per_binding_guard_proves_retained_values_only_without_intervening_mutation(string mutation, bool canonical)
    {
        string source = $$"""
            using Microsoft.Data.Sqlite;
            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, DateTimeOffset now, string imported)
                {
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue("$value", UtcInstantText.Format(now));
                    command.ExecuteNonQuery();
                    {{mutation}}
                    command.CommandText = "INSERT INTO Entries (UpdatedAt) VALUES ($value);";
                    command.ExecuteNonQuery();
                }
            }
            """;

        IReadOnlyList<InstantBindingViolation> violations = FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]);

        Assert.Equal(canonical, violations.Count == 0);
    }

    [Fact]
    public void Retained_binding_proof_rejects_an_execution_embedded_in_other_work()
    {
        const string source = """
            using Microsoft.Data.Sqlite;
            internal static class Fixture
            {
                internal static void Write(SqliteCommand command, DateTimeOffset now)
                {
                    bool changed = false;
                    command.CommandText = "INSERT INTO Entries (CreatedAt) VALUES ($value);";
                    _ = command.Parameters.AddWithValue("$value", UtcInstantText.Format(now));
                    _ = (changed = true) && command.ExecuteNonQuery() > 0;
                    command.CommandText = "INSERT INTO Entries (UpdatedAt) VALUES ($value);";
                    command.ExecuteNonQuery();
                }
            }
            """;

        Assert.NotEmpty(FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));
    }

    [Theory]
    [InlineData("CreatedAt < $value")]
    [InlineData("createdat < $value")]
    [InlineData("e.\"CreatedAt\" <= $value")]
    [InlineData("CreatedAt > $value")]
    [InlineData("CreatedAt >= $value")]
    [InlineData("CreatedAt = $value")]
    [InlineData("CreatedAt != $value")]
    [InlineData("CreatedAt <> $value")]
    [InlineData("LastSummarizedMessageAt IS $value")]
    [InlineData("LastSummarizedMessageAt IS NOT $value")]
    [InlineData("$value < e.\"CreatedAt\"")]
    [InlineData("$value >= CreatedAt")]
    [InlineData("$value = CreatedAt")]
    [InlineData("CreatedAt BETWEEN $value AND $end")]
    public void Per_binding_guard_follows_timestamp_comparisons_with_generic_bounds(string predicate)
    {
        string source = $$"""
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Read(SqliteCommand command, DateTimeOffset imported)
                {
                    command.CommandText = "SELECT Id FROM Entries WHERE {{predicate.Replace("\"", "\\\"", StringComparison.Ordinal)}};";
                    _ = command.Parameters.AddWithValue("$value", imported);
                    _ = command.Parameters.AddWithValue("$end", UtcInstantText.Format(imported));
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal("$value", violation.ParameterName);
        Assert.Equal("imported", violation.Value);
    }

    [Theory]
    [InlineData("SELECT Id FROM Entries WHERE CreatedAt < $value;", "UtcInstantText.Format(imported)")]
    [InlineData("DELETE FROM Entries WHERE CreatedAt <= $value;", "UtcInstantText.Format(imported)")]
    [InlineData("SELECT Id FROM Entries WHERE julianday(CreatedAt) < julianday($value);", "imported")]
    public void Per_binding_guard_accepts_canonical_bounds_and_parsed_SQLite_instants(string sql, string value)
    {
        string source = $$"""
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Read(SqliteCommand command, DateTimeOffset imported)
                {
                    command.CommandText = "{{sql}}";
                    _ = command.Parameters.AddWithValue("$value", {{value}});
                }
            }
            """;

        Assert.Empty(FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));
    }

    [Fact]
    public void Per_binding_guard_fails_closed_for_an_unrecognized_comparison_binding()
    {
        const string source = """
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Read(SqliteCommand command, DateTimeOffset imported)
                {
                    command.CommandText = "DELETE FROM Entries WHERE CreatedAt < :value;";
                    _ = command.Parameters.Add(new SqliteParameter(":value", imported));
                }
            }
            """;

        InstantBindingViolation violation = Assert.Single(
            FindInstantBindingViolations([new ProductionSource("Fixture.cs", source)]));

        Assert.Equal(":value", violation.ParameterName);
        Assert.Equal("<unproven binding>", violation.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Per_binding_guard_proves_common_bounds_only_after_unexecuted_alternative_queries(int executionPosition)
    {
        string source = $$"""
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Read(SqliteCommand command, DateTimeOffset at, bool filtered)
                {
                    if (filtered)
                    {
                        command.CommandText = "SELECT Id FROM Entries WHERE CreatedAt < $value;";
                        {{(executionPosition == 1 ? "command.ExecuteScalar();" : string.Empty)}}
                    }
                    else
                    {
                        command.CommandText = "SELECT Id FROM Entries WHERE CreatedAt <= $value;";
                    }

                    {{(executionPosition == 2 ? "command.ExecuteScalar();" : string.Empty)}}
                    _ = command.Parameters.AddWithValue("$value", UtcInstantText.Format(at));
                }
            }
            """;

        IReadOnlyList<InstantBindingViolation> violations = FindInstantBindingViolations(
            [new ProductionSource("Fixture.cs", source)]);

        if (executionPosition != 0)
        {
            Assert.NotEmpty(violations);
        }
        else
        {
            Assert.Empty(violations);
        }
    }

    [Theory]
    [InlineData("string", "ListPositionsAfterAsync", "@afterCreatedText", true)]
    [InlineData("DateTimeOffset", "ListPositionsAfterAsync", "@afterCreatedText", false)]
    [InlineData("string", "AnotherRead", "@afterCreatedText", false)]
    [InlineData("string", "BuildListCommand", "@afterTime", true)]
    [InlineData("DateTimeOffset", "BuildListCommand", "@afterTime", false)]
    [InlineData("string", "AnotherRead", "@afterTime", false)]
    [InlineData("string", "BuildListCommand", "@afterCreatedText", false)]
    [InlineData("string", "ListPositionsAfterAsync", "@afterTime", false)]
    public void Stored_ordering_cursor_exception_preserves_only_the_exact_string_binding(
        string propertyType, string method, string parameter, bool allowed)
    {
        string source = $$"""
            using Microsoft.Data.Sqlite;

            internal sealed record SagaMemoryPosition({{propertyType}} CreatedAt);

            internal static class SagaMemoryStore
            {
                internal static void {{method}}(SqliteCommand cmd, SagaMemoryPosition after)
                {
                    cmd.CommandText = "SELECT Id FROM saga_memories WHERE CreatedAt <= {{parameter}};";
                    AddParameter(cmd, "{{parameter}}", after.CreatedAt);
                }
            }
            """;

        IReadOnlyList<InstantBindingViolation> violations = FindInstantBindingViolations(
            [new ProductionSource("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs", source)]);

        Assert.Equal(allowed, violations.Count == 0);
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

    [Theory]
    [InlineData("(DateTimeOffset SortTime, Guid Id)?", "SortTime", true)]
    [InlineData("(string SortTime, Guid Id)?", "SortTime", false)]
    [InlineData("(Guid SortTime, Guid Id)?", "SortTime", false)]
    [InlineData("(DateTimeOffset SortTime, Guid Id)?", "Missing", false)]
    [InlineData("(DateTimeOffset SortTime, string SortTime)?", "SortTime", false)]
    public void Per_binding_guard_resolves_the_named_tuple_cursor_type_before_selecting_a_formatter(
        string tupleType, string member, bool allowed)
    {
        string source = $$"""
            using Microsoft.Data.Sqlite;

            internal static class Fixture
            {
                internal static void Read(SqliteCommand cmd, {{tupleType}} after)
                {
                    cmd.CommandText = "SELECT Id FROM Entries WHERE CreatedAt > @afterTime;";

                    if (after is { } cursor)
                    {
                        Add(cmd, "@afterTime", Format(cursor.{{member}}));
                    }
                }

                private static string Format(DateTimeOffset value) => UtcInstantText.Format(value);

                private static string Format(Guid value) => value.ToString("N");

                private static string Format(string value) => value;

                private static void Add(SqliteCommand cmd, string name, object value) => cmd.Parameters.AddWithValue(name, value);
            }
            """;

        IReadOnlyList<InstantBindingViolation> violations = FindInstantBindingViolations(
            [new ProductionSource("Fixture.cs", source)]);

        if (allowed)
        {
            Assert.Empty(violations);
        }
        else
        {
            InstantBindingViolation violation = Assert.Single(violations);

            Assert.Equal("@afterTime", violation.ParameterName);

            Assert.Equal("Format(cursor." + member + ")", violation.Value);
        }
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
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.GuildTransactions.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Market.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.MarketBids.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.MarketReads.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Mutations.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Notes.Reads.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Notes.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.Operator.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.OperatorContributions.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.OperatorList.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.OperatorMemberQuests.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.OperatorMembers.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildCohortStore.WithdrawOffer.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildLocalExecutionStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildLocalHostPolicy.Inbound.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalStore.Cohorts.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalStore.Distribution.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.Awards.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.Cancellation.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.CancellationWork.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.CohortCancellation.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.LocalLifecycle.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.LocalPending.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.LocalPort.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.OperatorList.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.PreDispatchRecovery.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.ProviderEffects.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.ProviderInput.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.Remote.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.RemoteCallbackReceiver.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.RemoteCancellationFrontier.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.RemoteProtocolStop.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.RemoteWait.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.TreeCursors.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.WaitCancellation.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildWorkspaceLeaseStore.Mutations.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildWorkspaceLeaseStore.cs",
            // Canonical market validation and a search cursor fallback format stored/DTO values.
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildMarketStorage.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Memory/MemorySearchService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GuildCanonicalSchemaDataInitializer.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildOwnerCleanupCoordinator.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalEvents.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalStore.Admission.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalStore.Delivery.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalStore.InboundPeer.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalStore.Outbox.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildPortalStore.Reads.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestCleanupParticipant.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.Continuations.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.Delivery.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.Dispatch.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.Results.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.AccountingRecovery.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.SpendCorrections.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.SpendReads.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildSpendRefreshRetry.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildSignetStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildStore.SourceClaims.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AExternalSpendLedger.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupCovenantRestoreReconciler.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreErasureEvidenceApplier.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreProtectedStatePurger.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/RestoreStagingManagedAuthoritySanitizationSession.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantSchemaRepairJournal.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Annals/AnnalsClaimWriter.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/AttachmentMemoryProvenanceStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BatchAccountingRecoveryStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.LexiconReplacement.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ArtifactSensitivityLedger.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignMaintenanceCheckpointStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignPathMarkerIntentStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Read.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.Write.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CampaignRollupStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCanonicalErasureTransaction.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCleanupWorker.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantCurationKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantDisclosureJournal.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEntryErasurePlan.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantEnvelopeStateStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantIndexRebuilder.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantManagedFileErasureKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantMutationKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantOwnerDeletionReader.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantProtectedArtifactErasureKernel.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantQuotaGuard.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/CovenantSearchOutboxWorker.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ExternalDisclosureStateStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/LocalErasureWorkItemStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/ManagedFileWriteIntentRecoveryService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/SessionDerivedArtifactStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/Covenant/SessionSummaryMaintenanceStore.cs",
            // Diagnostic context and snapshot hashes format observation instants, not database writes.
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Diagnostics.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Pruning.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireEntitySql.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyClaimStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/IdempotencyStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestAttemptStore.Claims.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestAttemptStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestDiscoveryStore.Accepted.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestDiscoveryStore.CompletedCheckpoint.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestDiscoveryStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestSweepStore.Accepted.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestSweepStore.Pending.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestSweepStore.Readmission.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestSweepStore.RequestAdmission.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestSweepStore.Work.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/LongRestSweepStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/LongRest/SagaLongRestService.Receipts.cs",
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
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionTerminalSuffixFinisher.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Hosting/WorkspaceIndexingService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Correction.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.Lifecycle.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.MemoryReview.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryErasureExposure.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Memory/SagaMemoryReviewService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.ProtectedTools.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.SessionTurnBegin.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.SessionTurnClaimBegin.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.SessionTurnClaimReplay.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.TurnCommit.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedArtifactTransferStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedSessionTransferIntentStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRecoveryCheckpointStore.cs",
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
            // Memory search formats parsed Guid workspace identities only; it writes no timestamp.
            "src/RetroDownfall.Arcanum.Infrastructure/Memory/MemorySearchService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantCampaignScopeProbe.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BatchRepository.cs",
            // Native memory inspection formats a parsed Session Guid, never a timestamp.
            "src/RetroDownfall.Arcanum.Infrastructure/Data/NativeMemorySearchReader.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.Lifecycle.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/UploadedFileRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ApprenticeRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ApprenticeRepository.Inspection.cs",
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
            "src/RetroDownfall.Arcanum.Infrastructure/Memory/MemoryRecallCandidateReader.cs",
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

    [Fact]
    public void Stored_text_mutation_inputs_preserve_other_cases_and_later_requests()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        ProductionSource[] first = StoredTextProofSources();

        long firstAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();

        ProductionSource[] second = StoredTextProofSources();

        long secondAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"stored-text fixture allocations: first request={firstAllocated}, second request={secondAllocated}");

        string[] owners =
        [
            StoredTextRoute("workspace"),
            StoredTextRoute("retention"),
            "src/RetroDownfall.Arcanum.Core/Memory/MemorySearchContinuation.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestRetentionPins.cs",
        ];

        Assert.Equal(owners.Order(StringComparer.Ordinal), first.Select(source => source.RelativePath).Order(StringComparer.Ordinal));

        Assert.Equal(first, second);

        Assert.NotSame(first, second);

        string owner = StoredTextRoute("workspace");

        int index = Array.FindIndex(first, source => source.RelativePath == owner);

        const string selected = "SELECT rowid,IndexedAt,RelativePath";

        Assert.Contains(selected, first[index].Text, StringComparison.Ordinal);

        first[index] = new(owner, first[index].Text.Replace(selected, "SELECT rowid,RelativePath,RelativePath", StringComparison.Ordinal));

        Assert.Contains(FindInstantBindingViolations(first), violation => violation.Source == owner && violation.ParameterName == "@time");

        ProductionSource[] later = StoredTextProofSources();

        Assert.Equal(second, later);

        Assert.NotSame(second, later);

        foreach (ProductionSource[] unchanged in new[] { second, later })
        {
            Assert.DoesNotContain(FindInstantBindingViolations(unchanged), violation => violation.Source == owner && violation.ParameterName == "@time");
        }
    }

    [Theory]
    [InlineData("workspace", "@time")]
    [InlineData("retention", "@afterAt")]
    public void Reviewed_stored_ordering_text_is_accepted_only_with_its_full_provenance(string route, string parameter)
    {
        ProductionSource[] sources = StoredTextProofSources();

        string owner = StoredTextRoute(route);

        Assert.DoesNotContain(FindInstantBindingViolations(sources),
            violation => violation.Source == owner && violation.ParameterName == parameter);
    }

    [Theory]
    [InlineData("workspace", "ordinal")]
    [InlineData("workspace", "source-column")]
    [InlineData("workspace", "target-column")]
    [InlineData("workspace", "reader-command")]
    [InlineData("workspace", "position-reassignment")]
    [InlineData("workspace", "position-ref-alias")]
    [InlineData("workspace", "position-implicit-in")]
    [InlineData("workspace", "candidate-implicit-in")]
    [InlineData("workspace", "candidate-position-implicit-in")]
    [InlineData("workspace", "after-union")]
    [InlineData("workspace", "after-seed")]
    [InlineData("workspace", "after-implicit-in")]
    [InlineData("workspace", "blob-alias")]
    [InlineData("workspace", "scope-union")]
    [InlineData("workspace", "scope-alias")]
    [InlineData("workspace", "scope-helper")]
    [InlineData("workspace", "scope-shadow")]
    [InlineData("workspace", "scope-pattern")]
    [InlineData("workspace", "scope-parameter")]
    [InlineData("workspace", "authored-list")]
    [InlineData("workspace", "record-type")]
    [InlineData("workspace", "record-getter")]
    [InlineData("workspace", "type-alias")]
    [InlineData("workspace", "global-type-alias")]
    [InlineData("retention", "ordinal")]
    [InlineData("retention", "source-column")]
    [InlineData("retention", "target-column")]
    [InlineData("retention", "reader-command")]
    [InlineData("retention", "cursor-reassignment")]
    [InlineData("retention", "cursor-ref-alias")]
    [InlineData("retention", "cursor-in-escape")]
    [InlineData("retention", "cursor-implicit-in")]
    [InlineData("retention", "created-implicit-in")]
    [InlineData("retention", "cursor-seed")]
    [InlineData("retention", "binding-lambda")]
    [InlineData("retention", "page-lambda")]
    [InlineData("retention", "predicate-union")]
    [InlineData("retention", "predicate-reassignment")]
    [InlineData("retention", "predicate-implicit-in")]
    [InlineData("retention", "predicate-helper")]
    [InlineData("retention", "predicate-shadow")]
    [InlineData("retention", "predicate-const")]
    [InlineData("retention", "reachable-const")]
    [InlineData("retention", "pins-alias")]
    [InlineData("retention", "nested-pins")]
    [InlineData("retention", "global-pins-alias")]
    public void Stored_text_provenance_refuses_changed_sources_commands_types_and_fragment_builders(string route, string mutation)
    {
        ProductionSource[] sources = StoredTextProofSources();

        string owner = StoredTextRoute(route);

        string parameter = route == "workspace" ? "@time" : "@afterAt";

        if (mutation is "global-type-alias" or "global-pins-alias")
        {
            string alias = route == "workspace" ? "MemorySearchPosition" : "GuildQuestRetentionPins";

            sources = [.. sources, new ProductionSource("Fixture.GlobalAliases.cs", $"global using {alias} = OtherNamespace.ForgedType;")];

            Assert.Contains(FindInstantBindingViolations(sources),
                violation => violation.Source == owner && violation.ParameterName == parameter);

            return;
        }

        string changedOwner = owner;

        string before;

        string after;

        if (route == "workspace")
        {
            const string position = "MemorySearchPosition position = new(3, reader.GetString(1), reader.GetInt64(3), reader.GetString(2), reader.GetString(4));";

            (before, after) = mutation switch
            {
                "ordinal" => (position, position.Replace("GetString(1)", "GetString(4)", StringComparison.Ordinal)),
                "source-column" => ("SELECT rowid,IndexedAt,RelativePath", "SELECT rowid,RelativePath,RelativePath"),
                "target-column" => ("AND IndexedAt=@time AND ChunkIndex", "AND RelativePath=@time AND IndexedAt>=@time AND ChunkIndex"),
                "reader-command" => ("await using (DbDataReader reader = await metadata.ExecuteReaderAsync(ct)", "await using DbCommand other = connection.CreateCommand(); other.Transaction = snapshot; other.CommandText = metadata.CommandText; await using (DbDataReader reader = await other.ExecuteReaderAsync(ct)"),
                "position-reassignment" => (position, position + " position = new(3, \"unproved\");"),
                "position-ref-alias" => (position, position + " { ref MemorySearchPosition alias = ref position; alias = new(3, \"unproved\"); }"),
                "position-implicit-in" => (position, position + " Tamper(position); static void Tamper(in MemorySearchPosition value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value) = new(3, \"unproved\"); }"),
                "candidate-implicit-in" => ("MemorySearchPosition previous = cursor;", "Tamper(candidate); static void Tamper(in (long Row, MemorySearchPosition Position, long Offset, long Length, long Bytes) value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value).Position = new(3, \"unproved\"); } MemorySearchPosition previous = cursor;"),
                "candidate-position-implicit-in" => ("MemorySearchPosition previous = cursor;", "Tamper(candidate.Position); static void Tamper(in MemorySearchPosition value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value) = new(3, \"unproved\"); } MemorySearchPosition previous = cursor;"),
                "after-union" => ("metadata.CommandText =", "after = \" UNION ALL SELECT rowid,'unproved',RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),0 FROM workspace_file_chunks\"; metadata.CommandText ="),
                "after-seed" => ("string after = \"\";", "string after = \" UNION ALL SELECT rowid,'unproved',RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),0 FROM workspace_file_chunks\";"),
                "after-implicit-in" => ("metadata.CommandText =", "Tamper(after); static void Tamper(in string value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value) = \" UNION ALL SELECT rowid,'unproved',RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),0 FROM workspace_file_chunks\"; } metadata.CommandText ="),
                "blob-alias" => ("using System.Data.Common;", "using System.Data.Common; using SqliteBlob = OtherNamespace.ForgedBlob;"),
                "scope-union" => ("metadata.CommandText =", "scopePredicates.Clear(); scopePredicates.Add(\"0) UNION ALL SELECT rowid,'unproved',RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),0 FROM workspace_file_chunks WHERE (1=1\"); metadata.CommandText ="),
                "scope-alias" => ("metadata.CommandText =", "var aliases = scopePredicates; aliases.Add(\"unproved\"); metadata.CommandText ="),
                "scope-helper" => ("=> windows\n", "=> true ? \"unproved\" : windows\n"),
                "scope-shadow" => ("List<string> scopePredicates = [];", "List<string> scopePredicates = []; string ScopePrefixPredicate(bool windows, int index) => \"unproved\";"),
                "scope-pattern" => ("scopePredicates.Add(ScopePrefixPredicate(OperatingSystem.IsWindows(), i));", "if (new Func<bool,int,string>((_, _) => \"0) UNION ALL SELECT rowid,'unproved',RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),0 FROM workspace_file_chunks WHERE (1=1\") is { } ScopePrefixPredicate) { scopePredicates.Add(ScopePrefixPredicate(OperatingSystem.IsWindows(), i)); }"),
                "scope-parameter" => ("scopePredicates.Add(ScopePrefixPredicate(OperatingSystem.IsWindows(), i));", "((Action<Func<bool,int,string>>)(ScopePrefixPredicate => { scopePredicates.Add(ScopePrefixPredicate(OperatingSystem.IsWindows(), i)); }))((_, _) => \"0) UNION ALL SELECT rowid,'unproved',RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),0 FROM workspace_file_chunks WHERE (1=1\");"),
                "authored-list" => ("internal static class GuildWorkspaceMemorySearchReader", "internal class List<T> : System.Collections.Generic.List<T> { } internal static class GuildWorkspaceMemorySearchReader"),
                "record-type" => ("string? Time = null", "object? Time = null"),
                "record-getter" => ("string? Tier = null);", "string? Tier = null) { public string? Time { get; init; } = \"unproved\"; }"),
                "type-alias" => ("using System.Data.Common;", "using System.Data.Common; using MemorySearchPosition = OtherNamespace.ForgedPosition;"),
                "global-type-alias" => ("namespace RetroDownfall.Arcanum.Core.Memory;", "global using MemorySearchPosition = OtherNamespace.ForgedPosition; namespace RetroDownfall.Arcanum.Core.Memory;"),
                _ => throw new InvalidOperationException(mutation),
            };

            if (mutation is "record-type" or "record-getter" or "global-type-alias")
            {
                changedOwner = "src/RetroDownfall.Arcanum.Core/Memory/MemorySearchContinuation.cs";
            }
        }
        else
        {
            const string predicate = "string contentPredicate = await GuildContentPredicateAsync(cancellationToken).ConfigureAwait(false);";

            string pageLoop = CSharpSyntaxTree.ParseText(sources.Single(source => source.RelativePath == owner).Text)
                .GetRoot().DescendantNodes().OfType<ForEachVariableStatementSyntax>()
                .Single(each => each.Expression.ToString() == "page"
                    && each.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText == "AddGuildCandidatesAsync")
                .ToString();

            (before, after) = mutation switch
            {
                "ordinal" => ("page.Add((reader.GetString(0), reader.IsDBNull(1) ? \"\" : reader.GetString(1)));", "page.Add((reader.GetString(0), reader.IsDBNull(0) ? \"\" : reader.GetString(0)));"),
                "source-column" => ("SELECT content.ContentId,content.CreatedAtUtc FROM guild_content content WHERE {contentPredicate} AND", "SELECT content.ContentId,content.ContentId FROM guild_content content WHERE {contentPredicate} AND"),
                "target-column" => ("content.CreatedAtUtc>@afterAt OR content.CreatedAtUtc=@afterAt", "content.ContentId>@afterAt OR content.ContentId=@afterAt"),
                "reader-command" => ("await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))\n                {\n                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))\n                    page.Add", "await using SqliteCommand other = connection.CreateCommand(); other.Transaction = transaction; other.CommandText = command.CommandText; await using (SqliteDataReader reader = await other.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))\n                {\n                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))\n                    page.Add"),
                "cursor-reassignment" => ("afterAt = created;", "afterAt = created; afterAt = \"unproved\";"),
                "cursor-ref-alias" => ("afterAt = created;", "afterAt = created; { ref string alias = ref afterAt; alias = \"unproved\"; }"),
                "cursor-in-escape" => ("afterAt = created;", "afterAt = created; Observe(in afterAt); static void Observe(in string value) { }"),
                "cursor-implicit-in" => ("afterAt = created;", "afterAt = created; Tamper(afterAt); static void Tamper(in string value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value) = \"unproved\"; }"),
                "created-implicit-in" => ("afterAt = created;", "Tamper(created); static void Tamper(in string value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value) = \"unproved\"; } afterAt = created;"),
                "cursor-seed" => ("string afterAt = \"\", afterId = \"\";", "string afterAt = \"unproved\", afterId = \"\";"),
                "binding-lambda" => ("Add(command, \"@afterAt\", afterAt);", "((Action<string>)(afterAt => { Add(command, \"@afterAt\", afterAt); }))(\"unproved\");"),
                "page-lambda" => (pageLoop, "await ((Func<List<(string Id, string Created)>, Task>)(async page => { " + pageLoop + " }))(new() { (\"00000000-0000-0000-0000-000000000001\", \"unproved\") });"),
                "predicate-union" => (predicate, "string contentPredicate = \"0 UNION ALL SELECT content.ContentId,'unproved' AS CreatedAtUtc FROM guild_content content WHERE 1=1\";"),
                "predicate-reassignment" => (predicate, predicate + " contentPredicate = \"unproved\";"),
                "predicate-implicit-in" => (predicate, predicate + " Tamper(contentPredicate); static void Tamper(in string value) { System.Runtime.CompilerServices.Unsafe.AsRef(in value) = \"0 UNION ALL SELECT content.ContentId,'unproved' AS CreatedAtUtc FROM guild_content content WHERE 1=1\"; }"),
                "predicate-helper" => ("return GuildContentRetentionPredicate;", "return \"unproved\";"),
                "predicate-shadow" => (predicate, "Task<string> GuildContentPredicateAsync(CancellationToken token) => Task.FromResult(\"unproved\"); " + predicate),
                "predicate-const" => ("content.ContentKindCode = 0 AND content.SensitivityCode = 0", "0 UNION ALL SELECT content.ContentId,'unproved' AS CreatedAtUtc FROM guild_content content WHERE content.SensitivityCode = 0"),
                "reachable-const" => ("dispatch.EvidenceCode=2", "dispatch.EvidenceCode=2 OR 'unproved'='unproved'"),
                "nested-pins" => ("private const string GuildContentCandidatePrefix", "private static class GuildQuestRetentionPins { internal const string NonAccountingUnresolved = \"unproved\"; internal const string UnresolvedPortalInbox = global::RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRetentionPins.UnresolvedPortalInbox; } private const string GuildContentCandidatePrefix"),
                "pins-alias" => ("using Microsoft.Data.Sqlite;", "using Microsoft.Data.Sqlite; using GuildQuestRetentionPins = OtherNamespace.ForgedPins;"),
                "global-pins-alias" => ("namespace RetroDownfall.Arcanum.Infrastructure.Guild;", "global using GuildQuestRetentionPins = OtherNamespace.ForgedPins; namespace RetroDownfall.Arcanum.Infrastructure.Guild;"),
                _ => throw new InvalidOperationException(mutation),
            };

            if (mutation is "reachable-const" or "global-pins-alias")
            {
                changedOwner = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestRetentionPins.cs";
            }
        }

        int index = Array.FindIndex(sources, source => source.RelativePath == changedOwner);

        Assert.True(index >= 0);

        string text = sources[index].Text;

        int searchStart = route == "retention" && mutation is not ("predicate-helper" or "predicate-const" or "reachable-const" or "pins-alias" or "global-pins-alias" or "nested-pins")
            ? text.IndexOf("private async Task AddGuildCandidatesAsync", StringComparison.Ordinal)
            : 0;

        int offset = text.IndexOf(before, searchStart, StringComparison.Ordinal);

        Assert.True(offset >= 0, $"Mutation {route}/{mutation} did not find its source.");

        sources[index] = new(changedOwner, text[..offset] + after + text[(offset + before.Length)..]);

        Assert.Contains(FindInstantBindingViolations(sources),
            violation => violation.Source == owner && violation.ParameterName == parameter);
    }

    private static string StoredTextRoute(string route) => route == "workspace"
        ? "src/RetroDownfall.Arcanum.Infrastructure/Memory/GuildWorkspaceMemorySearchReader.cs"
        : "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Guild.cs";

    // Retain only the four immutable templates. Each mutation case owns its array slots;
    // the independent whole-production guard above still reads a fresh source inventory.
    private static readonly Lazy<ProductionSource[]> StoredTextTemplates = new(() =>
    [
        .. ProductionSourceInventory.Sources().Where(source => source.IsExactOwner(StoredTextRoute("workspace"))
            || source.IsExactOwner(StoredTextRoute("retention"))
            || source.IsExactOwner("src/RetroDownfall.Arcanum.Core/Memory/MemorySearchContinuation.cs")
            || source.IsExactOwner("src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestRetentionPins.cs")),
    ], LazyThreadSafetyMode.ExecutionAndPublication);

    private static ProductionSource[] StoredTextProofSources() => [.. StoredTextTemplates.Value];

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
        StoredTextProofs rebindProofs = BuildStoredTextProofs(parsed);

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
                SqlInstantContract[] contracts = DiscoverSqlInstantContracts(scope, source.Root);
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
                            rebindProofs,
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
                        rebindProofs,
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
                    SqlInstantContract[] commandContracts = ResolveCommandContracts(
                        contracts,
                        commandKey,
                        declaration,
                        exactContractOrigin: null);
                    SqlInstantContract[] mappedContracts =
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
                            rebindProofs,
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
                        SqlInstantContract[] commandContracts = ResolveCommandContracts(
                            contracts,
                            commandKey,
                            assignment,
                            exactContractOrigin: null);
                        SqlInstantContract[] mappedContracts =
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

                foreach (SqlInstantContract contract in contracts)
                {
                    foreach (string parameterName in contract.InstantParameters)
                    {
                        if (inspected.Contains(new SqlParameterSlot(contract.Id, parameterName)))
                        {
                            continue;
                        }

                        if (!ProvesImmediatelyRetainedBinding(contract, parameterName, methods, memberTypes)
                            && !InspectTransitiveContractBinding(
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

    // SqliteCommand retains its parameter collection across execution and CommandText changes.
    // Accept only the adjacent bind/execute/reassign shape: an intervening statement could clear,
    // replace, or leak the parameter, so every other shape remains unproven.
    private static bool ProvesImmediatelyRetainedBinding(
        SqlInstantContract contract,
        string parameterName,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes)
    {
        if (contract.CommandKey is null
            || contract.Origin is not AssignmentExpressionSyntax assignment
            || assignment.Parent is not ExpressionStatementSyntax sqlStatement
            || sqlStatement.Parent is not BlockSyntax block)
        {
            return false;
        }

        int index = block.Statements.IndexOf(sqlStatement);

        if (index < 2 || block.Statements[index - 2] is not ExpressionStatementSyntax bindStatement
            || block.Statements[index - 1] is not ExpressionStatementSyntax executeStatement)
        {
            return false;
        }

        if (UnwrapDiscard(bindStatement.Expression) is not InvocationExpressionSyntax bind
            || !IsParameterBindingInvocation(bind))
        {
            return false;
        }

        SeparatedSyntaxList<ArgumentSyntax> arguments = bind.ArgumentList.Arguments;
        int parameterIndex = -1;

        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (TryReadParameterName(arguments[i].Expression, out string candidate) && candidate == parameterName)
            {
                parameterIndex = i;
                break;
            }
        }

        if (parameterIndex < 0 || BindingCommandKey(bind, parameterIndex) != contract.CommandKey
            || !IsCanonicalExpression(arguments[parameterIndex + 1].Expression, bind, methods, memberTypes, []))
        {
            return false;
        }

        ExpressionSyntax execution = UnwrapDiscard(executeStatement.Expression);

        if (execution is AwaitExpressionSyntax awaited)
        {
            execution = Unwrap(awaited.Expression);
        }

        if (execution is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "ConfigureAwait",
                    Expression: InvocationExpressionSyntax configured,
                },
            } configure
            && configure.ArgumentList.Arguments.Count == 1
            && configure.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            execution = configured;
        }

        return execution is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax member,
        } call
            && member.Name.Identifier.ValueText is "ExecuteNonQuery" or "ExecuteNonQueryAsync"
            && CommandIdentity(member.Expression) == contract.CommandKey
            && (call.ArgumentList.Arguments.Count == 0
                || call.ArgumentList.Arguments.Count == 1
                && call.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax);
    }

    private static ExpressionSyntax UnwrapDiscard(ExpressionSyntax expression)
    {
        expression = Unwrap(expression);

        if (expression is AssignmentExpressionSyntax assignment
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" })
        {
            expression = Unwrap(assignment.Right);
        }

        return expression;
    }

    private static void InspectBinding(
        ProductionSource source,
        IReadOnlyList<SqlInstantContract> contracts,
        HashSet<SqlParameterSlot> inspected,
        string parameterName,
        ExpressionSyntax value,
        SyntaxNode binding,
        string? commandKey,
        SyntaxNode? exactContractOrigin,
        bool canonicalByContract,
        IReadOnlyDictionary<string, List<MethodDeclarationSyntax>> methods,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes,
        StoredTextProofs rebindProofs,
        List<InstantBindingViolation> violations)
    {
        SqlInstantContract[] mapped = contracts
            .Where(contract => contract.InstantParameters.Contains(parameterName))
            .ToArray();

        SqlInstantContract[] provenance = ResolveCommandContracts(
            contracts,
            commandKey,
            binding,
            exactContractOrigin);
        SqlInstantContract[] resolved =
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
            MarkAlternativeQueryContracts(resolved[0], mapped, binding, parameterName, inspected);
        }
        else if (!semantic)
        {
            return;
        }

        if (canonicalByContract
            || IsExactStoredOrderingCursor(source, parameterName, value, binding, memberTypes)
            || IsReviewedStoredTextRebind(source, resolved, parameterName, value, binding, memberTypes, rebindProofs)
            || IsCanonicalExpression(value, binding, methods, memberTypes, []))
        {
            return;
        }

        AddViolation(source, value, parameterName, value.ToString(), violations);
    }

    private sealed record StoredTextProofs(bool Workspace, bool Retention);

    private static StoredTextProofs BuildStoredTextProofs(IReadOnlyList<ParsedSource> sources)
    {
        const string core = "src/RetroDownfall.Arcanum.Core/Memory/MemorySearchContinuation.cs";

        const string pins = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestRetentionPins.cs";

        bool HasAlias(string owner, params string[] names) => sources.Any(source =>
            source.Root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(alias =>
                alias.Alias is not null && names.Contains(alias.Alias.Name.Identifier.ValueText, StringComparer.Ordinal)
                && (alias.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) || source.Source.IsExactOwner(owner))));

        bool Declaration(string path, string type, string name, string expectedHash)
        {
            ParsedSource[] owners = sources.Where(source => source.Source.IsExactOwner(path)).ToArray();

            if (owners is not [ParsedSource owner]) return false;

            SyntaxNode[] declarations = owner.Root.DescendantNodes().Where(node => node switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText == name,
                FieldDeclarationSyntax field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name),
                RecordDeclarationSyntax record => record.Identifier.ValueText == name,
                _ => false,
            }).ToArray();

            if (declarations is not [SyntaxNode declaration]) return false;

            string expectedNamespace = path == core ? "RetroDownfall.Arcanum.Core.Memory"
                : path == pins ? "RetroDownfall.Arcanum.Infrastructure.Guild"
                : path == StoredTextRoute("workspace") ? "RetroDownfall.Arcanum.Infrastructure.Memory"
                : "RetroDownfall.Arcanum.Infrastructure.Data";

            string? actualNamespace = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

            string? actualType = declaration is RecordDeclarationSyntax recordType ? recordType.Identifier.ValueText
                : declaration.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

            return actualNamespace == expectedNamespace && actualType == type
                && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(declaration.DescendantTokens().Select(static token => $"{token.RawKind}:{token.Text.Length}:{token.Text}"))))) == expectedHash;
        }

        // Hashes cover token kind, token length and token text. SQL literals and raw-string contents
        // retain every byte; only surrounding C# trivia is excluded. These are nine reviewed
        // declarations, not a source-file or cursor-method exemption.
        TypeDeclarationSyntax[] authoredTypes = sources.SelectMany(source =>
            source.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()).ToArray();

        string[] externalNames = ["DbConnection", "DbCommand", "DbDataReader", "SqliteConnection", "SqliteTransaction", "SqliteCommand", "SqliteDataReader", "SqliteBlob", "List"];

        bool externalTypes = !authoredTypes.Any(type => externalNames.Contains(type.Identifier.ValueText, StringComparer.Ordinal));

        bool workspace = externalTypes && !HasAlias(StoredTextRoute("workspace"), "MemorySearchPosition", "DbConnection", "DbCommand", "DbDataReader", "SqliteConnection", "SqliteTransaction", "SqliteBlob", "List")
            && !HasAlias(core, "MemorySearchPosition")
            && authoredTypes.Count(type => type.Identifier.ValueText == "MemorySearchPosition") == 1
            && Declaration(core, "MemorySearchPosition", "MemorySearchPosition", "210F2F52DD936BAE8B2D80F5DF8EE29B87AAB39B395C5400D391DEEDC7DE5F4E")
            && Declaration(StoredTextRoute("workspace"), "GuildWorkspaceMemorySearchReader", "ScopePrefixPredicate", "0E14E155B61D4BFB33485405803DDBFDEB55304E90B677398BE5D3BA043E7765");

        bool retention = externalTypes && authoredTypes.Count(type => type.Identifier.ValueText == "GuildQuestRetentionPins") == 1
            && !HasAlias(StoredTextRoute("retention"), "GuildQuestRetentionPins", "SqliteConnection", "SqliteTransaction", "SqliteCommand", "SqliteDataReader", "List")
            && Declaration(StoredTextRoute("retention"), "DataRetentionService", "GuildContentPredicateAsync", "23619D7C2BE953D2E255858D2BA2D7FC76E90E919F21DA5ADE91A548893071AA")
            && Declaration(StoredTextRoute("retention"), "DataRetentionService", "GuildContentRetentionPredicate", "BF6BA0AE4566EECAAAF49BDF148D43E9C6D0E03BBA4F4816C939EAE11B6EB7AF")
            && Declaration(pins, "GuildQuestRetentionPins", "PhysicalWorkUnresolved", "325245A08D9ED40F0CCFEC8F15C9C8676C98AB4576E393F0349577AF87AEEA08")
            && Declaration(pins, "GuildQuestRetentionPins", "ExternalWorkUnresolved", "899AED8298BB7941498823A748E600E60C546B5779AAF379DF615EB03882B5C9")
            && Declaration(pins, "GuildQuestRetentionPins", "StoppedExecutionUnresolved", "419578C78F04149314C89E3D8CEA3CACCBECB6C6A23449A8CEFF7874B7813843")
            && Declaration(pins, "GuildQuestRetentionPins", "UnresolvedPortalInbox", "26F0DC70E041A4CA439364731D0617B14813E7C104AC052211CA38375FB00105")
            && Declaration(pins, "GuildQuestRetentionPins", "NonAccountingUnresolved", "57FE9CE83C3A201EDD15CA28021988488F2B19C5ED79D52EDEB49548F5ABEA83");

        return new(workspace, retention);
    }

    private static string RebindTokens(SyntaxNode node) =>
        string.Concat(node.DescendantTokens().Select(static token => $"{token.Text.Length}:{token.Text}"));

    private static string RebindTokens(string text)
    {
        CSharpParseOptions options = new(LanguageVersion.Preview);

        ExpressionSyntax expression = SyntaxFactory.ParseExpression(text, options: options);

        if (!expression.ContainsDiagnostics) return RebindTokens(expression);

        bool statementText = text.TrimEnd().EndsWith(';');

        StatementSyntax statement = SyntaxFactory.ParseStatement(statementText ? text : text + ";", options: options);

        if (!statement.ContainsDiagnostics)
        {
            return !statementText && statement is LocalDeclarationStatementSyntax local
                ? RebindTokens(local.Declaration)
                : RebindTokens(statement);
        }

        return string.Concat(SyntaxFactory.ParseTokens(text).Where(static token => !token.IsKind(SyntaxKind.EndOfFileToken))
            .Select(static token => $"{token.Text.Length}:{token.Text}"));
    }

    private static bool IsRebindExecutionRegion(SyntaxNode node, SyntaxNode scope)
    {
        MethodDeclarationSyntax? method = scope as MethodDeclarationSyntax
            ?? scope.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

        return method is not null && method.Span.Contains(node.Span)
            && !node.AncestorsAndSelf().TakeWhile(ancestor => !ReferenceEquals(ancestor, method))
                .Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
    }

    private static T? ExactRebindNode<T>(SyntaxNode scope, string text) where T : SyntaxNode
    {
        T[] matches = scope.DescendantNodesAndSelf().OfType<T>()
            .Where(node => IsRebindExecutionRegion(node, scope) && RebindTokens(node) == RebindTokens(text)).ToArray();

        return matches is [T match] ? match : null;
    }

    private static bool RebindBefore(SyntaxNode? first, SyntaxNode? second) =>
        first is not null && second is not null && first.Span.End <= second.SpanStart;

    private static bool RebindContains(SyntaxNode? outer, SyntaxNode? inner) =>
        outer is not null && inner is not null && outer.Span.Contains(inner.Span);

    // Include writes nested in lambdas/local functions: either can change a captured cursor later.
    // An assignment's entire left-hand side is checked, including tuple/deconstruction and index writes.
    // Ref-local creation itself is an escape even when the later write names only the alias.
    private static bool RebindWritesAreClosed(SyntaxNode scope, string[] names, params string[] expected)
    {
        HashSet<string> roots = new(names, StringComparer.Ordinal);

        bool Touches(SyntaxNode target) => target.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>().Any(id => roots.Contains(id.Identifier.ValueText));

        string[] actual = scope.DescendantNodesAndSelf().Where(node => node switch
        {
            VariableDeclaratorSyntax declaration => roots.Contains(declaration.Identifier.ValueText),
            AssignmentExpressionSyntax assignment => Touches(assignment.Left),
            PrefixUnaryExpressionSyntax prefix when prefix.Kind() is SyntaxKind.PreIncrementExpression
                or SyntaxKind.PreDecrementExpression => Touches(prefix.Operand),
            PostfixUnaryExpressionSyntax postfix when postfix.Kind() is SyntaxKind.PostIncrementExpression
                or SyntaxKind.PostDecrementExpression => Touches(postfix.Operand),
            RefExpressionSyntax reference => Touches(reference.Expression),
            MakeRefExpressionSyntax reference => Touches(reference.Expression),
            ArgumentSyntax argument when argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword
                or SyntaxKind.OutKeyword or SyntaxKind.InKeyword => Touches(argument.Expression),
            _ => false,
        }).Select(RebindTokens).Order(StringComparer.Ordinal).ToArray();

        return actual.SequenceEqual(expected.Select(RebindTokens).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool RebindReadsAreClosed(SyntaxNode scope, string name, params SyntaxNode?[] allowed) =>
        allowed.All(node => node is not null)
            && scope.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(identifier => identifier.Identifier.ValueText == name)
                .All(identifier => allowed.Any(node => node!.Span.Contains(identifier.Span)));

    private static bool RebindCandidateIsClosed(ForEachStatementSyntax each)
    {
        AssignmentExpressionSyntax? cursor = ExactRebindNode<AssignmentExpressionSyntax>(each, "cursor = candidate.Position");

        InvocationExpressionSyntax? hit = ExactRebindNode<InvocationExpressionSyntax>(each, WorkspaceHitAddition);

        if (cursor is null || hit is null) return false;

        foreach (IdentifierNameSyntax identifier in each.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => identifier.Identifier.ValueText == "candidate"))
        {
            if (identifier.Parent is not MemberAccessExpressionSyntax member || !ReferenceEquals(member.Expression, identifier)) return false;

            if (member.Name.Identifier.ValueText is "Row" or "Offset" or "Length" or "Bytes") continue;

            if (member.Name.Identifier.ValueText != "Position") return false;

            if (member.Parent is MemberAccessExpressionSyntax property && ReferenceEquals(property.Expression, member)
                && property.Name.Identifier.ValueText is "Time" or "Path" or "Number" or "Id") continue;

            if (ReferenceEquals(cursor.Right, member) || hit.Span.Contains(member.Span)) continue;

            return false;
        }

        return true;
    }

    // A collection cannot be aliased, passed to an unknown helper, sorted, replaced or mutated by
    // another receiver call. Count is observation only; its one Add and foreach are proved separately.
    private static bool RebindCollectionIsClosed(SyntaxNode scope, string name,
        InvocationExpressionSyntax add, ForEachStatementSyntax each)
    {
        foreach (IdentifierNameSyntax id in scope.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == name))
        {
            if (add.Span.Contains(id.Span) || ReferenceEquals(each.Expression, id)) continue;

            if (id.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Expression, id)
                && member.Name.Identifier.ValueText == "Count") continue;

            return false;
        }

        return true;
    }

    // Read-only receiver uses only. This prevents reader alias escape / NextResult / another command
    // being smuggled into an otherwise correctly spelled GetString(ordinal) construction.
    private static bool RebindReaderIsClosed(SyntaxNode scope, string name)
    {
        foreach (IdentifierNameSyntax id in scope.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == name))
        {
            if (id.Parent is not MemberAccessExpressionSyntax member || !ReferenceEquals(member.Expression, id)
                || member.Parent is not InvocationExpressionSyntax
                || member.Name.Identifier.ValueText is not ("ReadAsync" or "GetString" or "GetInt64" or "IsDBNull"))
                return false;
        }

        return true;
    }

    // Only reviewed assignments, the existing Bind helper, and ExecuteReaderAsync may use a protected
    // command. Unknown helpers/aliases and direct Parameters mutations do not inherit this proof.
    private static bool RebindCommandIsClosed(SyntaxNode scope, string name)
    {
        foreach (IdentifierNameSyntax id in scope.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == name))
        {
            if (id.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Expression, id)
                && (member.Name.Identifier.ValueText is "Transaction" or "CommandText"
                    && member.Parent is AssignmentExpressionSyntax assignment && ReferenceEquals(assignment.Left, member)
                    || member.Name.Identifier.ValueText == "ExecuteReaderAsync" && member.Parent is InvocationExpressionSyntax))
                continue;

            if (id.Parent is ArgumentSyntax argument
                && argument.Parent is ArgumentListSyntax list && list.Arguments.IndexOf(argument) == 0
                && list.Parent is InvocationExpressionSyntax call
                && call.Expression is IdentifierNameSyntax { Identifier.ValueText: "Bind" or "Add" }) continue;

            return false;
        }

        return true;
    }

    private static bool HasOneStringTime(IReadOnlyDictionary<string, HashSet<string>> members) =>
        members.TryGetValue("MemorySearchPosition.Time", out HashSet<string>? types)
            && types.Count == 1 && NormalizeType(types.Single()) == "string";

    private static bool IsReviewedStoredTextRebind(ProductionSource source,
        IReadOnlyList<SqlInstantContract> resolved, string parameter, ExpressionSyntax value,
        SyntaxNode binding, IReadOnlyDictionary<string, HashSet<string>> members, StoredTextProofs proofs)
    {
        if (resolved.Count != 1 || !resolved[0].InstantParameters.Contains(parameter)
            || binding is not InvocationExpressionSyntax call
            || binding.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { } method)
            return false;

        if (!IsRebindExecutionRegion(binding, method)) return false;

        string? owner = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

        if (source.IsExactOwner("src/RetroDownfall.Arcanum.Infrastructure/Memory/GuildWorkspaceMemorySearchReader.cs")
            && owner == "GuildWorkspaceMemorySearchReader" && method.Identifier.ValueText == "ReadAsync"
            && parameter == "@time" && resolved[0].CommandKey == "hydrate"
            && RebindTokens(value) == RebindTokens("candidate.Position.Time!") && HasOneStringTime(members))
            return proofs.Workspace && ProveWorkspaceFragments(method) && ProveWorkspaceStoredTime(method, call);

        if (source.IsExactOwner("src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.Guild.cs")
            && owner == "DataRetentionService" && method.Identifier.ValueText == "AddGuildCandidatesAsync"
            && parameter == "@afterAt" && resolved[0].CommandKey == "command"
            && RebindTokens(value) == RebindTokens("afterAt"))
            return proofs.Retention && ProveRetentionFragments(method) && ProveRetentionStoredTime(method, call);

        return false;
    }

    private static bool HasRebindHelperShadow(MethodDeclarationSyntax method, params string[] names) =>
        method.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken)
            && names.Contains(token.ValueText, StringComparer.Ordinal)
            && !(token.Parent is IdentifierNameSyntax identifier
                && (identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Expression, identifier)
                    || identifier.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Name, identifier))));

    private const string WorkspaceAfterAssignment = """
    after = " AND (IndexedAt<@time OR (IndexedAt=@time AND (RelativePath>@path OR (RelativePath=@path AND (ChunkIndex>@number OR (ChunkIndex=@number AND ChunkId>@id))))))"
    """;

    private static bool ProveWorkspaceFragments(MethodDeclarationSyntax method)
    {
        if (HasRebindHelperShadow(method, "ScopePrefixPredicate", "Bind")
            || !RebindWritesAreClosed(method, ["after", "scopePredicates"],
                "after = \"\"", WorkspaceAfterAssignment, "scopePredicates = []")) return false;

        InvocationExpressionSyntax? all = ExactRebindNode<InvocationExpressionSyntax>(method, "scopePredicates.Add(\"1=1\")");

        InvocationExpressionSyntax? prefix = ExactRebindNode<InvocationExpressionSyntax>(method,
            "scopePredicates.Add(ScopePrefixPredicate(OperatingSystem.IsWindows(), i))");

        InvocationExpressionSyntax? join = ExactRebindNode<InvocationExpressionSyntax>(method,
            "string.Join(\" OR \", scopePredicates)");

        if (all is null || prefix is null || join is null) return false;

        return RebindReadsAreClosed(method, "after",
                ExactRebindNode<AssignmentExpressionSyntax>(method, WorkspaceAfterAssignment),
                ExactRebindNode<AssignmentExpressionSyntax>(method, WorkspaceMetadataAssignment.Trim().TrimEnd(';')))
            && method.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(identifier => identifier.Identifier.ValueText == "scopePredicates")
            .All(identifier => all.Span.Contains(identifier.Span) || prefix.Span.Contains(identifier.Span)
                || join.Span.Contains(identifier.Span));
    }

    private static bool ProveRetentionFragments(MethodDeclarationSyntax method) =>
        !HasRebindHelperShadow(method, "GuildContentPredicateAsync", "GuildQuestRetentionPins", "Add")
            && ExactRebindNode<LocalDeclarationStatementSyntax>(method,
                "string contentPredicate = await GuildContentPredicateAsync(cancellationToken).ConfigureAwait(false);") is not null
            && RebindWritesAreClosed(method, ["contentPredicate"],
                "contentPredicate = await GuildContentPredicateAsync(cancellationToken).ConfigureAwait(false)")
            && RebindReadsAreClosed(method, "contentPredicate",
                ExactRebindNode<ExpressionStatementSyntax>(method, RetentionCursorAssignment));

    private const string WorkspaceHitAddition = """
    hits.Add(new(new(MemorySearchScope.Workspace, $"{candidate.Position.Path} lines {first}-{last}", excerpt,
                        $"Workspace {workspace.Label}, relative file {candidate.Position.Path}, chunk {candidate.Position.Number}",
                        "Derived and rebuildable from registered workspace files; not session memory.", candidate.Position.Id!), candidate.Position))
    """;

    private const string WorkspaceMetadataAssignment = """
    metadata.CommandText = "SELECT rowid,IndexedAt,RelativePath,ChunkIndex,ChunkId,CharOffset,CharLength,octet_length(Content),coalesce(octet_length(IndexedAt),0)+coalesce(octet_length(RelativePath),0)+coalesce(octet_length(ChunkId),0) FROM workspace_file_chunks WHERE WorkspacePath=@root AND ("
        + string.Join(" OR ", scopePredicates) + ")" + after
        + " ORDER BY IndexedAt DESC,RelativePath ASC,ChunkIndex ASC,ChunkId ASC LIMIT 513";
    """;

    private const string WorkspaceHydrateAssignment = """
    hydrate.CommandText = "SELECT StartLine,EndLine FROM workspace_file_chunks WHERE rowid=@row AND WorkspacePath=@root AND RelativePath=@path AND IndexedAt=@time AND ChunkIndex=@number AND ChunkId=@id AND octet_length(Content)=@bytes AND (arcanum_ordinal_contains(Content,@query) OR arcanum_ordinal_contains(RelativePath,@query))";
    """;

    private static bool ProveWorkspaceStoredTime(MethodDeclarationSyntax method, InvocationExpressionSyntax binding)
    {
        StatementSyntax? snapshot = ExactRebindNode<LocalDeclarationStatementSyntax>(method,
            "await using SqliteTransaction snapshot = ((SqliteConnection)connection).BeginTransaction(deferred: true);");
        StatementSyntax? metadata = ExactRebindNode<LocalDeclarationStatementSyntax>(method,
            "await using DbCommand metadata = connection.CreateCommand();");
        StatementSyntax? candidates = ExactRebindNode<LocalDeclarationStatementSyntax>(method,
            "List<(long Row, MemorySearchPosition Position, long Offset, long Length, long Bytes)> candidates = [];");
        AssignmentExpressionSyntax? query = ExactRebindNode<AssignmentExpressionSyntax>(method,
            WorkspaceMetadataAssignment.Trim().TrimEnd(';'));
        UsingStatementSyntax[] reads = method.DescendantNodes().OfType<UsingStatementSyntax>()
            .Where(node => IsRebindExecutionRegion(node, method) && node.Declaration is not null && RebindTokens(node.Declaration)
                == RebindTokens("DbDataReader reader = await metadata.ExecuteReaderAsync(ct).ConfigureAwait(false)")).ToArray();
        StatementSyntax? position = ExactRebindNode<LocalDeclarationStatementSyntax>(method,
            "MemorySearchPosition position = new(3, reader.GetString(1), reader.GetInt64(3), reader.GetString(2), reader.GetString(4));");
        InvocationExpressionSyntax? add = ExactRebindNode<InvocationExpressionSyntax>(method,
            "candidates.Add((reader.GetInt64(0), position, reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)))");
        ForEachStatementSyntax? each = binding.Ancestors().OfType<ForEachStatementSyntax>()
            .FirstOrDefault(node => node.Identifier.ValueText == "candidate" && RebindTokens(node.Expression) == RebindTokens("candidates")
                && node.Type.IsVar);

        if (snapshot is null || metadata is null || candidates is null || query is null
            || reads is not [UsingStatementSyntax read] || position is null || add is null || each is null
            || !RebindBefore(snapshot, metadata) || !RebindBefore(metadata, query) || !RebindBefore(query, read) || !RebindBefore(candidates, read)
            || !RebindContains(read, position) || !RebindContains(read, add) || !RebindBefore(position, add)
            || !RebindBefore(read, each) || !RebindContains(each, binding)) return false;

        if (ExactRebindNode<LocalDeclarationStatementSyntax>(each, "await using DbCommand hydrate = connection.CreateCommand();") is null
            || ExactRebindNode<ExpressionStatementSyntax>(method, "metadata.Transaction = snapshot;") is null
            || ExactRebindNode<ExpressionStatementSyntax>(each, "hydrate.Transaction = snapshot;") is null
            || ExactRebindNode<ExpressionStatementSyntax>(each, WorkspaceHydrateAssignment) is null
            || RebindTokens(binding) != RebindTokens("Bind(hydrate, \"@time\", candidate.Position.Time!)")) return false;

        // All writes are considered, including writes after this bind, not just preceding lexical spans.
        return RebindWritesAreClosed(method, ["connection", "snapshot", "candidates", "position", "candidate", "metadata", "hydrate", "reader"],
            "snapshot = ((SqliteConnection)connection).BeginTransaction(deferred: true)",
            "candidates = []", "position = new(3, reader.GetString(1), reader.GetInt64(3), reader.GetString(2), reader.GetString(4))",
            "metadata = connection.CreateCommand()", "metadata.Transaction = snapshot", WorkspaceMetadataAssignment.Trim().TrimEnd(';'),
            "reader = await metadata.ExecuteReaderAsync(ct).ConfigureAwait(false)", "hydrate = connection.CreateCommand()",
            "hydrate.Transaction = snapshot", WorkspaceHydrateAssignment.Trim().TrimEnd(';'))
            && RebindReadsAreClosed(method, "position", add)
            && RebindReadsAreClosed(method, "snapshot",
                ExactRebindNode<ExpressionStatementSyntax>(method, "metadata.Transaction = snapshot;"),
                ExactRebindNode<ExpressionStatementSyntax>(each, "hydrate.Transaction = snapshot;"))
            && RebindReadsAreClosed(method, "connection", snapshot, metadata,
                ExactRebindNode<LocalDeclarationStatementSyntax>(each, "await using DbCommand hydrate = connection.CreateCommand();"),
                ExactRebindNode<ImplicitObjectCreationExpressionSyntax>(each,
                    "new((SqliteConnection)connection, \"workspace_file_chunks\", \"Content\", candidate.Row, readOnly: true)"))
            && RebindCandidateIsClosed(each)
            && RebindCollectionIsClosed(method, "candidates", add, each)
            && RebindReaderIsClosed(read, "reader")
            && RebindCommandIsClosed(method, "metadata") && RebindCommandIsClosed(each, "hydrate")
            && each.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(node =>
                node.ArgumentList.Arguments.Count >= 2 && RebindTokens(node.ArgumentList.Arguments[0].Expression) == RebindTokens("hydrate")
                    && node.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax literal
                    && literal.Token.ValueText == "@time") == 1;
    }

    private const string RetentionCursorAssignment = """
    command.CommandText = $"SELECT content.ContentId,content.CreatedAtUtc FROM guild_content content WHERE {contentPredicate} AND (content.CreatedAtUtc>@afterAt OR content.CreatedAtUtc=@afterAt AND content.ContentId>@afterId) ORDER BY content.CreatedAtUtc,content.ContentId LIMIT 100";
    """;

    private static bool ProveRetentionStoredTime(MethodDeclarationSyntax method, InvocationExpressionSyntax binding)
    {
        WhileStatementSyntax? loop = binding.Ancestors().OfType<WhileStatementSyntax>().FirstOrDefault();

        if (loop is null || loop.Parent is not BlockSyntax branch
            || RebindTokens(binding) != RebindTokens("Add(command, \"@afterAt\", afterAt)")
            || ExactRebindNode<LocalDeclarationStatementSyntax>(method, "string afterAt = \"\", afterId = \"\";") is not { } seed
            || !RebindBefore(seed, loop)) return false;

        LocalDeclarationStatementSyntax? transaction = ExactRebindNode<LocalDeclarationStatementSyntax>(branch,
            "await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);");

        if (transaction is null || !RebindBefore(transaction, seed)
            || !RebindWritesAreClosed(branch, ["transaction"], "transaction = connection.BeginTransaction(deferred: true)")
            || !RebindWritesAreClosed(method, ["connection"],
                "connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false)")) return false;

        LocalDeclarationStatementSyntax? command = ExactRebindNode<LocalDeclarationStatementSyntax>(loop,
            "await using SqliteCommand command = connection.CreateCommand();");
        ExpressionStatementSyntax? query = ExactRebindNode<ExpressionStatementSyntax>(loop, RetentionCursorAssignment);
        LocalDeclarationStatementSyntax? page = ExactRebindNode<LocalDeclarationStatementSyntax>(loop,
            "List<(string Id, string Created)> page = [];");
        UsingStatementSyntax[] reads = loop.DescendantNodes().OfType<UsingStatementSyntax>()
            .Where(node => IsRebindExecutionRegion(node, method) && node.Declaration is not null && RebindTokens(node.Declaration)
                == RebindTokens("SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)")).ToArray();
        InvocationExpressionSyntax? add = ExactRebindNode<InvocationExpressionSyntax>(loop,
            "page.Add((reader.GetString(0), reader.IsDBNull(1) ? \"\" : reader.GetString(1)))");
        ForEachVariableStatementSyntax[] loops = loop.DescendantNodes().OfType<ForEachVariableStatementSyntax>()
            .Where(node => IsRebindExecutionRegion(node, method)
                && RebindTokens(node.Variable) == RebindTokens("(string id, string created)")
                && RebindTokens(node.Expression) == RebindTokens("page")).ToArray();

        if (command is null || query is null || page is null || reads is not [UsingStatementSyntax read]
            || add is null || loops is not [ForEachVariableStatementSyntax each]
            || !RebindBefore(command, query) || !RebindBefore(query, binding) || !RebindBefore(binding, page)
            || !RebindBefore(page, read) || !RebindContains(read, add) || !RebindBefore(read, each)
            || ExactRebindNode<ExpressionStatementSyntax>(loop, "command.Transaction = transaction;") is null
            || ExactRebindNode<ExpressionStatementSyntax>(each, "afterAt = created;") is null) return false;

        ExpressionStatementSyntax? advance = ExactRebindNode<ExpressionStatementSyntax>(each, "afterAt = created;");

        if (!RebindReadsAreClosed(method, "afterAt", binding, advance)
            || !RebindReadsAreClosed(each, "created", advance)) return false;

        // Whole-method afterAt write closure catches a later write used by the next loop iteration.
        if (!RebindWritesAreClosed(method, ["afterAt"], "afterAt = \"\"", "afterAt = created")) return false;

        if (!RebindWritesAreClosed(loop, ["created", "page", "command", "reader"],
            "page = []", "command = connection.CreateCommand()", "command.Transaction = transaction",
            RetentionCursorAssignment.Trim().TrimEnd(';'),
            "reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)")) return false;

        // Same closure as the workspace collection, adapted to deconstructing foreach.
        foreach (IdentifierNameSyntax id in loop.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(node => node.Identifier.ValueText == "page"))
        {
            if (add.Span.Contains(id.Span) || ReferenceEquals(each.Expression, id)) continue;
            if (id.Parent is MemberAccessExpressionSyntax member && ReferenceEquals(member.Expression, id)
                && member.Name.Identifier.ValueText == "Count") continue;
            return false;
        }

        return RebindReaderIsClosed(read, "reader") && RebindCommandIsClosed(loop, "command")
            && loop.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(node =>
                node.ArgumentList.Arguments.Count >= 2 && RebindTokens(node.ArgumentList.Arguments[0].Expression) == RebindTokens("command")
                    && node.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax literal
                    && literal.Token.ValueText == "@afterAt") == 1;
    }

    private static bool IsExactStoredOrderingCursor(
        ProductionSource source,
        string parameterName,
        ExpressionSyntax value,
        SyntaxNode binding,
        IReadOnlyDictionary<string, HashSet<string>> memberTypes) =>
        source.IsExactOwner("src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs")
            && value.ToString() == "after.CreatedAt"
            // Both cursor paths bind the exact stored ordering text carried by SagaMemoryPosition.
            // No other method/parameter pair or DateTimeOffset value inherits this exception.
            && (binding.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()
                ?.Identifier.ValueText, parameterName) is ("ListPositionsAfterAsync", "@afterCreatedText") or ("BuildListCommand", "@afterTime")
            && InferExpressionType(value, binding, memberTypes) == "string";

    private static void MarkAlternativeQueryContracts(
        SqlInstantContract resolved,
        IReadOnlyList<SqlInstantContract> mapped,
        SyntaxNode binding,
        string parameterName,
        HashSet<SqlParameterSlot> inspected)
    {
        IfStatementSyntax? alternatives = resolved.Origin.Ancestors().OfType<IfStatementSyntax>()
            .Where(branch => branch.Span.End < binding.SpanStart)
            .LastOrDefault();

        if (alternatives is null
            || binding.Ancestors().First(IsExecutableScope).DescendantNodes()
                .OfType<InvocationExpressionSyntax>().Any(invocation =>
                    invocation.SpanStart > alternatives.SpanStart
                    && invocation.SpanStart < binding.SpanStart
                    && invocation.Expression is MemberAccessExpressionSyntax execution
                    && CommandIdentity(execution.Expression) == resolved.CommandKey
                    && execution.Name.Identifier.ValueText.StartsWith("Execute", StringComparison.Ordinal))
            || binding.SyntaxTree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(assignment => assignment.SpanStart > alternatives.Span.End
                    && assignment.SpanStart < binding.SpanStart
                    && assignment.Left is MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "CommandText",
                        Expression: ExpressionSyntax command,
                    }
                    && CommandIdentity(command) == resolved.CommandKey))
        {
            return;
        }

        MarkInspected(mapped.Where(contract => contract.CommandKey == resolved.CommandKey
            && alternatives.Span.Contains(contract.Origin.Span)), parameterName, inspected);
    }

    private static SqlInstantContract[] ResolveCommandContracts(
        IReadOnlyList<SqlInstantContract> contracts,
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

        SqlInstantContract[] preceding =
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
        IEnumerable<SqlInstantContract> contracts,
        string parameterName,
        HashSet<SqlParameterSlot> inspected)
    {
        foreach (SqlInstantContract contract in contracts)
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
        SqlInstantContract contract,
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

    private static SqlInstantContract[] DiscoverSqlInstantContracts(SyntaxNode scope, SyntaxNode root)
    {
        List<SqlInstantContract> contracts = [];

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

            AddSqlInstantContract(
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

                AddSqlInstantContract(
                    contracts,
                    InvocationSqlCommandKey(invocation),
                    invocation,
                    argumentIndex,
                    sql);
            }
        }

        return [.. contracts];
    }

    private static void AddSqlInstantContract(
        List<SqlInstantContract> contracts,
        string? commandKey,
        SyntaxNode origin,
        int? sqlArgumentIndex,
        string sql)
    {
        if (!IsSqlStatement(sql))
        {
            return;
        }

        HashSet<string> parameters = DiscoverSqlMappedInstantParameters(sql);

        contracts.Add(new SqlInstantContract(
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
                || literal.Token.ValueText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                || literal.Token.ValueText.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
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
            || name is "now" or "before";
    }

    private static HashSet<string> DiscoverSqlMappedInstantParameters(string sql)
    {
        HashSet<string> instantColumns = new(
            UtcInstantColumnInventory.Core.SelectMany(static table => table.Columns)
                .Concat(UtcInstantColumnInventory.CovenantCanonical.SelectMany(static table => table.Columns)),
            StringComparer.OrdinalIgnoreCase);

        HashSet<string> parameters = new(StringComparer.Ordinal);

        if (!IsSqlStatement(sql))
        {
            return parameters;
        }

        const string column = "(?:[A-Za-z_][A-Za-z0-9_]*\\.)?\\\"?(?<column>[A-Za-z][A-Za-z0-9_]*)\\\"?";
        const string parameter = "(?<parameter>[@$:][A-Za-z][A-Za-z0-9_]*)";
        const string comparison = "(?:<=|>=|<>|!=|=|<|>|IS(?:\\s+NOT)?)";

        string[] predicates =
        [
            column + "\\s*" + comparison + "\\s*" + parameter,
            parameter + "\\s*" + comparison + "\\s*" + column,
            column + "\\s+BETWEEN\\s+" + parameter + "\\s+AND\\s+(?<end>[@$:][A-Za-z][A-Za-z0-9_]*)",
        ];

        foreach (string predicate in predicates)
        {
            foreach (Match match in Regex.Matches(
                         sql,
                         predicate,
                         RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                if (instantColumns.Contains(match.Groups["column"].Value))
                {
                    _ = parameters.Add(match.Groups["parameter"].Value);

                    if (match.Groups["end"].Success)
                    {
                        _ = parameters.Add(match.Groups["end"].Value);
                    }
                }
            }
        }

        AddInsertParameters(sql, instantColumns, parameters);

        return parameters;
    }

    private static bool IsSqlStatement(string sql) =>
        Regex.IsMatch(sql, @"\b(?:INSERT|UPDATE|SELECT|DELETE)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

            if (name is "UtcInstantText.Format" or "UtcInstantText.Normalize"
                || invocation.Expression.ToString() is "RetroDownfall.Arcanum.Infrastructure.Data.UtcInstantText.Format"
                    or "RetroDownfall.Arcanum.Infrastructure.Data.UtcInstantText.Normalize")
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
                && SyntaxFactory.ParseTypeName(NormalizeType(ownerType)!) is TupleTypeSyntax tuple)
            {
                TupleElementSyntax[] elements =
                [
                    .. tuple.Elements.Where(element => element.Identifier.ValueText == member.Name.Identifier.ValueText),
                ];

                // A tuple cursor's declared element type selects the formatter overload. Missing or
                // ambiguous names remain unproven; the member's spelling alone never implies an instant.
                return elements.Length == 1 ? elements[0].Type.ToString() : null;
            }

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

    private sealed record SqlInstantContract(
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

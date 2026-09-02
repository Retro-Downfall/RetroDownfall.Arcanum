using System.Data;

using System.Net;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The retention deletion routes, driven through HTTP against a labelled artifact.
/// </summary>
/// <remarks>
/// A test that constructs <c>ICovenantLabeledArtifactGuard</c> and calls it proves the guard's own
/// logic and nothing about whether a production delete route reaches it. These enter through
/// <c>POST /api/data/prune</c>, <c>DELETE /api/data/sessions/{id}</c> and
/// <c>POST /api/data/memory/reset</c>, seed the label through the production
/// <see cref="IArtifactSensitivityLedger"/>, and assert the artifact is still there afterwards.
///
/// <para>§10.20.2 fixes what "afterwards" has to mean: <c>Blocked</c> means the artifact is still
/// there and must stay, and the route refuses rather than reporting a deletion that did not happen.
/// A prune is the one arm that skips rather than refuses, because a sweep selects many candidates
/// and one protected member is a reason to leave that member alone, not to abandon the sweep.</para>
/// </remarks>
[Collection("ApiHost")]

[Trait("Category", "Integration")]

public sealed class CovenantLabeledRetentionRouteTests
{

    private static readonly Guid Generation = Guid.Parse("5E6F7081-92A3-4B5C-8D9E-0F1A2B3C4D5E");

    /// <summary>
    /// A labelled assistant Entry the entry-retention rule selects is left where it is.
    /// </summary>
    /// <remarks>
    /// Before the guard reached the prune this entry vanished with its <c>artifact_sensitivity</c>
    /// row still pointing at it, which is the one integrity state indistinguishable from data loss.
    /// </remarks>
    [SkippableFact]

    public async Task A_labeled_entry_survives_the_retention_prune_route()
    {

        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {

            SqliteConnection connection = await OpenAsync(scope);

            await SeedSessionAsync(connection, sessionId);

            await SeedEntryAsync(connection, sessionId, entryId);

            await LabelAsync(
                scope,
                SensitiveArtifactKind.AssistantEntry,
                entryId,
                sessionId);

        }

        HttpResponseMessage enabled = await client.PutAsync(
            "/api/data/retention",
            Json(
                new RetentionRuleUpdateRequest("entries", true, 1),
                ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        HttpResponseMessage pruned = await client.PostAsync(
            "/api/data/prune",
            Json(
                new DataRetentionApplyRequest(
                    new DataRetentionRequest(DataRetentionOperation.Prune)),
                ArcanumJsonContext.Default.DataRetentionApplyRequest));

        Assert.Equal(HttpStatusCode.OK, pruned.StatusCode);

        await using AsyncServiceScope after = factory.Services.CreateAsyncScope();

        SqliteConnection verify = await OpenAsync(after);

        Assert.Equal(1, await CountAsync(verify, "Entries", "Id", entryId));

        Assert.Equal(1, await CountAsync(verify, "artifact_sensitivity", "ArtifactId", entryId));

    }

    /// <summary>
    /// A Session holding a labelled Entry cannot be removed through the bulk session delete.
    /// </summary>
    /// <remarks>
    /// The single-entry route already dispatches through the purge boundary; this one removed every
    /// assistant Entry of a Session with one set-based statement that had never heard of a label.
    /// </remarks>
    [SkippableFact]

    public async Task A_labeled_entry_refuses_the_session_delete_route()
    {

        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {

            SqliteConnection connection = await OpenAsync(scope);

            await SeedSessionAsync(connection, sessionId);

            await SeedEntryAsync(connection, sessionId, entryId);

            await LabelAsync(
                scope,
                SensitiveArtifactKind.AssistantEntry,
                entryId,
                sessionId);

        }

        HttpResponseMessage deleted = await client.DeleteAsync(
            $"/api/data/sessions/{sessionId:D}");

        ApiResponse<DataRetentionApplyResult> body = await ReadAsync(deleted);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, body.Error?.Code);

        await using AsyncServiceScope after = factory.Services.CreateAsyncScope();

        SqliteConnection verify = await OpenAsync(after);

        Assert.Equal(1, await CountAsync(verify, "Entries", "Id", entryId));

        Assert.Equal(1, await CountAsync(verify, "Sessions", "Id", sessionId));

    }

    /// <summary>
    /// One labelled Saga memory refuses the untargeted whole-store reset.
    /// </summary>
    /// <remarks>
    /// The bulk arm exists for exactly this statement: a bare <c>DELETE FROM saga_memories</c>
    /// examines no identity, so no per-artifact check can see the rows it never enumerated.
    /// </remarks>
    [SkippableFact]

    public async Task A_labeled_saga_memory_refuses_the_untargeted_memory_reset_route()
    {

        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid memoryId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {

            SqliteConnection connection = await OpenAsync(scope);

            await SeedSagaMemoryAsync(connection, memoryId);

            await LabelAsync(
                scope,
                SensitiveArtifactKind.Saga,
                memoryId,
                sessionId: null);

        }

        HttpResponseMessage reset = await client.PostAsync(
            "/api/data/memory/reset",
            Json(
                new MemoryResetRequest(MemoryResetScope.Saga),
                ArcanumJsonContext.Default.MemoryResetRequest));

        ApiResponse<DataRetentionApplyResult> body = await ReadAsync(reset);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, body.Error?.Code);

        await using AsyncServiceScope after = factory.Services.CreateAsyncScope();

        SqliteConnection verify = await OpenAsync(after);

        Assert.Equal(1, await CountAsync(verify, "saga_memories", "Id", memoryId));

        Assert.Equal(1, await CountAsync(verify, "artifact_sensitivity", "ArtifactId", memoryId));

    }

    /// <summary>
    /// The prune's Saga and Lexicon candidates reach the guard the way its entry candidates do.
    /// </summary>
    /// <remarks>
    /// Both stores name their candidates by the stored identity string rather than by a
    /// <see cref="Guid"/>, so "the guard is wired" and "the guard is asked about the identity the
    /// label is keyed on" are two different claims. This asserts the second one, through the route.
    /// </remarks>
    [SkippableTheory]

    [InlineData("saga")]

    [InlineData("lexicon")]

    public async Task A_labeled_memory_survives_the_retention_prune_route(string store)
    {

        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid artifactId = Guid.NewGuid();

        bool saga = string.Equals(store, "saga", StringComparison.Ordinal);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {

            SqliteConnection connection = await OpenAsync(scope);

            if (saga)
            {

                await SeedSagaMemoryAsync(connection, artifactId);

            }
            else
            {

                await SeedLexiconEntryAsync(connection, artifactId);

            }

            await LabelAsync(
                scope,
                saga ? SensitiveArtifactKind.Saga : SensitiveArtifactKind.Lexicon,
                artifactId,
                sessionId: null);

        }

        HttpResponseMessage enabled = await client.PutAsync(
            "/api/data/retention",
            Json(
                new RetentionRuleUpdateRequest(saga ? "saga-memories" : "lexicon-entries", true, 1),
                ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        HttpResponseMessage pruned = await client.PostAsync(
            "/api/data/prune",
            Json(
                new DataRetentionApplyRequest(
                    new DataRetentionRequest(DataRetentionOperation.Prune)),
                ArcanumJsonContext.Default.DataRetentionApplyRequest));

        Assert.Equal(HttpStatusCode.OK, pruned.StatusCode);

        await using AsyncServiceScope after = factory.Services.CreateAsyncScope();

        SqliteConnection verify = await OpenAsync(after);

        Assert.Equal(
            1,
            await CountAsync(verify, saga ? "saga_memories" : "lexicon_entries", "Id", artifactId));

        Assert.Equal(1, await CountAsync(verify, "artifact_sensitivity", "ArtifactId", artifactId));

    }

    private static async Task<SqliteConnection> OpenAsync(AsyncServiceScope scope)
    {

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State is not ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(CancellationToken.None);

        }

        return connection;

    }

    private static async Task SeedSessionAsync(SqliteConnection connection, Guid sessionId)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Sessions" ("Id", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, 'labelled retention subject', 'active', $created, $created);
            """;

        _ = command.Parameters.AddWithValue("$id", Canonical(sessionId));

        _ = command.Parameters.AddWithValue("$created", Backdated);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task SeedEntryAsync(
        SqliteConnection connection,
        Guid sessionId,
        Guid entryId)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Entries" (
                "Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence", "IsPinned")
            VALUES ($id, $session, 2, 'labelled assistant content', 'test-model', $created, 1, 0);
            """;

        _ = command.Parameters.AddWithValue("$id", Canonical(entryId));

        _ = command.Parameters.AddWithValue("$session", Canonical(sessionId));

        _ = command.Parameters.AddWithValue("$created", Backdated);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task SeedSagaMemoryAsync(SqliteConnection connection, Guid memoryId)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
            VALUES ($id, 'labelled saga fact', $created, 1);
            """;

        _ = command.Parameters.AddWithValue("$id", Canonical(memoryId));

        _ = command.Parameters.AddWithValue("$created", Backdated);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task SeedLexiconEntryAsync(SqliteConnection connection, Guid entryId)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt)
            VALUES ($id, 'Labelled Term', 'labelled term', 'concept', '[]', '', $updated);
            """;

        _ = command.Parameters.AddWithValue("$id", Canonical(entryId));

        _ = command.Parameters.AddWithValue("$updated", Backdated);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task LabelAsync(
        AsyncServiceScope scope,
        SensitiveArtifactKind kind,
        Guid artifactId,
        Guid? sessionId)
    {

        IArtifactSensitivityLedger ledger = scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>();

        Result<LabeledArtifactWriteReceipt> receipt = await ledger.LabelAsync(
            new DerivedArtifactWrite(
                kind,
                artifactId,
                sessionId,
                null,
                null,
                1,
                Digest(11),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Generation])),
            CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.Message : string.Empty);

        Assert.NotNull(receipt.Value.LabelId);

    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        string column,
        Guid id)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"SELECT COUNT(*) FROM \"{table}\" WHERE lower(replace({column}, '-', '')) = $id";

        _ = command.Parameters.AddWithValue("$id", id.ToString("N"));

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);

    }

    private static async Task<ApiResponse<DataRetentionApplyResult>> ReadAsync(
        HttpResponseMessage response)
    {

        string payload = await response.Content.ReadAsStringAsync();

        return System.Text.Json.JsonSerializer.Deserialize(
            payload,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult)
            ?? throw new InvalidOperationException($"Unreadable retention response: {payload}");

    }

    private static StringContent Json<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        new(
            System.Text.Json.JsonSerializer.Serialize(value, typeInfo),
            System.Text.Encoding.UTF8,
            "application/json");

    private static CovenantDigest Digest(byte seed)
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(seed + index);

        }

        return new CovenantDigest(bytes);

    }

    private static string Canonical(Guid id) => id.ToString("D").ToUpperInvariant();

    private static string Backdated =>
        DateTimeOffset.UtcNow.AddDays(-400).UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            System.Globalization.CultureInfo.InvariantCulture);

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

}

using System.Data;

using System.Net;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

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
///
/// <para>A label table that cannot be read is a third answer, and neither of the other two. The routes
/// below that are driven with a guard standing in for that condition refuse with
/// <c>Covenant.Unavailable</c> (503) rather than <c>Covenant.ForbiddenAuthority</c>, and the prune skips
/// the candidate it cannot vouch for.</para>
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

    /// <summary>
    /// An untargeted memory reset asks the label guard inside the transaction that deletes, so a label
    /// written after the answer cannot be removed along with the memory it names.
    /// </summary>
    /// <remarks>
    /// A second writer on its own connection tries to label the memory right after the guard answers,
    /// which is where a label written between the check and the delete would land. The reset holds the
    /// write lock from the moment its transaction opens, so that writer is blocked; a reset that asked
    /// before its transaction opened would let the label commit and then delete the memory under it,
    /// leaving a label that names nothing.
    /// </remarks>
    [SkippableTheory]

    [InlineData("saga")]

    [InlineData("lexicon")]

    public async Task A_memory_reset_asks_the_label_guard_inside_its_transaction(string store)
    {

        RequireSqlCipher();

        bool saga = string.Equals(store, "saga", StringComparison.Ordinal);

        await AssertGuardAskedInsideTheDeleteTransactionAsync(
            saga ? SensitiveArtifactKind.Saga : SensitiveArtifactKind.Lexicon,
            saga ? SeedSagaMemoryAsync : SeedLexiconEntryAsync,
            saga ? "saga_memories" : "lexicon_entries",
            async client =>
            {

                MemoryResetRequest request = new(saga ? MemoryResetScope.Saga : MemoryResetScope.Lexicon);

                HttpResponseMessage planned = await client.PostAsync(
                    "/api/data/memory/reset/plan",
                    Json(request, ArcanumJsonContext.Default.MemoryResetRequest));

                string plannedPayload = await planned.Content.ReadAsStringAsync();

                ApiResponse<DataRetentionPlan> plan = System.Text.Json.JsonSerializer.Deserialize(
                    plannedPayload,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionPlan)
                    ?? throw new InvalidOperationException($"Unreadable retention plan: {plannedPayload}");

                Assert.True(plan.IsSuccess, plannedPayload);

                HttpResponseMessage reset = await client.PostAsync(
                    "/api/data/memory/reset",
                    Json(request with { ExpectedPlanId = plan.Data!.PlanId }, ArcanumJsonContext.Default.MemoryResetRequest));

                ApiResponse<DataRetentionApplyResult> body = await ReadAsync(reset);

                Assert.True(body.IsSuccess, body.Error?.Message);

            });

    }

    /// <summary>
    /// The retention prune asks the label guard inside the transaction that deletes each Saga memory and
    /// Lexicon entry it selects, for the reason the reset does.
    /// </summary>
    [SkippableTheory]

    [InlineData("saga")]

    [InlineData("lexicon")]

    public async Task A_prune_asks_the_label_guard_inside_each_candidates_transaction(string store)
    {

        RequireSqlCipher();

        bool saga = string.Equals(store, "saga", StringComparison.Ordinal);

        await AssertGuardAskedInsideTheDeleteTransactionAsync(
            saga ? SensitiveArtifactKind.Saga : SensitiveArtifactKind.Lexicon,
            saga ? SeedSagaMemoryAsync : SeedLexiconEntryAsync,
            saga ? "saga_memories" : "lexicon_entries",
            async client =>
            {

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

            });

    }

    /// <summary>
    /// The retention prune asks the label guard inside the transaction that deletes each assistant Entry
    /// it selects, so a label written after the answer cannot be removed along with the Entry it names.
    /// </summary>
    [SkippableFact]

    public async Task A_prune_asks_the_label_guard_inside_each_entrys_transaction()
    {

        RequireSqlCipher();

        await AssertGuardAskedInsideTheDeleteTransactionAsync(
            SensitiveArtifactKind.AssistantEntry,
            SeedSessionWithEntryAsync,
            "Entries",
            async client =>
            {

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

            });

    }

    /// <summary>
    /// A Session delete asks the label guard about each of its assistant Entries inside the transaction
    /// that deletes them, so a label written after the answer cannot be removed along with its Entry.
    /// </summary>
    /// <remarks>
    /// A Session delete is all or nothing, so the guard has to ask about the Entries the transaction will
    /// actually remove, which is the set read inside it.
    /// </remarks>
    [SkippableFact]

    public async Task A_session_delete_asks_the_label_guard_inside_its_transaction()
    {

        RequireSqlCipher();

        Guid sessionId = Guid.NewGuid();

        await AssertGuardAskedInsideTheDeleteTransactionAsync(
            SensitiveArtifactKind.AssistantEntry,
            async (connection, entryId) =>
            {

                await SeedSessionAsync(connection, sessionId);

                await SeedEntryAsync(connection, sessionId, entryId);

            },
            "Entries",
            async client =>
            {

                HttpResponseMessage deleted = await client.DeleteAsync($"/api/data/sessions/{sessionId:D}");

                ApiResponse<DataRetentionApplyResult> body = await ReadAsync(deleted);

                Assert.True(body.IsSuccess, body.Error?.Message);

            });

    }

    /// <summary>
    /// A label table that cannot be read refuses the Session delete with <c>Covenant.Unavailable</c>, not
    /// with the refusal a labelled Entry gets, and leaves the Session and its Entry in place.
    /// </summary>
    /// <remarks>
    /// Nothing was found to be protected, so there is no authority to lack: the protection could not be
    /// checked, which is the Grimoire's condition and the operator's to repair. A route that read the
    /// unreadable answer as a pass would delete a Session whose Entries might be labelled.
    /// </remarks>
    [SkippableFact]

    public async Task An_unreadable_label_refuses_the_session_delete_route()
    {

        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = HostWithUnreadableGuard();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {

            SqliteConnection connection = await OpenAsync(scope);

            await SeedSessionAsync(connection, sessionId);

            await SeedEntryAsync(connection, sessionId, entryId);

        }

        HttpResponseMessage deleted = await client.DeleteAsync(
            $"/api/data/sessions/{sessionId:D}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, deleted.StatusCode);

        ApiResponse<DataRetentionApplyResult> body = await ReadAsync(deleted);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, body.Error?.Code);

        await using AsyncServiceScope after = factory.Services.CreateAsyncScope();

        SqliteConnection verify = await OpenAsync(after);

        Assert.Equal(1, await CountAsync(verify, "Entries", "Id", entryId));

        Assert.Equal(1, await CountAsync(verify, "Sessions", "Id", sessionId));

    }

    /// <summary>
    /// A label table that cannot be read refuses the untargeted memory reset with
    /// <c>Covenant.Unavailable</c> and leaves the store's rows in place.
    /// </summary>
    /// <remarks>
    /// The reset's statement examines no identity, so the guard's bulk question is the only protection it
    /// has. Answered "unreadable", the reset must stop rather than read the failure as "nothing is
    /// labelled here" and empty the table.
    /// </remarks>
    [SkippableTheory]

    [InlineData("saga")]

    [InlineData("lexicon")]

    public async Task An_unreadable_label_refuses_the_untargeted_memory_reset_route(string store)
    {

        RequireSqlCipher();

        bool saga = string.Equals(store, "saga", StringComparison.Ordinal);

        await using ArcanumWebApplicationFactory factory = HostWithUnreadableGuard();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid artifactId = Guid.NewGuid();

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

        }

        HttpResponseMessage reset = await client.PostAsync(
            "/api/data/memory/reset",
            Json(
                new MemoryResetRequest(saga ? MemoryResetScope.Saga : MemoryResetScope.Lexicon),
                ArcanumJsonContext.Default.MemoryResetRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reset.StatusCode);

        ApiResponse<DataRetentionApplyResult> body = await ReadAsync(reset);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, body.Error?.Code);

        await using AsyncServiceScope after = factory.Services.CreateAsyncScope();

        SqliteConnection verify = await OpenAsync(after);

        Assert.Equal(
            1,
            await CountAsync(verify, saga ? "saga_memories" : "lexicon_entries", "Id", artifactId));

    }

    /// <summary>
    /// A prune skips an Entry candidate whose label cannot be read and leaves every row in place, and the
    /// sweep itself still succeeds.
    /// </summary>
    /// <remarks>
    /// A sweep selects many candidates, so one it cannot vouch for is a reason to leave that candidate
    /// alone, as a pin is, not to abandon the sweep or to delete on a guess. The Session the Entry belongs
    /// to is asserted too, because the rule that selects the Entry must not carry the Session away with it.
    /// </remarks>
    [SkippableFact]

    public async Task An_unreadable_label_makes_the_prune_skip_the_entry_candidate()
    {

        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = HostWithUnreadableGuard();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {

            SqliteConnection connection = await OpenAsync(scope);

            await SeedSessionAsync(connection, sessionId);

            await SeedEntryAsync(connection, sessionId, entryId);

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

        Assert.Equal(1, await CountAsync(verify, "Sessions", "Id", sessionId));

    }

    /// <summary>
    /// Seeds one unlabelled memory or entry the operation deletes, runs the operation with a second
    /// writer trying to label it right after the guard answers, and asserts that writer was blocked.
    /// </summary>
    private static async Task AssertGuardAskedInsideTheDeleteTransactionAsync(
        SensitiveArtifactKind kind,
        Func<SqliteConnection, Guid, Task> seed,
        string table,
        Func<HttpClient, Task> operation)
    {

        SqliteConnection? intruderConnection = null;

        LabelIntruder intruder = new(
            _ => Task.FromResult(intruderConnection!),
            kind);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {

                // The transaction form is the registration that builds the guard; the Core form forwards to
                // it, so replacing this one puts the intruder in front of every caller.
                Func<IServiceProvider, object> real = services
                    .Last(static descriptor => descriptor.ServiceType == typeof(ICovenantLabeledArtifactTransactionGuard))
                    .ImplementationFactory
                    ?? throw new InvalidOperationException("The guard is registered by a factory.");

                services.AddScoped<ICovenantLabeledArtifactTransactionGuard>(
                    sp => new LabelIntrusionGuard((ICovenantLabeledArtifactTransactionGuard)real(sp), intruder));

            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid artifactId = Guid.NewGuid();

        intruder.ArtifactId = artifactId;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {

            SqliteConnection connection = await OpenAsync(scope);

            await seed(connection, artifactId);

        }

        Result<IGrimoireOrdinaryConnectionLease> opened = await factory.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, CancellationToken.None);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        intruderConnection = lease.Connection;

        await operation(client);

        Assert.Equal(1, intruder.Attempts);

        Assert.Equal(1, intruder.Blocked);

        await using AsyncServiceScope after = factory.Services.CreateAsyncScope();

        SqliteConnection verify = await OpenAsync(after);

        Assert.Equal(0, await CountAsync(verify, table, "Id", artifactId));

        // No label survives the artifact it names.
        Assert.Equal(0, await CountAsync(verify, "artifact_sensitivity", "ArtifactId", artifactId));

        Assert.Equal(1, intruder.AskedInsideTransaction);

        Assert.Equal(0, intruder.AskedOutsideTransaction);

    }

    /// <summary>
    /// A host whose labelled-artifact guard cannot read the label table.
    /// </summary>
    /// <remarks>
    /// The transaction form is the registration the composition builds the guard from, and the Core form
    /// forwards to it, so replacing this one puts the unreadable answer in front of every caller.
    /// </remarks>
    private static ArcanumWebApplicationFactory HostWithUnreadableGuard() =>
        new()
        {
            ServiceOverrides = static services =>
                services.AddScoped<ICovenantLabeledArtifactTransactionGuard>(
                    static _ => new UnreadableLabeledArtifactGuard()),
        };

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

    private static async Task SeedSessionWithEntryAsync(SqliteConnection connection, Guid entryId)
    {

        Guid sessionId = Guid.NewGuid();

        await SeedSessionAsync(connection, sessionId);

        await SeedEntryAsync(connection, sessionId, entryId);

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

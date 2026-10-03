using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// What the direct-deletion routes answer when a purged item's label changed between the purge's first
/// read and the kernel's own reread.
/// </summary>
/// <remarks>
/// <para>Entered through the mapped routes on a host with the Covenant on. Memories are written through
/// the Saga store's insert, entries through the Lexicon service, and labels through the host's own
/// sensitivity ledger. Raw SQL here is assertion-only.</para>
///
/// <para>The one seam is a ledger decorator whose arm replaces exactly the next label read for one
/// artifact. That read is the purge's first one, so the kernel is handed a label that is not the one in
/// the database, which is how a label that moves between the two reads looks to it. Every later read
/// goes to the real ledger, so the coordinator's reread sees what is actually there (§10.20.2).</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class CovenantSensitivePurgeRouteDispositionTests
{
    /// <summary>
    /// A labelled memory whose label moved is refused as stale, and both the memory and its label stay.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_Saga_memory_whose_label_moved_is_refused_and_keeps_the_row()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LabelReadArm arm = new();

        await using ArcanumWebApplicationFactory factory = Host(arm);

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await InsertSagaAsync(factory);

        await LabelAsync(factory, SensitiveArtifactKind.Saga, id);

        arm.Arm(id, CovenantErasureAuthorityFixture.Label(id, Guid.NewGuid(), SensitiveArtifactKind.Saga));

        using HttpResponseMessage response = await client.DeleteAsync($"/api/saga/{id:D}");

        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Contains(ErrorCodes.Covenant.StaleSnapshot, body, StringComparison.Ordinal);

        Assert.True(arm.Fired);

        Assert.Equal(1, await CountAsync(factory, "saga_memories", "Id", id));

        Assert.Equal(1, await CountAsync(factory, "artifact_sensitivity", "ArtifactId", id));
    }

    /// <summary>
    /// A memory whose label is gone by the time the kernel looks is deleted through the ordinary path,
    /// so the route's 204 is true.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_Saga_memory_whose_label_vanished_deletes_it_through_the_ordinary_path()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LabelReadArm arm = new();

        await using ArcanumWebApplicationFactory factory = Host(arm);

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await InsertSagaAsync(factory);

        arm.Arm(id, CovenantErasureAuthorityFixture.Label(id, Guid.NewGuid(), SensitiveArtifactKind.Saga));

        using HttpResponseMessage response = await client.DeleteAsync($"/api/saga/{id:D}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.True(arm.Fired);

        Assert.Equal(0, await CountAsync(factory, "saga_memories", "Id", id));

        Assert.Equal(0, await CountAsync(factory, "artifact_sensitivity", "ArtifactId", id));
    }

    /// <summary>
    /// A labelled Lexicon entry whose label moved is refused as stale, and the entry and its label stay.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_a_Lexicon_entry_whose_label_moved_is_refused_and_keeps_the_entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LabelReadArm arm = new();

        await using ArcanumWebApplicationFactory factory = Host(arm);

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid identity = await ScribeAsync(factory, "Moved Term");

        await LabelAsync(factory, SensitiveArtifactKind.Lexicon, identity);

        arm.Arm(identity, CovenantErasureAuthorityFixture.Label(identity, Guid.NewGuid(), SensitiveArtifactKind.Lexicon));

        using HttpResponseMessage response = await client.DeleteAsync("/api/memory/lexicon/Moved%20Term");

        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Contains(ErrorCodes.Covenant.StaleSnapshot, body, StringComparison.Ordinal);

        Assert.True(arm.Fired);

        Assert.Equal(1, await CountAsync(factory, "lexicon_entries", "Id", identity));

        Assert.Equal(1, await CountAsync(factory, "artifact_sensitivity", "ArtifactId", identity));
    }

    /// <summary>
    /// Compaction that erases one protected Entry and then meets a refused one reports the Entry it
    /// erased, and why it stopped.
    /// </summary>
    /// <remarks>
    /// The two oldest Entries are labelled and selected first. The first is erased through the shared
    /// kernel; the second's label is replaced on the purge's first read, so the kernel is handed a label
    /// that is not the one stored and the coordinator blocks it as stale. The route answered a success
    /// that said nothing was removed while the first Entry was already gone. Every unlabelled Entry stays,
    /// because a stop never falls back to the ordinary delete.
    /// </remarks>
    [SkippableFact]
    public async Task Compacting_after_erasing_one_protected_Entry_and_meeting_a_refused_one_reports_both()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LabelReadArm arm = new();

        await using ArcanumWebApplicationFactory factory = Host(arm);

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        Guid[] entries = await AppendEntriesAsync(client, sessionId, 8);

        await LabelAsync(factory, SensitiveArtifactKind.AssistantEntry, entries[0], sessionId);

        await LabelAsync(factory, SensitiveArtifactKind.AssistantEntry, entries[1], sessionId);

        arm.Arm(
            entries[1],
            CovenantErasureAuthorityFixture.Label(entries[1], Guid.NewGuid(), SensitiveArtifactKind.AssistantEntry));

        CompactResult result = await CompactAsync(client, sessionId);

        Assert.True(arm.Fired);

        Assert.Equal(1, result.EntriesRemoved);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.StoppedBy);

        Assert.True(result.TokensAfter < result.TokensBefore);

        Assert.Equal(0, await CountAsync(factory, "Entries", "Id", entries[0]));

        Assert.Equal(0, await CountAsync(factory, "artifact_sensitivity", "ArtifactId", entries[0]));

        Assert.Equal(1, await CountAsync(factory, "Entries", "Id", entries[1]));

        Assert.Equal(1, await CountAsync(factory, "artifact_sensitivity", "ArtifactId", entries[1]));

        Assert.Equal(7, await CountAsync(factory, "Entries", "SessionId", sessionId));
    }

    /// <summary>
    /// Compaction whose first protected Entry is refused removes nothing, says so, and names the stop.
    /// </summary>
    [SkippableFact]
    public async Task Compacting_when_the_first_protected_Entry_is_refused_reports_zero_removed_and_the_block()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        LabelReadArm arm = new();

        await using ArcanumWebApplicationFactory factory = Host(arm);

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        Guid[] entries = await AppendEntriesAsync(client, sessionId, 8);

        await LabelAsync(factory, SensitiveArtifactKind.AssistantEntry, entries[0], sessionId);

        arm.Arm(
            entries[0],
            CovenantErasureAuthorityFixture.Label(entries[0], Guid.NewGuid(), SensitiveArtifactKind.AssistantEntry));

        CompactResult result = await CompactAsync(client, sessionId);

        Assert.True(arm.Fired);

        Assert.Equal(0, result.EntriesRemoved);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.StoppedBy);

        Assert.Equal(result.TokensBefore, result.TokensAfter);

        Assert.Equal(8, await CountAsync(factory, "Entries", "SessionId", sessionId));

        Assert.Equal(1, await CountAsync(factory, "artifact_sensitivity", "ArtifactId", entries[0]));
    }

    /// <summary>
    /// Compaction that erased a protected Entry answers with the protected-response headers.
    /// </summary>
    /// <remarks>
    /// The compact handler never sees the purge outcome, so it cannot mark the response itself. The mark
    /// comes from the conditional retention-purge authority the route declares, which marks the response
    /// when it issues the authority and so before any Entry is erased, exactly as it does for the Entry
    /// delete route.
    /// </remarks>
    [SkippableFact]
    public async Task Compacting_after_erasing_a_protected_Entry_marks_the_response_protected()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = Host(new LabelReadArm());

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        Guid[] entries = await AppendEntriesAsync(client, sessionId, 8);

        await LabelAsync(factory, SensitiveArtifactKind.AssistantEntry, entries[0], sessionId);

        using HttpResponseMessage response = await client.PostAsync($"/api/sessions/{sessionId:D}/compact", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(0, await CountAsync(factory, "Entries", "Id", entries[0]));

        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
    }

    /// <summary>
    /// A host with the Covenant, Saga and memory management on, an in-memory credential store, and the
    /// armed ledger in front of the real one.
    /// </summary>
    /// <remarks>
    /// Turning Saga on also turns on the embedding substrate it runs on, and configuration validation
    /// refuses that substrate without a provider and model, so the host names the same test provider the
    /// erasure route suites do. Nothing here calls it.
    /// </remarks>
    private static ArcanumWebApplicationFactory Host(LabelReadArm arm)
    {
        InMemoryOsCredentialStore credentials = new();

        return new ArcanumWebApplicationFactory
        {
            SettingsOverride = static settings => settings with
            {
                Features = settings.Features with
                {
                    Covenant = true,
                    Saga = true,
                    Embeddings = true,
                    MemoryManagement = true,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = 64,
                    },
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IOsCredentialStore>();

                services.AddSingleton<IOsCredentialStore>(credentials);

                services.AddSingleton(arm);

                services.AddScoped<IArtifactSensitivityLedger>(static sp => new ArmedLabelLedger(
                    new ArtifactSensitivityLedger(sp.GetRequiredService<ICovenantConnectionSource>()),
                    sp.GetRequiredService<LabelReadArm>()));
            },
        };
    }

    /// <summary>Creates one Session through the route.</summary>
    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        string payload = JsonSerializer.Serialize(
            new CreateSessionRequest(CampaignId: null, Title: "compaction under a block"),
            ArcanumJsonContext.Default.CreateSessionRequest);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/sessions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        return Assert.IsType<SessionDetailDto>(body?.Data).Id;
    }

    /// <summary>
    /// Appends large assistant Entries one at a time through the route, so the Session is over any
    /// default compaction threshold, and returns their identities oldest first.
    /// </summary>
    private static async Task<Guid[]> AppendEntriesAsync(HttpClient client, Guid sessionId, int count)
    {
        List<Guid> ids = [];

        for (int index = 0; index < count; index++)
        {
            string payload = JsonSerializer.Serialize(
                new AppendEntryRequest(
                    MessageRole.Assistant,
                    string.Join(' ', Enumerable.Repeat($"ward-stone-{index}", 4_000)),
                    "test-model"),
                ArcanumJsonContext.Default.AppendEntryRequest);

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/sessions/{sessionId:D}/entries",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            ApiResponse<EntryDto>? body = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                ArcanumJsonContext.Default.ApiResponseEntryDto);

            ids.Add(Assert.IsType<EntryDto>(body?.Data).Id);
        }

        return [.. ids];
    }

    /// <summary>Compacts one Session through the route and returns the successful result.</summary>
    private static async Task<CompactResult> CompactAsync(HttpClient client, Guid sessionId)
    {
        using HttpResponseMessage response = await client.PostAsync($"/api/sessions/{sessionId:D}/compact", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<CompactResult>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCompactResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        return Assert.IsType<CompactResult>(body.Data);
    }

    /// <summary>Writes one Saga memory through the store's own insert.</summary>
    private static async Task<Guid> InsertSagaAsync(ArcanumWebApplicationFactory factory)
    {
        Guid id = Guid.NewGuid();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        SagaMemoryWriteOutcome outcome = await scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>().InsertAsync(
            id.ToString("D"),
            "The ward-stone lies under the mill.",
            DateTimeOffset.UtcNow,
            sessionId: null,
            tags: null,
            source: "extraction",
            new float[64],
            CancellationToken.None);

        Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);

        return id;
    }

    /// <summary>
    /// Scribes one Global entry through the host's Lexicon service and returns the identity the delete
    /// route resolves for it.
    /// </summary>
    private static async Task<Guid> ScribeAsync(ArcanumWebApplicationFactory factory, string name)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

        LexiconScope tier = LexiconScope.ForResolvedCampaign(null);

        Result<LexiconEntryDto> scribed = await lexicon.UpsertAsync(
            name,
            "Place",
            ["guards the mill"],
            tier,
            CancellationToken.None);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        Result<Guid?> identity = await lexicon.FindAllLifecycleIdentityForDeletionAsync(
            name,
            tier,
            CancellationToken.None);

        Assert.True(identity.IsSuccess, identity.IsFailure ? identity.Error.Message : null);

        return Assert.IsType<Guid>(identity.Value);
    }

    /// <summary>Labels one artifact through the host's sensitivity ledger, the one production writer.</summary>
    private static async Task LabelAsync(
        ArcanumWebApplicationFactory factory,
        SensitiveArtifactKind kind,
        Guid artifactId,
        Guid? sessionId = null)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ICovenantAvailability availability = scope.ServiceProvider.GetRequiredService<ICovenantAvailability>();

        Result<LabeledArtifactWriteReceipt> receipt = await scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>()
            .LabelAsync(
                new DerivedArtifactWrite(
                    kind,
                    artifactId,
                    sessionId,
                    null,
                    null,
                    1,
                    CovenantOperationGateFixture.Digest(11),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([availability.Current.DatasetGeneration!.Value])),
                CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.Message : null);

        Assert.NotNull(receipt.Value.LabelId);
    }

    /// <summary>
    /// Counts the rows naming one identity, in any spelling, on a fresh read-only connection.
    /// Assertion-only.
    /// </summary>
    private static async Task<long> CountAsync(
        ArcanumWebApplicationFactory factory,
        string table,
        string column,
        Guid id)
    {
        Result<IGrimoireOrdinaryConnectionLease> opened = await factory.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, CancellationToken.None);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        await using SqliteCommand command = lease.Connection.CreateCommand();

        command.CommandText = $"SELECT count(*) FROM \"{table}\" WHERE lower(replace({column}, '-', '')) = $id;";

        _ = command.Parameters.AddWithValue("$id", id.ToString("N"));

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Replaces exactly the next label read for one artifact, once.
    /// </summary>
    private sealed class LabelReadArm
    {
        private readonly Lock _gate = new();

        private Guid? _artifactId;

        private ArtifactSensitivityLabel? _answer;

        private bool _fired;

        internal bool Fired
        {
            get
            {
                lock (_gate)
                {
                    return _fired;
                }
            }
        }

        internal void Arm(Guid artifactId, ArtifactSensitivityLabel? answer)
        {
            lock (_gate)
            {
                _artifactId = artifactId;

                _answer = answer;

                _fired = false;
            }
        }

        internal bool TryTake(Guid artifactId, out ArtifactSensitivityLabel? answer)
        {
            lock (_gate)
            {
                if (_artifactId != artifactId)
                {
                    answer = null;

                    return false;
                }

                answer = _answer;

                _artifactId = null;

                _answer = null;

                _fired = true;

                return true;
            }
        }
    }

    /// <summary>
    /// The real ledger, except that an armed read is answered by the arm instead.
    /// </summary>
    private sealed class ArmedLabelLedger(IArtifactSensitivityLedger inner, LabelReadArm arm) : IArtifactSensitivityLedger
    {
        public Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
            DerivedArtifactWrite write,
            CancellationToken cancellationToken) =>
            inner.LabelAsync(write, cancellationToken);

        public Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
            SensitiveArtifactKind artifactKind,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            arm.TryTake(artifactId, out ArtifactSensitivityLabel? answer)
                ? Task.FromResult(Result<ArtifactSensitivityLabel?>.Success(answer))
                : inner.TryReadLabelAsync(artifactKind, artifactId, cancellationToken);

        public Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            inner.ReadSessionProjectionAsync(sessionId, cancellationToken);
    }
}

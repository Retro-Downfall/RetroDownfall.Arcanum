using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// Spec §19.2 #7: deleting the OS key while erasure rows exist fails every automatic writer closed, and
/// <c>reset-key</c> discards the rows nothing can verify any more and unblocks them.
/// </summary>
/// <remarks>
/// <para>Every step is the production one. The fingerprints come from actual erases through the routes;
/// the Session's entries arrive through the append route; extraction runs through its own service under
/// its own work lease; the Lexicon scribe goes through the host's own service; and the agent proposal is
/// applied by the host's Covenant kernel under the write lease and gate capture turn publication uses.
/// Only the model and the embedding provider are fakes.</para>
///
/// <para>Both hosts share one profile and one credential store, so the second host starts over the
/// first one's Grimoire with the key gone exactly as an operator who deleted it would leave it.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryErasureKeyLossEndToEndTests
{
    private const string Service = ArcanumCredentialIdentity.Service;

    private const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount;

    private const string T = "The vault key hangs behind the mill door.";

    private const string Keeper = "Vault Keeper";

    private const string VaultKey = "preference.vault";

    private const string HarborKey = "preference.harbor";

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task Deleting_the_key_while_rows_exist_fails_closed_until_reset_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore inner = new();

        SecretAccessRecordingCredentialStore credentials = new(inner);

        await using RestartableArcanumProfileFixture profile = new();

        Guid campaign;

        Guid session;

        // 1. One erase in every store, under the key the first erase creates.
        await using (ArcanumWebApplicationFactory first = Host(inner, credentials, profile))
        {
            MemoryErasureRouteDriver driver = new(first.CreateClient());

            (campaign, session) = await BoundSessionAsync(first);

            _ = await driver.EraseSagaAsync(await MemoryErasureRouteDriver.InsertSagaAsync(first, T, session));

            Assert.True((await UpsertAsync(first, Keeper, "Person", ["keeps the vault key"])).IsSuccess);

            _ = await driver.EraseLexiconAsync(Keeper, null);

            _ = await driver.SetCovenantAsync(CovenantScope.Campaign, campaign, VaultKey, "Keep the vault key offline.");

            _ = await driver.EraseCovenantAsync(CovenantScope.Campaign, campaign, VaultKey);
        }

        // 2. The key is deleted; the restarted host's warm-up proves it absent.
        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Delete(Service, Account).Status);

        await using ArcanumWebApplicationFactory factory = Host(inner, credentials, profile);

        HttpClient client = factory.CreateAuthenticatedClient();

        Assert.Equal(
            MemoryErasureKeyState.Absent,
            factory.Services.GetRequiredService<IMemoryErasureKeyProvider>().Latch.State);

        // 3. Status reports the key lost, with every row unverifiable.
        MemoryErasureStatusDto lost = await StatusAsync(client);

        Assert.Equal(MemoryErasureKeyStatus.Lost, lost.KeyStatus);

        AssertStores(lost.Stores, covenant: (1, 1, 1), saga: (1, 1, 1), lexicon: (1, 1, 1));

        // 4. Extraction defers before the model call, and the Session's watermark stays where it was.
        await AppendAsync(client, session, "Where does the miller keep the vault key?");

        factory.FakeIntelligence.NextText = Conclusions(T);

        long through = await LatestSequenceAsync(factory, session);

        int calls = factory.FakeIntelligence.ExecutePromptCallCount;

        Assert.Equal(SagaExtractionOutcome.DeferredForErasureKey, await ExtractAsync(factory, session, 0, through));

        Assert.Equal(calls, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Null(await CursorAsync(factory, session));

        // 5. The scribe and an agent proposal fail closed as lost.
        Result<LexiconEntryDto> scribe = await UpsertAsync(factory, "Harbor Master", "Place", ["alpha"]);

        Assert.True(scribe.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, scribe.Error.Code);

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await ProposeAsync(factory, campaign, HarborKey);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.MemoryErasure.KeyLost, refused.Error.Code);

        Assert.Equal(0, await ProposedVersionsAsync(factory, HarborKey));

        // 6. reset-key discards every unverifiable row and creates a key.
        MemoryErasureKeyResetPreflightDto prepared;

        using (HttpResponseMessage response = await client.PostAsync("/api/memory/erasure/reset-key/prepare", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            prepared = await MemoryErasureRouteDriver.ReadDataAsync(
                response,
                ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetPreflightDto);
        }

        Assert.Equal(MemoryErasureKeyStatus.Lost, prepared.KeyStatus);

        Assert.All(prepared.Stores, static store => Assert.Equal(1, store.Unverifiable));

        using (HttpResponseMessage response = await new MemoryErasureRouteDriver(factory.CreateClient()).PostAsync(
            "/api/memory/erasure/reset-key",
            new MemoryErasureKeyResetRequest(prepared.PreflightToken),
            ArcanumJsonContext.Default.MemoryErasureKeyResetRequest))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.Equal(
                new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, 3, 3, KeyCreated: true),
                await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetResultDto));
        }

        Assert.Equal(OsCredentialStoreStatus.Ok, inner.ProbePresence(Service, Account));

        // 7. Every writer is unblocked, and the discarded erasures are learnable again.
        Assert.Equal(SagaExtractionOutcome.Completed, await ExtractAsync(factory, session, 0, through));

        Assert.Equal(calls + 1, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(through, (await CursorAsync(factory, session))?.EntrySequence);

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM saga_memories WHERE Content = $value;", T));

        Assert.True((await UpsertAsync(factory, Keeper, "Person", ["keeps the vault key"])).IsSuccess);

        Result<IReadOnlyList<CovenantMutationReceipt>> accepted = await ProposeAsync(factory, campaign, HarborKey);

        Assert.True(accepted.IsSuccess, accepted.IsFailure ? accepted.Error.Message : null);

        Assert.Equal(CovenantMutationOutcome.Applied, Assert.Single(accepted.Value).Outcome);

        Assert.Equal(1, await ProposedVersionsAsync(factory, HarborKey));

        MemoryErasureStatusDto present = await StatusAsync(client);

        Assert.Equal(MemoryErasureKeyStatus.Present, present.KeyStatus);

        AssertStores(present.Stores, covenant: (0, 0, 0), saga: (0, 0, 0), lexicon: (0, 0, 0));
    }

    private static ArcanumWebApplicationFactory Host(
        InMemoryOsCredentialStore inner,
        SecretAccessRecordingCredentialStore credentials,
        RestartableArcanumProfileFixture profile) =>
        MemoryErasureRouteDriver.Host(inner, profile, covenant: true).WithRecordingCredentials(credentials);

    private static async Task<MemoryErasureStatusDto> StatusAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/memory/erasure");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseMemoryErasureStatusDto);
    }

    private static void AssertStores(
        IReadOnlyList<MemoryErasureStoreCountsDto> stores,
        (long Fingerprints, long Unverifiable, long Receipts) covenant,
        (long Fingerprints, long Unverifiable, long Receipts) saga,
        (long Fingerprints, long Unverifiable, long Receipts) lexicon) =>
        Assert.Equal<MemoryErasureStoreCountsDto>(
            [
                new(MemoryReviewStore.Covenant, covenant.Fingerprints, covenant.Unverifiable, covenant.Receipts),
                new(MemoryReviewStore.Saga, saga.Fingerprints, saga.Unverifiable, saga.Receipts),
                new(MemoryReviewStore.Lexicon, lexicon.Fingerprints, lexicon.Unverifiable, lexicon.Receipts),
            ],
            stores);

    /// <summary>Scribes one Global entry through the host's own Lexicon service, reporting what it decided.</summary>
    private static async Task<Result<LexiconEntryDto>> UpsertAsync(
        ArcanumWebApplicationFactory factory,
        string name,
        string type,
        IReadOnlyList<string> facts)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<ILexiconService>()
            .UpsertAsync(name, type, facts, LexiconScope.Global, Token);
    }

    /// <summary>
    /// Applies one agent proposal the way turn publication does: under the Campaign's write lease, with
    /// the erasure gate captured from the latch before the serializable transaction begins.
    /// </summary>
    private static async Task<Result<IReadOnlyList<CovenantMutationReceipt>>> ProposeAsync(
        ArcanumWebApplicationFactory factory,
        Guid campaign,
        string key)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        SqliteConnection connection = await scope.ServiceProvider
            .GetRequiredService<ICovenantConnectionSource>()
            .GetOpenConnectionAsync(Token);

        CovenantMutationKernel kernel = scope.ServiceProvider.GetRequiredService<CovenantMutationKernel>();

        Result<CovenantWriteLease> acquired = await factory.Services
            .GetRequiredService<ICovenantOperationGate>()
            .AcquireWriteAsync(CovenantOperationScope.ForCampaign(campaign), Token);

        Assert.True(acquired.IsSuccess, acquired.IsFailure ? acquired.Error.Message : null);

        await using CovenantWriteLease lease = acquired.Value;

        using CovenantAgentErasureGate erasureGate = kernel.CaptureErasureGate();

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        CovenantMutationBatch batch = new(
            new Guid((byte[])(await ScalarAsync(connection, transaction, "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;"))!),
            Convert.ToInt64(
                await ScalarAsync(connection, transaction, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;"),
                CultureInfo.InvariantCulture),
            expectedCampaignRegistryEpoch: null,
            DateTimeOffset.UtcNow,
            [
                CovenantMutationFixture.AgentPropose(
                    campaign,
                    key,
                    "The model suggests remembering the harbor chain.",
                    expectedRevision: 0,
                    expectedKeyEpoch: 0),
            ]);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await kernel.ApplyBatchAsync(
            batch,
            new CovenantMutationTransaction(connection, transaction),
            erasureGate,
            Token);

        if (applied.IsSuccess)
        {
            await transaction.CommitAsync(Token);
        }
        else
        {
            await transaction.RollbackAsync(Token);
        }

        return applied;
    }

    /// <summary>How many Proposed versions any entry of this key holds. Assertion-only.</summary>
    private static Task<long> ProposedVersionsAsync(ArcanumWebApplicationFactory factory, string key) =>
        ScalarAsync(
            factory,
            """
            SELECT count(*)
            FROM covenant_versions AS version
            JOIN covenant_entries AS entry ON entry.EntryId = version.EntryId
            WHERE entry.NormalizedKey = $value AND version.LaneCode = 2;
            """,
            key);

    /// <summary>One extraction pass over the Session's entries, under the extraction work lease.</summary>
    private static async Task<SagaExtractionOutcome> ExtractAsync(
        ArcanumWebApplicationFactory factory,
        Guid session,
        long afterExclusive,
        long through)
    {
        SagaExtractionService extraction = factory.Services.GetRequiredService<SagaExtractionService>();

        IGrimoireConnectionAdmissionGate gate = factory.Services.GetRequiredService<IGrimoireConnectionAdmissionGate>();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease lease = acquired!;

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumSettings settings = factory.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue;

        return await extraction.ExtractForSessionAsync(
            scope.ServiceProvider,
            lease,
            new SagaExtractionRequest(
                session,
                [],
                HadUnprovenancedAttachmentContent: false,
                AfterEntrySequenceExclusive: afterExclusive,
                ThroughEntrySequence: through),
            settings.ResolveEmbeddings(),
            settings,
            Token);
    }

    private static async Task<SagaExtractionCursor?> CursorAsync(ArcanumWebApplicationFactory factory, Guid session)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>().GetExtractionCursorAsync(session, Token);
    }

    /// <summary>
    /// Registers a Campaign through its route and binds a new Session to it through the turn-begin
    /// store, as a turn would.
    /// </summary>
    private static async Task<(Guid Campaign, Guid Session)> BoundSessionAsync(ArcanumWebApplicationFactory factory)
    {
        string path = Path.Combine(factory.TempHome, "key-loss-campaign");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await factory.CreateAuthenticatedClient().PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest("Key loss", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        CampaignDto campaign = await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto);

        using IServiceScope scope = factory.Services.CreateScope();

        Result<Guid> session = await scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>().CreateBoundSessionAsync(
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(campaign.Id),
                campaignAvailabilityGeneration: 1,
                pathIdentityPolicyVersion: 1,
                pathIdentityRevision: null,
                rootIdentityDigest: null),
            "Key loss",
            Token);

        Assert.True(session.IsSuccess, session.IsFailure ? session.Error.Message : null);

        return (campaign.Id, session.Value);
    }

    private static async Task AppendAsync(HttpClient client, Guid session, string content)
    {
        using HttpResponseMessage appended = await client.PostAsync(
            $"/api/sessions/{session}/entries",
            JsonContent.Create(new AppendEntryRequest(MessageRole.User, content), ArcanumJsonContext.Default.AppendEntryRequest));

        Assert.Equal(HttpStatusCode.OK, appended.StatusCode);
    }

    private static async Task<long> LatestSequenceAsync(ArcanumWebApplicationFactory factory, Guid session)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        return await db.Entries
            .Where(entry => entry.SessionId == session)
            .MaxAsync(entry => (long?)entry.Sequence) ?? 0L;
    }

    /// <summary>The extraction model's answer: one memory per conclusion, none from an attachment.</summary>
    private static string Conclusions(params string[] contents)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("memories");

            foreach (string content in contents)
            {
                writer.WriteStartObject();

                writer.WriteString("content", content);

                writer.WriteNull("attachmentId");

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<long> ScalarAsync(ArcanumWebApplicationFactory factory, string sql, string value)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = command.Parameters.AddWithValue("$value", value);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Token);
    }
}

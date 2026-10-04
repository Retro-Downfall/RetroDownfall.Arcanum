using System.Data;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// A restarted host that holds only Covenant fingerprints resolves its erasure key before it serves,
/// so agent writes of keys nobody erased keep flowing without an operator call.
/// </summary>
/// <remarks>
/// <para>Agent turns read the latch and never probe the credential store, because that read would
/// happen under the turn lease. The Saga and Lexicon chokepoints resolve the key at their own phase
/// one when they meet evidence; the Covenant has no such phase. Without the startup warm-up, a host
/// restarted over Covenant fingerprints would hold an unresolved latch and refuse every agent proposal
/// as unverifiable until the operator happened to run an erase or a status.</para>
///
/// <para>Both hosts share one profile and so one credential store. The second host is asked for no
/// erasure, status or release, so only its warm-up can have resolved the latch.</para>
/// </remarks>
[Collection("ApiHost")]
[Trait("Category", "Integration")]
public sealed class CovenantErasureRestartTests
{

    private const string ErasedKey = "erased.key";

    private const string UnrelatedKey = "unrelated.key";

    private const string OperatorManaged = "This Covenant key is managed by the operator in this scope.";

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task A_restarted_host_with_only_Covenant_fingerprints_admits_an_unrelated_agent_proposal_without_an_operator_call()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using RestartableArcanumProfileFixture profile = new();

        Guid campaignId;

        await using (ArcanumWebApplicationFactory hostA = CreateFactory(profile))
        {

            HttpClient client = hostA.CreateAuthenticatedClient();

            campaignId = await CreateCampaignAsync(hostA, client);

            MemoryErasureKeyOpenResult opened = hostA.Services
                .GetRequiredService<IMemoryErasureKeyCreator>()
                .OpenOrCreate(evidenceRowsExist: false);

            Assert.Equal(MemoryErasureKeyState.Present, opened.State);

            using MemoryErasureKey key = opened.Key!;

            await using AsyncServiceScope scope = hostA.Services.CreateAsyncScope();

            SqliteConnection connection = await scope.ServiceProvider
                .GetRequiredService<ICovenantConnectionSource>()
                .GetOpenConnectionAsync(Token);

            await MemoryErasureTestKeys.SeedFingerprintAsync(
                connection,
                key,
                MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, campaignId, ErasedKey),
                Token);

        }

        await using ArcanumWebApplicationFactory hostB = CreateFactory(profile);

        // Readiness: the host has started and its bootstrapper has run.
        _ = hostB.CreateAuthenticatedClient();

        IMemoryErasureKeyProvider provider = hostB.Services.GetRequiredService<IMemoryErasureKeyProvider>();

        Assert.Equal(MemoryErasureKeyState.Present, provider.Latch.State);

        await using AsyncServiceScope serving = hostB.Services.CreateAsyncScope();

        SqliteConnection served = await serving.ServiceProvider
            .GetRequiredService<ICovenantConnectionSource>()
            .GetOpenConnectionAsync(Token);

        Assert.False(await MemoryErasureEvidence.AnyAsync(served, null, MemoryReviewStore.Saga, Token));

        Assert.False(await MemoryErasureEvidence.AnyAsync(served, null, MemoryReviewStore.Lexicon, Token));

        Assert.True(await MemoryErasureEvidence.AnyAsync(served, null, MemoryReviewStore.Covenant, Token));

        CovenantMutationKernel kernel = serving.ServiceProvider.GetRequiredService<CovenantMutationKernel>();

        ICovenantOperationGate gate = hostB.Services.GetRequiredService<ICovenantOperationGate>();

        await using CovenantWriteLease lease = (await gate.AcquireWriteAsync(
            CovenantOperationScope.ForCampaign(campaignId),
            Token)).Value;

        using CovenantAgentErasureGate erasureGate = kernel.CaptureErasureGate();

        Result<IReadOnlyList<CovenantMutationReceipt>> unrelated = await ApplyAgentProposalAsync(
            served,
            kernel,
            erasureGate,
            campaignId,
            UnrelatedKey,
            provider,
            expectedClassification: CovenantAgentErasureState.Clear);

        Assert.True(unrelated.IsSuccess, unrelated.IsFailure ? unrelated.Error.Message : string.Empty);

        Assert.Equal(CovenantMutationOutcome.Applied, Assert.Single(unrelated.Value).Outcome);

        Result<IReadOnlyList<CovenantMutationReceipt>> erased = await ApplyAgentProposalAsync(
            served,
            kernel,
            erasureGate,
            campaignId,
            ErasedKey,
            provider,
            expectedClassification: CovenantAgentErasureState.Withheld);

        Assert.True(erased.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, erased.Error.Code);

        Assert.Equal(OperatorManaged, erased.Error.Message);

    }

    private static ArcanumWebApplicationFactory CreateFactory(RestartableArcanumProfileFixture profile)
    {

        ArcanumWebApplicationFactory factory = profile.CreateFactory();

        factory.SettingsOverride = static settings => settings with
        {

            Features = settings.Features with { Covenant = true },

        };

        return factory;

    }

    /// <summary>
    /// Applies one agent proposal in a serializable transaction and, before that transaction ends,
    /// asks a gate read from the latch how the staging store would classify the same key.
    /// </summary>
    private static async Task<Result<IReadOnlyList<CovenantMutationReceipt>>> ApplyAgentProposalAsync(
        SqliteConnection connection,
        CovenantMutationKernel kernel,
        CovenantAgentErasureGate erasureGate,
        Guid campaignId,
        string key,
        IMemoryErasureKeyProvider provider,
        CovenantAgentErasureState expectedClassification)
    {

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        CovenantMutationBatch batch = new(
            new Guid((byte[])(await ScalarAsync(
                connection,
                transaction,
                "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;"))!),
            Convert.ToInt64(
                await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;"),
                CultureInfo.InvariantCulture),
            expectedCampaignRegistryEpoch: null,
            DateTimeOffset.UtcNow,
            [
                CovenantMutationFixture.AgentPropose(
                    campaignId,
                    key,
                    "The model suggests remembering this.",
                    expectedRevision: 0,
                    expectedKeyEpoch: 0),
            ]);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await kernel.ApplyBatchAsync(
            batch,
            new CovenantMutationTransaction(connection, transaction),
            erasureGate,
            Token);

        using (CovenantAgentErasureGate staging = CovenantAgentErasureGate.FromLatch(provider))
        {

            Assert.Equal(
                expectedClassification,
                await staging.ClassifyAsync(connection, transaction, CovenantScope.Campaign, campaignId, key, Token));

        }

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

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Token);

    }

    private static async Task<Guid> CreateCampaignAsync(ArcanumWebApplicationFactory factory, HttpClient client)
    {

        string path = Path.Combine(factory.TempHome, $"covenant-erasure-restart-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        RegisterCampaignRequest request = new("Covenant Erasure Restart", path, WorkspaceType.Campaign, null);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.RegisterCampaignRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/campaigns",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<CampaignDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        return body!.Data!.Id;

    }

}

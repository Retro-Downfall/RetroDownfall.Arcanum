using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal sealed class MemoryEvaluationSourceHarness(SagaStoreHarness saga) : IAsyncDisposable
{
    internal SagaStoreHarness Saga { get; } = saga;

    internal SqliteConnection Connection => (SqliteConnection)Saga.Connection;

    internal CovenantOperationGate Gate { get; private set; } = null!;

    internal CovenantTurnLease Lease { get; private set; } = null!;

    internal ArcanumInvocationContext Invocation { get; private set; } = null!;

    internal Guid? CampaignId { get; private set; }

    internal MemoryEvaluationSourceReader Reader => new(new FixedCovenantConnectionSource(Connection), Gate, TimeProvider.System);

    internal static async Task<MemoryEvaluationSourceHarness> CreateAsync() => new(await SagaStoreHarness.CreateAsync(annalsEnabled: true));

    internal async Task AdmitAsync(Guid? campaign = null)
    {
        CampaignId = campaign;

        FakeCovenantCampaignScopeProbe campaigns = new();

        if (campaign is { } id)
        {
            campaigns.Set(id, CovenantCampaignScopeState.Live);
        }

        Gate = CovenantOperationGateFixture.CreateGate(campaigns: campaigns);

        CanonicalCampaignContext context = campaign is { } bound
            ? CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(bound), 1, 1, null, null)
            : CanonicalCampaignContext.GlobalOnly;

        Lease = Require(await Gate.AcquireTurnAsync(context, CancellationToken.None));

        Invocation = Require(ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn, context,
            InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), Lease.Snapshot.RuntimeAuthorityGeneration, Lease.Snapshot.AuthorityEpoch)));
    }

    internal async Task<MemoryEvaluationValidatedRequest> RequestAsync(string memory)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "SELECT v.VersionId, v.ContentHashFormatCode, v.ContentHash FROM annal_claims c JOIN annal_heads h ON h.ClaimId = c.ClaimId JOIN annal_versions v ON v.VersionId = h.CurrentVersionId WHERE c.SubjectStoreCode = 1 AND c.SubjectId = $id";

        command.Parameters.AddWithValue("$id", memory);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        MemoryEvaluationSagaTarget target = new(memory, reader.GetString(0), (AnnalContentHashFormat)reader.GetInt32(1), new((byte[])reader[2]));

        return Require(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with
        {
            Scope = new(CampaignId is null ? MemoryEvaluationScopeKind.Global : MemoryEvaluationScopeKind.Campaign, CampaignId),
            Memory = new([], [target], [])
        }));
    }

    internal static T Require<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : string.Empty);

        return result.Value;
    }

    public async ValueTask DisposeAsync()
    {
        if (Lease is not null)
        {
            await Lease.DisposeAsync();
        }

        await Saga.DisposeAsync();
    }
}

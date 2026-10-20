using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class MemoryDelegationMutationTests
{
    [SkippableFact]
    public async Task Long_rest_suppression_fences_selected_source_without_changing_its_Annals_head()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await h.Source.Saga.Store.InsertAsync("a", "fact", DateTimeOffset.UtcNow.AddMinutes(-2), null, null, "extraction", h.Source.Saga.Embedding(), CancellationToken.None);

        var before = (await h.Source.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "source", CancellationToken.None))!;

        var survivor = (await h.Source.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "a", CancellationToken.None))!;

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);

        var effect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));

        string digest = Convert.ToHexString(AnnalContentDigest.ForSagaMemory("fact"));

        LongRestRequest request = new(LongRestTransformationKind.ExactDuplicates,
            [new("source", before.CurrentVersionId, digest), new("a", survivor.CurrentVersionId, digest)]);

        var service = new SagaLongRestService(h.Source.Saga.Context, TimeProvider.System, h.Coordinator);

        var applied = service.ApplyAsync(request, CancellationToken.None);

        try
        {
            Assert.True(root.Revocation.IsCancellationRequested);

            Assert.False(applied.IsCompleted);
        }
        finally
        {
            await effect.DisposeAsync();
        }

        Assert.Equal(LongRestOutcome.Applied, MemoryEvaluationSourceHarness.Require(await applied).Outcome);

        Assert.Equal(before.CurrentVersionId, (await h.Source.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "source", CancellationToken.None))!.CurrentVersionId);

        Assert.Empty(await h.Source.Saga.Store.GetByIdsAsync(["source"], CancellationToken.None));

        Assert.True((await h.Coordinator.AdmitEffectAsync(root)).IsFailure);
    }

    [SkippableFact]
    public async Task Global_closure_inside_owned_mutation_does_not_wait_on_itself_and_abort_reopens_fresh()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var old = await h.PrepareAsync();

        var mutation = await h.Coordinator.BeginMutationAsync();

        var gate = new GrimoireConnectionAdmissionGate(h.Time, new CovenantConnectionDrain(), GrimoireInstallationMaintenancePaths.Instance, h.Coordinator);

        var owner = new CovenantExclusiveRecoveryOwner(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, h.Digest);

        await using var closing = MemoryEvaluationSourceHarness.Require(gate.BeginOrResumeExclusive(owner));

        try
        {
            Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.True((await gate.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);
        }
        finally
        {
            await mutation.DisposeAsync();
        }

        Assert.True(old.Revocation.IsCancellationRequested);

        await using var fresh = await h.PrepareAsync(Guid.NewGuid());

        Assert.False(fresh.Revocation.IsCancellationRequested);
    }

    [SkippableFact]
    public async Task Global_closing_revokes_capsules_and_stage_one_waits_for_effect_disposal()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);

        var effect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));

        var gate = new GrimoireConnectionAdmissionGate(h.Time, new CovenantConnectionDrain(), GrimoireInstallationMaintenancePaths.Instance, h.Coordinator);

        var owner = new CovenantExclusiveRecoveryOwner(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, h.Digest);

        await using var closing = MemoryEvaluationSourceHarness.Require(gate.BeginOrResumeExclusive(owner));

        var drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        try
        {
            Assert.True(root.Revocation.IsCancellationRequested);

            Assert.False(drain.IsCompleted);

            Assert.True((await h.TryPrepareAsync(Guid.NewGuid())).IsFailure);
        }
        finally
        {
            await effect.DisposeAsync();
        }

        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
    }

    [SkippableTheory]
    [InlineData("delete")]
    [InlineData("delete-all")]
    [InlineData("insert")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("correct")]
    [InlineData("pin")]
    [InlineData("lexicon-upsert")]
    [InlineData("lexicon-delete")]
    public async Task Source_mutation_cancels_and_drains_admitted_provider_before_sql(string action)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);

        var effect = MemoryEvaluationSourceHarness.Require(await h.Coordinator.AdmitEffectAsync(root));

        ServiceCollection services = new();

        services.AddSingleton<IMemoryDelegationMutationBarrier>(h.Coordinator);

        await using var provider = services.BuildServiceProvider();

        var store = ActivatorUtilities.CreateInstance<SagaMemoryStore>(provider, h.Source.Saga.Context, h.Source.Saga.VectorAccelerator, h.Source.Saga.Options, MemoryErasureTestKeys.Isolated());

        var lexicon = ActivatorUtilities.CreateInstance<LexiconService>(provider, h.Source.Saga.Context, NullLogger<LexiconService>.Instance, h.Source.Saga.Options, MemoryErasureTestKeys.Isolated());

        Task deletion = action switch
        {
            "delete" => store.DeleteAsync("source", CancellationToken.None),
            "delete-all" => store.DeleteAllAsync(CancellationToken.None),
            "insert" => store.InsertAsync("another", "fact", DateTimeOffset.UtcNow, null, null, "extraction", h.Source.Saga.Embedding(), CancellationToken.None),
            "retire" => store.RetireAsync("source", AnnalContentDigest.ForSagaMemory("fact"), DateTimeOffset.UtcNow, CancellationToken.None),
            "reinstate" => store.ReinstateAsync("source", AnnalContentDigest.ForSagaMemory("fact"), h.Source.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None),
            "correct" => store.CorrectAsync("source", AnnalContentDigest.ForSagaMemory("fact"), "new fact", h.Source.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None),
            "pin" => store.SetPinAsync("source", true, DateTimeOffset.UtcNow, CancellationToken.None),
            "lexicon-upsert" => lexicon.UpsertAsync("Compiler", "tool", ["fact"], LexiconScope.Global),
            "lexicon-delete" => lexicon.DeleteByNameAsync("Compiler", LexiconScope.Global),
            _ => throw new InvalidOperationException()
        };

        try
        {
            Assert.True(effect.Revocation.IsCancellationRequested);

            Assert.False(deletion.IsCompleted);

            await using var query = h.Source.Connection.CreateCommand();

            query.CommandText = "SELECT COUNT(*) FROM saga_memories WHERE Id = 'source'";

            Assert.Equal(1L, await query.ExecuteScalarAsync());
        }
        finally
        {
            await effect.DisposeAsync();
        }

        await deletion;

        Assert.True((await h.Coordinator.AdmitEffectAsync(root)).IsFailure);
    }
}

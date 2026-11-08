using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_shared_engine_admits_each_unit_with_its_own_Guild_work_and_effect_lease(bool sterile)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildLocalExecutionInput input = LocalInput(row) with { Mode = sterile ? GuildLocalExecutionMode.Sterile : GuildLocalExecutionMode.Apprentice };

        GuildLocalExecutionBinding binding = await CreateLocalAsync(f, row, input);

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestDriver driver = await f.DriverAsync(row);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), ReceiptDigest("unit admission"),
            row.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        GrimoireConnectionAdmissionGate engineGate = new(TimeProvider.System);

        int disposedWork = 0, disposedEffects = 0;

        RecordingGrimoireWorkAdmissionGate admission = new(engineGate)
        {
            BeforeWorkLeaseDisposalAsync = () => { Interlocked.Increment(ref disposedWork); return ValueTask.CompletedTask; },
            BeforeEffectGroupDisposalAsync = () => { Interlocked.Increment(ref disposedEffects); return ValueTask.CompletedTask; },
        };

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        using ApprenticeService service = new(services.GetRequiredService<IServiceScopeFactory>(), new EngineOptions(), new(),
            NullLogger<ApprenticeService>.Instance, admission);

        EngineRuntime runtime = new();

        AdmissionObservedRuntime observed = new(admission, runtime);

        var result = await service.ExecuteGuildAsync(binding, input, new(f.Store, driver, dispatch.Value), observed,
            new TestGuildEngineWait(static (_, _, _) => throw new InvalidOperationException("Unexpected input wait")), default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("public final answer", result.Value.Text);

        Assert.Equal(sterile ? 0 : 3, runtime.TurnCalls);

        Assert.Equal(sterile ? 1 : 0, runtime.SterileCalls);

        Assert.Equal(0, observed.UnadmittedCalls);

        int expectedUnits = sterile ? 1 : 3;

        Assert.Equal(expectedUnits, admission.RequestedWorkKinds.Count);

        Assert.All(admission.RequestedWorkKinds, kind => Assert.Equal(GrimoireWorkKind.GuildStorage, kind));

        Assert.Equal(expectedUnits, admission.IssuedWorkLeases.Count);

        // Planning and step execution own groups; the step's review shares that unit.
        // The final read-only completion check owns Work but starts no external effect.
        int expectedEffects = sterile ? 1 : 2;

        Assert.Equal(expectedEffects, admission.EffectGroupLeases.Count);

        Assert.Equal(expectedEffects, admission.EffectGroupAttempts);

        for (int index = 0; index < expectedUnits; index++)
        {
            if (index < expectedEffects)
                Assert.Same(admission.IssuedWorkLeases[index], admission.EffectGroupLeases[index]);

            for (int prior = 0; prior < index; prior++)
                Assert.NotSame(admission.IssuedWorkLeases[prior], admission.IssuedWorkLeases[index]);
        }

        Assert.Equal(expectedUnits, disposedWork);

        Assert.Equal(expectedEffects, disposedEffects);

        // The real gate must close without any work remaining, independent of AsyncLocal
        // restoration when the awaited engine returns to its caller's execution context.
        await GrimoireGateTestEdges.DrainedAbortAsync(engineGate);
    }

    private sealed class AdmissionObservedRuntime(RecordingGrimoireWorkAdmissionGate admission, EngineRuntime inner)
        : IGuildLocalInferenceRuntime
    {
        internal int UnadmittedCalls;

        public Task<Result<PromptTurnResult>> ExecuteAsync(GuildLocalInferenceExecution execution,
            GuildLocalExecutionInput input, CancellationToken cancellationToken)
        {
            if (!admission.HasActiveWorkLease || !admission.HasActiveEffectGroup) UnadmittedCalls++;

            return inner.ExecuteAsync(execution, input, cancellationToken);
        }

        public Task<Result<PromptTurnResult>> ExecuteTurnAsync(GuildLocalInferenceExecution execution,
            GuildLocalExecutionInput input, GuildLocalInferenceTurn turn, CancellationToken cancellationToken)
        {
            if (!admission.HasActiveWorkLease || !admission.HasActiveEffectGroup) UnadmittedCalls++;

            return inner.ExecuteTurnAsync(execution, input, turn, cancellationToken);
        }
    }
}

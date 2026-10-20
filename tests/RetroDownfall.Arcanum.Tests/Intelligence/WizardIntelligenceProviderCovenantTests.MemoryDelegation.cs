using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Core.Lexicon;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryDelegation;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderCovenantTests
{
    [SkippableFact]
    public async Task Capsule_child_has_selected_fragments_only_and_no_ordinary_authority()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest));
        await using var sourceUse = MemoryEvaluationSourceHarness.Require(await child.AdmitEffectAsync());
        var selected = child.Sources;
        var protection = new MemoryDelegationProtection(child.Sensitivity, child.Revocation, child.AdmitEffectAsync);
        using var isolated = SubagentExecutionAmbient.EnterChild(new(10000, null), child, protection, selected);
        var chat = Scripted();
        var wizard = Wizard(chat, Gate(confirmed: 4, proposed: 4));
        var request = new PingRequest("task", ModelName, "", DisableMcpTools: true, DisableAllTools: true,
            UnattendedMode: true, SkipSpellRouting: true, StatelessMessages: [new CoreChatMessage("user", "task")]);
        var result = await wizard.ExecutePromptAsync(request, ArcanumInvocationContext.None, default);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        string body = Assert.Single(chat.SystemPrompts);
        foreach (var fragment in selected.Fragments) Assert.Contains(fragment.Text, body, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmed.0", body, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(chat.Options).Tools ?? []);
        Assert.False(ArcanumInvocationContext.None.CanReadCovenant);
        Assert.False(ArcanumInvocationContext.None.CanStageCovenantMutation);
    }
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capsule_disclosure_refusal_or_revocation_after_ack_prevents_provider_IO(bool revokeAfterAck)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest));
        await using var sourceUse = MemoryEvaluationSourceHarness.Require(await child.AdmitEffectAsync());
        var protection = new MemoryDelegationProtection(child.Sensitivity, child.Revocation, child.AdmitEffectAsync);
        using var isolated = SubagentExecutionAmbient.EnterChild(new(10000, null), child, protection, child.Sources);
        var journal = new CapsuleJournal(revokeAfterAck, () => h.Time.Advance(TimeSpan.FromMinutes(5)));
        var gate = new CovenantDispatchGate(new PlannedContextProvider(CovenantCompositionFixture.Plan(0, 0)), journal,
            new CleanSensitivityLedger(), new EstablishedAuthority(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CovenantDispatchGate>.Instance);
        var chat = Scripted();
        var wizard = Wizard(chat, gate);
        var result = await wizard.ExecutePromptAsync(new("task", ModelName, "", DisableMcpTools: true,
            DisableAllTools: true, UnattendedMode: true, SkipSpellRouting: true,
            StatelessMessages: [new CoreChatMessage("user", "task")]), ArcanumInvocationContext.None, default);
        Assert.True(result.IsFailure);
        Assert.Equal(1, journal.Calls);
        Assert.Empty(chat.SystemPrompts);
    }

    private sealed class CapsuleJournal(bool revokeAfterAck, Action revoke) : RetroDownfall.Arcanum.Core.Covenant.ICovenantDisclosureJournal
    {
        internal int Calls { get; private set; }
        public ValueTask<RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Core.Covenant.CovenantDisclosureReceipt>> AcknowledgeAsync(
            RetroDownfall.Arcanum.Core.Covenant.CovenantDisclosureDraft draft,
            RetroDownfall.Arcanum.Core.Covenant.CovenantDisclosureEffectCategory category,
            RetroDownfall.Arcanum.Core.Covenant.ProviderCallSensitivity sensitivity, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity.None, sensitivity.Level);
            if (!revokeAfterAck) return ValueTask.FromResult(RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Core.Covenant.CovenantDisclosureReceipt>.Failure(
                new(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Covenant.Unavailable, "Disclosure unavailable.")));
            revoke();
            return ValueTask.FromResult(RetroDownfall.Arcanum.Core.Primitives.Result<RetroDownfall.Arcanum.Core.Covenant.CovenantDisclosureReceipt>.Success(new(draft, 1)));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_proposed_bytes_are_data_attributed_and_never_silently_dropped(bool insufficientBudget)
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(CancellationToken.None);

        Guid campaign = CovenantOperationGateFixture.CampaignOne;

        await fixture.AddCampaignAsync(campaign, "proposed", CancellationToken.None);

        var proposed = await fixture.SeedHeadAsync(CovenantScope.Campaign, campaign, "selected", CovenantLane.Proposed,
            CovenantOperation.Set, "model writable background", CancellationToken.None);

        FakeCovenantAvailability availability = new();

        Guid dataset = await fixture.ReadDatasetGenerationAsync(CancellationToken.None);

        availability.Mutate(value => value with { DatasetGeneration = dataset });

        var gate = CovenantOperationGateFixture.CreateGate(availability: availability);

        var context = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(campaign), 1, 1, null, null);

        await using var lease = MemoryEvaluationSourceHarness.Require(await gate.AcquireTurnAsync(context, CancellationToken.None));

        var parent = MemoryEvaluationSourceHarness.Require(ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
            context, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), lease.Snapshot.RuntimeAuthorityGeneration, lease.Snapshot.AuthorityEpoch)));

        MemoryDelegationGrant grant = new(new(MemoryEvaluationScopeKind.Campaign, campaign), new([new(proposed.EntryId, proposed.VersionId, proposed.Compiled!.FragmentHash)], [], []));

        MemoryDelegationSourceReader reader = new(new FixedCovenantConnectionSource(fixture.Connection), gate, TimeProvider.System, fixture.Store);


        var coordinator = new RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation.MemoryDelegationCoordinator(TimeProvider.System);
        Guid rootId = Guid.NewGuid();
        await using var root = MemoryEvaluationSourceHarness.Require(await coordinator.PrepareAsync(rootId, grant, parent, lease, reader));
        var child = MemoryEvaluationSourceHarness.Require(await coordinator.ConsumeAsync(root, rootId, Guid.NewGuid(), new(new byte[32])));
        await using var sourceUse = MemoryEvaluationSourceHarness.Require(await child.AdmitEffectAsync());
        var protection = new MemoryDelegationProtection(child.Sensitivity, child.Revocation, child.AdmitEffectAsync);
        using var isolated = SubagentExecutionAmbient.EnterChild(new(10000, null), child, protection, child.Sources);
        var chat = Scripted();
        var audit = new FakeInferenceAuditLogger();
        var wizard = Wizard(chat, Gate(0, 0), audit, insufficientBudget ? 128 : 32768);
        var result = await wizard.ExecutePromptAsync(new("task", ModelName, "", DisableAllTools: true, DisableMcpTools: true,
            SkipSpellRouting: true, UnattendedMode: true, StatelessMessages: [new CoreChatMessage("user", "task")]),
            ArcanumInvocationContext.None, default, new InferenceAuditContext { RequestType = "test" });
        if (insufficientBudget)
        {
            Assert.True(result.IsFailure);
            Assert.Empty(chat.SystemPrompts);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Error.Message);
            var text = Assert.Single(chat.SystemPrompts);
            Assert.Contains("## DATA", text);
            Assert.Contains("model writable background", text);
            var tokens = Assert.Single(LastBreakdown(audit).Components, row => row.Source == ContextTokenSource.CovenantProposed);
            Assert.True(tokens.Estimate.TokenCount > 0);
        }
    }
    [SkippableFact]
    public async Task Derived_only_ordinary_child_uses_explicit_input_without_source_materialization()
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync(sensitive: true);
        await using var root = await h.PrepareAsync();
        var child = MemoryEvaluationSourceHarness.Require(await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest));
        string selectedFragment = Assert.Single(child.Sources.Fragments).Text;
        await child.DisposeAsync();
        var binding = new MemoryDelegationRootBinding(new PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        var protection = binding.CaptureOutputProtection();
        using var isolated = SubagentExecutionAmbient.EnterChild(new(10000, null), protection: protection);
        var chat = Scripted();
        var wizard = Wizard(chat, Gate(4, 4));
        var result = await wizard.ExecutePromptAsync(new("explicit derived task", ModelName, "", DisableAllTools: true, DisableMcpTools: true,
            SkipSpellRouting: true, UnattendedMode: true, StatelessMessages: [new CoreChatMessage("user", "explicit derived task")]),
            ArcanumInvocationContext.None, default);
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(ContentSensitivity.CovenantDerived, result.Value.Sensitivity!.Level);
        Assert.Equal(root.Sensitivity.Provenance, result.Value.Sensitivity.Provenance);
        Assert.Null(SubagentExecutionAmbient.MemoryDelegation);
        Assert.DoesNotContain(selectedFragment, Assert.Single(chat.SystemPrompts), StringComparison.Ordinal);
        Assert.DoesNotContain("confirmed.0", Assert.Single(chat.SystemPrompts), StringComparison.Ordinal);
        Assert.Empty(Assert.Single(chat.Options).Tools ?? []);
        Assert.False(root.Revocation.IsCancellationRequested);
    }
}

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Drives one <c>retire_covenant</c> call through the production tool pipeline.
/// </summary>
/// <remarks>
/// The pipeline is real, the retirement policy is real, the egress guard is real. What is stood up around
/// them is the staging material a live provider attempt publishes — a collector, an admission receipt,
/// and a head probe — because what is under test is what the pipeline does with a retirement, not how
/// a provider attempt comes to have one.
///
/// <para>The disclosure journal records order rather than writing rows, which is the whole assertion
/// the guard exists for: the receipt commits, and only then does the effect run.</para>
/// </remarks>
internal sealed class CovenantRetirementHarness
{

    internal const string Disclosure = "the operator prefers repo-root builds";

    private readonly ToolExecutionPipeline _pipeline;

    private readonly OrderingJournal _journal;

    private readonly StubProbe _probe = new();

    internal CovenantRetirementHarness(IWard ward, WardPolicySettings wardPolicy)
    {

        _journal = new OrderingJournal(Order);

        _pipeline = new ToolExecutionPipeline(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings
            {
                Security = new SecuritySettings { Ward = wardPolicy },
            }),
            ward,
            new PermissiveSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance,
            covenantEgressGuard: new CovenantToolEgressGuard(_journal),
            covenantAuthority: new StubAuthority());

    }

    /// <summary>Everything that happened, in the order it happened.</summary>
    internal List<string> Order { get; } = [];

    internal List<ToolExecutionEvent> ObservedEvents { get; } = [];

    internal bool ToolRan { get; private set; }

    internal CovenantDisclosureDraft? DisclosureDraft => _journal.Drafts.SingleOrDefault();

    /// <summary>Set to make the disclosure journal refuse, so the guard stops the effect.</summary>
    internal Error? JournalFailure
    {

        get => _journal.Failure;

        init => _journal.Failure = value;

    }

    /// <summary>Set to make the head probe refuse the target, as it does for a pinned head.</summary>
    internal Error? PreflightFailure
    {

        get => _probe.Failure;

        init => _probe.Failure = value;

    }

    /// <summary>
    /// Publishes the staging material a live provider attempt publishes.
    /// </summary>
    internal IDisposable PublishStaging(CovenantRetirementPreflight? preflight = null)
    {

        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

        _probe.Preflight = preflight ?? Preflight();

        return CovenantToolStagingAmbient.Push(new CovenantToolStagingContext(
            new CovenantMutationCollector(Guid.CreateVersion7(), plan.Digest, CovenantTask6Fixture.BranchId),
            CovenantCapabilityFixtures.Campaign(),
            CovenantCapabilityFixtures.Admission(plan),
            CovenantCapabilityFixtures.Materialization(),
            _probe,
            false,
            new CovenantToolCapabilityRegistry(),
            CancellationToken.None));

    }

    internal Task<ToolExecutionPipeline.ProcessedToolCall> RetireAsync(
        InvocationAttendance attendance = InvocationAttendance.Attended,
        bool eligibleInvocation = true,
        string key = "preference.builds",
        string lane = "Confirmed") =>
        _pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent(
                "call-retire",
                CovenantToolNames.RetireCovenant,
                new Dictionary<string, object?>
                {
                    ["key"] = key,
                    ["lane"] = lane,
                }),
            new PingRequest("hi", WorkingDirectory: "/tmp"),
            new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(Run, CovenantToolNames.RetireCovenant)],
            },
            activeSpell: null,
            sessionId: "session-1",
            new ToolExecutionPipeline.TurnContext
            {
                Invocation = eligibleInvocation
                    ? EligibleInvocation(attendance)
                    : ArcanumInvocationContext.None,
            },
            suppressInvocationFailures: false,
            CancellationToken.None,
            observer: evt =>
            {
                ObservedEvents.Add(evt);

                return ValueTask.CompletedTask;
            });

    /// <summary>
    /// The exact canonical target the live probe resolved before staging.
    /// </summary>
    internal static CovenantRetirementPreflight Preflight(
        string normalizedKey = "preference.builds",
        CovenantLane lane = CovenantLane.Confirmed)
    {

        CovenantRetirementPreflight baseline = CovenantCapabilityFixtures.RetirementPreflight();

        return new CovenantRetirementPreflight(
            baseline.EntryId,
            baseline.VersionId,
            lane,
            baseline.TargetLaneRevision,
            normalizedKey,
            $"- {normalizedKey}: \"{Disclosure}\"",
            baseline.RenderedHash,
            globalFallbackApplies: true,
            baseline.KeyEpoch,
            baseline.PreflightBodyDigest);

    }

    private static ArcanumInvocationContext EligibleInvocation(InvocationAttendance attendance) =>
        ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CovenantCapabilityFixtures.Campaign(),
            attendance,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(
                Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301"),
                runtimeAuthorityGeneration: 1,
                authorityEpoch: 7)).Value;

    private string Run()
    {

        ToolRan = true;

        Order.Add("tool");

        return "staged";

    }

    /// <summary>A journal that records that it committed, so the guard's ordering is observable.</summary>
    private sealed class OrderingJournal(List<string> order) : ICovenantDisclosureJournal
    {

        internal Error? Failure { get; set; }

        internal List<CovenantDisclosureDraft> Drafts { get; } = [];

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken)
        {

            if (Failure is { } failure)
            {

                return ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Failure(failure));

            }

            order.Add("disclosed");

            Drafts.Add(draft);

            return ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Success(
                new CovenantDisclosureReceipt(draft, allocatedSubjectOrdinal: 1)));

        }

    }

    private sealed class StubProbe : ICovenantTurnHeadProbe
    {

        internal Error? Failure { get; set; }

        internal CovenantRetirementPreflight? Preflight { get; set; }

        public ValueTask<Result<CovenantLaneHeadProbe>> ProbeAsync(
            CovenantLane lane,
            string normalizedKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantLaneHeadProbe>.Success(
                CovenantLaneHeadProbe.NotFound(CovenantOperationScope.Global, lane, normalizedKey, 0)));

        public ValueTask<Result<CovenantSectionOccupancy>> ProbeSectionAsync(
            CovenantLane lane,
            System.Collections.Immutable.ImmutableArray<string> excludedKeys,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<CovenantQuotaSnapshot>> ProbeScopeAsync(
            System.Collections.Immutable.ImmutableArray<string> excludedKeys,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<CovenantRetirementPreflight>> ResolveRetirementPreflightAsync(
            CovenantLane lane,
            string normalizedKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Failure is { } failure
                ? Result<CovenantRetirementPreflight>.Failure(failure)
                : Result<CovenantRetirementPreflight>.Success(Preflight!));

    }

    private sealed class PermissiveSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class StubAuthority : ICovenantAuthoritySnapshotProvider
    {

        public CovenantAuthoritySnapshot? Current { get; } = new(
            1,
            Guid.Parse("11111111-2222-3333-4444-555555555555").ToString("D"),
            AuthorityEpoch: 11,
            MasterKeyVersion: 1,
            RecoveryEnvelopeEpoch: 1,
            CovenantHostToolsState.Clean,
            null);

    }

}

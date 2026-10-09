using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class CampaignRollupMaintenanceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_provider_selection_from_an_intermediate_configuration_is_refused_before_payment(bool protectedInput, bool changedModel)
    {
        Harness harness = new();

        harness.Store.Protected = protectedInput;

        harness.Settings.DefaultModel = null;

        ProviderSettings acceptedProvider = harness.Settings.Providers[0];

        ProviderSettings intermediateProvider = new()
        {
            Name = acceptedProvider.Name,
            Endpoint = changedModel ? acceptedProvider.Endpoint : "https://different-provider.test/v1",
            ContextWindowLimit = acceptedProvider.ContextWindowLimit,
            Models = [changedModel ? "intermediate-model" : "model"],
        };

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability? capability = protectedInput ? await harness.CapabilityAsync(authority) : null;

        harness.Clients.BeforeResolve = () => harness.Settings.Providers = [intermediateProvider];

        harness.Clients.BeforeReturn = () => harness.Settings.Providers = [acceptedProvider];

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(
            Session, null, capability, CancellationToken.None);

        Assert.True(result.IsFailure || result.Value.Deferred);

        Assert.Same(acceptedProvider, harness.Settings.Providers[0]);

        Assert.Equal(0, harness.Provider.Calls);

        Assert.Empty(harness.Journal.Drafts);

        Assert.Empty(harness.Runs.Operations);

        Assert.Empty(harness.Store.Outputs);
    }

    [Fact]
    public async Task Campaign_maintenance_uses_only_the_closed_plaintext_estimator_and_executor()
    {
        Harness harness = new();

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(
            Session, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(0, harness.Estimator.GenericCalls);

        Assert.Equal(0, harness.Executor.GenericCalls);

        Assert.Equal(1, harness.Estimator.ClosedCalls);

        Assert.Equal(1, harness.Executor.ClosedCalls);

        Assert.Equal(1, harness.Provider.Calls);

        Assert.Single(harness.Store.Outputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_configuration_change_after_reservation_prevents_the_first_physical_send(bool protectedInput)
    {
        Harness harness = new();

        harness.Store.Protected = protectedInput;

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability? capability = protectedInput ? await harness.CapabilityAsync(authority) : null;

        harness.Runs.OnStart = () => harness.Settings.DefaultModel = "changed-model";

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, capability, CancellationToken.None);

        Assert.True(result.IsFailure || result.Value.Deferred);

        Assert.Equal(0, harness.Provider.Calls);

        Assert.Empty(harness.Journal.Drafts);

        Assert.Empty(harness.Store.Outputs);

        Assert.Empty(harness.Runs.Operations);
    }

    [Fact]
    public async Task A_capability_freezes_the_configuration_accepted_before_pending_maintenance_resumes()
    {
        Harness harness = new();

        harness.Store.Protected = true;

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability capability = await harness.CapabilityAsync(authority);

        harness.Settings.FastModel = "changed-fast-model";

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, capability, CancellationToken.None);

        Assert.True(result.IsFailure || result.Value.Deferred);

        Assert.Equal(0, harness.Provider.Calls);

        Assert.Equal(0, harness.Store.Reads);

        Assert.Empty(harness.Journal.Drafts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_configuration_change_after_paid_invalid_output_prevents_a_correction_send(bool protectedInput)
    {
        Harness harness = new();

        harness.Store.Protected = protectedInput;

        harness.Provider.Responses.Enqueue("{\"summary\":\"\"}");

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability? capability = protectedInput ? await harness.CapabilityAsync(authority) : null;

        harness.Provider.BeforeResponse = () => harness.Settings.FastModel = "changed-fast-model";

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, capability, CancellationToken.None);

        Assert.True(result.IsFailure || result.Value.Deferred);

        Assert.Equal(1, harness.Provider.Calls);

        Assert.Equal(protectedInput ? 1 : 0, harness.Journal.Drafts.Count);

        Assert.Single(harness.Runs.Operations);

        Assert.Single(harness.Audit.Records);

        Assert.Empty(harness.Store.Outputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_configuration_change_after_one_paid_page_prevents_the_next_page_dispatch(bool protectedInput)
    {
        Harness harness = new();

        harness.Store.Protected = protectedInput;

        harness.Store.RemainingPages = 2;

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability? capability = protectedInput ? await harness.CapabilityAsync(authority) : null;

        harness.Provider.BeforeResponse = () => harness.Settings.FastModel = "changed-fast-model";

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, capability, CancellationToken.None);

        Assert.True(result.IsFailure || result.Value.Deferred);

        Assert.Equal(1, harness.Provider.Calls);

        Assert.Equal(protectedInput ? 1 : 0, harness.Journal.Drafts.Count);

        Assert.Single(harness.Store.Outputs);

        Assert.Single(harness.Runs.Operations);
    }

    [Fact]
    public async Task Every_completed_physical_response_including_correction_has_one_content_free_audit_record()
    {
        Harness harness = new();

        harness.Provider.Responses.Enqueue("{\"summary\":\"/Users/mat/private.txt\"}");

        harness.Provider.Responses.Enqueue("{\"summary\":\"Keep the bounded contribution.\"}");

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, harness.Provider.Calls);

        Assert.Equal(2, harness.Audit.Records.Count);

        Assert.All(harness.Audit.Records, record =>
        {
            Assert.Equal("campaign-contribution", record.RequestType);

            Assert.Equal(Session.ToString(), record.SessionId);

            Assert.Equal(Campaign.ToString(), record.CampaignId);

            Assert.Equal(40, record.PromptTokens);

            Assert.Equal(10, record.CompletionTokens);

            Assert.Equal(50, record.TotalTokens);

            Assert.Equal(0, record.ToolCalls);

            Assert.Empty(record.ToolNames);

            Assert.Null(record.ToolArgumentsJson);

            Assert.Null(record.ClientIp);
        });

        Assert.All(harness.Audit.Tokens, token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task A_failed_audit_sink_does_not_undo_a_paid_summary_or_its_billing()
    {
        Harness harness = new();

        harness.Audit.Throw = true;

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Single(harness.Audit.Records);

        Assert.Single(harness.Store.Outputs);

        Assert.Single(harness.Runs.Operations);
    }

    private static readonly Guid Session = Guid.NewGuid();

    private static readonly Guid Campaign = CovenantOperationGateFixture.CampaignOne;

    [Fact]
    public async Task An_authority_for_a_different_Campaign_cannot_mint_a_request_maintenance_capability()
    {
        Harness harness = new();

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability valid = await harness.CapabilityAsync(authority);

        CanonicalCampaignContext other = CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignTwo);

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, other, InvocationAttendance.Attended,
            CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.AuthorityEpoch)).Value;

        Result<CampaignRollupMaintenanceCapability> result = CampaignRollupMaintenanceCapability.Create(
            invocation, valid.ClaimLease, other, authority, valid.ConfigurationDigest);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Disabled_rollups_do_not_read_resolve_or_dispatch()
    {
        Harness harness = new();

        harness.Settings.Features.CampaignRollups = false;

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.Deferred);

        Assert.Equal(0, harness.Store.Reads);

        Assert.Equal(0, harness.Clients.Resolutions);

        Assert.Equal(0, harness.Provider.Calls);
    }

    [Fact]
    public async Task Wrapped_attachment_blocks_are_omitted_before_dispatch_while_prose_and_logical_keys_survive()
    {
        Harness harness = new();

        string encodedLine = new('A', 64);

        harness.Store.Entries =
        [
            Entry(MessageRole.User, "Decision: keep design.pdf@v2 and build/index.ts.\n"
                + string.Join("\n", Enumerable.Repeat(encodedLine, 4))
                + "\nNext confirm the unresolved interface decision."),
        ];

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(1, harness.Provider.Calls);

        string payload = string.Join("\n", harness.Provider.Messages.Select(static message => message.Text));

        Assert.Contains("design.pdf@v2", payload, StringComparison.Ordinal);

        Assert.Contains("build/index.ts", payload, StringComparison.Ordinal);

        Assert.Contains("unresolved interface decision", payload, StringComparison.Ordinal);

        Assert.DoesNotContain(encodedLine, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Short_clean_sessions_publish_structured_native_tail_without_paths_tools_or_attachment_bytes()
    {
        Harness harness = new();

        harness.Store.Entries =
        [
            Entry(MessageRole.User, "Decision: keep design.pdf@v2 and build/index.ts; host /Users/mat/secret.txt data:text/plain;base64,SGVsbG8= " + new string('A', 300)),
            Entry(MessageRole.Assistant, "Use a bounded contribution."),
            Entry(MessageRole.Tool, "PRIVATE ATTACHMENT EXCERPT"),
        ];

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, 17, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(1, result.Value.PublishedPages);

        Assert.Equal(17, harness.Store.LastUpperSequence);

        Assert.Single(harness.Store.Outputs);

        Assert.Equal("Keep the bounded contribution.", harness.Store.Outputs[0]);

        string payload = string.Join("\n", harness.Provider.Messages.Select(static message => message.Text));

        Assert.Contains("design.pdf@v2", payload);

        Assert.Contains("build/index.ts", payload);

        Assert.DoesNotContain("/Users/mat", payload);

        Assert.DoesNotContain("data:", payload);

        Assert.DoesNotContain(new string('A', 300), payload);

        Assert.DoesNotContain("PRIVATE ATTACHMENT EXCERPT", payload);

        Assert.Empty(harness.Provider.Options!.Tools!);

        Assert.Equal(CampaignRollupLimits.OutputTokens, harness.Provider.Options.MaxOutputTokens);

        Assert.IsType<ChatResponseFormatJson>(harness.Provider.Options.ResponseFormat);

        Assert.Empty(harness.Journal.Drafts);

        Assert.Single(harness.Runs.Operations);

        Assert.All(harness.Store.PublishTokens, static token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task Protected_background_work_defers_before_text_materialization_or_provider_resolution()
    {
        Harness harness = new();

        harness.Store.Protected = true;

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.Deferred);

        Assert.Equal(0, harness.Store.ProtectedPayloadReads);

        Assert.Equal(0, harness.Clients.Resolutions);

        Assert.Equal(0, harness.Provider.Calls);
    }

    [Fact]
    public async Task Protected_send_is_claim_bound_disclosed_before_transport_and_committed_with_exact_attempt()
    {
        Harness harness = new();

        harness.Store.Protected = true;

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability capability = await harness.CapabilityAsync(authority);

        harness.Provider.BeforeResponse = () =>
        {
            Assert.Single(harness.Journal.Drafts);

            Assert.NotNull(harness.Checkpoints.Disclosure);

            Assert.Equal(CovenantDisclosureEffectCategory.MaintenanceAttempt, harness.Journal.Categories.Single());

            Assert.Equal(capability.ClaimLease.Claim.ClaimId, harness.Journal.Drafts.Single().SubjectId);
        };

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, capability, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(capability.ClaimLease.Claim.PreRequestHistoryRevision, harness.Store.LastUpperSequence);

        CampaignMaintenancePublication publication = Assert.IsType<CampaignMaintenancePublication>(harness.Store.Publications.Single());

        Assert.Equal(Session, publication.Attempt.Identity.SourceSessionId);

        Assert.Equal(harness.Checkpoints.Disclosure, publication.DisclosureReceiptDigest);

        Assert.Equal(1UL, publication.Attempt.PhysicalProviderAttemptOrdinal);

        Assert.Equal(CovenantMaintenanceStep.CampaignContribution, publication.Attempt.Identity.Step);

        Assert.True((await harness.Service.ProcessSessionAsync(Session, null, capability, CancellationToken.None)).IsFailure);

        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task Invalid_paid_output_gets_only_one_explicit_correction_with_distinct_disclosures_and_billing()
    {
        Harness harness = new();

        harness.Store.Protected = true;

        harness.Provider.Responses.Enqueue("{\"summary\":\"/Users/mat/secret.txt\"}");

        harness.Provider.Responses.Enqueue("{\"summary\":\"Keep the bounded contribution.\"}");

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(
            Session, null, await harness.CapabilityAsync(authority), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(2, harness.Provider.Calls);

        Assert.Equal(2, harness.Journal.Drafts.Count);

        Assert.NotEqual(harness.Journal.Drafts[0].EffectIdentityDigest, harness.Journal.Drafts[1].EffectIdentityDigest);

        Assert.Equal(2, harness.Runs.Operations.Count);

        Assert.Single(harness.Store.Outputs);

        Assert.DoesNotContain("/Users/mat", string.Join("\n", harness.Provider.Messages.Select(static message => message.Text)));

        Assert.Equal(2UL, harness.Store.Publications.Single()!.Attempt.PhysicalProviderAttemptOrdinal);
    }

    [Fact]
    public async Task Two_invalid_paid_responses_publish_nothing_and_do_not_advance()
    {
        Harness harness = new();

        harness.Provider.Responses.Enqueue("not-json");

        harness.Provider.Responses.Enqueue("{\"summary\":\"\"}");

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(2, harness.Provider.Calls);

        Assert.Equal(2, harness.Runs.Operations.Count);

        Assert.Empty(harness.Store.Outputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_advances_only_when_a_successful_paid_response_has_already_returned(bool afterPaid)
    {
        Harness harness = new();

        using CancellationTokenSource cancellation = new();

        harness.Provider.BeforeResponse = () =>
        {
            cancellation.Cancel();

            if (!afterPaid)
            {
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };

        if (afterPaid)
        {
            Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, cancellation.Token);

            Assert.True(result.IsSuccess);

            Assert.Single(harness.Store.Outputs);

            Assert.Single(harness.Runs.Operations);

            Assert.All(harness.Store.PublishTokens, static token => Assert.False(token.CanBeCanceled));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.ProcessSessionAsync(Session, null, null, cancellation.Token));

            Assert.Empty(harness.Store.Outputs);

            Assert.Empty(harness.Runs.Operations);
        }

        Assert.Single(harness.Runs.Completed);
    }

    [Fact]
    public async Task A_feature_change_after_reservation_but_before_transport_prevents_disclosure_and_send()
    {
        Harness harness = new();

        harness.Store.Protected = true;

        await using CovenantTurnLease authority = await harness.AuthorityAsync();

        CampaignRollupMaintenanceCapability capability = await harness.CapabilityAsync(authority);

        harness.Runs.OnStart = () => harness.Settings.Features.CampaignRollups = false;

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, capability, CancellationToken.None);

        Assert.True(result.IsFailure || result.Value.Deferred);

        Assert.Empty(harness.Journal.Drafts);

        Assert.Equal(0, harness.Provider.Calls);

        Assert.Empty(harness.Store.Outputs);
    }

    [Fact]
    public async Task All_bounded_pages_are_processed_without_a_lifetime_ceiling()
    {
        Harness harness = new();

        harness.Store.RemainingPages = 7;

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessCampaignAsync(Campaign, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(7, result.Value.PublishedPages);

        Assert.Equal(7, harness.Provider.Calls);

        Assert.Equal(7, harness.Runs.Operations.Count);
    }

    [Fact]
    public async Task Opaque_transport_is_a_typed_deferred_result_without_dispatch()
    {
        Harness harness = new();

        harness.Clients.Unsupported = true;

        Result<CampaignRollupMaintenanceResult> result = await harness.Service.ProcessSessionAsync(Session, null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.Deferred);

        Assert.Equal(0, harness.Provider.Calls);

        Assert.Empty(harness.Store.Outputs);
    }

    private static CampaignContributionEntry Entry(MessageRole role, string content) =>
        new(Guid.NewGuid(), 1, (int)role, content, DateTimeOffset.UtcNow, Digest(1), Digest(2), ContentSensitivity.None, GenerationProvenance.CreateExact([]));

    private static CovenantDigest Digest(byte value) => CovenantOperationGateFixture.Digest(value);

    private sealed class Harness
    {
        public ArcanumSettings Settings { get; } = new()
        {
            DefaultModel = "model",
            Features = new FeatureSettings { CampaignRollups = true, Covenant = true },
            Providers =
            [
                new()
                {
                    Name = "provider",
                    Endpoint = "https://provider.test/v1",
                    ContextWindowLimit = 32768,
                    Models = ["model"],
                },
            ],
        };

        public StoreDouble Store { get; } = new();

        public CheckpointDouble Checkpoints { get; } = new();

        public ProviderDouble Provider { get; } = new();

        public JournalDouble Journal { get; } = new();

        public RunDouble Runs { get; } = new();

        public AuditDouble Audit { get; } = new();

        public ClientDouble Clients { get; }

        public CampaignRollupMaintenance Service { get; }

        public EstimatorProbe Estimator { get; }

        public ExecutorProbe Executor { get; }

        public Harness()
        {
            Clients = new(Provider, Settings);

            ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

            Estimator = new(estimator);

            Executor = new(new ModelCallExecutor(estimator));

            TestOptionsMonitor<ArcanumSettings> options = new(Settings);

            Service = new(Store, Checkpoints, Clients, Executor, Estimator,
                options, new BudgetMonitor(null!, null!, options, NullLogger<BudgetMonitor>.Instance), Journal,
                NullLogger<CampaignRollupMaintenance>.Instance, Runs, new UnconfiguredReservations(), Audit);
        }

        public async Task<CovenantTurnLease> AuthorityAsync() =>
            (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CovenantOperationGateFixture.CampaignContext(Campaign), CancellationToken.None)).Value;

        public async Task<CampaignRollupMaintenanceCapability> CapabilityAsync(CovenantTurnLease authority)
        {
            CanonicalCampaignContext campaign = CovenantOperationGateFixture.CampaignContext(Campaign);

            ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
                ArcanumExecutionSurface.SessionBackedOperatorTurn, campaign, InvocationAttendance.Attended,
                CovenantContextPolicy.Default, ToolPolicy.NoTools,
                CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.AuthorityEpoch)).Value;

            Guid boot = Guid.NewGuid();

            SessionTurnClaim claim = new(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), Session, SessionTurnSurface.Intelligence,
                Digest(1), Digest(2), SessionTurnClaimState.PendingMaintenance, null, 17, 0, 0, Guid.NewGuid(), null, null, boot, 0, 0, null, DateTimeOffset.UtcNow);

            SessionTurnClaimLease lease = new(claim, SessionTurnClaimDisposition.Created, Guid.NewGuid(), Guid.NewGuid(), boot, DateTimeOffset.UtcNow.AddMinutes(5));

            Result<CampaignRollupMaintenanceCapability> result = await Service.CreateCapabilityAsync(invocation, lease, campaign, authority, CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            return result.Value;
        }
    }

    private sealed class EstimatorProbe(IModelTokenEstimator inner) : IModelTokenEstimator
    {
        public int GenericCalls { get; private set; }

        public int ClosedCalls { get; private set; }

        public ContextTokenBreakdown EstimateCampaignMaintenance(ProviderSettings provider, string canonicalModel, CampaignMaintenancePayload payload)
        {
            ClosedCalls++;

            return inner.EstimateCampaignMaintenance(provider, canonicalModel, payload);
        }

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            GenericCalls++;

            return inner.EstimateContext(request);
        }

        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string canonicalModel) => inner.ResolveProfile(provider, canonicalModel);

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string canonicalModel) => inner.ResolveEffectiveProfile(provider, canonicalModel);

        public TokenEstimate EstimateText(ProviderSettings provider, string canonicalModel, string? text) => inner.EstimateText(provider, canonicalModel, text);
    }

    private sealed class ExecutorProbe(IModelCallExecutor inner) : IModelCallExecutor
    {
        public int GenericCalls { get; private set; }

        public int ClosedCalls { get; private set; }

        public Task<Result<CampaignMaintenanceCallResult>> ExecuteCampaignMaintenanceAsync(IChatClient chatClient, CampaignMaintenancePayload payload,
            ITurnBudget budget, Func<CancellationToken, Task> beforeSend, CancellationToken cancellationToken, ModelCallContext context)
        {
            ClosedCalls++;

            return inner.ExecuteCampaignMaintenanceAsync(chatClient, payload, budget, beforeSend, cancellationToken, context);
        }

        public Task<ModelCallOutcome> ExecuteBufferedAsync(IChatClient chatClient, IList<ChatMessage> messages, ChatOptions options,
            ITurnBudget budget, ModelCallPurpose purpose, CancellationToken cancellationToken, ModelCallContext? context)
        {
            GenericCalls++;

            return inner.ExecuteBufferedAsync(chatClient, messages, options, budget, purpose, cancellationToken, context);
        }

        public IAsyncEnumerable<ModelCallUpdate> ExecuteStreamingAsync(IChatClient chatClient, IList<ChatMessage> messages, ChatOptions options,
            ITurnBudget budget, ModelCallPurpose purpose, CancellationToken cancellationToken, ModelCallContext? context) =>
            inner.ExecuteStreamingAsync(chatClient, messages, options, budget, purpose, cancellationToken, context);
    }

    private sealed class AuditDouble : IInferenceAuditLogger
    {
        public bool Throw { get; set; }

        public List<InferenceAuditRecord> Records { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task LogAsync(InferenceAuditRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);

            Tokens.Add(cancellationToken);

            return Throw ? Task.FromException(new IOException("Audit sink unavailable.")) : Task.CompletedTask;
        }

        public Task<IReadOnlyList<InferenceAuditRecord>> QueryAsync(DateTimeOffset? from, DateTimeOffset? to, string? model, string? sessionId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<AuditQueryPage<InferenceAuditRecord>>> QueryPageAsync(DateTimeOffset? from, DateTimeOffset? to, string? model, string? sessionId, int limit, string? cursor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnconfiguredReservations : IBudgetReservationService
    {
        public Task<Result<BudgetReservation>> ReserveAsync(BudgetReservationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<BudgetReservation>.Success(new(
                Guid.NewGuid(), request.RunId, request.BudgetPeriod, 0m, 0m,
                BudgetReservationStatus.Released, request.ExpiresAt, DateTimeOffset.UtcNow)));

        public Task<Result> AdjustAsync(Guid reservationId, decimal reservedUsd, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<Result> RecheckDailyLimitAsync(Guid reservationId, decimal delegatedSpendUsd, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task ExtendExpiryAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class ProviderDouble : IChatClient
    {
        public int Calls { get; private set; }

        public List<ChatMessage> Messages { get; private set; } = [];

        public ChatOptions? Options { get; private set; }

        public Queue<string> Responses { get; } = new();

        public Action? BeforeResponse { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            Messages = [.. messages];

            Options = options;

            BeforeResponse?.Invoke();

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                Responses.Count > 0 ? Responses.Dequeue() : "{\"summary\":\"Keep the bounded contribution.\"}"))
            {
                Usage = new UsageDetails { InputTokenCount = 40, OutputTokenCount = 10 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ClientDouble(ProviderDouble provider, ArcanumSettings settings) : ICampaignRollupProviderClientFactory
    {
        public int Resolutions { get; private set; }

        public bool Unsupported { get; set; }

        public Action? BeforeResolve { get; set; }

        public Action? BeforeReturn { get; set; }

        public Task<Result<ChatClientLease?>> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
        {
            Resolutions++;

            BeforeResolve?.Invoke();

            ProviderSettings selected = settings.Providers[0];

            ChatClientLease? resolved = Unsupported ? null : new(provider, selected, selected.Models[0].Name, null);

            BeforeReturn?.Invoke();

            return Task.FromResult(Result<ChatClientLease?>.Success(resolved));
        }
    }

    private sealed class RunDouble : ITurnRunWriter
    {
        public List<BillableOperationRecord> Operations { get; } = [];

        public List<InferenceRunStatus> Completed { get; } = [];

        public Action? OnStart { get; set; }

        public Task<Guid> StartRunAsync(InferenceRunStart start, CancellationToken cancellationToken = default)
        {
            OnStart?.Invoke();

            return Task.FromResult(Guid.NewGuid());
        }

        public Task CompleteRunAsync(Guid runId, InferenceRunStatus status, CancellationToken cancellationToken = default)
        {
            Completed.Add(status);

            return Task.CompletedTask;
        }

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<Guid> RecordBillableOperationAsync(BillableOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.CanBeCanceled);

            Operations.Add(operation);

            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class JournalDouble : ICovenantDisclosureJournal
    {
        public List<CovenantDisclosureDraft> Drafts { get; } = [];

        public List<CovenantDisclosureEffectCategory> Categories { get; } = [];

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category, ProviderCallSensitivity sensitivity, CancellationToken cancellationToken)
        {
            Drafts.Add(draft);

            Categories.Add(category);

            return ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Success(new(draft, (ulong)Drafts.Count)));
        }
    }

    private sealed class CheckpointDouble : ICampaignMaintenanceCheckpointStore
    {
        private ulong _ordinal;

        public CovenantDigest? Disclosure { get; private set; }

        public Task<Result> ValidateClaimAsync(SessionTurnClaimLease claimLease, CanonicalCampaignContext campaign, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<SessionTurnClaimLease>> RenewClaimAsync(SessionTurnClaimLease claimLease, CanonicalCampaignContext campaign, CancellationToken cancellationToken) =>
            Task.FromResult(Result<SessionTurnClaimLease>.Success(claimLease));

        public Task<Result<CampaignMaintenanceAttempt>> PrepareAttemptAsync(CampaignMaintenanceIdentity identity, CovenantDigest providerCallDigest, CancellationToken cancellationToken) =>
            Task.FromResult(Result<CampaignMaintenanceAttempt>.Success(new(identity, 0, ++_ordinal, providerCallDigest, CovenantMaintenanceCheckpoint.Prepared, null, null)));

        public Task<Result> ValidateAttemptAsync(CampaignMaintenanceAttempt attempt, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<CampaignMaintenanceAttempt>> RecordDisclosureAsync(CampaignMaintenanceAttempt attempt, CovenantDigest disclosureReceiptDigest, CancellationToken cancellationToken)
        {
            Disclosure = disclosureReceiptDigest;

            return Task.FromResult(Result<CampaignMaintenanceAttempt>.Success(attempt with { CheckpointRevision = attempt.CheckpointRevision + 1 }));
        }
    }

    private sealed class StoreDouble : ICampaignRollupStore
    {
        public bool Protected { get; set; }

        public int RemainingPages { get; set; } = 1;

        public int Reads { get; private set; }

        public int ProtectedPayloadReads { get; private set; }

        public long? LastUpperSequence { get; private set; }

        public ImmutableArray<CampaignContributionEntry> Entries { get; set; } = [Entry(MessageRole.User, "Use a bounded contribution.")];

        public List<string> Outputs { get; } = [];

        public List<CampaignMaintenancePublication?> Publications { get; } = [];

        public List<CancellationToken> PublishTokens { get; } = [];

        private GenerationProvenance Provenance => GenerationProvenance.CreateExact(Protected ? [Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")] : []);

        private ContentSensitivity Sensitivity => Protected ? ContentSensitivity.CovenantDerived : ContentSensitivity.None;

        public Task<Result<CampaignContributionInput?>> PrepareContributionAsync(Guid sessionId, long? throughSequence, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken)
        {
            Reads++;

            LastUpperSequence = throughSequence;

            if (Protected && authority is null)
            {
                return Task.FromResult(Result<CampaignContributionInput?>.Failure(new(ErrorCodes.Covenant.ForbiddenAuthority, "Protected payload withheld.")));
            }

            if (Protected)
            {
                ProtectedPayloadReads++;
            }

            return Task.FromResult(Result<CampaignContributionInput?>.Success(RemainingPages == 0 ? null :
                new(Session, Campaign, Outputs.Count, 0, 0, 0, null,
                    [.. Entries.Select(entry => entry with { Sensitivity = Sensitivity, Provenance = Provenance })], Digest((byte)(Outputs.Count + 1)), RemainingPages > 1)));
        }

        public Task<Result<CampaignRollupInput?>> PrepareRollupAsync(Guid campaignId, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken)
        {
            Reads++;

            return Task.FromResult(Result<CampaignRollupInput?>.Success(RemainingPages == 0 ? null :
                new(Campaign, Outputs.Count, 0, null, [Artifact(null)], Digest((byte)(Outputs.Count + 1)), Session, RemainingPages > 1)));
        }

        public Task<Result<CampaignRollupArtifact>> PublishContributionAsync(CampaignContributionInput input, string content, CampaignMaintenancePublication? maintenancePublication, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) =>
            PublishAsync(Session, content, maintenancePublication, cancellationToken);

        public Task<Result<CampaignRollupArtifact>> PublishRollupAsync(CampaignRollupInput input, string content, CampaignMaintenancePublication? maintenancePublication, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) =>
            PublishAsync(null, content, maintenancePublication, cancellationToken);

        private Task<Result<CampaignRollupArtifact>> PublishAsync(Guid? session, string content, CampaignMaintenancePublication? publication, CancellationToken token)
        {
            Outputs.Add(content);

            Publications.Add(publication);

            PublishTokens.Add(token);

            RemainingPages--;

            return Task.FromResult(Result<CampaignRollupArtifact>.Success(Artifact(session)));
        }

        private CampaignRollupArtifact Artifact(Guid? session) => new(Guid.NewGuid(), Campaign, session, Outputs.Count, "Existing clean summary.", Digest(3),
            Sensitivity, Provenance, CovenantDigests.Sensitivity(Provenance.ToDigestInput(Sensitivity)), 0, Digest(1), 17, 1);

        public Task<Result<CampaignRollupArtifact?>> ReadCurrentAsync(Guid campaignId, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> ValidateAsync(CampaignRollupArtifact artifact, ICovenantSnapshotReadLease? authority, CancellationToken cancellationToken) => Task.FromResult(Result.Success());

        public Task<Result<CampaignRollupStatus>> ReadStatusAsync(Guid campaignId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindPendingContributionsAsync(DateTimeOffset idleCutoffUtc, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindPendingContributionsForCampaignAsync(Guid campaignId, DateTimeOffset idleCutoffUtc, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, "provider")]
    [InlineData(true, "provider")]
    [InlineData(false, "spell")]
    [InlineData(true, "spell")]
    [InlineData(false, "begin")]
    [InlineData(true, "begin")]
    [InlineData(false, "structured")]
    [InlineData(true, "structured")]
    [InlineData(false, "exception")]
    [InlineData(true, "exception")]
    [InlineData(false, "maintenance")]
    [InlineData(true, "maintenance")]
    [InlineData(false, "deferred")]
    [InlineData(true, "deferred")]
    [InlineData(true, "deferred-cancel")]
    [InlineData(false, "capability")]
    [InlineData(true, "capability")]
    [InlineData(true, "capability-cancel")]
    [InlineData(true, "capability-pre-cancel")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "fallback-success")]
    [InlineData(true, "fallback-success")]
    public async Task Claimed_terminal_failure_replays_the_same_typed_error_from_the_real_store(bool streaming, string mode)
    {
        bool capability = mode.StartsWith("capability", StringComparison.Ordinal);

        bool fallback = capability || mode is "deferred" or "deferred-cancel" or "fallback-success";

        await using LongRestMaintenanceTests.MaintenanceFixture fixture = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            ownWorker: false, configure: configuration =>
            {
                configuration.Features.Lexicon = false;

                if (fallback) configuration.Providers = [configuration.Providers[0], configuration.Providers[0] with { Name = "backup" }];

                if (capability)
                {
                    configuration.Features.Reasoning = true;

                    configuration.Providers[0].Models = [new("model", Reasoning: new ModelReasoningSettings { WireDialect = ReasoningWireDialect.OpenRouter })];

                    configuration.Providers[1].Models = ["model"];
                }
            });

        await Task7ParkAsync(fixture);

        await using Task7ProtectedPreparation preparation = await Task7ProtectedPreparation.CreateAsync(fixture);

        Task7SuccessfulMaintenanceResponses(fixture, preparation);

        using CancellationTokenSource caller = new();

        ScriptingChatClient chat = new();

        if (mode == "success")
        {
            chat.EnqueueText("successful reply");

            chat.EnqueueStreamTokens("successful reply");
        }
        else if (mode == "structured")
        {
            chat.EnqueueText("{}");

            chat.EnqueueText("{}");

            chat.EnqueueStreamTokens("{}");
        }
        else
        {
            chat.EnqueueException(fallback ? new HttpRequestException("controlled connectivity failure") : new InvalidOperationException("controlled provider failure"));

            chat.EnqueueImmediateStreamFailure(fallback ? new HttpRequestException("controlled connectivity failure") : new InvalidOperationException("controlled provider failure"));
        }

        if (mode == "fallback-success")
        {
            chat.EnqueueText("successful reply");

            chat.EnqueueStreamTokens("successful reply");
        }

        int liveFallbackChecks = 0;

        IProviderHealthTracker tracker = new TerminalReplayHealthProbe(() =>
        {
            if (!capability) return;

            SessionTurnRequestIdentity identity = Assert.Single(preparation.Claims.Acquisitions);

            SessionTurnClaim live = preparation.NativeClaims.FindAsync(identity.OriginInstallationId, identity.ClientTurnId, CancellationToken.None).AsTask().GetAwaiter().GetResult().Value!;

            Assert.Equal(SessionTurnClaimState.Begun, live.State);

            Assert.False(live.IsTerminal);

            Assert.Null(live.Outcome);

            Assert.NotNull(live.OwnerBootId);

            Assert.NotNull(live.AssistantEntryId);

            liveFallbackChecks++;

            if (mode == "capability-pre-cancel")
            {
                caller.Cancel();

                caller.Token.ThrowIfCancellationRequested();
            }
        });

        WizardIntelligenceProvider wizard = CreateWizard(chat, fixture.Options.CurrentValue,
            new FakeGrimoireRepository { Session = new Session { Id = preparation.Request.SessionId!.Value } },
            db: fixture.H.Saga.Context, ordinaryConnections: FixtureOrdinaryConnectionFactory.For(fixture.H.Saga.Context),
            campaignRollups: mode == "maintenance"
                ? ActivatorUtilities.CreateInstance<CampaignRollupTurnPreparer>(preparation.Services, new TerminalReplayMaintenanceRefusal())
                : preparation.Preparer,
            claimedBeginStore: mode is "begin" or "exception" or "cancel" ? new TerminalReplayBeginRefusal(preparation.Begin, mode == "exception", mode == "cancel" ? caller : null) : preparation.Begin,
            claims: preparation.Claims, turnCommitter: preparation.Repository,
            factory: fallback ? new TerminalReplayFallbackFactory(chat, fixture.Options.CurrentValue.Providers[0], mode != "fallback-success") : null,
            healthTracker: fallback ? tracker : null);

        PingRequest request = preparation.Request with
        {
            Reasoning = capability ? new ReasoningRequestOptions(Effort: ReasoningEffortLevel.High) : null,
            SkipSpellRouting = mode != "spell",
            OverrideSpellName = mode == "spell" ? "missing-spell-for-typed-replay" : null,
            ResponseFormat = mode == "structured" ? "json_schema" : null,
            ResponseFormatJsonSchema = mode == "structured"
                ? StrictJsonSchema(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}""", AdHocJson.Options)) : null,
        };

        async Task<string> ExecuteAsync(CancellationToken token = default)
        {
            if (!streaming)
            {
                Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, fixture.Invocation, token);

                if (mode is "success" or "fallback-success")
                {
                    Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

                    return result.Value.Text;
                }

                Assert.True(result.IsFailure);

                return result.Error.Code;
            }

            List<IntelligenceEvent> frames = [];

            try
            {
                // The public transport pumps through channels and may finish cleanup before its
                // consumer sees the error. Drive the existing iterator directly to pin cancellation
                // after the capability frame and before its finally, without changing production.
                IAsyncEnumerable<IntelligenceEvent> stream = mode == "capability-cancel" && token.CanBeCanceled
                    ? (IAsyncEnumerable<IntelligenceEvent>)typeof(WizardIntelligenceProvider)
                        .GetMethod("StreamPromptCoreAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(wizard, [request, fixture.Invocation, token, null])!
                    : wizard.StreamPromptAsync(request, fixture.Invocation, token);

                await foreach (IntelligenceEvent frame in stream)
                {
                    frames.Add(frame);

                    if (mode is "deferred-cancel" or "capability-cancel" && token.CanBeCanceled && frame.Type == IntelligenceEventType.Error) caller.Cancel();
                }
            }
            catch (OperationCanceledException) when (mode is "deferred-cancel" or "capability-cancel" && caller.IsCancellationRequested
                && frames.Any(frame => frame.Type == IntelligenceEventType.Error))
            {
                // The caller stopped after observing the terminal error. Inspect its durable replay.
            }

            if (mode is "success" or "fallback-success")
            {
                Assert.DoesNotContain(frames, frame => frame.Type == IntelligenceEventType.Error);

                Assert.Contains(frames, frame => frame.Type == IntelligenceEventType.Result);

                return string.Concat(frames.Where(frame => frame.Type == IntelligenceEventType.Token).Select(frame => frame.Data));
            }

            return Assert.Single(frames, frame => frame.Type == IntelligenceEventType.Error).Data!;
        }

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            string original;

            if (mode is "cancel" or "capability-pre-cancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(caller.Token));

                original = ErrorCodes.Hub.SessionTurnRestoredInterrupted;
            }
            else
            {
                original = await ExecuteAsync(mode is "deferred-cancel" or "capability-cancel" ? caller.Token : default);
            }

            if (capability) Assert.Equal(1, liveFallbackChecks);

            SessionTurnRequestIdentity acquired = Assert.Single(preparation.Claims.Acquisitions);

            SessionTurnClaim claim = (await preparation.NativeClaims.FindAsync(acquired.OriginInstallationId, acquired.ClientTurnId, CancellationToken.None)).Value!;

            Assert.NotNull(claim);

            int callsBeforeReplay = chat.BufferedCallCount + chat.StreamingCallCount;

            string replay = await ExecuteAsync();

            Assert.Equal(mode switch
            {
                "spell" => ErrorCodes.Validation.SpellOverride,
                "begin" => ErrorCodes.Hub.ContextBudgetExceeded,
                "structured" => ErrorCodes.StructuredOutput.ValidationFailed,
                "maintenance" => ErrorCodes.Covenant.StaleSnapshot,
                "cancel" or "capability-pre-cancel" => ErrorCodes.Hub.SessionTurnRestoredInterrupted,
                "capability" or "capability-cancel" => ErrorCodes.Validation.UnsupportedReasoningControl,
                "success" or "fallback-success" => "successful reply",
                _ => ErrorCodes.Hub.Error,
            }, original);

            Assert.Equal(original, replay);

            if (mode is "success" or "fallback-success")
            {
                Assert.Equal(SessionTurnClaimState.Committed, claim.State);

                Assert.Null(claim.Outcome!.TerminalErrorCode);
            }
            else
            {
                Assert.Equal(original, claim.Outcome!.TerminalErrorCode);

                Assert.Equal(RetroDownfall.Arcanum.Api.Primitives.ArcanumErrorMapper.ResolveStatusCode(original), claim.Outcome.TerminalHttpStatus);
            }

            Assert.Equal(callsBeforeReplay, chat.BufferedCallCount + chat.StreamingCallCount);

            Assert.Equal(mode is "spell" or "begin" or "exception" or "maintenance" or "cancel" ? 0 : mode is "structured" or "fallback-success" ? 2 : 1, callsBeforeReplay);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    private sealed class TerminalReplayHealthProbe(Action failed) : IProviderHealthTracker
    {
        private readonly RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker _inner = new(
            NullLogger<RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker>.Instance);

        public event Action<ProviderHealthStatus>? HealthChanged
        {
            add => _inner.HealthChanged += value;
            remove => _inner.HealthChanged -= value;
        }

        public bool IsHealthy(string providerName) => _inner.IsHealthy(providerName);

        public void MarkFailed(string providerName)
        {
            failed();

            _inner.MarkFailed(providerName);
        }

        public void MarkHealthy(string providerName) => _inner.MarkHealthy(providerName);

        public IReadOnlyList<ProviderHealthStatus> GetAllStatuses() => _inner.GetAllStatuses();

        public void Remove(string providerName) => _inner.Remove(providerName);
    }

    private sealed class TerminalReplayBeginRefusal(ISessionTurnClaimBeginStore inner, bool throws, CancellationTokenSource? cancel) : ISessionTurnClaimBeginStore
    {
        public ValueTask<Result<SessionTurnClaimInputSnapshot>> ReadClaimInputAsync(Guid sessionId, CancellationToken cancellationToken) =>
            inner.ReadClaimInputAsync(sessionId, cancellationToken);

        public ValueTask<Result<AssistantReplyBeginReceipt>> BeginClaimedAssistantReplyAsync(SessionTurnClaimLease claimLease,
            CanonicalCampaignContext campaign, string prompt, string model, CancellationToken cancellationToken)
        {
            if (cancel is not null)
            {
                cancel.Cancel();

                cancellationToken.ThrowIfCancellationRequested();
            }

            return throws ? throw new InvalidOperationException("Controlled unexpected begin failure.")
                : ValueTask.FromResult(Result<AssistantReplyBeginReceipt>.Failure(new Error(ErrorCodes.Hub.ContextBudgetExceeded, "Controlled begin refusal.")));
        }
    }

    private sealed class TerminalReplayMaintenanceRefusal : ILongRestMaintenance
    {
        public Task<Result<LongRestMaintenanceCapability>> CreateCapabilityAsync(Guid operationId, ArcanumInvocationContext invocation,
            SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease lease, CancellationToken cancellationToken) =>
            Task.FromResult(Result<LongRestMaintenanceCapability>.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "Controlled maintenance refusal.")));
    }

    private sealed class TerminalReplayFallbackFactory(ScriptingChatClient chat, ProviderSettings first, bool finalFails) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(chat, first, "model", ownedHttpClient: null));

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken) =>
            provider.Name == first.Name || !finalFails
                ? Task.FromResult(new ChatClientLease(chat, provider, resolvedModel, ownedHttpClient: null))
                : throw new InvalidOperationException("Controlled final candidate lease failure.");
    }
}

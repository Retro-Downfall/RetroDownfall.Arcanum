using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextEviction_attachment_preflight_does_not_count_discarded_candidate_trims(bool streaming)
    {
        Guid sessionId = Guid.NewGuid();

        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];

        const string attachmentText = "explicit-attachment-evidence:";

        byte[] payload = Encoding.UTF8.GetBytes(attachmentText + new string('a', 2500));

        Dictionary<Guid, SessionAttachmentRecord> records = ids.ToDictionary(
            static id => id,
            id => new SessionAttachmentRecord(
                id, sessionId, EntryId: null, PendingTurnId: null, SessionAttachmentState.Bound,
                $"notes-{id:N}", $"notes-{id:N}.txt", Version: 1, RelativePath: $"noop/{id:N}",
                ContentSha256: id.ToString("N"), MimeType: "text/plain", ByteLength: payload.Length,
                SessionAttachmentKind.Text, DateTimeOffset.UtcNow));

        int reads = 0;

        NoOpSessionAttachmentStore store = new(
            records: records,
            readBytes: (_, _) =>
            {
                reads++;

                return Task.FromResult<ReadOnlyMemory<byte>>(payload);
            });

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { ContextWindowLimit = 9000 }],
        };

        ScriptingChatClient chat = new();

        if (streaming) chat.EnqueueStreamTokens("compared explicit attachments");
        else chat.EnqueueText("compared explicit attachments");

        EvidenceContextEstimator estimator = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)));

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings,
            grimoire: new FakeGrimoireRepository { Session = new Session { Id = sessionId, Entries = [] } },
            sessionAttachmentStore: store, modelTokenEstimator: estimator);

        PingRequest request = BaseRequest() with
        {
            Prompt = "Compare the explicit attachments.",
            SessionId = sessionId,
            AttachmentReferences = [.. ids],
            StatelessMessages =
            [
                new CoreChatMessage("user", "Read the old evidence."),
                new CoreChatMessage("assistant", string.Empty, ToolCalls: [new CoreToolCall("old-call", "historical_read", "{}")]),
                new CoreChatMessage("tool", "old-evidence:" + new string('z', 6500), ToolCallId: "old-call"),
                new CoreChatMessage("user", "Compare the explicit attachments."),
            ],
            SkipSpellRouting = true,
            DisableAllTools = true,
            MaxOutputTokens = 128,
        };

        IReadOnlyList<InferenceNotice> notices;

        if (streaming)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);

            notices = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Result).Notices;
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

            notices = result.Value.Notices;
        }

        Assert.Equal(2, reads);

        IReadOnlyList<MeAiChatMessage> sent = Assert.Single(streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls);

        Assert.Empty(sent.SelectMany(static message => message.Contents).OfType<FunctionResultContent>());

        Assert.Equal(2, sent.SelectMany(static message => message.Contents).OfType<TextContent>()
            .Count(content => content.Text.Contains(attachmentText, StringComparison.Ordinal)));

        Assert.Contains(estimator.Observations, static observation => observation.StartsWith("over:", StringComparison.Ordinal));

        ToolEvidenceEviction eviction = Assert.IsType<ToolEvidenceEviction>(
            Assert.Single(notices, static notice => notice.Code == InferenceNoticeCode.ToolEvidenceTrimmed).ToolEvidenceEviction);

        Assert.Equal(1, eviction.Exchanges);

        Assert.Equal(2, eviction.Messages);

        Assert.Equal(1, eviction.Calls);

        Assert.Equal(1, eviction.HistoricalExchanges);

        Assert.Empty(eviction.RecentRoundOrdinals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContextEviction_fallback_preserves_cumulative_removal_operations(bool streaming, bool secondAttemptTrims)
    {
        ProviderSettings first = DefaultProvider() with { Name = "first", ContextWindowLimit = 6000 };

        ProviderSettings second = DefaultProvider() with { Name = "second", ContextWindowLimit = secondAttemptTrims ? 6000 : 20000 };

        ArcanumSettings settings = DefaultSettings() with { Providers = [first, second] };

        ScriptingChatClient chat = new();

        if (streaming)
        {
            chat.EnqueueImmediateStreamFailure(new HttpRequestException("controlled precommit connectivity failure"));

            chat.EnqueueStreamTokens("fallback answer");
        }
        else
        {
            chat.EnqueueException(new HttpRequestException("controlled precommit connectivity failure"));

            chat.EnqueueText("fallback answer");
        }

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings,
            factory: new DiagnosticFallbackFactory(chat),
            healthTracker: new RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker(
                NullLogger<RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker>.Instance));

        PingRequest request = BaseRequest() with
        {
            Prompt = "Answer from the available evidence.",
            Model = ModelName,
            StatelessMessages =
            [
                new CoreChatMessage("user", "Read old evidence."),
                new CoreChatMessage("assistant", string.Empty, ToolCalls: [new CoreToolCall("old-call", "historical_read", "{}")]),
                new CoreChatMessage("tool", "old-evidence:" + new string('z', 6500), ToolCallId: "old-call"),
                new CoreChatMessage("user", "Answer from the available evidence."),
            ],
            SkipSpellRouting = true,
            DisableAllTools = true,
            MaxOutputTokens = 128,
        };

        IReadOnlyList<InferenceNotice> notices;

        int expectedOperations = secondAttemptTrims ? 2 : 1;

        if (streaming)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);

            IntelligenceEvent terminal = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Result);

            Assert.Equal("fallback answer", terminal.Message);

            notices = terminal.Notices;

            ToolEvidenceEviction firstNotice = Assert.IsType<ToolEvidenceEviction>(
                Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Status && frame.Notices.Count > 0)
                    .Notices.Single().ToolEvidenceEviction);

            Assert.Equal(1, firstNotice.Exchanges);

            ToolEvidenceEviction finalContext = Assert.IsType<ToolEvidenceEviction>(
                frames.Last(static frame => frame.Type == IntelligenceEventType.Context).ContextBreakdown!.ToolEvidenceEviction);

            Assert.Equal(expectedOperations, finalContext.Exchanges);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

            Assert.Equal("fallback answer", result.Value.Text);

            notices = result.Value.Notices;
        }

        List<IReadOnlyList<MeAiChatMessage>> calls = streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls;

        Assert.Equal(2, calls.Count);

        Assert.Empty(calls[0].SelectMany(static message => message.Contents).OfType<FunctionResultContent>());

        Assert.Equal(!secondAttemptTrims, calls[1].SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Any());

        ToolEvidenceEviction eviction = Assert.IsType<ToolEvidenceEviction>(
            Assert.Single(notices, static notice => notice.Code == InferenceNoticeCode.ToolEvidenceTrimmed).ToolEvidenceEviction);

        Assert.Equal(expectedOperations, eviction.Exchanges);

        Assert.Equal(expectedOperations * 2, eviction.Messages);

        Assert.Equal(expectedOperations, eviction.Calls);

        Assert.Equal(expectedOperations, eviction.HistoricalExchanges);

        Assert.Empty(eviction.RecentRoundOrdinals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContextEviction_structured_retry_publishes_new_eviction_before_blocked_retry_finishes(bool streaming, bool abandon)
    {
        const string toolName = "read_retry_evidence";

        const string finalAnswer = "{\"answer\":\"complete\"}";

        string invalidAnswer = "invalid-json:" + new string('x', 3000);

        ScriptingChatClient chat = new();

        TaskCompletionSource retryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource retryExited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool retryCancelled = false;

        TaskCompletionSource noticeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource contextObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        List<string[]> sentResultIds = [];

        chat.BeforeCall = () => sentResultIds.Add(chat.LastBufferedMessages
            .SelectMany(static message => message.Contents).OfType<FunctionResultContent>()
            .Select(static result => result.CallId).ToArray());

        if (streaming)
        {
            chat.EnqueueStreamToolCall(toolName, "retry-evidence-call");

            chat.EnqueueStreamTokens(invalidAnswer);

            chat.EnqueueBufferedResponder(async cancellationToken =>
            {
                retryEntered.TrySetResult();

                try
                {
                    await releaseRetry.Task.WaitAsync(cancellationToken);

                    return new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, finalAnswer));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    retryCancelled = true;

                    throw;
                }
                finally
                {
                    retryExited.TrySetResult();
                }
            });
        }
        else
        {
            chat.EnqueueToolCall(toolName, "retry-evidence-call");

            chat.EnqueueText(invalidAnswer);

            chat.EnqueueText(finalAnswer);
        }

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(AIFunctionFactory.Create(() => "retry-evidence:" + new string('a', 6500), toolName, "Reads bounded evidence."));

        ArcanumSettings settings = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { ContextWindowLimit = 14000 }],
        };

        EvidenceContextEstimator estimator = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)));

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, mcp: mcp, modelTokenEstimator: estimator);

        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""");

        PingRequest request = BaseRequest() with
        {
            Prompt = "Read the evidence and return an answer object.",
            SkipSpellRouting = true,
            MaxOutputTokens = 4096,
            ResponseFormat = "json_schema",
            ResponseFormatJsonSchema = StrictJsonSchema(schema.RootElement),
        };

        IReadOnlyList<InferenceNotice> notices;

        List<IntelligenceEvent> frames = [];

        if (streaming)
        {
            async Task ConsumeAsync()
            {
                await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None))
                {
                    frames.Add(frame);

                    if (frame.Type == IntelligenceEventType.Status && frame.Notices.Any(static notice => notice.Code == InferenceNoticeCode.ToolEvidenceTrimmed))
                        noticeObserved.TrySetResult();

                    if (frame.Type == IntelligenceEventType.Context && frame.ContextBreakdown?.ToolEvidenceEviction is { Exchanges: 1 })
                    {
                        contextObserved.TrySetResult();

                        if (abandon)
                        {
                            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                            break;
                        }
                    }
                }
            }

            Task consume = ConsumeAsync();

            try
            {
                await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                Task observations = Task.WhenAll(noticeObserved.Task, contextObserved.Task);

                Task observedBeforeCompletion = await Task.WhenAny(observations, Task.Delay(TimeSpan.FromSeconds(5)));

                Assert.Same(observations, observedBeforeCompletion);

                if (abandon)
                {
                    await consume.WaitAsync(TimeSpan.FromSeconds(10));

                    Assert.True(retryCancelled);

                    Assert.True(retryExited.Task.IsCompletedSuccessfully);
                }
            }
            finally
            {
                releaseRetry.TrySetResult();

                await consume.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);

            if (abandon)
            {
                Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Result);

                notices = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Status && frame.Notices.Count > 0).Notices;
            }
            else
            {
                IntelligenceEvent terminal = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Result);

                Assert.Equal(finalAnswer, terminal.Message);

                notices = terminal.Notices;
            }
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

            Assert.Equal(finalAnswer, result.Value.Text);

            notices = result.Value.Notices;
        }

        Assert.Equal(3, sentResultIds.Count);

        Assert.Contains("retry-evidence-call", sentResultIds[1]);

        Assert.Empty(sentResultIds[2]);

        ToolEvidenceEviction eviction = Assert.IsType<ToolEvidenceEviction>(
            Assert.Single(notices, static notice => notice.Code == InferenceNoticeCode.ToolEvidenceTrimmed).ToolEvidenceEviction);

        Assert.Equal(1, eviction.Exchanges);

        Assert.Equal(2, eviction.Messages);

        Assert.Equal(1, eviction.Calls);

        Assert.Equal(new[] { 1 }, eviction.RecentRoundOrdinals);

        Assert.Equal(0, eviction.HistoricalExchanges);
    }
}

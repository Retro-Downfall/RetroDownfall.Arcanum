using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    public static IEnumerable<object[]> RetrievalDiagnosticCases()
    {
        foreach (string surface in new[] { "preview", "buffered", "streamed" })
        {
            foreach (string outcome in new[] { "embedding-failed", "embedding-threw", "search-failed", "metadata-threw", "empty", "unavailable", "disabled", "included", "getter-threw", "unmaterialized", "missing-workspace" })
            {
                yield return [surface, outcome];
            }
        }
    }

    [Theory]
    [MemberData(nameof(RetrievalDiagnosticCases))]
    public async Task RetrievalDiagnostics_actual_outcome_is_visible_without_private_payloads(string surface, string outcome)
    {
        const string privateFailure = "PRIVATE_RETRIEVAL_FAILURE /private/secrets?token=HIDDEN\r\n";

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                CodebaseRetrieval = outcome != "disabled",
                Saga = false,
                AttachmentRetrieval = false,
                Tapestry = false,
                Lexicon = false,
            },
        };

        RetrievalDiagnosticWeave weave = new(outcome, privateFailure);

        FakeRagDivinationService divination = new()
        {
            Fail = outcome == "search-failed",
            Results = outcome is "metadata-threw" or "included" or "unmaterialized" ? [new("candidate", 0.99f, EmptyDivinationMetadata)] : [],
        };

        await using ArcanumDbContext db = CreateWorkspaceChunksDbContext(Path.Combine(_workspace.Root, $"diagnostic-{Guid.NewGuid():N}.db"));

        if (outcome is "included" or "unmaterialized")
        {
            await SeedWorkspaceFileChunkAsync(db, _workspace.Root, "src/diagnostic.cs", outcome == "unmaterialized" ? "other" : "candidate", "public class DiagnosticContent {}");
        }

        ScriptingChatClient chat = new();

        chat.EnqueueText("ordinary answer");

        chat.EnqueueStreamTokens("ordinary answer");

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave,
            divinationService: divination, db: db, logger: logger);

        ContextPreviewResult? preview = null;

        PingRequest request = BaseRequest() with { Prompt = "inspect the local diagnostic source", WorkingDirectory = outcome == "missing-workspace" ? "" : _workspace.Root, SkipSpellRouting = true, DisableAllTools = true };

        if (surface == "preview")
        {
            Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(new ContextPreviewRequest(
                Prompt: request.Prompt, Model: ModelName, WorkingDirectory: request.WorkingDirectory, AttachedFiles: request.AttachedFiles, DisableAllTools: true),
                InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            preview = result.Value;

            Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);
        }
        else if (surface == "buffered")
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal("ordinary answer", result.Value.Text);
        }
        else
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);
        }

        Assert.Equal(outcome is "disabled" or "unavailable" or "getter-threw" or "missing-workspace" ? 0 : 1, weave.Calls);

        bool failed = outcome is "embedding-failed" or "embedding-threw" or "search-failed" or "metadata-threw" or "getter-threw";

        TestLogEntry[] warnings = [.. logger.Entries.Where(static entry => entry.Level >= LogLevel.Warning)];

        if (failed)
        {
            TestLogEntry warning = Assert.Single(warnings);

            Assert.NotNull(warning.Properties);

            Assert.Contains("RetrievalReason", warning.Properties.Select(static pair => pair.Key));

            Assert.Contains("RetrievalSource", warning.Properties.Select(static pair => pair.Key));

            Assert.Equal(outcome is "embedding-failed" or "embedding-threw" ? "Embedding" : outcome == "getter-threw" ? "Availability" : outcome == "metadata-threw" ? "Materialization" : "Search",
                Assert.Single(warning.Properties, static pair => pair.Key == "RetrievalStage").Value?.ToString());

            Assert.Contains("continuing", warning.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(warnings);
        }

        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);

            Assert.DoesNotContain(privateFailure, entry.Message, StringComparison.Ordinal);

            Assert.DoesNotContain((entry.Properties ?? []).Select(static pair => pair.Value), value => value?.ToString()?.Contains("PRIVATE_RETRIEVAL", StringComparison.Ordinal) == true);
        });

        if (preview is not null)
        {
            ContextPreviewSource source = Assert.Single(preview.Sources, static source => source.Source == ContextTokenSource.WorkspaceRag);

            string reason = outcome switch
            {
                "disabled" => "disabled",
                "unavailable" => "unavailable",
                "embedding-failed" or "embedding-threw" or "getter-threw" => "embedding failed",
                "unmaterialized" => "no current source content",
                "missing-workspace" => "No Workspace",
                "search-failed" => "retrieval failed",
                "metadata-threw" => "retrieval failed",
                "empty" => "no hits",
                _ => "Included",
            };

            Assert.Contains(reason, source.Reason, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(outcome == "included", source.Included);

            if (outcome is not ("disabled" or "missing-workspace"))
            {
                ContextPreviewAuxiliaryCall call = Assert.Single(preview.AuxiliaryCalls, static call => call.Purpose == "retrieval-embedding");

                Assert.Equal(outcome is not ("unavailable" or "getter-threw"), call.Invoked);

                Assert.Contains(outcome is "embedding-failed" or "embedding-threw" or "getter-threw" ? "failed" : outcome == "unavailable" ? "unavailable" : "succeeded", call.Reason, StringComparison.OrdinalIgnoreCase);

                Assert.Equal(outcome is "embedding-failed" or "embedding-threw" or "unavailable" or "getter-threw" ? null : "actual-provider", call.Provider);

                Assert.Equal(outcome is "embedding-failed" or "embedding-threw" or "unavailable" or "getter-threw" ? null : "actual-model", call.Model);
            }
        }
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("buffered")]
    [InlineData("streamed")]
    public async Task RetrievalDiagnostics_caller_cancellation_keeps_its_token_and_never_reports_an_outage(string surface)
    {
        using CancellationTokenSource cancelled = new();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, CodebaseRetrieval = true, Lexicon = false },
        };

        RetrievalDiagnosticWeave weave = new("cancelled", "PRIVATE_CANCELLED") { Cancellation = cancelled };

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave, logger: logger);

        PingRequest request = BaseRequest() with { Prompt = "cancel local retrieval", WorkingDirectory = _workspace.Root, SkipSpellRouting = true, DisableAllTools = true };

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (surface == "preview")
            {
                _ = await wizard.PreviewContextAsync(new ContextPreviewRequest(Prompt: request.Prompt, Model: ModelName,
                    WorkingDirectory: request.WorkingDirectory, DisableAllTools: true), InvocationContexts.AttendedSession(), cancelled.Token);
            }
            else if (surface == "buffered")
            {
                _ = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), cancelled.Token);
            }
            else
            {
                await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), cancelled.Token)) { }
            }
        });

        Assert.Equal(cancelled.Token, failure.CancellationToken);

        Assert.Equal(1, weave.Calls);

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrievalDiagnostics_fallback_reuses_embedding_and_coalesces_only_its_source_warning(bool streamed)
    {
        ProviderSettings first = DefaultProvider() with { Name = "first" };

        ProviderSettings second = DefaultProvider() with { Name = "second" };

        ArcanumSettings configuration = DefaultSettings() with
        {
            Providers = [first, second],
            Features = DefaultSettings().Features with { Embeddings = true, CodebaseRetrieval = true, Lexicon = false },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueException(new HttpRequestException("PRIVATE_TRANSPORT_FAILURE"));

        chat.EnqueueText("fallback answer");

        chat.EnqueueImmediateStreamFailure(new HttpRequestException("PRIVATE_TRANSPORT_FAILURE"));

        chat.EnqueueStreamTokens("fallback answer");

        FakeRagWeaveService weave = new() { Available = true };

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave,
            divinationService: new FakeRagDivinationService { Fail = true },
            factory: new DiagnosticFallbackFactory(chat),
            healthTracker: new RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger<RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker>.Instance), logger: logger);

        PingRequest request = BaseRequest() with
        {
            Prompt = "retrieve once, retry the answer",
            Model = ModelName,
            WorkingDirectory = _workspace.Root,
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        if (streamed)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);

            Assert.Equal(2, chat.StreamingCallCount);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal("fallback answer", result.Value.Text);

            Assert.Equal(2, chat.BufferedCallCount);
        }

        Assert.Equal(1, weave.EmbedCallCount);

        TestLogEntry warning = Assert.Single(logger.Entries, static entry => entry.Properties?.Any(static pair => pair.Key == "RetrievalSource") == true);

        Assert.Equal(LogLevel.Warning, warning.Level);

        Assert.Equal("Workspace", Assert.Single(warning.Properties!, static pair => pair.Key == "RetrievalSource").Value?.ToString());

        Assert.DoesNotContain(logger.Entries, static entry => entry.Message.Contains("PRIVATE_TRANSPORT_FAILURE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetrievalDiagnostics_preview_None_denies_Tapestry_without_embedding_or_source_reads()
    {
        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, Tapestry = true, Lexicon = false },
        };

        FakeRagWeaveService weave = new() { Available = true };

        ScriptingChatClient chat = new();

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave, logger: logger);

        Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(new ContextPreviewRequest(
            Prompt: "inspect without memory authority", Model: ModelName, WorkingDirectory: _workspace.Root, DisableAllTools: true),
            InvocationContexts.NoContextSession(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        ContextPreviewSource source = Assert.Single(result.Value.Sources, static source => source.Source == ContextTokenSource.TapestryRag);

        Assert.False(source.Included);

        Assert.Contains("no eligible source scope", source.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, weave.EmbedCallCount);

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrievalDiagnostics_attachment_candidates_distinguish_failure_from_actual_admission_rejection(bool failed)
    {
        Guid sessionId = Guid.NewGuid();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, AttachmentRetrieval = true, Lexicon = false },
        };

        string body = string.Concat(Enumerable.Repeat("word ", 60_000));

        SessionAttachmentRetrievedChunk chunk = new("chunk", sessionId, Guid.NewGuid(), "logical", 1, "note.txt",
            "text/plain", "content-hash", 0, 0, body.Length, 1, 1, body, 0.99f);

        FakeSessionAttachmentRetrieval retrieval = new([chunk]);

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration,
            grimoire: new FakeGrimoireRepository { Session = new() { Id = sessionId, Entries = [] } },
            weaveService: new FakeRagWeaveService(), logger: logger,
            sessionAttachmentRetrieval: failed ? new ThrowingSessionAttachmentRetrieval(new IOException("PRIVATE_ATTACHMENT_FAILURE")) : retrieval);

        Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(new ContextPreviewRequest(
            Prompt: "inspect semantic attachment admission", Model: ModelName, SessionId: sessionId, DisableAllTools: true),
            InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        ContextPreviewSource source = Assert.Single(result.Value.Sources, static source => source.Source == ContextTokenSource.AttachmentRag);

        Assert.False(source.Included);

        Assert.Contains(failed ? "retrieval failed" : "retrieved but is not included", source.Reason, StringComparison.OrdinalIgnoreCase);

        TestLogEntry[] warnings = [.. logger.Entries.Where(static entry => entry.Level >= LogLevel.Warning)];

        if (failed)
        {
            TestLogEntry warning = Assert.Single(warnings);

            Assert.Equal(sessionId, Assert.Single(warning.Properties!, static pair => pair.Key == "SessionId").Value);

            Assert.Null(warning.Exception);

            Assert.DoesNotContain("PRIVATE_ATTACHMENT", warning.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal([sessionId], retrieval.SessionIds);

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) > configuration.ResolveEmbeddings().Attachments.MaxRetrievedBytes);

            Assert.Empty(warnings);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetrievalDiagnostics_combined_Saga_Lexicon_lane_preserves_independent_outcomes(bool failed)
    {
        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, Saga = true, Lexicon = false },
        };

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(new(), configuration, weaveService: new FakeRagWeaveService(),
            divinationService: new FakeRagDivinationService { Fail = failed }, logger: logger);

        Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(new ContextPreviewRequest(
            Prompt: "inspect independent memory outcomes", Model: ModelName, DisableAllTools: true),
            InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsSuccess);

        ContextPreviewSource source = Assert.Single(result.Value.Sources, static source => source.Source == ContextTokenSource.LexiconSaga);

        Assert.False(source.Included);

        Assert.Contains(failed ? "Saga: Source retrieval failed" : "Saga: Retrieval succeeded with no hits", source.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Lexicon: Skipped because the source feature is disabled", source.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(failed ? 1 : 0, logger.Entries.Count(static entry => entry.Level >= LogLevel.Warning));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("buffered")]
    [InlineData("streamed")]
    public async Task RetrievalDiagnostics_registration_caller_cancellation_is_not_an_outage_and_stops_source_work(string surface)
    {
        using CancellationTokenSource caller = new();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, CodebaseRetrieval = true, Lexicon = false },
        };

        FakeRagWeaveService weave = new();

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        ScriptingChatClient chat = new();

        RegistrationCancellation indexing = new(caller);

        FakeRagDivinationService divination = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave,
            workspaceIndexingService: indexing, divinationService: divination, logger: logger);

        PingRequest request = BaseRequest() with
        {
            Prompt = "cancel during registration",
            WorkingDirectory = _workspace.Root,
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (surface == "preview")
            {
                _ = await wizard.PreviewContextAsync(new ContextPreviewRequest(Prompt: request.Prompt, Model: ModelName,
                    WorkingDirectory: _workspace.Root, DisableAllTools: true), InvocationContexts.AttendedSession(), caller.Token);
            }
            else if (surface == "buffered")
            {
                _ = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), caller.Token);
            }
            else
            {
                await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), caller.Token)) { }
            }
        });

        Assert.Equal(caller.Token, failure.CancellationToken);

        Assert.Equal(1, indexing.Registrations);

        Assert.Equal(0, indexing.Resolutions);

        Assert.Null(divination.LastWorkspaceScope);

        Assert.Equal(1, weave.EmbedCallCount);

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Properties?.Any(static pair => pair.Key == "RetrievalSource") == true);
    }

    private sealed class RegistrationCancellation(CancellationTokenSource caller) : IWorkspaceIndexingService
    {
        public int Registrations { get; private set; }

        public int Resolutions { get; private set; }

        public void RegisterWorkspace(string workspacePath)
        {
            Registrations++;

            caller.Cancel();

            caller.Token.ThrowIfCancellationRequested();
        }

        public string ResolveIndexedWorkspacePath(string workspacePath)
        {
            Resolutions++;

            return workspacePath;
        }

        public void UnregisterWorkspace(string workspacePath) => throw new NotSupportedException();

        public Result<WorkspaceIndexQueueDisposition> QueueIndexNow(string workspacePath) => throw new NotSupportedException();
    }

    public static IEnumerable<object[]> RetrievalOwnedSkipCases()
    {
        foreach (string surface in new[] { "preview", "buffered", "streamed" })
        {
            foreach (string origin in new[] { "owned", "wrapper", "spoof", "forwarded" })
            {
                yield return [surface, origin];
            }
        }
    }

    [Theory]
    [MemberData(nameof(RetrievalOwnedSkipCases))]
    public async Task RetrievalDiagnostics_disabled_transition_requires_the_actual_owned_service_result(string surface, string origin)
    {
        const string canary = "PRIVATE_DISABLED_LOOKALIKE /private/error?secret=HIDDEN";

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, CodebaseRetrieval = true, Lexicon = false },
        };

        configuration.Integrations.Embeddings.Provider = "configured-provider";

        configuration.Integrations.Embeddings.Model = "configured-model";

        ReloadingEmbeddingMonitor monitor = new(configuration, origin != "forwarded");

        RefusingDiagnosticEmbeddingFactory factory = new(canary);

        PreviewLedgerWriter writer = new();

        using ServiceProvider services = new ServiceCollection().AddSingleton<RetroDownfall.Arcanum.Core.Storage.ITurnRunWriter>(writer).BuildServiceProvider();

        WeaveService actual = new(factory, monitor, services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WeaveService>.Instance);

        IWeaveService weave = origin == "wrapper" ? new ForwardingDiagnosticWeave(actual)
            : origin == "spoof" ? new RetrievalDiagnosticWeave("matching-disabled-code", canary) : actual;

        ScriptingChatClient chat = new();

        chat.EnqueueText("ordinary answer");

        chat.EnqueueStreamTokens("ordinary answer");

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave, logger: logger);

        ContextPreviewResult? preview = null;

        PingRequest request = BaseRequest() with
        {
            Prompt = "inspect a configuration transition",
            WorkingDirectory = _workspace.Root,
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        if (surface == "preview")
        {
            Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(new ContextPreviewRequest(Prompt: request.Prompt,
                Model: ModelName, WorkingDirectory: _workspace.Root, DisableAllTools: true), InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            preview = result.Value;
        }
        else if (surface == "buffered")
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal("ordinary answer", result.Value.Text);
        }
        else
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);
        }

        Assert.Equal(origin == "forwarded" ? 1 : 0, factory.Resolutions);

        Assert.Empty(writer.Operations);

        if (origin is "owned" or "wrapper")
        {
            Assert.Equal(2, monitor.Reads);

            Assert.Empty(writer.Runs);
        }

        TestLogEntry[] warnings = [.. logger.Entries.Where(static entry => entry.Level >= LogLevel.Warning)];

        if (origin == "owned") Assert.Empty(warnings);
        else Assert.Single(warnings);

        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);

            Assert.DoesNotContain(canary, entry.Message, StringComparison.Ordinal);
        });

        if (preview is not null)
        {
            ContextPreviewSource source = Assert.Single(preview.Sources, static source => source.Source == ContextTokenSource.WorkspaceRag);

            Assert.False(source.Included);

            Assert.Contains(origin == "owned" ? "unavailable" : "embedding failed", source.Reason, StringComparison.OrdinalIgnoreCase);

            ContextPreviewAuxiliaryCall call = Assert.Single(preview.AuxiliaryCalls, static call => call.Purpose == "retrieval-embedding");

            Assert.True(call.Invoked);

            Assert.Null(call.Provider);

            Assert.Null(call.Model);

            Assert.Null(call.Tokens);

            Assert.Equal(TokenEstimateClassification.Unknown, call.Classification);

            Assert.Contains(origin == "owned" ? "disabled or incomplete" : "failed", call.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("buffered")]
    [InlineData("streamed")]
    public async Task RetrievalDiagnostics_caller_cancellation_precedes_owned_disabled_skip(string surface)
    {
        using CancellationTokenSource caller = new();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, CodebaseRetrieval = true, Lexicon = false },
        };

        configuration.Integrations.Embeddings.Provider = "configured-provider";

        configuration.Integrations.Embeddings.Model = "configured-model";

        RefusingDiagnosticEmbeddingFactory factory = new("PRIVATE_CANCELLED_SKIP");

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        WeaveService weave = new(factory, new ReloadingEmbeddingMonitor(configuration, true, caller),
            services.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<WeaveService>.Instance);

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        ScriptingChatClient chat = new();

        FakeRagWorkspaceIndexingService indexing = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave,
            workspaceIndexingService: indexing, logger: logger);

        PingRequest request = BaseRequest() with
        {
            Prompt = "cancel the disabled transition",
            WorkingDirectory = _workspace.Root,
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (surface == "preview")
            {
                _ = await wizard.PreviewContextAsync(new ContextPreviewRequest(Prompt: request.Prompt, Model: ModelName,
                    WorkingDirectory: _workspace.Root, DisableAllTools: true), InvocationContexts.AttendedSession(), caller.Token);
            }
            else if (surface == "buffered")
            {
                _ = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), caller.Token);
            }
            else
            {
                await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), caller.Token)) { }
            }
        });

        Assert.Equal(caller.Token, failure.CancellationToken);

        Assert.Equal(0, factory.Resolutions);

        Assert.Empty(indexing.RegisteredPaths);

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Properties?.Any(static pair => pair.Key == "RetrievalSource") == true);
    }

    private sealed class ReloadingEmbeddingMonitor(ArcanumSettings initial, bool reload, CancellationTokenSource? caller = null) : IOptionsMonitor<ArcanumSettings>
    {
        public int Reads { get; private set; }

        public ArcanumSettings CurrentValue
        {
            get
            {
                Reads++;

                if (reload && Reads > 1)
                {
                    caller?.Cancel();

                    return DefaultSettings();
                }

                return initial;
            }
        }

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

    private sealed class RefusingDiagnosticEmbeddingFactory(string canary) : IEmbeddingGeneratorFactory
    {
        public int Resolutions { get; private set; }

        public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken)
        {
            Resolutions++;

            throw new TapestryProviderRefusalException(new Error(ErrorCodes.Embeddings.FeatureDisabled, canary));
        }
    }

    private sealed class ForwardingDiagnosticWeave(IWeaveService actual) : IWeaveService
    {
        public bool IsAvailable => actual.IsAvailable;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) => actual.EmbedAsync(text, cancellationToken);

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) => actual.EmbedBatchAsync(texts, cancellationToken);

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) => actual.ChunkAsync(text, cancellationToken);
    }

    private sealed class DiagnosticFallbackFactory(ScriptingChatClient chat) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(chat, provider, resolvedModel, null));
    }

    private sealed class RetrievalDiagnosticWeave(string outcome, string privateFailure) : IWeaveService
    {
        public CancellationTokenSource? Cancellation { get; init; }

        public int Calls { get; private set; }

        public bool IsAvailable => outcome == "getter-threw" ? throw new IOException(privateFailure) : outcome != "unavailable";

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;

            if (Cancellation is not null)
            {
                Cancellation.Cancel();

                cancellationToken.ThrowIfCancellationRequested();
            }

            if (outcome == "embedding-threw") throw new IOException(privateFailure);

            if (outcome == "matching-disabled-code") return Task.FromResult(Result<Embedding<float>>.Failure(new Error(ErrorCodes.Embeddings.FeatureDisabled, privateFailure)));

            if (outcome == "embedding-failed") return Task.FromResult(Result<Embedding<float>>.Failure(new Error(privateFailure, privateFailure)));

            Embedding<float> embedding = new(new float[] { 1, 0, 0 })
            {
                AdditionalProperties = new() { [EmbeddingProvenance.MetadataKey] = new EmbeddingProvenance("actual-provider", "actual-model") },
            };

            return Task.FromResult(Result<Embedding<float>>.Success(embedding));
        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

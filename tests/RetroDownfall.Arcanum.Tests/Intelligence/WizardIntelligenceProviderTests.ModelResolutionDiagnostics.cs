using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Resilience;
using RetroDownfall.Arcanum.Infrastructure.Familiars;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    public static IEnumerable<object[]> ModelResolutionDiagnosticCases()
    {
        foreach (string surface in new[] { "preview", "buffered", "streamed" })
        {
            foreach (bool resilient in new[] { false, true })
            {
                yield return [surface, resilient, "missing", resilient ? "NoConfiguredCandidate" : "NoConfiguredModel"];

                yield return [surface, resilient, "dialect", "InvalidOutputTokenDialect"];

                yield return [surface, resilient, "kind", "UnsupportedProviderKind"];

                yield return [surface, resilient, "same-message", "Unknown"];

                yield return [surface, resilient, "private-message", "Unknown"];

                yield return [surface, resilient, "relayed-owned-guard", "Unknown"];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModelResolutionDiagnosticCases))]
    public async Task ModelResolution_diagnostics_report_only_the_actual_closed_guard_origin(
        string surface, bool resilient, string refusal, string expectedReason)
    {
        const string privateModel = "PRIVATE_UNRESOLVED_MODEL";

        const string privateFailure = "PRIVATE_CREDENTIAL_FAILURE https://private.invalid/token?secret=HIDDEN\r\nprivate-path";

        const string noModelMessage = "No AI model could be resolved. Configure Arcanum:Providers (with non-empty Models) and Arcanum:DefaultModel, or pass a model override that matches a configured model.";

        ArcanumSettings configuration = DefaultSettings();

        ProviderSettings provider = DefaultProvider() with
        {
            Type = refusal == "kind" ? (AiProviderKind)999 : AiProviderKind.OpenAICompatible,
            OutputTokenLimitWireDialect = refusal == "dialect" ? (OutputTokenLimitWireDialect)999 : OutputTokenLimitWireDialect.MaxTokens,
        };

        configuration.Providers = [provider];

        ResolutionRefusingHttpFactory http = new();

        ResolutionCredentialResolver credential = new(refusal switch
        {
            "same-message" => new InvalidOperationException(noModelMessage),
            "private-message" => new InvalidOperationException(privateFailure),
            _ => null,
        });

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), credential);

        if (refusal == "relayed-owned-guard")
        {
            Exception? previous = await Record.ExceptionAsync(() => factory.ResolveClientAsync(privateModel, CancellationToken.None));

            credential.Failure = Assert.IsAssignableFrom<InvalidOperationException>(previous);
        }

        ProviderHealthTracker? health = resilient ? new(NullLogger<ProviderHealthTracker>.Instance) : null;

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        ScriptingChatClient unused = new();

        WizardIntelligenceProvider wizard = CreateWizard(unused, configuration, factory: factory, healthTracker: health, logger: logger);

        PingRequest request = BaseRequest() with
        {
            Prompt = "resolve without provider dispatch",
            Model = refusal == "missing" ? privateModel : ModelName,
            SkipSpellRouting = true,
            DisableAllTools = true,
            WorkingDirectory = _workspace.Root,
        };

        string? code;

        if (surface == "preview")
        {
            Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(new ContextPreviewRequest(
                Prompt: request.Prompt, Model: request.Model, WorkingDirectory: request.WorkingDirectory,
                NoRetrieval: true, DisableAllTools: true), InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsFailure);

            code = result.Error.Code;
        }
        else if (surface == "streamed")
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            IntelligenceEvent error = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Error);

            Assert.DoesNotContain(frames, static frame => frame.Type is IntelligenceEventType.Token or IntelligenceEventType.Result);

            code = error.Data;
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request,
                InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsFailure);

            code = result.Error.Code;
        }

        // This batch changes diagnostics, not C's public failure-attribution contract.
        Assert.Equal(surface == "preview" || !resilient || refusal == "missing" ? ErrorCodes.Hub.Model : ErrorCodes.Hub.Error, code);

        Assert.Equal(0, http.Sends);

        Assert.Equal(0, unused.BufferedCallCount);

        Assert.Equal(0, unused.StreamingCallCount);

        if (health is not null) Assert.Empty(health.GetAllStatuses());

        Assert.All(logger.Entries, entry =>
        {
            Assert.Null(entry.Exception);

            Assert.DoesNotContain(privateModel, entry.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(privateFailure, entry.Message, StringComparison.Ordinal);

            Assert.All(entry.Properties ?? [], property =>
            {
                string value = property.Value?.ToString() ?? string.Empty;

                Assert.DoesNotContain(privateModel, value, StringComparison.Ordinal);

                Assert.DoesNotContain(privateFailure, value, StringComparison.Ordinal);
            });
        });

        TestLogEntry diagnostic = Assert.Single(logger.Entries, entry => entry.Properties?.Any(
            pair => pair.Key == "ResolutionReason" && string.Equals(pair.Value?.ToString(), expectedReason, StringComparison.Ordinal)) == true);

        Assert.Equal(LogLevel.Warning, diagnostic.Level);

        Assert.Contains("Arcanum:Providers", diagnostic.Message, StringComparison.Ordinal);

        Assert.Contains("Arcanum:DefaultModel", diagnostic.Message, StringComparison.Ordinal);

        if (resilient && surface != "preview" && refusal != "missing")
        {
            Assert.Equal(ModelName, Assert.Single(diagnostic.Properties!, static pair => pair.Key == "Model").Value);
        }
    }

    [Fact]
    public async Task ModelResolution_valid_real_factory_still_resolves_without_physical_dispatch()
    {
        ArcanumSettings configuration = DefaultSettings();

        ResolutionRefusingHttpFactory http = new();

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), new ResolutionCredentialResolver());

        using ChatClientLease lease = await factory.ResolveClientAsync(ModelName, CancellationToken.None);

        Assert.Equal(ModelName, lease.ResolvedModel);

        Assert.Equal(0, http.Sends);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("buffered")]
    [InlineData("streamed")]
    public async Task ModelResolution_caller_cancellation_remains_cancellation_without_a_guard_diagnostic(string surface)
    {
        using CancellationTokenSource cancellation = new();

        ArcanumSettings configuration = DefaultSettings();

        ResolutionRefusingHttpFactory http = new();

        ResolutionCredentialResolver credential = new(cancel: cancellation.Cancel);

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), credential);

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), configuration, factory: factory, logger: logger);

        PingRequest request = BaseRequest() with { Prompt = "cancel during credential resolution", Model = ModelName, SkipSpellRouting = true, DisableAllTools = true };

        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            if (surface == "preview")
            {
                _ = await wizard.PreviewContextAsync(new ContextPreviewRequest(Prompt: request.Prompt, Model: ModelName,
                    NoRetrieval: true, DisableAllTools: true), InvocationContexts.AttendedSession(), cancellation.Token);
            }
            else if (surface == "buffered")
            {
                _ = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), cancellation.Token);
            }
            else
            {
                await foreach (IntelligenceEvent _ in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), cancellation.Token)) { }
            }
        });

        OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);

        Assert.Equal(cancellation.Token, cancelled.CancellationToken);

        Assert.Equal(0, http.Sends);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level is LogLevel.Warning or LogLevel.Error);
    }

    [Fact]
    public async Task ModelResolution_nested_real_factory_guard_cannot_become_the_outer_resolution_cause()
    {
        ArcanumSettings configuration = DefaultSettings();

        ResolutionRefusingHttpFactory http = new();

        ChatClientFactory? factory = null;

        ReentrantResolutionCredentialResolver credential = new(async token =>
        {
            using ChatClientLease nested = await factory!.ResolveClientAsync("missing-nested-model", token);
        });

        factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), credential);

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(new ScriptingChatClient(), configuration, factory: factory, logger: logger);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest() with
        {
            Prompt = "resolve with a reentrant credential collaborator",
            Model = ModelName,
            SkipSpellRouting = true,
            DisableAllTools = true,
        }, InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, http.Sends);

        AssertResolutionDiagnostic(logger, "Unknown");
    }

    [Fact]
    public async Task ModelResolution_concurrent_real_factory_attempts_cannot_share_guard_provenance()
    {
        ArcanumSettings configuration = DefaultSettings();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ReentrantResolutionCredentialResolver credential = new(async token =>
        {
            entered.TrySetResult();

            await release.Task.WaitAsync(token);

            throw new InvalidOperationException("PRIVATE_CONCURRENT_CREDENTIAL_FAILURE");
        });

        ResolutionRefusingHttpFactory http = new();

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), credential);

        TestCapturingLogger<WizardIntelligenceProvider> firstLog = new();

        TestCapturingLogger<WizardIntelligenceProvider> secondLog = new();

        WizardIntelligenceProvider firstWizard = CreateWizard(new ScriptingChatClient(), configuration, factory: factory, logger: firstLog);

        WizardIntelligenceProvider secondWizard = CreateWizard(new ScriptingChatClient(), configuration, factory: factory, logger: secondLog);

        PingRequest request = BaseRequest() with { Prompt = "concurrent model resolution", Model = ModelName, SkipSpellRouting = true, DisableAllTools = true };

        Task<Result<PromptTurnResult>> first = firstWizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

        Result<ContextPreviewResult>? second = null;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The first real factory froze the valid provider before its awaited resolver. The second
            // request reads a different current kind, exercising an actual owned guard concurrently.
            configuration.Providers[0].Type = (AiProviderKind)999;

            second = await secondWizard.PreviewContextAsync(new ContextPreviewRequest(Prompt: request.Prompt,
                Model: ModelName, NoRetrieval: true, DisableAllTools: true), InvocationContexts.AttendedSession(), CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
        }

        Result<PromptTurnResult> firstResult = await first;

        Assert.True(firstResult.IsFailure);

        Assert.True(second!.IsFailure);

        Assert.Equal(0, http.Sends);

        AssertResolutionDiagnostic(firstLog, "Unknown");

        AssertResolutionDiagnostic(secondLog, "UnsupportedProviderKind");

        Assert.DoesNotContain(firstLog.Entries, static entry => entry.Message.Contains("PRIVATE_CONCURRENT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("preview", false)]
    [InlineData("preview", true)]
    [InlineData("buffered", false)]
    [InlineData("buffered", true)]
    [InlineData("streamed", false)]
    [InlineData("streamed", true)]
    public async Task ModelResolution_Familiar_pass_through_override_is_not_configured_log_metadata(string surface, bool resilient)
    {
        const string canary = "PRIVATE_UNCONFIGURED_FAMILIAR_OVERRIDE";

        ArcanumSettings configuration = DefaultSettings() with
        {
            Providers = [DefaultProvider() with { Type = AiProviderKind.ClaudeCodeCli, OutputTokenLimitWireDialect = (OutputTokenLimitWireDialect)999 }],
        };

        ResolutionRefusingHttpFactory http = new();

        ResolutionRefusingFamiliar runner = new();

        ChatClientFactory factory = new(http, new TestOptionsMonitor<ArcanumSettings>(configuration), familiarProcessRunner: runner);

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, factory: factory, logger: logger,
            healthTracker: resilient ? new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance) : null);

        PingRequest request = BaseRequest() with { Prompt = "refuse before any Familiar send", Model = canary, SkipSpellRouting = true, DisableAllTools = true };

        if (surface == "preview")
        {
            Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(new ContextPreviewRequest(Prompt: request.Prompt,
                Model: canary, NoRetrieval: true, DisableAllTools: true), InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsFailure);
        }
        else if (surface == "buffered")
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsFailure);
        }
        else
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Error);
        }

        AssertResolutionDiagnostic(logger, "InvalidOutputTokenDialect");

        TestLogEntry warning = Assert.Single(logger.Entries, static entry => entry.Level == LogLevel.Warning);

        Assert.DoesNotContain(canary, warning.Message, StringComparison.Ordinal);

        Assert.Equal("Unknown", Assert.Single(warning.Properties!, static pair => pair.Key == "Model").Value);

        Assert.Equal(0, runner.Dispatches);

        Assert.Equal(0, http.Sends);

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);
    }

    private sealed class ResolutionRefusingFamiliar : IFamiliarProcessRunner
    {
        public int Dispatches { get; private set; }

        public IAsyncEnumerable<string> RunLinesAsync(FamiliarProcessRequest request, CancellationToken cancellationToken)
        {
            Dispatches++;

            throw new InvalidOperationException("This test forbids Familiar execution.");
        }

        public Task<FamiliarProcessOutput> RunToCompletionAsync(FamiliarProcessRequest request, CancellationToken cancellationToken)
        {
            Dispatches++;

            throw new InvalidOperationException("This test forbids Familiar execution.");
        }
    }

    private static void AssertResolutionDiagnostic(TestCapturingLogger<WizardIntelligenceProvider> logger, string expectedReason)
    {
        TestLogEntry diagnostic = Assert.Single(logger.Entries, entry => entry.Properties?.Any(
            pair => pair.Key == "ResolutionReason" && string.Equals(pair.Value?.ToString(), expectedReason, StringComparison.Ordinal)) == true);

        Assert.Equal(LogLevel.Warning, diagnostic.Level);

        Assert.Null(diagnostic.Exception);
    }

    private sealed class ReentrantResolutionCredentialResolver(Func<CancellationToken, Task> resolve) : IProviderApiKeyResolver
    {
        public async Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            await resolve(cancellationToken);

            return null;
        }
    }

    private sealed class ResolutionCredentialResolver(Exception? failure = null, Action? cancel = null) : IProviderApiKeyResolver
    {
        internal Exception? Failure { get; set; } = failure;

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            cancel?.Invoke();

            cancellationToken.ThrowIfCancellationRequested();

            return Failure is null ? Task.FromResult<string?>(null) : Task.FromException<string?>(Failure);
        }
    }

    private sealed class ResolutionRefusingHttpFactory : IHttpClientFactory
    {
        internal int Sends { get; private set; }

        public HttpClient CreateClient(string name) => new(new RefusingHandler(this));

        private sealed class RefusingHandler(ResolutionRefusingHttpFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Sends++;

                throw new InvalidOperationException("The model-resolution refusal unexpectedly reached physical SDK dispatch.");
            }
        }
    }
}

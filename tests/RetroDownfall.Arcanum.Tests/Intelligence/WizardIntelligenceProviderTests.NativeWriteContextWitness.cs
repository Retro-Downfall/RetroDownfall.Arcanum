using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [SkippableTheory]
    [InlineData(12_000)]
    [InlineData(32_000)]
    public async Task Completed_native_file_mutations_remain_grounded_after_later_reads_fill_context(int window)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using GrimoireFixture fixture = new();
        using ArcanumTestHomeScope home = new("native-write-context-witness");
        await using ServiceProvider provider = fixture.CreateComposedProvider(GrimoireComposition.NonPooledCli);
        using IServiceScope scope = provider.CreateScope();
        IGrimoireRepository repository = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

        const string earlierUser = "Preserve the earlier catalog ownership decision.";
        const string currentUser = "Create a new catalog document, edit the example and inspect the test output.";
        const string newPath = "committed-catalog.txt";
        const string editPath = "edited-catalog.txt";
        const string failedPath = "untouched-catalog.txt";
        const string written = "new private catalog";
        const string beforeEdit = "old catalog title";
        const string edited = "updated catalog title";
        const string preserved = "untouched catalog text";

        Result<Guid> session = await ((ISessionTurnBeginStore)repository).CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "Native tool retention", CancellationToken.None);
        Assert.True(session.IsSuccess, session.IsFailure ? session.Error.Message : null);
        (Guid sessionId, Guid earlierAssistant) = await repository.BeginAssistantReplyAsync(session.Value, earlierUser, ModelName);
        await repository.FinalizeAssistantEntryAsync(earlierAssistant, "The catalog remains owner-scoped.");
        Session before = Assert.IsType<Session>(await repository.GetSessionAsync(sessionId));

        _workspace.WriteFile(editPath, beforeEdit);
        _workspace.WriteFile(failedPath, preserved);
        _workspace.WriteFile("first-diagnostics.txt", ContextDiagnosticLines("Previous"));
        _workspace.WriteFile("last-diagnostics.txt", ContextDiagnosticLines("Latest"));
        _workspace.WriteFile("final-observation.txt", "The latest narrow catalog check passed.");

        ConfigurableSanctumGuard guard = new();
        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(
            configureServices: services => services.AddSingleton<ISanctumGuard>(guard));
        IReadOnlyList<AITool> available = await manager.GetAvailableToolsAsync(_workspace.Root);
        FakeMcpConnectionManager mcp = new();
        mcp.Tools.AddRange(available.Where(static tool => tool.Name is "write_file" or "edit_file" or "read_file"));
        Assert.Equal(3, mcp.Tools.Count);

        ScriptingChatClient chat = new();
        chat.EnqueueToolCall("write_file", "observed-write", new() { ["relativePath"] = newPath, ["content"] = written });
        chat.EnqueueToolCall("edit_file", "observed-edit", new()
        {
            ["relativePath"] = editPath,
            ["exactSearchText"] = beforeEdit,
            ["replacementText"] = edited,
        });
        chat.EnqueueToolCall("edit_file", "stale-edit", new()
        {
            ["relativePath"] = failedPath,
            ["exactSearchText"] = "wrong precondition",
            ["replacementText"] = "must never be written",
        });
        chat.EnqueueToolCall("read_file", "first-diagnostics", WorkspaceReadArguments("first-diagnostics.txt", 1, 120));
        chat.EnqueueToolCall("read_file", "last-diagnostics", WorkspaceReadArguments("last-diagnostics.txt", 1, 120));
        chat.EnqueueToolCall("read_file", "narrow-observation", WorkspaceReadArguments("final-observation.txt"));
        chat.EnqueueText("Deterministic terminal response.");

        ProviderSettings selectedProvider = DefaultProvider() with { ContextWindowLimit = window };
        ArcanumSettings settings = DefaultSettings() with { Providers = [selectedProvider] };
        ModelTokenEstimator estimator = new(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));
        ICanonicalCampaignContextResolver campaign = new CanonicalCampaignContextResolver(
            scope.ServiceProvider.GetRequiredService<ISessionCampaignBindingReader>(),
            scope.ServiceProvider.GetRequiredService<ICampaignPathIdentityReader>(),
            scope.ServiceProvider.GetRequiredService<ICampaignAvailabilityReader>());
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire: repository, mcp: mcp,
            sanctumGuard: guard, modelTokenEstimator: estimator, campaignResolver: campaign);
        PingRequest request = WorkspaceOutcomeRequest(_workspace.Root) with
        {
            SessionId = sessionId,
            Prompt = currentUser,
            MaxOutputTokens = 128,
        };

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedGlobalOnlySession(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(7, chat.AllBufferedCalls.Count);

        FunctionResultContent writeAttempt = Assert.Single(chat.AllBufferedCalls[1].SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>(), static observation => observation.CallId == "observed-write");
        string exactWrite = Assert.IsType<string>(writeAttempt.Result);
        Assert.Contains("Wrote file ", exactWrite, StringComparison.Ordinal);
        Assert.Contains(newPath, exactWrite, StringComparison.Ordinal);
        FunctionResultContent editAttempt = Assert.Single(chat.AllBufferedCalls[2].SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>(), static observation => observation.CallId == "observed-edit");
        string exactEdit = Assert.IsType<string>(editAttempt.Result);
        Assert.Contains("Replaced 1 occurrence", exactEdit, StringComparison.Ordinal);
        Assert.Contains(editPath, exactEdit, StringComparison.Ordinal);
        FunctionResultContent failedAttempt = Assert.Single(chat.AllBufferedCalls[3].SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>(), static observation => observation.CallId == "stale-edit");
        string exactRefusal = Assert.IsType<string>(failedAttempt.Result);
        Assert.Contains(failedPath, exactRefusal, StringComparison.Ordinal);
        Assert.Contains("Exact search text not found", exactRefusal, StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllText(Path.Combine(_workspace.Root, newPath)));
        Assert.Equal(edited, File.ReadAllText(Path.Combine(_workspace.Root, editPath)));
        Assert.Equal(preserved, File.ReadAllText(Path.Combine(_workspace.Root, failedPath)));

        Session after = Assert.IsType<Session>(await repository.GetSessionAsync(sessionId));
        Assert.All(before.Entries, entry => Assert.Contains(after.Entries, saved => saved.Id == entry.Id && saved.Content == entry.Content));
        Assert.Equal(6, after.Entries.Count(static entry => entry.ToolName is "write_file" or "edit_file" or "read_file"));
        foreach (string evidence in new[] { exactWrite, exactEdit, exactRefusal })
        {
            Assert.Contains(after.Entries, entry => entry.Role == MessageRole.System && entry.Content == $"[ToolResult: {evidence}]");
        }

        var final = chat.AllBufferedCalls[^1];
        ChatOptions finalOptions = Assert.IsType<ChatOptions>(chat.LastChatOptions);
        ContextTokenBreakdown finalBudget = estimator.EstimateContext(new ModelTokenizationRequest(
            selectedProvider, ModelName, final, finalOptions, ReservedAnswerTokens: 128, ReservedReasoningTokens: 0));
        Assert.InRange(finalBudget.TotalTokens, 1, window);

        AIContent[] finalContent = final.SelectMany(static message => message.Contents).ToArray();
        TextContent[] finalText = finalContent.OfType<TextContent>().ToArray();
        FunctionCallContent[] finalCalls = finalContent.OfType<FunctionCallContent>().ToArray();
        FunctionResultContent[] finalResults = finalContent.OfType<FunctionResultContent>().ToArray();
        Assert.Contains(finalText, text => text.Text.Contains(earlierUser, StringComparison.Ordinal));
        Assert.Contains(finalText, text => text.Text.Contains(currentUser, StringComparison.Ordinal));
        Assert.Contains(finalResults, observation => observation.CallId == "narrow-observation"
            && observation.Result?.ToString()?.Contains("The latest narrow catalog check passed.", StringComparison.Ordinal) is true);

        ToolEvidenceEviction? trim = result.Value.Notices.SingleOrDefault(static notice => notice.Code == InferenceNoticeCode.ToolEvidenceTrimmed)?.ToolEvidenceEviction;
        bool HasCompleteExchange(string callId, string toolName, string exactResult) =>
            finalCalls.Any(call => call.CallId == callId && call.Name == toolName)
            && finalResults.Any(observation => observation.CallId == callId && observation.Result is string text && text == exactResult);

        bool fullWrite = HasCompleteExchange("observed-write", "write_file", exactWrite);
        bool fullEdit = HasCompleteExchange("observed-edit", "edit_file", exactEdit);

        if (window == 32_000)
        {
            Assert.Null(trim);
            Assert.True(fullWrite && fullEdit, "The 32k control must keep the original native call and result objects.");
            return;
        }

        Assert.NotNull(trim);
        Assert.Equal(0, trim.HistoricalExchanges);

        if (fullWrite && fullEdit)
        {
            return;
        }

        // The original native failure is a typed FunctionResultContent. Model-visible replacement
        // text must contain only grounded successful mutation facts, never a failed path or file body.
        Assert.DoesNotContain(finalText, copy => copy.Text.Contains(failedPath, StringComparison.Ordinal));
        foreach (string body in new[] { written, beforeEdit, edited, preserved })
        {
            Assert.DoesNotContain(finalText, copy => copy.Text.Contains(body, StringComparison.Ordinal));
        }

        bool compactWrite = finalText.Any(copy => copy.Text.Contains(newPath, StringComparison.Ordinal));
        bool compactEdit = finalText.Any(copy => copy.Text.Contains(editPath, StringComparison.Ordinal));
        Assert.True((fullWrite || compactWrite) && (fullEdit || compactEdit),
            $"Both native mutations committed. The final provider payload used {finalBudget.TotalTokens}/{window} tokens, retained write {fullWrite}/{compactWrite} and edit {fullEdit}/{compactEdit} as full/compact facts, preserved the latest read and both user messages, and evicted {trim.Exchanges} exchanges.");
    }

    private static string ContextDiagnosticLines(string label) => string.Join(
        '\n', Enumerable.Range(1, 120).Select(line =>
            string.Create(CultureInfo.InvariantCulture, $"{label} catalog case {line:D3}: owner scope and duplicate edition keys verified.")));
}

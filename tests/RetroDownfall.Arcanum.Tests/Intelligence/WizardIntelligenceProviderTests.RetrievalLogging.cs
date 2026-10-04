using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    /// <summary>
    /// A retrieval failure is logged by exception type only, as every sibling retrieval path does: the
    /// message and stack of a store or provider fault can carry file paths, attachment names or query
    /// text that must not reach the log sink.
    /// </summary>
    [Fact]
    public async Task AttachmentRetrievalFailure_LogsTypeOnly()
    {
        const string canary = "CANARY_ATTACHMENT_RETRIEVAL_/private/notes.md";

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        ArcanumSettings settings = DefaultSettings() with
        {
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                AttachmentRetrieval = true,
            },
        };

        ScriptingChatClient chat = new();

        chat.EnqueueText("buffered answer");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            weaveService: new FakeRagWeaveService { Available = true },
            sessionAttachmentRetrieval: new ThrowingSessionAttachmentRetrieval(new InvalidOperationException(canary)),
            logger: logger);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                SessionId = Guid.NewGuid(),
                Prompt = "find the note",
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        TestLogEntry failure = Assert.Single(
            logger.Entries,
            static entry => entry.Message.Contains("attachment semantic retrieval", StringComparison.OrdinalIgnoreCase));

        Assert.Null(failure.Exception);

        Assert.DoesNotContain(canary, failure.Message, StringComparison.Ordinal);

        Assert.Contains(nameof(InvalidOperationException), failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(canary, StringComparison.Ordinal));
    }

    private sealed class ThrowingSessionAttachmentRetrieval(Exception failure) : ISessionAttachmentRetrievalService
    {
        public Task<SessionAttachmentRetrievedChunk[]> SearchAsync(
            Guid sessionId,
            Embedding<float> queryEmbedding,
            bool includeHistorical,
            CancellationToken cancellationToken) =>
            throw failure;

        public Task<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>> GetStatusesAsync(
            IReadOnlyList<Guid> attachmentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus>>(
                new Dictionary<Guid, SessionAttachmentIndexStatus>());
    }
}

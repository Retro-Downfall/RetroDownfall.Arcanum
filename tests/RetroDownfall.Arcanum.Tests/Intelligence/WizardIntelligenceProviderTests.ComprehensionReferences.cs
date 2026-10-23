using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

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
    public static TheoryData<string> ComprehensionRegistryMimeTypes()
    {
        TheoryData<string> data = [];

        foreach (SessionAttachmentComprehensionFormat format in SessionAttachmentComprehensionPolicy.Formats)
        {
            data.Add(format.MimeType);
        }

        return data;
    }

    /// <summary>
    /// A per-turn attachment reference to a Comprehension document, driven through the real provider: the
    /// preparation that validates the reference must defer it to extraction instead of failing the whole turn.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComprehensionRegistryMimeTypes))]
    public async Task ExplicitAttachmentReference_ToAComprehensionDocument_ReachesTheModelFramedAsUntrustedData(string mimeType)
    {
        // PDF is parsed by the registry's isolated worker, run here in this process (the process boundary has its own tests).
        using IDisposable pdfWorker = InProcessPdfWorker.Use();

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        NoOpSessionAttachmentStore store = new(
            records: new Dictionary<Guid, SessionAttachmentRecord> { [record.Id] = record },
            readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes));

        ScriptingChatClient chat = new();

        chat.EnqueueText("summarized");

        ArcanumSettings settings = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { AttachmentRetrieval = true, Comprehension = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: new FakeGrimoireRepository
            {
                Session = new Session { Id = sessionId, Entries = [] },
            },
            sessionAttachmentStore: store);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "Summarize the referenced document.",
                SessionId = sessionId,
                AttachmentReferences = [record.Id],
                SkipSpellRouting = true,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        MeAiChatMessage userMessage = chat.LastBufferedMessages.Last(
            static message => message.Role == ChatRole.User);

        TextContent explicitContent = Assert.Single(
            userMessage.Contents.OfType<TextContent>(),
            static content => content.AdditionalProperties is not null
                && content.AdditionalProperties.TryGetValue("arcanum.context_source", out object? source)
                && string.Equals(source as string, "explicitAttachment", StringComparison.Ordinal));

        SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format);

        Assert.Contains("UNTRUSTED DATA; " + format!.FormatName + "; layout not preserved", explicitContent.Text, StringComparison.Ordinal);
    }
}

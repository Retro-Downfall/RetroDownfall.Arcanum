using System.Text;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentDiagnostics_Reference_preserves_extractor_reason_or_logs_read_failure(bool readFailure)
    {
        Guid sessionId = Guid.NewGuid();
        byte[] bytes = Encoding.UTF8.GetBytes("private-document-canary not a ZIP");
        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, SessionAttachmentComprehensionPolicy.WordDocumentMimeType, bytes.Length);
        NoOpSessionAttachmentStore store = new(record: record, readBytes: (_, _) => readFailure
            ? Task.FromException<ReadOnlyMemory<byte>>(new InvalidDataException("/private/secret-key private-document-canary"))
            : Task.FromResult<ReadOnlyMemory<byte>>(bytes));
        ScriptingChatClient chat = new();
        TestCapturingLogger<WizardIntelligenceProvider> logger = new();
        ArcanumSettings settings = DefaultSettings() with { Features = DefaultSettings().Features with { AttachmentRetrieval = true, Comprehension = true } };
        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire: new FakeGrimoireRepository { Session = new Session { Id = sessionId, Entries = [] } }, sessionAttachmentStore: store, logger: logger);
        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(BaseRequest() with { Prompt = "Summarize this document.", SessionId = sessionId, AttachmentReferences = [record.Id], SkipSpellRouting = true, DisableMcpTools = true }, InvocationContexts.AttendedSession(), CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.AttachedFiles, result.Error.Code);
        Assert.DoesNotContain("private-document-canary", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        if (readFailure)
        {
            TestLogEntry entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(record.Id.ToString(), StringComparison.Ordinal));
            Assert.Null(entry.Exception);
            Assert.Contains(nameof(InvalidDataException), entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-key", entry.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Document package is not a valid ZIP container.", result.Error.Message, StringComparison.Ordinal);
        }
        Assert.Empty(chat.LastBufferedMessages);
    }
}

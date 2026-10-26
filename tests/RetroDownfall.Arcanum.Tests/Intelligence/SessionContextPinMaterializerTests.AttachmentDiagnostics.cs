using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class SessionContextPinMaterializerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentDiagnostics_Deferred_reference_and_pin_advise_retry_without_reading(bool pinPath)
    {
        Guid sessionId = Guid.NewGuid();
        byte[] bytes = "ARCCOMP1\ncontent"u8.ToArray();
        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, SessionAttachmentComprehensionPolicy.MimeType, bytes.Length);
        using SessionAttachmentDocumentSnapshot holder = await SessionAttachmentDocumentExtractors.TextContainerHost.ReadSnapshotAsync(_ => Task.FromResult<ReadOnlyMemory<byte>>(bytes), bytes.Length, 1024, CancellationToken.None, waitForSlot: false);
        Assert.Null(holder.Refusal);
        int reads = 0;
        NoOpSessionAttachmentStore store = new(record, readBytes: (_, _) => { reads++; return Task.FromResult<ReadOnlyMemory<byte>>(bytes); });
        ArcanumSettings settings = new() { Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true } };
        string text;
        if (pinPath)
        {
            SessionContextPinRecord pin = new(Guid.NewGuid(), sessionId, SessionContextPinKind.Attachment, record.Id.ToString("D"), "document", record.ContentSha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var result = await new SessionContextPinMaterializer(new StaticPinStore(pin), store, CreateSessions(), new TestOptionsSnapshot<ArcanumSettings>(settings)).MaterializeAsync(sessionId, _workspace, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(40));
            text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;
        }
        else
        {
            var result = await SessionAttachmentTurnService.MaterializeReferenceAsync(record, store, settings, "model", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Empty(result.Contents);
            text = result.ErrorMessage!;
        }
        Assert.Contains("temporarily busy", text, StringComparison.Ordinal);
        Assert.Contains("again shortly", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task AttachmentDiagnostics_Reference_read_cancellation_propagates()
    {
        Guid sessionId = Guid.NewGuid();
        byte[] bytes = "ARCCOMP1\ncontent"u8.ToArray();
        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, SessionAttachmentComprehensionPolicy.MimeType, bytes.Length);
        using CancellationTokenSource caller = new();
        NoOpSessionAttachmentStore store = new(record, readBytes: (_, _) => { caller.Cancel(); return Task.FromException<ReadOnlyMemory<byte>>(new OperationCanceledException(caller.Token)); });
        ArcanumSettings settings = new() { Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionAttachmentTurnService.MaterializeReferenceAsync(record, store, settings, "model", caller.Token));
    }

    [Fact]
    public async Task AttachmentDiagnostics_Pin_preserves_safe_extractor_failure_without_document_content()
    {
        Guid sessionId = Guid.NewGuid();
        byte[] bytes = Encoding.UTF8.GetBytes("private-document-canary not a ZIP");
        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, SessionAttachmentComprehensionPolicy.WordDocumentMimeType, bytes.Length);
        SessionContextPinRecord pin = new(Guid.NewGuid(), sessionId, SessionContextPinKind.Attachment, record.Id.ToString("D"), "document", record.ContentSha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        NoOpSessionAttachmentStore store = new(record, readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes));
        ArcanumSettings settings = new() { Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true } };
        SessionContextPinMaterialization result = await new SessionContextPinMaterializer(new StaticPinStore(pin), store, CreateSessions(), new TestOptionsSnapshot<ArcanumSettings>(settings)).MaterializeAsync(sessionId, _workspace, CancellationToken.None);
        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;
        Assert.Contains("status: Error", text, StringComparison.Ordinal);
        Assert.Contains("Document package is not a valid ZIP container.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-document-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", text, StringComparison.OrdinalIgnoreCase);
    }
}

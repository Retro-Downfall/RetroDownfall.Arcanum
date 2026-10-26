using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class SessionAttachmentToolInjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentDiagnostics_Refresh_tool_distinguishes_limit_from_source_failure(bool limit)
    {
        Guid sessionId = Guid.NewGuid();
        AttachmentSourceMetadata source = new(AttachmentSourceKind.WorkspaceFile, "workspace", "notes.txt", "/workspace/notes.txt", "OLD", "1:1", DateTimeOffset.UtcNow, 3, AttachmentSourceStatus.Refreshable, null);
        SessionAttachmentRecord record = RefreshRecord(Guid.NewGuid(), sessionId, "notes.txt", source);
        FakeSessionAttachmentStore store = new() { RefreshFailure = limit ? new AttachmentLimitExceededException("private-path-canary") : new InvalidOperationException("private-path-canary") };
        store.Records.Add(record);
        FakeAttachmentSourceResolver resolver = new(new AttachmentSourceResolution(source with { LastObservedContentSha256 = "NEW", LastObservedByteLength = 7 }, Encoding.UTF8.GetBytes("changed"), "text/plain"));
        ToolExecutionPipeline pipeline = CreatePipeline(new ArcanumSettings { Features = new FeatureSettings { Attachments = true } }, store, resolver);
        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;
        try
        {
            var result = await pipeline.ProcessSingleToolCallAsync(new FunctionCallContent("refresh-failure", "refresh_session_file", new Dictionary<string, object?> { ["attachmentId"] = record.Id }), new PingRequest("refresh", WorkingDirectory: "/workspace"), new ChatOptions { Tools = [AIFunctionFactory.Create(() => "accepted", "refresh_session_file")] }, null, sessionId.ToString(), new() { VisibleAttachmentIds = new HashSet<Guid> { record.Id } }, false, default);
            Assert.Contains(limit ? "attachment_budget_exhausted" : "source_unavailable", result.ResultText, StringComparison.Ordinal);
            Assert.DoesNotContain("private-path-canary", result.ResultText, StringComparison.Ordinal);
            Assert.Null(result.AttachmentRefresh);
            Assert.Null(result.AdditionalContextContents);
            Assert.Single(store.Records);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }
    }
}

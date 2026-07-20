using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SessionAttachmentToolInjectionTests
{

    [Fact]
    public async Task ProcessSingleToolCall_AttachSessionFile_InjectsTextContent()
    {

        Guid sessionId = Guid.NewGuid();

        FakeSessionAttachmentStore store = new();

        store.Records.Add(
            new SessionAttachmentRecord(
                Guid.NewGuid(),
                sessionId,
                null,
                null,
                SessionAttachmentState.Bound,
                "notes.txt",
                "notes.txt",
                1,
                "rel/notes.txt",
                "abc",
                "text/plain",
                5,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        store.BytesByLogical["notes.txt"] = Encoding.UTF8.GetBytes("hello");

        ArcanumSettings settings = new()
        {
            Attachments = new AttachmentsSettings { Enabled = true, EnableModelAttachTool = true },
            Cli = new CliSettings { MaxAttachFileSizeBytes = 1024 * 1024 },
        };

        ToolExecutionPipeline pipeline = CreatePipeline(settings, store);

        FunctionCallContent fcc = new(
            "call_attach_1",
            "attach_session_file",
            new Dictionary<string, object?> { ["logicalName"] = "notes.txt" });

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () => "Attached 'notes.txt' v1 (Text, 5 bytes). Content will be injected into the next model turn.",
                    "attach_session_file"),
            ],
        };

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
                .ProcessSingleToolCallAsync(
                    fcc,
                    new PingRequest("hi", WorkingDirectory: "/tmp"),
                    chatOptions,
                    activeSpell: null,
                    sessionId: sessionId.ToString("D"),
                    turnContext: new ToolExecutionPipeline.TurnContext(),
                    suppressInvocationFailures: false,
                    cancellationToken: CancellationToken.None);

            Assert.False(processed.Failed);

            Assert.NotNull(processed.AdditionalContextContents);

            TextContent text = Assert.IsType<TextContent>(Assert.Single(processed.AdditionalContextContents!));

            Assert.Equal("hello", text.Text);

            List<MeAiChatMessage> messages =
            [
                new MeAiChatMessage(ChatRole.User, "prompt"),
            ];

            ToolExecutionPipeline.AppendToolExchangeToMessages(messages, fcc, processed.CallId, processed.ResultText);

            messages.Add(new MeAiChatMessage(ChatRole.User, processed.AdditionalContextContents!.ToList()));

            Assert.Equal(ChatRole.User, messages[^1].Role);
            Assert.Contains(messages[^1].Contents, static c => c is TextContent { Text: "hello" });
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    [Fact]
    public async Task ProcessSingleToolCall_AttachSessionFile_InjectsDataContentForImage()
    {

        Guid sessionId = Guid.NewGuid();

        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        FakeSessionAttachmentStore store = new();

        store.Records.Add(
            new SessionAttachmentRecord(
                Guid.NewGuid(),
                sessionId,
                null,
                null,
                SessionAttachmentState.Bound,
                "shot.png",
                "shot.png",
                1,
                "rel/shot.png",
                "abc",
                "image/png",
                pngBytes.Length,
                SessionAttachmentKind.Image,
                DateTimeOffset.UtcNow));

        store.BytesByLogical["shot.png"] = pngBytes;

        ArcanumSettings settings = new()
        {
            Attachments = new AttachmentsSettings { Enabled = true, EnableModelAttachTool = true },
            Cli = new CliSettings { MaxAttachFileSizeBytes = 1024 * 1024 },
        };

        ToolExecutionPipeline pipeline = CreatePipeline(settings, store);

        FunctionCallContent fcc = new(
            "call_attach_img",
            "attach_session_file",
            new Dictionary<string, object?> { ["logicalName"] = "shot.png" });

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () => "Attached 'shot.png' v1 (Image, 8 bytes). Content will be injected into the next model turn.",
                    "attach_session_file"),
            ],
        };

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
                .ProcessSingleToolCallAsync(
                    fcc,
                    new PingRequest("hi", WorkingDirectory: "/tmp"),
                    chatOptions,
                    activeSpell: null,
                    sessionId: sessionId.ToString("D"),
                    turnContext: new ToolExecutionPipeline.TurnContext(),
                    suppressInvocationFailures: false,
                    cancellationToken: CancellationToken.None);

            Assert.False(processed.Failed);

            DataContent data = Assert.IsType<DataContent>(Assert.Single(processed.AdditionalContextContents!));

            Assert.Equal("image/png", data.MediaType);

            Assert.True(data.Data.Span.SequenceEqual(pngBytes));
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    [Fact]
    public async Task ProcessSingleToolCall_AttachSessionFile_WithoutAmbient_SkipsInjection()
    {

        FakeSessionAttachmentStore store = new();

        ToolExecutionPipeline pipeline = CreatePipeline(new ArcanumSettings(), store);

        FunctionCallContent fcc = new(
            "call_attach_2",
            "attach_session_file",
            new Dictionary<string, object?> { ["logicalName"] = "notes.txt" });

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () => "No current session; cannot attach a session file.",
                    "attach_session_file"),
            ],
        };

        SessionAttachmentToolAmbient.CurrentSessionId = null;

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
            .ProcessSingleToolCallAsync(
                fcc,
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                chatOptions,
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: false,
                cancellationToken: CancellationToken.None);

        Assert.Null(processed.AdditionalContextContents);

    }

    private static ToolExecutionPipeline CreatePipeline(ArcanumSettings settings, ISessionAttachmentStore store) =>
        new(
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            store,
            NullLogger<ToolExecutionPipeline>.Instance);

    private sealed class FakeWard : IWard
    {

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WardResolution(true, null, DateTimeOffset.UtcNow));

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) => ResolveStatus.Success;

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

    private sealed class AllowAllSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class FakeSessionAttachmentStore : ISessionAttachmentStore
    {

        public List<SessionAttachmentRecord> Records { get; } = [];

        public Dictionary<string, byte[]> BytesByLogical { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<SessionAttachmentRecord> PersistNewAsync(
            Guid? sessionId,
            string? pendingTurnId,
            Guid? entryId,
            string logicalNameHint,
            string originalFileName,
            ReadOnlyMemory<byte> bytes,
            string mimeType,
            SessionAttachmentKind kind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PromotePendingAsync(
            string pendingTurnId,
            Guid sessionId,
            Guid? entryId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionAttachmentRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Records.FirstOrDefault(r => r.Id == id));

        public Task<SessionAttachmentRecord?> GetByLogicalAsync(
            Guid sessionId,
            string logicalKey,
            int? version,
            CancellationToken cancellationToken = default)
        {

            IEnumerable<SessionAttachmentRecord> matches = Records.Where(r =>
                r.SessionId == sessionId
                && string.Equals(r.LogicalKey, logicalKey, StringComparison.OrdinalIgnoreCase)
                && r.State == SessionAttachmentState.Bound);

            if (version is not null)
            {
                matches = matches.Where(r => r.Version == version.Value);
            }

            return Task.FromResult(matches.OrderByDescending(r => r.Version).FirstOrDefault());

        }

        public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionAttachmentRecord>>(
                Records.Where(r => r.SessionId == sessionId && r.State == SessionAttachmentState.Bound).ToList());

        public Task<IReadOnlyList<SessionAttachmentIndexItem>> BuildIndexAsync(
            Guid sessionId,
            int maxItems,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionAttachmentIndexItem>>([]);

        public Task<ReadOnlyMemory<byte>> ReadBytesAsync(
            SessionAttachmentRecord record,
            CancellationToken cancellationToken = default)
        {

            if (BytesByLogical.TryGetValue(record.LogicalKey, out byte[]? bytes))
            {
                return Task.FromResult<ReadOnlyMemory<byte>>(bytes);
            }

            return Task.FromResult(ReadOnlyMemory<byte>.Empty);

        }

        public Task DeleteStalePendingAsync(TimeSpan olderThan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ValidateReferencesAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            int maxReferences,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

}

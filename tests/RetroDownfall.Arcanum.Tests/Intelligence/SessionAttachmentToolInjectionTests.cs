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

            Assert.Contains("[Attached: notes.txt]", text.Text, StringComparison.Ordinal);

            Assert.Contains("hello", text.Text, StringComparison.Ordinal);

            Assert.Contains("```\nhello\n```", text.Text, StringComparison.Ordinal);

            List<MeAiChatMessage> messages =
            [
                new MeAiChatMessage(ChatRole.User, "prompt"),
            ];

            ToolExecutionPipeline.AppendToolExchangeToMessages(messages, fcc, processed.CallId, processed.ResultText);

            messages.Add(new MeAiChatMessage(ChatRole.User, processed.AdditionalContextContents!.ToList()));

            Assert.Equal(ChatRole.User, messages[^1].Role);
            Assert.Contains(messages[^1].Contents, static c => c is TextContent t && t.Text.Contains("hello", StringComparison.Ordinal));
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
            Cli = new CliSettings { MaxAttachFileSizeBytes = 4096 * 4096 },
            Scrying = new ScryingSettings { Enabled = true, MaxImageBytes = 4096 * 4096 },
            Providers =
            [
                new ProviderSettings
                {
                    Name = "test",
                    Models = [new ModelEntry("vision-model", SupportsVision: true)],
                },
            ],
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
                    new PingRequest("hi", Model: "vision-model", WorkingDirectory: "/tmp"),
                    chatOptions,
                    activeSpell: null,
                    sessionId: sessionId.ToString("D"),
                    turnContext: new ToolExecutionPipeline.TurnContext(),
                    suppressInvocationFailures: false,
                    cancellationToken: CancellationToken.None);

            Assert.False(processed.Failed);

            Assert.Equal(2, processed.AdditionalContextContents!.Count);

            TextContent notice = Assert.IsType<TextContent>(processed.AdditionalContextContents[0]);

            Assert.Contains("[Attached image:", notice.Text, StringComparison.Ordinal);

            DataContent data = Assert.IsType<DataContent>(processed.AdditionalContextContents[1]);

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

    [Fact]
    public async Task ProcessSingleToolCall_AttachSessionFile_Denied_DoesNotInject()
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
            Ward = new WardSettings
            {
                Enabled = true,
                ForbiddenArts = ["attach_session_file"],
                AutoDenyInUnattendedMode = true,
            },
        };

        ToolExecutionPipeline pipeline = CreatePipeline(settings, store);

        FunctionCallContent fcc = new(
            "call_attach_denied",
            "attach_session_file",
            new Dictionary<string, object?> { ["logicalName"] = "notes.txt" });

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () => "should not run",
                    "attach_session_file"),
            ],
        };

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
                .ProcessSingleToolCallAsync(
                    fcc,
                    new PingRequest("hi", WorkingDirectory: "/tmp", UnattendedMode: true),
                    chatOptions,
                    activeSpell: null,
                    sessionId: sessionId.ToString("D"),
                    turnContext: new ToolExecutionPipeline.TurnContext { CampaignRequiresWard = true },
                    suppressInvocationFailures: false,
                    cancellationToken: CancellationToken.None);

            Assert.False(processed.Failed);

            Assert.Null(processed.AdditionalContextContents);

            Assert.Contains("unattended mode", processed.ResultText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    [Fact]
    public async Task TryBuildContentsAsync_FailedImageValidation_DoesNotConsumeBudgetOrInjectOnce()
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
                "shot.png",
                "shot.png",
                1,
                "rel/shot.png",
                "abc",
                "image/png",
                8,
                SessionAttachmentKind.Image,
                DateTimeOffset.UtcNow));

        store.BytesByLogical["shot.png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

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
                "def",
                "text/plain",
                5,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        store.BytesByLogical["notes.txt"] = Encoding.UTF8.GetBytes("hello");

        ArcanumSettings settings = new()
        {
            Attachments = new AttachmentsSettings { Enabled = true, MaxReferencesPerTurn = 1 },
            Cli = new CliSettings { MaxAttachFileSizeBytes = 1024 * 1024 },
            Scrying = new ScryingSettings { Enabled = false },
        };

        SessionAttachmentTurnBudget.BeginTurn(maxReferences: 1, initialConsumed: 0);

        try
        {
            IReadOnlyList<AIContent>? failed = await SessionAttachmentToolInjection.TryBuildContentsAsync(
                store,
                sessionId,
                "shot.png",
                version: null,
                settings,
                requestModel: "vision-model");

            Assert.Null(failed);

            Assert.Equal(1, SessionAttachmentTurnBudget.Remaining);

            IReadOnlyList<AIContent>? ok = await SessionAttachmentToolInjection.TryBuildContentsAsync(
                store,
                sessionId,
                "notes.txt",
                version: null,
                settings,
                requestModel: null);

            Assert.NotNull(ok);

            Assert.Equal(0, SessionAttachmentTurnBudget.Remaining);

            IReadOnlyList<AIContent>? secondNotes = await SessionAttachmentToolInjection.TryBuildContentsAsync(
                store,
                sessionId,
                "notes.txt",
                version: null,
                settings,
                requestModel: null);

            Assert.Null(secondNotes);
        }
        finally
        {
            SessionAttachmentTurnBudget.EndTurn();
        }

    }

    [Fact]
    public async Task ProcessSingleToolCall_AttachPostProcessThrows_Tolerate_SynthesizesFailureWithoutInject()
    {

        Guid sessionId = Guid.NewGuid();

        ThrowingSessionAttachmentStore store = new();

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

        ArcanumSettings settings = new()
        {
            Attachments = new AttachmentsSettings { Enabled = true, EnableModelAttachTool = true },
            Cli = new CliSettings { MaxAttachFileSizeBytes = 1024 * 1024 },
        };

        ToolExecutionPipeline pipeline = CreatePipeline(settings, store);

        FunctionCallContent fcc = new(
            "call_attach_throw",
            "attach_session_file",
            new Dictionary<string, object?> { ["logicalName"] = "notes.txt" });

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () => "Attached 'notes.txt' v1 (Text, 5 bytes).",
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
                    suppressInvocationFailures: true,
                    cancellationToken: CancellationToken.None);

            Assert.True(processed.Failed);

            Assert.Null(processed.AdditionalContextContents);

            Assert.Contains("internal error", processed.ResultText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    [Fact]
    public async Task ProcessSingleToolCall_AttachPostProcessThrows_WithoutTolerate_Rethrows()
    {

        Guid sessionId = Guid.NewGuid();

        ThrowingSessionAttachmentStore store = new();

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

        ToolExecutionPipeline pipeline = CreatePipeline(
            new ArcanumSettings
            {
                Attachments = new AttachmentsSettings { Enabled = true, EnableModelAttachTool = true },
                Cli = new CliSettings { MaxAttachFileSizeBytes = 1024 * 1024 },
            },
            store);

        FunctionCallContent fcc = new(
            "call_attach_throw2",
            "attach_session_file",
            new Dictionary<string, object?> { ["logicalName"] = "notes.txt" });

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () => "Attached 'notes.txt' v1 (Text, 5 bytes).",
                    "attach_session_file"),
            ],
        };

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline
                .ProcessSingleToolCallAsync(
                    fcc,
                    new PingRequest("hi", WorkingDirectory: "/tmp"),
                    chatOptions,
                    activeSpell: null,
                    sessionId: sessionId.ToString("D"),
                    turnContext: new ToolExecutionPipeline.TurnContext(),
                    suppressInvocationFailures: false,
                    cancellationToken: CancellationToken.None));
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    [Fact]
    public void AppendToolExchanges_ThenUserExtras_KeepsExtrasAfterAllToolResults()
    {

        List<MeAiChatMessage> messages =
        [
            new MeAiChatMessage(ChatRole.User, "prompt"),
        ];

        FunctionCallContent other = new("c1", "other_tool");

        FunctionCallContent attach = new("c2", "attach_session_file");

        ToolExecutionPipeline.AppendToolExchangeToMessages(messages, other, "c1", "other-result");

        ToolExecutionPipeline.AppendToolExchangeToMessages(messages, attach, "c2", "attach-ok");

        List<AIContent> extras = [new TextContent("framed-notes")];

        messages.Add(new MeAiChatMessage(ChatRole.User, extras));

        Assert.Equal(ChatRole.Tool, messages[^2].Role);

        Assert.Equal(ChatRole.User, messages[^1].Role);

        Assert.Equal("framed-notes", Assert.IsType<TextContent>(Assert.Single(messages[^1].Contents)).Text);

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

    private sealed class ThrowingSessionAttachmentStore : FakeSessionAttachmentStore
    {

        public override Task<ReadOnlyMemory<byte>> ReadBytesAsync(
            SessionAttachmentRecord record,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated attachment read failure");

    }

    private class FakeSessionAttachmentStore : ISessionAttachmentStore
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

        public virtual Task<ReadOnlyMemory<byte>> ReadBytesAsync(
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

        public Task ReconcileAsync(TimeSpan pendingOlderThan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ValidateReferencesAsync(
            Guid sessionId,
            IReadOnlyList<Guid> attachmentIds,
            int maxReferences,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IDisposable> AcquireSessionGateAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IDisposable>(EmptyDisposable.Instance);

        public Task DeleteRowsForSessionInAmbientTransactionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteSessionDirectory(Guid sessionId) => true;

        public Task ClearEntryIdsInAmbientTransactionAsync(
            Guid sessionId,
            IReadOnlyList<Guid> entryIds,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SessionAttachmentRecord>> ListBoundForForkAsync(
            Guid sourceSessionId,
            IReadOnlySet<Guid>? copiedSourceEntryIds,
            CancellationToken cancellationToken = default)
        {

            IEnumerable<SessionAttachmentRecord> bound = Records.Where(r =>
                r.SessionId == sourceSessionId && r.State == SessionAttachmentState.Bound);

            if (copiedSourceEntryIds is not null)
            {
                bound = bound.Where(r => r.EntryId is { } eid && copiedSourceEntryIds.Contains(eid));
            }

            return Task.FromResult<IReadOnlyList<SessionAttachmentRecord>>(bound.ToList());

        }

        public Task CopyBytesForForkAsync(
            Guid forkSessionId,
            IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task InsertForkRowsInAmbientTransactionAsync(
            Guid forkSessionId,
            IReadOnlyList<SessionAttachmentForkCopyPlan> plans,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private sealed class EmptyDisposable : IDisposable
        {

            public static readonly EmptyDisposable Instance = new();

            public void Dispose()
            {
            }

        }

    }

}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SessionAttachmentToolInjectionTests
{

    [Fact]

    public async Task Binary_attachment_is_never_read_or_injected_as_text()

    {

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord record = new(

            Guid.NewGuid(),

            sessionId,

            null,

            null,

            SessionAttachmentState.Bound,

            "report",

            "report.pdf",

            1,

            "rel/report.pdf",

            "abc",

            "application/pdf",

            5,

            SessionAttachmentKind.Binary,

            DateTimeOffset.UtcNow);

        ThrowingSessionAttachmentStore store = new();

        store.Records.Add(record);

        ArcanumSettings settings = new();

        IReadOnlyList<AIContent>? attached = await SessionAttachmentToolInjection.TryBuildContentsAsync(

            store,

            sessionId,

            record.LogicalKey,

            version: null,

            settings,

            requestModel: null);

        IReadOnlyList<AIContent>? refreshed = await SessionAttachmentToolInjection.TryBuildRefreshedContentsAsync(

            store,

            record,

            settings,

            requestModel: null);

        Assert.Null(attached);

        Assert.Null(refreshed);

    }

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
            Features = new FeatureSettings { Attachments = true },
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

            string expectedFence = string.Join(global::System.Environment.NewLine, "```", "hello", "```");

            Assert.Contains(
                expectedFence,
                text.Text,
                StringComparison.Ordinal);

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
            Features = new FeatureSettings { Attachments = true, Scrying = true },
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
            Features = new FeatureSettings { Attachments = true },
            Security = new SecuritySettings
            {
                Ward = new WardPolicySettings
                {
                    Enabled = true,
                    ForbiddenArts = ["attach_session_file"],
                    AutoDenyInUnattendedMode = true,
                },
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
    public async Task TryBuildContentsAsync_FailedImageValidation_DoesNotMarkInjectOnce()
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
            Features = new FeatureSettings { Attachments = true, Scrying = false },
        };

        SessionAttachmentTurnBudget.BeginTurn();

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

            IReadOnlyList<AIContent>? ok = await SessionAttachmentToolInjection.TryBuildContentsAsync(
                store,
                sessionId,
                "notes.txt",
                version: null,
                settings,
                requestModel: null);

            Assert.NotNull(ok);

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
            Features = new FeatureSettings { Attachments = true },
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
                Features = new FeatureSettings { Attachments = true },
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

        FunctionCallContent attach = new("c2", "refresh_session_file");

        ToolExecutionPipeline.AppendToolExchangeToMessages(messages, other, "c1", "other-result");

        ToolExecutionPipeline.AppendToolExchangeToMessages(messages, attach, "c2", "attach-ok");

        List<AIContent> extras = [new TextContent("framed-notes")];

        messages.Add(new MeAiChatMessage(ChatRole.User, extras));

        Assert.Equal(ChatRole.Tool, messages[^2].Role);

        Assert.Equal(ChatRole.User, messages[^1].Role);

        Assert.Equal("framed-notes", Assert.IsType<TextContent>(Assert.Single(messages[^1].Contents)).Text);

    }

    [Fact]
    public async Task ProcessSingleToolCall_RefreshSessionFile_PersistsAndQueuesStructuredFreshContent()
    {
        Guid sessionId = Guid.NewGuid();
        Guid originalId = Guid.NewGuid();
        DateTimeOffset freshness = DateTimeOffset.UtcNow;
        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "workspace",
            "notes.txt",
            "/workspace/notes.txt",
            "OLD",
            "1:1",
            freshness.AddMinutes(-1),
            3,
            AttachmentSourceStatus.Refreshable,
            null);
        FakeSessionAttachmentStore store = new();
        store.Records.Add(new SessionAttachmentRecord(
            originalId,
            sessionId,
            Guid.NewGuid(),
            null,
            SessionAttachmentState.Bound,
            "notes.txt",
            "notes.txt",
            1,
            "rel/notes.txt",
            "OLD",
            "text/plain",
            3,
            SessionAttachmentKind.Text,
            DateTimeOffset.UtcNow,
            source));
        FakeAttachmentSourceResolver resolver = new(
            new AttachmentSourceResolution(
                source with
                {
                    LastObservedContentSha256 = "NEW",
                    LastObservedWriteTime = freshness,
                    LastObservedByteLength = 7,
                    Status = AttachmentSourceStatus.PriorVersion,
                },
                Encoding.UTF8.GetBytes("changed"),
                "text/plain"));
        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Attachments = true },
            Security = new SecuritySettings { AllowedUploadMimeTypes = ["text/plain"] },
        };
        ToolExecutionPipeline pipeline = CreatePipeline(settings, store, resolver);
        FunctionCallContent call = new(
            "refresh-1",
            "refresh_session_file",
            new Dictionary<string, object?> { ["logicalKey"] = "notes.txt" });
        ChatOptions options = new()
        {
            Tools = [AIFunctionFactory.Create(() => "accepted", "refresh_session_file")],
        };
        SessionAttachmentTurnBudget.BeginTurn();
        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            ToolExecutionPipeline.ProcessedToolCall processed = await pipeline.ProcessSingleToolCallAsync(
                call,
                new PingRequest("refresh", Model: "vision-model", WorkingDirectory: "/workspace"),
                options,
                activeSpell: null,
                sessionId.ToString("D"),
                new ToolExecutionPipeline.TurnContext
                {
                    VisibleAttachmentIds = new HashSet<Guid> { originalId },
                    AssistantEntryId = Guid.NewGuid(),
                },
                suppressInvocationFailures: false,
                CancellationToken.None);

            Assert.False(processed.Failed);
            Assert.Contains("\"success\":true", processed.ResultText, StringComparison.Ordinal);
            Assert.Contains("\"newVersionCreated\":true", processed.ResultText, StringComparison.Ordinal);
            Assert.Contains("\"queuedForInjection\":true", processed.ResultText, StringComparison.Ordinal);
            Assert.Equal(2, processed.AttachmentRefresh?.Version);
            TextContent text = Assert.IsType<TextContent>(Assert.Single(processed.AdditionalContextContents!));
            Assert.Contains("logicalKey=notes.txt", text.Text, StringComparison.Ordinal);
            Assert.Contains("version=2", text.Text, StringComparison.Ordinal);
            Assert.Contains("sourceFreshness=", text.Text, StringComparison.Ordinal);
            Assert.Contains("changed", text.Text, StringComparison.Ordinal);

            ToolExecutionPipeline.ProcessedToolCall repeated = await pipeline.ProcessSingleToolCallAsync(
                call,
                new PingRequest("refresh", Model: "vision-model", WorkingDirectory: "/workspace"),
                options,
                activeSpell: null,
                sessionId.ToString("D"),
                new ToolExecutionPipeline.TurnContext
                {
                    VisibleAttachmentIds = new HashSet<Guid> { originalId },
                },
                suppressInvocationFailures: false,
                CancellationToken.None);

            Assert.Contains("\"newVersionCreated\":false", repeated.ResultText, StringComparison.Ordinal);
            Assert.Contains("\"queuedForInjection\":false", repeated.ResultText, StringComparison.Ordinal);
            Assert.Null(repeated.AdditionalContextContents);
            Assert.Equal(2, store.Records.Count);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
            SessionAttachmentTurnBudget.EndTurn();
        }
    }

    [Fact]
    public async Task ProcessSingleToolCall_RefreshSessionFile_RejectsInvisibleSnapshotAndAmbiguousSelectors()
    {
        Guid sessionId = Guid.NewGuid();
        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "workspace",
            "notes.txt",
            "/workspace/notes.txt",
            "HASH",
            "1:1",
            DateTimeOffset.UtcNow,
            4,
            AttachmentSourceStatus.Refreshable,
            null);
        FakeSessionAttachmentStore store = new();
        SessionAttachmentRecord lower = RefreshRecord(Guid.NewGuid(), sessionId, "notes.txt", source);
        SessionAttachmentRecord upper = RefreshRecord(Guid.NewGuid(), sessionId, "NOTES.TXT", source);
        SessionAttachmentRecord snapshot = RefreshRecord(Guid.NewGuid(), sessionId, "snapshot.txt", source: null);
        store.Records.AddRange([lower, upper, snapshot]);
        FakeAttachmentSourceResolver resolver = new(
            new AttachmentSourceResolution(source, Encoding.UTF8.GetBytes("same"), "text/plain"));
        ToolExecutionPipeline pipeline = CreatePipeline(
            new ArcanumSettings { Features = new FeatureSettings { Attachments = true } },
            store,
            resolver);
        ChatOptions options = new()
        {
            Tools = [AIFunctionFactory.Create(() => "accepted", "refresh_session_file")],
        };
        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            async Task<ToolExecutionPipeline.ProcessedToolCall> InvokeAsync(
                Dictionary<string, object?> arguments,
                IReadOnlySet<Guid> visible) =>
                await pipeline.ProcessSingleToolCallAsync(
                    new FunctionCallContent(Guid.NewGuid().ToString("N"), "refresh_session_file", arguments),
                    new PingRequest("refresh", WorkingDirectory: "/workspace"),
                    options,
                    activeSpell: null,
                    sessionId.ToString("D"),
                    new ToolExecutionPipeline.TurnContext { VisibleAttachmentIds = visible },
                    suppressInvocationFailures: false,
                    CancellationToken.None);

            ToolExecutionPipeline.ProcessedToolCall ambiguous = await InvokeAsync(
                new Dictionary<string, object?> { ["logicalKey"] = "notes.txt" },
                new HashSet<Guid> { lower.Id, upper.Id });
            ToolExecutionPipeline.ProcessedToolCall invisible = await InvokeAsync(
                new Dictionary<string, object?> { ["attachmentId"] = lower.Id.ToString("D") },
                new HashSet<Guid>());
            ToolExecutionPipeline.ProcessedToolCall snapshotOnly = await InvokeAsync(
                new Dictionary<string, object?> { ["attachmentId"] = snapshot.Id },
                new HashSet<Guid> { snapshot.Id });

            Assert.Contains("ambiguous_logical_key", ambiguous.ResultText, StringComparison.Ordinal);
            Assert.Contains("attachment_not_visible", invisible.ResultText, StringComparison.Ordinal);
            Assert.Contains("source_unavailable", snapshotOnly.ResultText, StringComparison.Ordinal);
            Assert.All(
                new[] { ambiguous, invisible, snapshotOnly },
                processed =>
                {
                    Assert.Null(processed.AdditionalContextContents);
                    Assert.Null(processed.AttachmentRefresh);
                });
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }
    }

    [Fact]
    public async Task RefreshSessionAttachmentAsync_Image_DoesNotRequireDefaultModelVision()
    {
        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "workspace",
            "shot.png",
            "/workspace/shot.png",
            "HASH",
            "1:1",
            DateTimeOffset.UtcNow,
            8,
            AttachmentSourceStatus.Refreshable,
            null);

        FakeSessionAttachmentStore store = new();

        store.Records.Add(RefreshRecord(
            attachmentId,
            sessionId,
            "shot.png",
            source,
            SessionAttachmentKind.Image,
            "image/png"));

        ArcanumSettings settings = new()
        {
            DefaultModel = "text-model",

            Features = new FeatureSettings { Attachments = true, Scrying = true },

            Providers =
            [
                new ProviderSettings
                {
                    Name = "test",

                    Models = [new ModelEntry("text-model", SupportsVision: false)],
                },
            ],
        };

        FakeAttachmentSourceResolver resolver = new(CreateImageRefreshResolution(source));

        ToolExecutionPipeline pipeline = CreatePipeline(settings, store, resolver);

        var refreshed = await pipeline.RefreshSessionAttachmentAsync(
            sessionId,
            attachmentId,
            campaignId: null);

        Assert.True(refreshed.IsSuccess, refreshed.Error.Message);

        Assert.True(refreshed.Value.NewVersionCreated);

        Assert.Equal(2, refreshed.Value.Version);

        Assert.Equal(
            SessionAttachmentContentPolicy.ResolveMaximumReadBytes(settings),
            resolver.LastMaximumBytes);
    }

    [Fact]
    public async Task ProcessSingleToolCall_RefreshSessionFile_RejectsDetectedImageForNonVisionModel()
    {
        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "workspace",
            "artifact.bin",
            "/workspace/artifact.bin",
            "HASH",
            "1:1",
            DateTimeOffset.UtcNow,
            8,
            AttachmentSourceStatus.Refreshable,
            null);

        FakeSessionAttachmentStore store = new();

        store.Records.Add(RefreshRecord(
            attachmentId,
            sessionId,
            "artifact.bin",
            source,
            SessionAttachmentKind.Binary,
            "application/pdf"));

        FakeAttachmentSourceResolver resolver = new(CreateImageRefreshResolution(source));

        ToolExecutionPipeline pipeline = CreatePipeline(
            new ArcanumSettings
            {
                Features = new FeatureSettings { Attachments = true, Scrying = true },
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "test",

                        Models = [new ModelEntry("text-model", SupportsVision: false)],
                    },
                ],
            },
            store,
            resolver);

        ChatOptions options = new()
        {
            Tools = [AIFunctionFactory.Create(() => "accepted", "refresh_session_file")],
        };

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            ToolExecutionPipeline.ProcessedToolCall processed = await pipeline.ProcessSingleToolCallAsync(
                new FunctionCallContent(
                    "image-refresh",
                    "refresh_session_file",
                    new Dictionary<string, object?> { ["attachmentId"] = attachmentId }),
                new PingRequest("refresh", Model: "text-model", WorkingDirectory: "/workspace"),
                options,
                activeSpell: null,
                sessionId.ToString("D"),
                new ToolExecutionPipeline.TurnContext
                {
                    VisibleAttachmentIds = new HashSet<Guid> { attachmentId },
                },
                suppressInvocationFailures: false,
                CancellationToken.None);

            Assert.Contains("image_policy_denied", processed.ResultText, StringComparison.Ordinal);

            Assert.Contains("does not support vision", processed.ResultText, StringComparison.OrdinalIgnoreCase);

            Assert.Null(processed.AdditionalContextContents);

            Assert.NotNull(resolver.LastMaximumBytes);

            Assert.Single(store.Records);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }
    }

    [Fact]
    public async Task RefreshSessionAttachmentAsync_BinaryToImage_AppliesRefreshedImageMimePolicy()
    {
        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "workspace",
            "artifact.bin",
            "/workspace/artifact.bin",
            "HASH",
            "1:1",
            DateTimeOffset.UtcNow,
            8,
            AttachmentSourceStatus.Refreshable,
            null);

        FakeSessionAttachmentStore store = new();

        store.Records.Add(RefreshRecord(
            attachmentId,
            sessionId,
            "artifact.bin",
            source,
            SessionAttachmentKind.Binary,
            "application/pdf"));

        ToolExecutionPipeline pipeline = CreatePipeline(
            new ArcanumSettings
            {
                Features = new FeatureSettings { Attachments = true, Scrying = true },

                Security = new SecuritySettings
                {
                    AllowedImageMimeTypes = ["image/jpeg"],
                },
            },
            store,
            new FakeAttachmentSourceResolver(CreateImageRefreshResolution(source)));

        var refreshed = await pipeline.RefreshSessionAttachmentAsync(
            sessionId,
            attachmentId,
            campaignId: null);

        Assert.True(refreshed.IsFailure);

        Assert.Equal(ErrorCodes.Attachment.InvalidContent, refreshed.Error.Code);

        Assert.Contains("not permitted", refreshed.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Single(store.Records);
    }

    [Fact]
    public async Task ProcessSingleToolCall_RefreshSessionFile_UnexpectedFailure_UsesModePolicy()
    {
        Guid sessionId = Guid.NewGuid();
        Guid attachmentId = Guid.NewGuid();
        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "workspace",
            "notes.txt",
            "/workspace/notes.txt",
            "HASH",
            "1:1",
            DateTimeOffset.UtcNow,
            4,
            AttachmentSourceStatus.Refreshable,
            null);
        FakeSessionAttachmentStore store = new();
        store.Records.Add(RefreshRecord(attachmentId, sessionId, "notes.txt", source));
        ToolExecutionPipeline pipeline = CreatePipeline(
            new ArcanumSettings { Features = new FeatureSettings { Attachments = true } },
            store,
            new ThrowingAttachmentSourceResolver());
        FunctionCallContent call = new(
            "refresh-failure",
            "refresh_session_file",
            new Dictionary<string, object?> { ["attachmentId"] = attachmentId });
        ChatOptions options = new()
        {
            Tools = [AIFunctionFactory.Create(() => "accepted", "refresh_session_file")],
        };
        ToolExecutionPipeline.TurnContext turn = new()
        {
            VisibleAttachmentIds = new HashSet<Guid> { attachmentId },
        };
        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ProcessSingleToolCallAsync(
                call,
                new PingRequest("refresh", WorkingDirectory: "/workspace"),
                options,
                activeSpell: null,
                sessionId.ToString("D"),
                turn,
                suppressInvocationFailures: false,
                CancellationToken.None));

            ToolExecutionPipeline.ProcessedToolCall tolerated = await pipeline.ProcessSingleToolCallAsync(
                call,
                new PingRequest("refresh", WorkingDirectory: "/workspace"),
                options,
                activeSpell: null,
                sessionId.ToString("D"),
                turn,
                suppressInvocationFailures: true,
                CancellationToken.None);

            Assert.True(tolerated.Failed);
            Assert.Contains("internal error", tolerated.ResultText, StringComparison.OrdinalIgnoreCase);
            Assert.Null(tolerated.AdditionalContextContents);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }
    }

    private static SessionAttachmentRecord RefreshRecord(
        Guid id,
        Guid sessionId,
        string logicalKey,
        AttachmentSourceMetadata? source,
        SessionAttachmentKind kind = SessionAttachmentKind.Text,
        string mimeType = "text/plain") =>
        new(
            id,
            sessionId,
            Guid.NewGuid(),
            null,
            SessionAttachmentState.Bound,
            logicalKey,
            logicalKey,
            1,
            "rel/" + logicalKey,
            "HASH",
            mimeType,
            4,
            kind,
            DateTimeOffset.UtcNow,
            source);

    private static AttachmentSourceResolution CreateImageRefreshResolution(
        AttachmentSourceMetadata source)
    {
        byte[] imageBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        return new AttachmentSourceResolution(
            source with
            {
                LastObservedContentSha256 = "NEW",

                LastObservedWriteTime = DateTimeOffset.UtcNow,

                LastObservedByteLength = imageBytes.Length,

                Status = AttachmentSourceStatus.PriorVersion,
            },
            imageBytes,
            "image/png");
    }

    private static ToolExecutionPipeline CreatePipeline(
        ArcanumSettings settings,
        ISessionAttachmentStore store,
        IAttachmentSourceResolver? resolver = null) =>
        new(
            new TestOptionsSnapshot<ArcanumSettings>(settings),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            store,
            NullLogger<ToolExecutionPipeline>.Instance,
            attachmentSourceResolver: resolver);

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

        public Task<SessionAttachmentRefreshPersistence> PersistRefreshedAsync(
            SessionAttachmentRecord latest,
            Guid? entryId,
            AttachmentSourceResolution current,
            CancellationToken cancellationToken = default)
        {
            string hash = current.Metadata.LastObservedContentSha256!;

            SessionAttachmentKind refreshedKind = SessionAttachmentContentPolicy.Classify(
                current.DetectedMimeType!);

            SessionAttachmentRecord record;

            bool created;

            if (string.Equals(latest.ContentSha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                record = latest with { Source = current.Metadata };
                int index = Records.FindIndex(row => row.Id == latest.Id);
                Records[index] = record;
                created = false;
            }
            else
            {
                record = latest with
                {
                    Id = Guid.NewGuid(),
                    EntryId = entryId,
                    Version = latest.Version + 1,
                    ContentSha256 = hash,
                    MimeType = current.DetectedMimeType!,
                    ByteLength = current.VerifiedBytes.Length,
                    Kind = refreshedKind,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Source = current.Metadata with
                    {
                        Status = AttachmentSourceStatus.Refreshable,
                        DiagnosticReason = null,
                    },
                };
                Records.Add(record);
                created = true;
            }

            BytesByLogical[record.LogicalKey] = current.VerifiedBytes.ToArray();
            return Task.FromResult(new SessionAttachmentRefreshPersistence(record, created));
        }

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

    private sealed class FakeAttachmentSourceResolver : IAttachmentSourceResolver
    {
        private readonly AttachmentSourceResolution _resolution;

        public FakeAttachmentSourceResolver(AttachmentSourceResolution resolution)
        {
            _resolution = resolution;
        }

        public long? LastMaximumBytes { get; private set; }

        public Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
            AttachmentSourceClaim claim,
            ReadOnlyMemory<byte> snapshotBytes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_resolution);

        public Task<AttachmentSourceMetadata> RevalidateAsync(
            AttachmentSourceMetadata source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_resolution.Metadata);

        public Task<AttachmentSourceResolution> ResolveCurrentAsync(
            AttachmentSourceMetadata source,
            string expectedSnapshotSha256,
            long maxBytes,
            AttachmentSourcePathAuthorizer authorizeCanonicalPath,
            CancellationToken cancellationToken = default)
        {
            LastMaximumBytes = maxBytes;

            return Task.FromResult(_resolution);
        }
    }

    private sealed class ThrowingAttachmentSourceResolver : IAttachmentSourceResolver
    {
        public Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
            AttachmentSourceClaim claim,
            ReadOnlyMemory<byte> snapshotBytes,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("source resolver should not have been invoked");

        public Task<AttachmentSourceMetadata> RevalidateAsync(
            AttachmentSourceMetadata source,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("source resolver should not have been invoked");

        public Task<AttachmentSourceResolution> ResolveCurrentAsync(
            AttachmentSourceMetadata source,
            string expectedSnapshotSha256,
            long maxBytes,
            AttachmentSourcePathAuthorizer authorizeCanonicalPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("source resolver should not have been invoked");
    }

}

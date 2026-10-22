using System.Globalization;

using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Events;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Platform;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The Comprehension seam driven the way production reaches it: the real internal tool server through the real
/// MCP bridge tool into the real tool pipeline for <c>attach_session_file</c>, and the per-turn reference path from
/// <see cref="SessionAttachmentTurnService.PrepareAsync"/> through materialization. A guard that refuses a document
/// before the materializer runs is invisible to a test that calls the materializer directly.
/// </summary>
public sealed class SessionAttachmentComprehensionSeamTests
{
    private const string TextOnlyRefusal =
        "The selected attachment can be downloaded or exported, but its content cannot be injected into a model turn.";

    public static TheoryData<string> RegistryMimeTypes()
    {
        TheoryData<string> data = [];

        foreach (SessionAttachmentComprehensionFormat format in SessionAttachmentComprehensionPolicy.Formats)
        {
            data.Add(format.MimeType);
        }

        return data;
    }

    public static TheoryData<bool, bool> GateCombinationsWithAGateOff() => new()
    {
        { false, false },
        { true, false },
        { false, true },
    };

    public static TheoryData<string> IneligibleBinaryMimeTypes() =>
    [
        "application/pdf",
        "application/octet-stream",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
        "application/msword",
    ];

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public async Task AttachSessionFile_ThroughTheRealToolAndPipeline_InjectsTheFramedDocument(string mimeType)
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        ArcanumSettings settings = BothGates();

        string expected = await ExtractedTextAsync(record, bytes, settings);

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, settings);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.AttachAsync(sessionId, record.LogicalKey);

        Assert.False(processed.Failed, processed.ResultText);

        TextContent injected = Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(processed.AdditionalContextContents)));

        SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format);

        Assert.Contains("UNTRUSTED DATA; " + format!.FormatName + "; layout not preserved", injected.Text, StringComparison.Ordinal);

        Assert.Contains(expected, injected.Text, StringComparison.Ordinal);

        Assert.Equal("explicitAttachment", injected.AdditionalProperties!["arcanum.context_source"]);
    }

    /// <summary>
    /// The gates are judged once, by the turn's settings, where the post-tool materializer runs. The tool server
    /// cannot see the turn's settings, so it judges a registry document by its format alone and leaves the gates
    /// to that one decision; the model is told the document was not attached, with the existing refusal.
    /// </summary>
    [Theory]
    [MemberData(nameof(GateCombinationsWithAGateOff))]
    public async Task AttachSessionFile_WithAGateOff_IsNotAttachedWithTheExistingRefusal(bool retrievalEnabled, bool comprehensionEnabled)
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        string mimeType = SessionAttachmentComprehensionPolicy.WordDocumentMimeType;

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Attachments = true, AttachmentRetrieval = retrievalEnabled, Comprehension = comprehensionEnabled },
        };

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, settings);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.AttachAsync(sessionId, record.LogicalKey);

        Assert.Null(processed.AdditionalContextContents);

        Assert.False(processed.Failed, processed.ResultText);

        Assert.Equal("Not attached: 'document.bin' v1. " + TextOnlyRefusal, processed.ResultText);
    }

    public static TheoryData<string, string> UnextractableDocuments() => new()
    {
        { "PK\u0003\u0004 not really a package", "Document package is not a valid ZIP container." },
        { "\u00D0\u00CF\u0011\u00E0\u00A1\u00B1\u001A\u00E1 OLE2 EncryptedPackage", "Encrypted document packages are not supported." },
    };

    /// <summary>
    /// The tool accepts a registry document before anything extracts it. When extraction then yields nothing, the
    /// model is told the document was not attached and the code-owned reason, never that content is coming.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnextractableDocuments))]
    public async Task AttachSessionFile_UnextractableDocument_TellsTheModelItWasNotAttachedAndWhy(string payload, string reason)
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(payload);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, SessionAttachmentComprehensionPolicy.WordDocumentMimeType, bytes.Length);

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, BothGates());

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.AttachAsync(sessionId, record.LogicalKey);

        Assert.Null(processed.AdditionalContextContents);

        Assert.False(processed.Failed, processed.ResultText);

        Assert.Equal("Not attached: 'document.bin' v1. " + reason, processed.ResultText);
    }

    [Fact]
    public async Task AttachSessionFile_PackageWithoutItsParts_TellsTheModelItWasNotAttachedAndWhy()
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = ComprehensionPackageBuilder.Build(("notes.txt", "not a document part"));

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, SessionAttachmentComprehensionPolicy.WordDocumentMimeType, bytes.Length);

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, BothGates());

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.AttachAsync(sessionId, record.LogicalKey);

        Assert.Null(processed.AdditionalContextContents);

        Assert.Equal("Not attached: 'document.bin' v1. Document package is missing a required part.", processed.ResultText);
    }

    /// <summary>
    /// A configuration reload between the turn's start and the tool call: the tool and the materializer must
    /// still decide alike, by the turn's settings.
    /// </summary>
    [Fact]
    public async Task AttachSessionFile_GateTurnedOnAfterTheTurnBegan_FollowsTheTurnSettings()
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        string mimeType = SessionAttachmentComprehensionPolicy.WordDocumentMimeType;

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, BothGates(), requestSettings: RetrievalOnly());

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.AttachAsync(sessionId, record.LogicalKey);

        Assert.Null(processed.AdditionalContextContents);

        Assert.Equal("Not attached: 'document.bin' v1. " + TextOnlyRefusal, processed.ResultText);
    }

    [Fact]
    public async Task AttachSessionFile_GateTurnedOffAfterTheTurnBegan_FollowsTheTurnSettings()
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        string mimeType = SessionAttachmentComprehensionPolicy.WordDocumentMimeType;

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        string expected = await ExtractedTextAsync(record, bytes, BothGates());

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, RetrievalOnly(), requestSettings: BothGates());

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.AttachAsync(sessionId, record.LogicalKey);

        Assert.False(processed.Failed, processed.ResultText);

        TextContent injected = Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(processed.AdditionalContextContents)));

        Assert.Contains(expected, injected.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(IneligibleBinaryMimeTypes))]
    public async Task AttachSessionFile_IneligibleBinaryWithBothGatesOn_KeepsTheExistingRefusal(string mimeType)
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = [0x50, 0x4B, 0x03, 0x04];

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, BothGates());

        McpToolsCallResultWire wire = await harness.CallToolAsync(sessionId, record.LogicalKey);

        Assert.True(wire.IsError);

        Assert.Equal(TextOnlyRefusal, wire.Content[0].Text);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.AttachAsync(sessionId, record.LogicalKey);

        Assert.True(processed.Failed);

        Assert.Null(processed.AdditionalContextContents);
    }

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public async Task PerTurnReference_ThroughPrepareThenMaterialize_InjectsTheFramedDocument(string mimeType)
    {
        Guid sessionId = Guid.NewGuid();

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        ArcanumSettings settings = BothGates();

        string expected = await ExtractedTextAsync(record, bytes, settings);

        NoOpSessionAttachmentStore store = new(
            records: new Dictionary<Guid, SessionAttachmentRecord> { [record.Id] = record },
            readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes));

        SessionAttachmentTurnPreparation preparation = await SessionAttachmentTurnService.PrepareAsync(
            new PingRequest("summarize the document", SessionId: sessionId, AttachmentReferences: [record.Id]),
            store,
            settings,
            turnSessionId: sessionId,
            turnEntryId: null,
            pendingTurnId: null);

        Assert.True(preparation.ErrorMessage is null, preparation.ErrorMessage);

        SessionAttachmentExplicitMaterialization deferred = Assert.Single(preparation.ExplicitMaterializations!);

        Assert.True(deferred.RequiresMaterialization);

        Assert.Equal(record.Id, deferred.Record.Id);

        SessionAttachmentReferenceMaterialization materialized = await SessionAttachmentTurnService.MaterializeReferenceAsync(
            deferred.Record,
            store,
            settings,
            "model",
            CancellationToken.None);

        Assert.Null(materialized.ErrorMessage);

        TextContent injected = Assert.IsType<TextContent>(Assert.Single(materialized.Contents));

        SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format);

        Assert.Contains("UNTRUSTED DATA; " + format!.FormatName + "; layout not preserved", injected.Text, StringComparison.Ordinal);

        Assert.Contains(expected, injected.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(GateCombinationsWithAGateOff))]
    public async Task PerTurnReference_WithAGateOff_KeepsTheExistingRefusal(bool retrievalEnabled, bool comprehensionEnabled)
    {
        Guid sessionId = Guid.NewGuid();

        string mimeType = SessionAttachmentComprehensionPolicy.WordDocumentMimeType;

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, 8);

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Attachments = true, AttachmentRetrieval = retrievalEnabled, Comprehension = comprehensionEnabled },
        };

        SessionAttachmentTurnPreparation preparation = await PrepareReferenceAsync(record, settings);

        Assert.Equal($"Attachment '{record.Id}' cannot be injected into model context.", preparation.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(IneligibleBinaryMimeTypes))]
    public async Task PerTurnReference_IneligibleBinaryWithBothGatesOn_KeepsTheExistingRefusal(string mimeType)
    {
        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, 8);

        SessionAttachmentTurnPreparation preparation = await PrepareReferenceAsync(record, BothGates());

        Assert.Equal($"Attachment '{record.Id}' cannot be injected into model context.", preparation.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public async Task RefreshSessionFile_RealTool_HasNoKindRefusalForAComprehensionDocument(string mimeType)
    {
        Guid sessionId = Guid.NewGuid();

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "workspace",
            "document.bin",
            "/workspace/document.bin",
            "document-hash",
            "1:1",
            DateTimeOffset.UtcNow,
            bytes.Length,
            AttachmentSourceStatus.Refreshable,
            null);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length) with { Source = source };

        await using AttachToolHarness harness = await AttachToolHarness.CreateAsync(record, bytes, BothGates());

        McpToolsCallResultWire wire = await harness.CallRefreshToolAsync(sessionId, record.LogicalKey);

        Assert.False(wire.IsError, wire.Content[0].Text);

        Assert.StartsWith("Refresh accepted for", wire.Content[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public async Task Refresh_MaterializesEveryRegistryDocumentFramedAsUntrustedData(string mimeType)
    {
        SessionAttachmentTurnBudget.BeginTurn();

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        ArcanumSettings settings = BothGates();

        string expected = await ExtractedTextAsync(record, bytes, settings);

        IReadOnlyList<AIContent>? contents = await SessionAttachmentToolInjection.TryBuildRefreshedContentsAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            record,
            settings,
            null);

        TextContent injected = Assert.IsType<TextContent>(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AIContent>>(contents)));

        SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format);

        Assert.Contains("UNTRUSTED DATA; " + format!.FormatName + "; layout not preserved", injected.Text, StringComparison.Ordinal);

        Assert.Contains(expected, injected.Text, StringComparison.Ordinal);

        Assert.Equal("refreshedFile", injected.AdditionalProperties!["arcanum.context_source"]);
    }

    [Theory]
    [MemberData(nameof(RegistryMimeTypes))]
    public async Task Pin_MaterializesEveryRegistryDocumentFramedAsUntrustedData(string mimeType)
    {
        Guid sessionId = Guid.NewGuid();

        byte[] bytes = ComprehensionSeamDocuments.Bytes(mimeType);

        SessionAttachmentRecord record = ComprehensionSeamDocuments.Record(sessionId, mimeType, bytes.Length);

        ArcanumSettings settings = BothGates();

        string expected = await ExtractedTextAsync(record, bytes, settings);

        SessionContextPinRecord pin = new(
            Guid.NewGuid(), sessionId, SessionContextPinKind.Attachment,
            record.Id.ToString("D"), "document", record.ContentSha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        // An attachment pin never reads a session entry, so the session repository is not consulted.
        SessionContextPinMaterializer materializer = new(
            new StaticPinStore(pin),
            new NoOpSessionAttachmentStore(record, readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            null!,
            new TestOptionsSnapshot<ArcanumSettings>(settings));

        SessionContextPinMaterialization result = await materializer.MaterializeAsync(sessionId, Path.GetTempPath(), CancellationToken.None);

        string text = Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text;

        SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out SessionAttachmentComprehensionFormat? format);

        Assert.Contains("status: Current", text, StringComparison.Ordinal);

        Assert.Contains("UNTRUSTED DATA; " + format!.FormatName + "; layout not preserved", text, StringComparison.Ordinal);

        Assert.Contains(expected.TrimEnd('\n'), text, StringComparison.Ordinal);
    }

    private static Task<SessionAttachmentTurnPreparation> PrepareReferenceAsync(
        SessionAttachmentRecord record,
        ArcanumSettings settings) =>
        SessionAttachmentTurnService.PrepareAsync(
            new PingRequest("summarize", SessionId: record.SessionId, AttachmentReferences: [record.Id]),
            new NoOpSessionAttachmentStore(records: new Dictionary<Guid, SessionAttachmentRecord> { [record.Id] = record }),
            settings,
            turnSessionId: record.SessionId,
            turnEntryId: null,
            pendingTurnId: null);

    private static ArcanumSettings BothGates() =>
        new()
        {
            Features = new FeatureSettings { Attachments = true, AttachmentRetrieval = true, Comprehension = true },
        };

    private static ArcanumSettings RetrievalOnly() =>
        new()
        {
            Features = new FeatureSettings { Attachments = true, AttachmentRetrieval = true, Comprehension = false },
        };

    /// <summary>What the registry host extracts from the package, so a path's output is compared with the extractor, not a literal.</summary>
    private static async Task<string> ExtractedTextAsync(SessionAttachmentRecord record, byte[] bytes, ArcanumSettings settings)
    {
        SessionAttachmentExtractionResult extraction = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            record,
            settings,
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, extraction.Status);

        Assert.False(string.IsNullOrWhiteSpace(extraction.Text));

        return extraction.Text;
    }

    /// <summary>
    /// The real <see cref="ArcanumInternalToolServer"/> behind its in-process transport, reached by the real
    /// <see cref="McpBridgeTool"/> inside the real <see cref="ToolExecutionPipeline"/>.
    /// </summary>
    private sealed class AttachToolHarness : IAsyncDisposable
    {
        private readonly InProcessMcpTransport _transport;

        private readonly Task _serverTask;

        private readonly CancellationTokenSource _lifetime;

        private readonly ToolExecutionPipeline _pipeline;

        private readonly AIFunction _tool;

        private int _nextId;

        private AttachToolHarness(
            InProcessMcpTransport transport,
            Task serverTask,
            CancellationTokenSource lifetime,
            ToolExecutionPipeline pipeline)
        {
            _transport = transport;

            _serverTask = serverTask;

            _lifetime = lifetime;

            _pipeline = pipeline;

            _tool = new McpBridgeTool(
                "attach_session_file",
                "attach",
                JsonDocument.Parse("{}").RootElement.Clone(),
                new ForwardingClient(this),
                toolOutputCapBytes: 64 * 1024);
        }

        /// <param name="settings">The host's current settings, which the tool server reads.</param>
        /// <param name="requestSettings">The turn's settings snapshot, which the pipeline reads; the host's when omitted.</param>
        public static async Task<AttachToolHarness> CreateAsync(
            SessionAttachmentRecord record,
            byte[] bytes,
            ArcanumSettings settings,
            ArcanumSettings? requestSettings = null)
        {
            NoOpSessionAttachmentStore store = new(
                record,
                readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes),
                openRead: (_, _) => throw new InvalidOperationException("Documents must use stored bytes."));

            ServiceCollection services = new();

            services.AddSingleton<ISessionAttachmentStore>(store);

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings));

            IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            UnseenServantPacer pacer = new(
                new SilentEventBus(),
                new TestOptionsMonitor<ArcanumSettings>(settings),
                scopeFactory,
                NullLogger<UnseenServantPacer>.Instance);

            (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
                new HumanPromptRegistry(),
                scopeFactory,
                pacer,
                workspaceRootNormalizedOrNull: null,
                listDirectoryMaxPaths: 64,
                intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with
                {
                    EnableLexiconSystem = false,
                    EnableArchiveSearch = false,
                },
                maxFileReadSizeBytes: 1024 * 1024,
                conclaveEnabled: false,
                sagaEnabled: false,
                a2aClientEnabled: false,
                attachmentsToolEnabled: true,
                maxJsonRpcLineBytes: 2_097_152,
                logger: NullLogger<ArcanumInternalToolServer>.Instance,
                allowHostProcessTools: true);

            CancellationTokenSource lifetime = new();

            Task serverTask = server.RunAsync(lifetime.Token);

            await transport.StartAsync();

            ToolExecutionPipeline pipeline = new(
                new TestOptionsSnapshot<ArcanumSettings>(requestSettings ?? settings),
                new AllowingWard(),
                new AllowAllSanctumGuard(),
                store,
                NullLogger<ToolExecutionPipeline>.Instance);

            return new AttachToolHarness(transport, serverTask, lifetime, pipeline);
        }

        /// <summary>The tool call as the model makes it: through the pipeline, the bridge tool, and the real server.</summary>
        public async Task<ToolExecutionPipeline.ProcessedToolCall> AttachAsync(Guid sessionId, string logicalName)
        {
            SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

            try
            {
                return await _pipeline.ProcessSingleToolCallAsync(
                    new FunctionCallContent(
                        "call_attach_" + logicalName,
                        "attach_session_file",
                        new Dictionary<string, object?> { ["logicalName"] = logicalName }),
                    new PingRequest("hi", WorkingDirectory: "/tmp"),
                    new ChatOptions { Tools = [_tool] },
                    activeSpell: null,
                    sessionId: sessionId.ToString("D"),
                    turnContext: new ToolExecutionPipeline.TurnContext(),
                    suppressInvocationFailures: true,
                    cancellationToken: CancellationToken.None);
            }
            finally
            {
                SessionAttachmentToolAmbient.CurrentSessionId = null;
            }
        }

        /// <summary>The raw <c>tools/call</c> result of the real server, before the bridge turns an error into an exception.</summary>
        public async Task<McpToolsCallResultWire> CallToolAsync(Guid sessionId, string logicalName)
        {
            SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

            try
            {
                return await ForwardAsync(logicalName, null);
            }
            finally
            {
                SessionAttachmentToolAmbient.CurrentSessionId = null;
            }
        }

        /// <summary>The raw <c>refresh_session_file</c> result of the real server for a logical key.</summary>
        public async Task<McpToolsCallResultWire> CallRefreshToolAsync(Guid sessionId, string logicalKey)
        {
            SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

            try
            {
                return await SendAsync(
                    "refresh_session_file",
                    JsonSerializer.SerializeToElement(
                        new RefreshSessionFileParams(null, logicalKey),
                        McpJsonSerializerContext.Default.RefreshSessionFileParams));
            }
            finally
            {
                SessionAttachmentToolAmbient.CurrentSessionId = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();

            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _transport.DisposeAsync().ConfigureAwait(false);

            _lifetime.Dispose();
        }

        private Task<McpToolsCallResultWire> ForwardAsync(string logicalName, int? version) =>
            SendAsync(
                "attach_session_file",
                JsonSerializer.SerializeToElement(
                    new AttachSessionFileParams(logicalName, version),
                    McpJsonSerializerContext.Default.AttachSessionFileParams));

        private async Task<McpToolsCallResultWire> SendAsync(string toolName, JsonElement arguments)
        {
            int id = Interlocked.Increment(ref _nextId);

            RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol.JsonRpcRequest request = new()
            {
                Method = "tools/call",
                Params = JsonSerializer.SerializeToElement(
                    new McpToolsCallParams
                    {
                        Name = toolName,
                        Arguments = arguments,
                    },
                    McpJsonSerializerContext.Default.McpToolsCallParams),
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            await _transport.WriteRequestAsync(request).ConfigureAwait(false);

            McpInboundEnvelope envelope = await _transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return JsonSerializer.Deserialize(
                envelope.Response!.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;
        }

        /// <summary>The seam <see cref="McpBridgeTool"/> calls through: the production tool server instead of an SDK client.</summary>
        private sealed class ForwardingClient(AttachToolHarness owner) : IMcpClient
        {
            public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async Task<CallToolResult> CallToolAsync(
                string toolName,
                IReadOnlyDictionary<string, object?> arguments,
                TimeSpan? requestTimeout = null,
                CancellationToken cancellationToken = default)
            {
                Assert.Equal("attach_session_file", toolName);

                string logicalName = Convert.ToString(arguments["logicalName"], CultureInfo.InvariantCulture)!;

                McpToolsCallResultWire wire = await owner.ForwardAsync(logicalName, null).ConfigureAwait(false);

                return new CallToolResult
                {
                    IsError = wire.IsError,
                    Content = [.. wire.Content.Select(static block => (ContentBlock)new TextContentBlock { Text = block.Text ?? string.Empty })],
                };
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StaticPinStore(params SessionContextPinRecord[] pins) : ISessionContextPinStore
    {
        public Task<IReadOnlyList<SessionContextPinRecord>> ListAsync(
            Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionContextPinRecord>>(pins);

        public Task<SessionContextPinRecord> UpsertAsync(
            Guid sessionId, SessionContextPinKind kind, string targetIdentifier, string displayLabel,
            string? contentVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid sessionId, Guid pinId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SilentEventBus : IEventBus
    {
        public void Publish<T>(T @event) where T : notnull
        {
        }

        public async IAsyncEnumerable<T> Subscribe<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            where T : notnull
        {
            await Task.CompletedTask;

            yield break;
        }
    }

    private sealed class AllowingWard : IWard
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

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin) =>
            new(allowed, reason, DateTimeOffset.UtcNow, origin);

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
}

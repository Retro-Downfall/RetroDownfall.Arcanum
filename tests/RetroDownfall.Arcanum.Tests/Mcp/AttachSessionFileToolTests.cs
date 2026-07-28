using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class AttachSessionFileToolTests
{

    [Fact]
    public async Task ToolsList_AdvertisesAttachSessionFile_WhenEnabled()
    {

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: true);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.Contains(tools.Tools, static t => t.Name == "attach_session_file");

    }

    [Fact]
    public async Task ToolsList_DoesNotAdvertiseAttachSessionFile_WhenDisabled()
    {

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: false);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.DoesNotContain(tools.Tools, static t => t.Name == "attach_session_file");

    }

    [Fact]
    public async Task ToolsCall_WhenDisabled_ReturnsError()
    {

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: false);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new AttachSessionFileParams("notes.txt"),
            McpJsonSerializerContext.Default.AttachSessionFileParams);

        McpToolsCallResultWire result = await session.CallToolAsync("attach_session_file", arguments);

        Assert.True(result.IsError);

        Assert.Contains("disabled", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_WithoutAmbientSession_ReturnsError()
    {

        FakeSessionAttachmentStore store = new();

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: true, store: store);

        SessionAttachmentToolAmbient.CurrentSessionId = null;

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new AttachSessionFileParams("notes.txt"),
            McpJsonSerializerContext.Default.AttachSessionFileParams);

        McpToolsCallResultWire result = await session.CallToolAsync("attach_session_file", arguments);

        Assert.True(result.IsError);

        Assert.Contains("No current session", result.Content![0].Text!, StringComparison.Ordinal);

        Assert.DoesNotContain("Available logical names", result.Content![0].Text!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_TwoSessionsConcurrent_EachResolvesOwnSessionOnly()
    {

        Guid sessionA = Guid.NewGuid();

        Guid sessionB = Guid.NewGuid();

        FakeSessionAttachmentStore store = new();

        store.Records.Add(
            new SessionAttachmentRecord(
                Guid.NewGuid(),
                sessionA,
                null,
                null,
                SessionAttachmentState.Bound,
                "a-only.txt",
                "a-only.txt",
                1,
                "rel/a.txt",
                "abc",
                "text/plain",
                4,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        store.Records.Add(
            new SessionAttachmentRecord(
                Guid.NewGuid(),
                sessionB,
                null,
                null,
                SessionAttachmentState.Bound,
                "b-only.txt",
                "b-only.txt",
                1,
                "rel/b.txt",
                "def",
                "text/plain",
                4,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        await using TestMcpSession mcpA = await CreateSessionAsync(attachmentsToolEnabled: true, store: store);

        await using TestMcpSession mcpB = await CreateSessionAsync(attachmentsToolEnabled: true, store: store);

        JsonElement argsA = JsonSerializer.SerializeToElement(
            new AttachSessionFileParams("a-only.txt"),
            McpJsonSerializerContext.Default.AttachSessionFileParams);

        JsonElement argsB = JsonSerializer.SerializeToElement(
            new AttachSessionFileParams("b-only.txt"),
            McpJsonSerializerContext.Default.AttachSessionFileParams);

        Task<McpToolsCallResultWire> taskA = Task.Run(async () =>
        {
            SessionAttachmentToolAmbient.CurrentSessionId = sessionA;

            try
            {
                return await mcpA.CallToolAsync("attach_session_file", argsA);
            }
            finally
            {
                SessionAttachmentToolAmbient.CurrentSessionId = null;
            }
        });

        Task<McpToolsCallResultWire> taskB = Task.Run(async () =>
        {
            SessionAttachmentToolAmbient.CurrentSessionId = sessionB;

            try
            {
                return await mcpB.CallToolAsync("attach_session_file", argsB);
            }
            finally
            {
                SessionAttachmentToolAmbient.CurrentSessionId = null;
            }
        });

        McpToolsCallResultWire[] results = await Task.WhenAll(taskA, taskB);

        Assert.False(results[0].IsError);

        Assert.False(results[1].IsError);

        Assert.Contains("a-only.txt", results[0].Content![0].Text!, StringComparison.Ordinal);

        Assert.Contains("b-only.txt", results[1].Content![0].Text!, StringComparison.Ordinal);

        Assert.DoesNotContain("b-only.txt", results[0].Content![0].Text!, StringComparison.Ordinal);

        Assert.DoesNotContain("a-only.txt", results[1].Content![0].Text!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsList_OpaqueInvocationToken_AbsentFromSchema()
    {

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: true);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        McpToolDefinitionWire attach = Assert.Single(tools.Tools, static t => t.Name == "attach_session_file");

        string schemaJson = attach.InputSchema.GetRawText();

        Assert.DoesNotContain(
            SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
            schemaJson,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_OpaqueTokenPath_StripsTokenBeforeToolLogicAndSnapshot()
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
                12,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: true, store: store);

        // Snapshot / audit must never include the host opaque field even if the model invents it.
        Dictionary<string, object?> modelArgs = new(StringComparer.Ordinal)
        {
            ["logicalName"] = "notes.txt",
            [SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName] = "model-supplied-should-not-leak",
        };

        FunctionCallContent fcc = new("call-1", "attach_session_file", modelArgs);

        string grimoireSnapshot = ToolExecutionPipeline.SerializeToolArgumentsForGrimoire(fcc);

        Assert.DoesNotContain(
            SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
            grimoireSnapshot,
            StringComparison.Ordinal);

        Assert.DoesNotContain("model-supplied-should-not-leak", grimoireSnapshot, StringComparison.Ordinal);

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            McpToolsCallParams callParams = new()
            {
                Name = "attach_session_file",
                Arguments = JsonSerializer.SerializeToElement(
                    new AttachSessionFileParams("notes.txt"),
                    McpJsonSerializerContext.Default.AttachSessionFileParams),
            };

            JsonElement paramsElement = JsonSerializer.SerializeToElement(
                callParams,
                McpJsonSerializerContext.Default.McpToolsCallParams);

            JsonRpcRequest request = new()
            {
                Method = "tools/call",
                Params = paramsElement,
                Id = JsonSerializer.SerializeToElement(99, McpJsonSerializerContext.Default.Int32),
            };

            // Opaque-only binding (no request-id map entry).
            await session.Transport.WriteRequestWithOpaqueAmbientAsync(request);

            McpInboundEnvelope envelope = await session.Transport.InboundReader.ReadAsync();

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            McpToolsCallResultWire result = JsonSerializer.Deserialize(
                envelope.Response!.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

            Assert.False(result.IsError);

            Assert.Contains("notes.txt", result.Content![0].Text!, StringComparison.Ordinal);

            Assert.DoesNotContain(
                SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
                result.Content![0].Text!,
                StringComparison.Ordinal);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    [Fact]
    public async Task ToolsCall_MissingLogicalName_ListsAvailableNames()
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
                12,
                SessionAttachmentKind.Text,
                DateTimeOffset.UtcNow));

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: true, store: store);

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            JsonElement arguments = JsonSerializer.SerializeToElement(
                new AttachSessionFileParams("missing.txt"),
                McpJsonSerializerContext.Default.AttachSessionFileParams);

            McpToolsCallResultWire result = await session.CallToolAsync("attach_session_file", arguments);

            Assert.True(result.IsError);

            Assert.Contains("notes.txt", result.Content![0].Text!, StringComparison.Ordinal);

            Assert.Contains("missing.txt", result.Content![0].Text!, StringComparison.Ordinal);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    [Fact]
    public async Task ToolsCall_Success_ReturnsAckWithoutImageBytes()
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
                2,
                "rel/shot.png",
                "abc",
                "image/png",
                pngBytes.Length,
                SessionAttachmentKind.Image,
                DateTimeOffset.UtcNow));

        store.BytesByLogical["shot.png"] = pngBytes;

        await using TestMcpSession session = await CreateSessionAsync(attachmentsToolEnabled: true, store: store);

        SessionAttachmentToolAmbient.CurrentSessionId = sessionId;

        try
        {
            JsonElement arguments = JsonSerializer.SerializeToElement(
                new AttachSessionFileParams("shot.png", 2),
                McpJsonSerializerContext.Default.AttachSessionFileParams);

            McpToolsCallResultWire result = await session.CallToolAsync("attach_session_file", arguments);

            Assert.False(result.IsError);

            string text = result.Content![0].Text!;

            Assert.Contains("Attached 'shot.png' v2", text, StringComparison.Ordinal);

            Assert.Contains("Image", text, StringComparison.Ordinal);

            Assert.Contains("injected into the next model turn", text, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(Convert.ToBase64String(pngBytes), text, StringComparison.Ordinal);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = null;
        }

    }

    private static async Task<TestMcpSession> CreateSessionAsync(
        bool attachmentsToolEnabled,
        FakeSessionAttachmentStore? store = null)
    {

        ServiceCollection services = new();

        services.AddSingleton<ISessionAttachmentStore>(store ?? new FakeSessionAttachmentStore());

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        IntelligenceSettings intelligenceSettings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
        };

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            humanPrompts,
            scopeFactory,
            pacer,
            workspaceRootNormalizedOrNull: null,
            executeCommandTimeout: TimeSpan.FromSeconds(30),
            executeCommandTimeoutSecondsForDisplay: 30,
            listDirectoryMaxPaths: 64,
            intelligenceSettings: intelligenceSettings,
            maxFileReadSizeBytes: 1024 * 1024,
            conclaveEnabled: false,
            sagaEnabled: false,
            a2aClientEnabled: false,
            attachmentsToolEnabled: attachmentsToolEnabled,
            maxJsonRpcLineBytes: 2_097_152,
            logger: NullLogger<ArcanumInternalToolServer>.Instance);

        CancellationTokenSource cts = new();

        Task serverTask = server.RunAsync(cts.Token);

        await transport.StartAsync();

        return new TestMcpSession(transport, serverTask, cts);

    }

    private sealed class FakeEventBus : IEventBus
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

            return Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("hello"));

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

    private sealed class TestMcpSession(
        InProcessMcpTransport transport,
        Task serverTask,
        CancellationTokenSource lifetime) : IAsyncDisposable
    {

        private int _nextId;

        internal InProcessMcpTransport Transport => transport;

        public async ValueTask DisposeAsync()
        {

            lifetime.Cancel();

            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await transport.DisposeAsync().ConfigureAwait(false);

            lifetime.Dispose();

        }

        public async Task<JsonRpcResponse> SendRequestAsync(string method, JsonElement? parameters)
        {

            int id = Interlocked.Increment(ref _nextId);

            JsonRpcRequest request = new()
            {
                Method = method,
                Params = parameters,
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            await transport.WriteRequestAsync(request).ConfigureAwait(false);

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return envelope.Response!;

        }

        public async Task<McpToolsCallResultWire> CallToolAsync(string name, JsonElement arguments)
        {

            McpToolsCallParams callParams = new() { Name = name, Arguments = arguments };

            JsonElement paramsElement = JsonSerializer.SerializeToElement(
                callParams,
                McpJsonSerializerContext.Default.McpToolsCallParams);

            JsonRpcResponse response = await SendRequestAsync("tools/call", paramsElement).ConfigureAwait(false);

            return JsonSerializer.Deserialize(
                response.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        }

    }

}

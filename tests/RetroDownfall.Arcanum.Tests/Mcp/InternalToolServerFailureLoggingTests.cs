using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// The internal tool server's error logs reach the rolling log and <c>GET /api/logs</c>. A memory tool
/// whose backing store fails must not copy the memory text it was asked about into them: neither the
/// query or entry name the model chose, nor the exception, whose message (an FTS fragment, say) can echo
/// that same text. These tools log the exception type only, like the <c>execute_command</c> sites.
/// </summary>
public sealed class InternalToolServerFailureLoggingTests
{
    private const string MemoryText = "the operator's private diagnosis is SENTINEL-7731";

    [Theory]
    [InlineData("read_saga", "read_saga failed")]
    [InlineData("scribe_lexicon", "scribe_lexicon failed")]
    [InlineData("delete_lexicon", "delete_lexicon failed")]
    [InlineData("search_archives", "search_archives failed")]
    public async Task A_failing_memory_tool_logs_its_exception_type_and_none_of_the_memory_text(
        string tool,
        string expectedMessageStart)
    {
        CapturingLoggerFactory loggers = new();

        await using TestSession session = await TestSession.CreateAsync(loggers);

        McpToolsCallResultWire result = await session.CallToolAsync(tool, ArgumentsFor(tool));

        Assert.True(result.IsError);

        Assert.Equal("An internal error occurred during tool execution.", result.Content![0].Text);

        CapturingLoggerFactory.CapturedLogEntry entry = Assert.Single(
            loggers.Entries,
            static entry => entry.Level == LogLevel.Error
                && entry.Category.EndsWith(nameof(ArcanumInternalToolServer), StringComparison.Ordinal));

        Assert.StartsWith(expectedMessageStart, entry.Message, StringComparison.Ordinal);

        Assert.Contains(nameof(StoreFailureException), entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("SENTINEL-7731", entry.Message, StringComparison.Ordinal);

        // The exception object would carry its message and stack into any structured sink.
        Assert.Null(entry.Exception);
    }

    private static JsonElement ArgumentsFor(string tool) => tool switch
    {
        "read_saga" => JsonSerializer.SerializeToElement(
            new ReadSagaParams(MemoryText),
            McpJsonSerializerContext.Default.ReadSagaParams),
        "scribe_lexicon" => JsonSerializer.SerializeToElement(
            new ScribeLexiconParams(MemoryText, null, ["a fact"]),
            McpJsonSerializerContext.Default.ScribeLexiconParams),
        "delete_lexicon" => JsonSerializer.SerializeToElement(
            new DeleteLexiconParams(MemoryText),
            McpJsonSerializerContext.Default.DeleteLexiconParams),
        _ => JsonSerializer.SerializeToElement(
            new SearchArchivesParams(MemoryText),
            McpJsonSerializerContext.Default.SearchArchivesParams),
    };

    /// <summary>What a failing store throws: its message echoes the text it was asked to search for.</summary>
    private sealed class StoreFailureException() : Exception($"fts5: syntax error near \"{MemoryText}\"");

    private sealed class TestSession(
        InProcessMcpTransport transport,
        Task serverTask,
        CancellationTokenSource lifetime) : IAsyncDisposable
    {
        private int _nextId;

        public static async Task<TestSession> CreateAsync(CapturingLoggerFactory loggers)
        {
            ServiceCollection services = new();

            // Every store these tools reach fails the way an FTS-backed one can: with a message that quotes
            // the text it was given.
            services.AddScoped<IWeaveService>(static _ => throw new StoreFailureException());

            services.AddScoped<ILexiconService>(static _ => throw new StoreFailureException());

            services.AddScoped<IGrimoireRepository>(static _ => throw new StoreFailureException());

            IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            IUnseenServantPacer pacer = new UnseenServantPacer(
                new SilentEventBus(),
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                scopeFactory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UnseenServantPacer>.Instance);

            (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
                new HumanPromptRegistry(),
                scopeFactory,
                pacer,
                workspaceRootNormalizedOrNull: null,
                listDirectoryMaxPaths: 16,
                intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with
                {
                    EnableLexiconSystem = true,
                    EnableArchiveSearch = true,
                },
                maxFileReadSizeBytes: 1024,
                conclaveEnabled: false,
                sagaEnabled: true,
                a2aClientEnabled: false,
                attachmentsToolEnabled: false,
                maxJsonRpcLineBytes: 1_048_576,
                logger: loggers.CreateLogger<ArcanumInternalToolServer>(),
                allowHostProcessTools: true);

            CancellationTokenSource lifetime = new();

            Task serverTask = server.RunAsync(lifetime.Token);

            await transport.StartAsync();

            return new TestSession(transport, serverTask, lifetime);
        }

        public async Task<McpToolsCallResultWire> CallToolAsync(string name, JsonElement arguments)
        {
            JsonElement parameters = JsonSerializer.SerializeToElement(
                new McpToolsCallParams { Name = name, Arguments = arguments },
                McpJsonSerializerContext.Default.McpToolsCallParams);

            await transport.WriteRequestAsync(new JsonRpcRequest
            {
                Method = "tools/call",
                Params = parameters,
                Id = JsonSerializer.SerializeToElement(Interlocked.Increment(ref _nextId), McpJsonSerializerContext.Default.Int32),
            });

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync();

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return JsonSerializer.Deserialize(
                envelope.Response!.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();

            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }

            await transport.DisposeAsync();

            lifetime.Dispose();
        }
    }

    private sealed class SilentEventBus : RetroDownfall.Arcanum.Core.Events.IEventBus
    {
        public void Publish<T>(T @event)
            where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken)
            where T : notnull =>
            AsyncEnumerable.Empty<T>();
    }
}

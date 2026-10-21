using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Core.Weave;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

[Collection(TheForgeProcessEnvironmentCollection.Name)]
public sealed class ForgeRequestDeadlineTests
{
    [Theory]
    [InlineData("compact")]
    [InlineData("upload")]
    [InlineData("ndjson")]
    [InlineData("sse")]
    [InlineData("download")]
    [InlineData("preview")]
    public async Task Long_routes_survive_the_production_transport_deadline_until_the_caller_completes_them(string route)
    {
        using TheForgeTestHomeScope home = new("forge-long-deadline");

        using ServiceProvider services = ServiceCollectionConfigurator.Build();

        using HttpClient configured = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ArcanumApiClient.HttpClientName);

        using DelayedHeadersHandler handler = new();

        // Compress the production finite HttpClient budget to exercise its real cancellation without
        // spending 100 seconds per case. An unbounded production transport remains unbounded here.
        DeadlineFactory factory = new(handler, configured.Timeout == Timeout.InfiniteTimeSpan
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(30));

        StaticTheForgeSettingsMonitor settings = new(new TheForgeSettings { BaseUrl = "http://localhost:5001" });

        ManualTimerTimeProvider clock = new();

        ArcanumApiClient api = new(factory, settings, new ApiKeyProvider(), NullLogger<ArcanumApiClient>.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        OpenAiCompatApiClient files = new(factory, settings, new ApiKeyProvider(),
            NullLogger<OpenAiCompatApiClient>.Instance, ImmediateTheForgeLocalMutationRunner.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        using CancellationTokenSource caller = new(TimeSpan.FromSeconds(10));

        string localPath = Path.Combine(home.Root, "payload.txt");

        await File.WriteAllTextAsync(localPath, "payload");

        Task<bool> call = CallAsync(route, api, files, localPath, caller.Token);

        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            // Completion is controlled only by the response gate or cancellation, never a competing
            // success timer. A finite transport expires while headers are deliberately held.
            await Task.Delay(150);

            clock.Advance(TimeSpan.FromHours(1));

            Assert.False(call.IsCompleted);
        }
        finally
        {
            handler.Response.TrySetResult(ResponseFor(route));
        }

        Assert.True(await call.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, clock.TimersCreated);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("delete")]
    [InlineData("openai")]
    public async Task Ordinary_calls_keep_a_real_header_deadline_and_report_timeout_without_cancelling_the_caller(string route)
    {
        ManualTimerTimeProvider clock = new();

        using DelayedHeadersHandler handler = new();

        DeadlineFactory factory = new(handler, Timeout.InfiniteTimeSpan);

        StaticTheForgeSettingsMonitor settings = new(new TheForgeSettings { BaseUrl = "http://localhost:5001" });

        ArcanumApiClient api = new(factory, settings, new ApiKeyProvider(), NullLogger<ArcanumApiClient>.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        OpenAiCompatApiClient files = new(factory, settings, new ApiKeyProvider(),
            NullLogger<OpenAiCompatApiClient>.Instance, ImmediateTheForgeLocalMutationRunner.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        using CancellationTokenSource caller = new();

        Task<string?> call = OrdinaryErrorAsync(route, api, files, caller.Token);

        await clock.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(99));

        Assert.False(call.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Connection.Timeout", await call.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.False(caller.IsCancellationRequested);

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("openai")]
    public async Task Ordinary_body_reads_outlive_the_disarmed_header_deadline_and_dispose_the_response(string route)
    {
        ManualTimerTimeProvider clock = new();

        GatedContent body = new(route == "api"
            ? "{\"isSuccess\":true,\"data\":true}"
            : "{\"id\":\"file-test\",\"object\":\"file\",\"bytes\":7,\"filename\":\"payload.txt\",\"purpose\":\"batch\",\"created_at\":1}");

        using ImmediateHeadersHandler handler = new(body);

        DeadlineFactory factory = new(handler, Timeout.InfiniteTimeSpan);

        StaticTheForgeSettingsMonitor settings = new(new TheForgeSettings { BaseUrl = "http://localhost:5001" });

        ArcanumApiClient api = new(factory, settings, new ApiKeyProvider(), NullLogger<ArcanumApiClient>.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        OpenAiCompatApiClient files = new(factory, settings, new ApiKeyProvider(),
            NullLogger<OpenAiCompatApiClient>.Instance, ImmediateTheForgeLocalMutationRunner.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        Task<string?> call = OrdinaryErrorAsync(route, api, files, CancellationToken.None);

        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, clock.ActiveTimers);

        clock.Advance(TimeSpan.FromHours(1));

        Assert.False(call.IsCompleted);

        body.Release.TrySetResult();

        Assert.Null(await call.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("upload")]
    [InlineData("ndjson")]
    [InlineData("sse")]
    [InlineData("download")]
    [InlineData("preview")]
    public async Task Long_routes_preserve_caller_cancellation_instead_of_mapping_it_to_a_timeout(string route)
    {
        using TheForgeTestHomeScope home = new("forge-long-cancel");

        using DelayedHeadersHandler handler = new();

        DeadlineFactory factory = new(handler, Timeout.InfiniteTimeSpan);

        StaticTheForgeSettingsMonitor settings = new(new TheForgeSettings { BaseUrl = "http://localhost:5001" });

        ArcanumApiClient api = new(factory, settings, new ApiKeyProvider(), NullLogger<ArcanumApiClient>.Instance);

        OpenAiCompatApiClient files = new(factory, settings, new ApiKeyProvider(),
            NullLogger<OpenAiCompatApiClient>.Instance, ImmediateTheForgeLocalMutationRunner.Instance);

        string localPath = Path.Combine(home.Root, "payload.txt");

        await File.WriteAllTextAsync(localPath, "payload");

        using CancellationTokenSource caller = new();

        Task<bool> call = CallAsync(route, api, files, localPath, caller.Token);

        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<string?> OrdinaryErrorAsync(string route, ArcanumApiClient api,
        OpenAiCompatApiClient files, CancellationToken cancellationToken) => route switch
    {
        "api" => (await api.GetAsync("/api/health", TheForgeJsonContext.Default.ApiResponseBoolean, cancellationToken))?.Error?.Code,
        "delete" => (await api.DeleteNoContentAsync("/api/campaigns/test", cancellationToken)).ErrorCode,
        "openai" => (await files.GetAsync("/v1/files/file-test", TheForgeJsonContext.Default.OpenAiFileObject, cancellationToken)).ErrorCode,
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    private sealed class ImmediateHeadersHandler(HttpContent body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
    }

    private sealed class GatedContent(string body) : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Started.TrySetResult();

            await Release.Task;

            await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;

            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("session-export")]
    [InlineData("daemon")]
    [InlineData("tool")]
    [InlineData("mcp-invoke")]
    [InlineData("mcp-start")]
    [InlineData("mcp-stop")]
    [InlineData("mcp-restart")]
    [InlineData("mcp-reload")]
    [InlineData("arsenal")]
    [InlineData("provider")]
    [InlineData("session-divination")]
    [InlineData("workspace-divination")]
    [InlineData("saga-divination")]
    [InlineData("campaign-export")]
    [InlineData("embeddings-reset")]
    [InlineData("workspace-read")]
    [InlineData("workspace-write")]
    [InlineData("workspace-patch")]
    [InlineData("workspace-delete")]
    [InlineData("saga-reset")]
    [InlineData("batch-create")]
    [InlineData("spell-cast")]
    [InlineData("spell-export")]
    [InlineData("spell-import")]
    [InlineData("spell-clone")]
    public async Task Inline_work_routes_finish_after_the_ordinary_header_budget_without_arming_a_deadline(string route)
    {
        ManualTimerTimeProvider clock = new();

        using DelayedHeadersHandler handler = new();

        DeadlineFactory factory = new(handler, Timeout.InfiniteTimeSpan);

        StaticTheForgeSettingsMonitor settings = new(new TheForgeSettings { BaseUrl = "http://localhost:5001" });

        ArcanumApiClient api = new(factory, settings, new ApiKeyProvider(), NullLogger<ArcanumApiClient>.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        OpenAiCompatApiClient files = new(factory, settings, new ApiKeyProvider(),
            NullLogger<OpenAiCompatApiClient>.Instance, ImmediateTheForgeLocalMutationRunner.Instance)
        {
            HeadersDeadlineClock = clock,
        };

        using CancellationTokenSource caller = new(TimeSpan.FromSeconds(10));

        Task<bool> call = InlineWorkAsync(route, api, files, caller.Token);

        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromHours(1));

        string body = route switch
        {
            "mcp-start" or "mcp-stop" or "mcp-restart" => "{\"isSuccess\":true,\"data\":true}",
            "mcp-reload" => "{\"isSuccess\":true,\"data\":\"done\"}",
            "batch-create" => "{}",
            _ => "{\"isSuccess\":true,\"data\":null}",
        };

        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        Assert.True(await call.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, clock.TimersCreated);
    }

    private static async Task<bool> InlineWorkAsync(string route, ArcanumApiClient api, OpenAiCompatApiClient files,
        CancellationToken cancellationToken)
    {
        using JsonDocument arguments = JsonDocument.Parse("{}");

        ArcanumSseClient sse = new(api, NullLogger<ArcanumSseClient>.Instance);

        return route switch
        {
            "fork" => (await new SessionService(api, sse).ForkAsync(Guid.Empty, null, cancellationToken))?.IsSuccess == true,
            "session-export" => (await new SessionService(api, sse).ExportAsync(Guid.Empty, "json", cancellationToken))?.IsSuccess == true,
            "daemon" => (await new DaemonService(api, sse).RunAsync("test", cancellationToken))?.IsSuccess == true,
            "tool" => (await new ToolInvokeService(api).InvokeAsync(new ToolInvokeRequest("list_files", arguments.RootElement), cancellationToken))?.IsSuccess == true,
            "mcp-invoke" => (await new McpService(api, sse).InvokeDiagnosticMcpAsync(new McpToolInvokeRequest
            {
                ToolName = "list_files",
                ServerName = "test-server",
                Arguments = arguments.RootElement,
            }, cancellationToken))?.IsSuccess == true,
            "mcp-start" => (await new McpService(api, sse).StartAsync("test", cancellationToken))?.IsSuccess == true,
            "mcp-stop" => (await new McpService(api, sse).StopAsync("test", cancellationToken))?.IsSuccess == true,
            "mcp-restart" => (await new McpService(api, sse).RestartAsync("test", cancellationToken))?.IsSuccess == true,
            "mcp-reload" => (await new McpService(api, sse).ReloadAsync(null, cancellationToken))?.IsSuccess == true,
            "arsenal" => (await new McpService(api, sse).GetArsenalAsync(null, cancellationToken))?.IsSuccess == true,
            "provider" => (await new ModelService(api).TestProviderAsync(new ProviderTestRequest("http://localhost:11434/v1", null, AiProviderKind.OpenAICompatible), cancellationToken))?.IsSuccess == true,
            "session-divination" => (await new DivinationService(api).SearchSessionsAsync(new SemanticSearchRequest("test"), cancellationToken))?.IsSuccess == true,
            "workspace-divination" => (await new DivinationService(api).SearchWorkspaceFilesAsync("test", new WorkspaceSemanticSearchRequest("test"), cancellationToken))?.IsSuccess == true,
            "saga-divination" => (await new SagaService(api).SearchAsync("test", null, cancellationToken))?.IsSuccess == true,
            "campaign-export" => (await new ExportImportService(api).ExportCampaignAsync(Guid.Empty, cancellationToken))?.IsSuccess == true,
            "embeddings-reset" => (await new WeaveInspectorService(api).ResetEmbeddingsAsync("workspace_file", cancellationToken))?.IsSuccess == true,
            "workspace-read" => (await new WorkspaceService(api).GetFileContentsAsync("test", "test.txt", cancellationToken))?.IsSuccess == true,
            "workspace-write" => (await new WorkspaceService(api).WriteFileContentsAsync("test", "test.txt", "test", cancellationToken))?.IsSuccess == true,
            "workspace-patch" => (await new WorkspaceService(api).ReplaceTextBlockAsync("test", "test.txt", new TextBlockReplaceRequest("old", "new"), cancellationToken))?.IsSuccess == true,
            "workspace-delete" => (await new WorkspaceService(api).DeleteFileAsync("test", "test.txt", true, cancellationToken))?.IsSuccess == true,
            "saga-reset" => (await new SagaService(api).DeleteAllAsync(cancellationToken)).Success,
            "batch-create" => (await new FilesBatchesService(files).CreateBatchAsync("file-input", null, null, cancellationToken)).Success,
            "spell-cast" => (await new SpellService(api).CastAsync("test", null, cancellationToken))?.IsSuccess == true,
            "spell-export" => (await new SpellService(api).ExportAsync("test", null, cancellationToken))?.IsSuccess == true,
            "spell-import" => (await new SpellService(api).ImportAsync(new SpellImportRequest(
                new SpellExportDto(null, "---\nname: test\ndescription: test\n---\nbody", []), null, null), cancellationToken))?.IsSuccess == true,
            "spell-clone" => (await new SpellService(api).CloneAsync("test", new CloneSpellRequest("copy"), cancellationToken))?.IsSuccess == true,
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    private static HttpResponseMessage ResponseFor(string route) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(route switch
        {
            "compact" => "{\"isSuccess\":true,\"data\":{}}",
            "upload" => "{\"id\":\"file-test\",\"object\":\"file\",\"bytes\":7,\"filename\":\"payload.txt\",\"purpose\":\"batch\",\"created_at\":1}",
            "ndjson" => "{\"type\":\"token\",\"message\":\"\",\"data\":\"done\"}\n",
            "sse" => "data: done\n\n",
            _ => "done",
        }, Encoding.UTF8),
    };

    private static async Task<bool> CallAsync(string route, ArcanumApiClient api, OpenAiCompatApiClient files,
        string localPath, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case "compact":
                return (await new SessionService(api, new ArcanumSseClient(api, NullLogger<ArcanumSseClient>.Instance))
                    .CompactAsync(Guid.Empty, cancellationToken))?.IsSuccess == true;

            case "upload":
                return (await new FilesBatchesService(files).UploadFileAsync(localPath, "batch", cancellationToken)).Success;

            case "ndjson":
                await foreach (var frame in new SessionService(api, new ArcanumSseClient(api, NullLogger<ArcanumSseClient>.Instance))
                    .PingStreamAsync(new PingRequest("test"), cancellationToken))
                {
                    return frame is not null;
                }

                return false;

            case "sse":
                await foreach (SseEvent frame in api.GetSseAsync("/api/events/mcp", cancellationToken))
                {
                    return frame.Data == "done";
                }

                return false;

            case "download":
                return (await files.DownloadToFileAsync("/v1/files/file-test/content", localPath + ".out", cancellationToken)).Success;

            case "preview":
                var opened = await files.OpenContentStreamAsync("/v1/files/file-test/content", cancellationToken);

                if (!opened.Success || opened.Data is null)
                {
                    return false;
                }

                await using (Stream stream = opened.Data)
                {
                    using StreamReader reader = new(stream);

                    return await reader.ReadToEndAsync(cancellationToken) == "done";
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private sealed class DeadlineFactory(HttpMessageHandler handler, TimeSpan timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout };
    }

    private sealed class DelayedHeadersHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            }

            Entered.TrySetResult();

            return await Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ApiKeyProvider : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");

        public Task PersistPastedKeyAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ClearPasteDecline() { }
    }
}

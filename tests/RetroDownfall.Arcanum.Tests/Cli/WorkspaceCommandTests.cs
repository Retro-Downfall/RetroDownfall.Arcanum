using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;


namespace RetroDownfall.Arcanum.Tests.Cli;


[Collection("GlobalConsole")]

public sealed class WorkspaceCommandTests
{

    [Fact]

    public void Workspace_help_lists_the_complete_server_backed_command_surface()
    {

        CliTestResult result = RunCommand(new RecordingHandler(), ["workspace", "--help"]);


        Assert.Equal(0, result.ExitCode);

        Assert.Contains("list", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("current", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("register", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("show", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("tree", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("info", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("read", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("search", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("index", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("index-status", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("chunks", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("unregister", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("server", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Campaign", result.Output, StringComparison.Ordinal);

    }


    [Fact]

    public void Workspace_register_posts_the_server_host_path()
    {

        WorkspaceInfo workspace = Workspace();

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<WorkspaceInfo>(workspace, true, null),
            ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
            HttpStatusCode.Created));


        CliTestResult result = RunCommand(
            handler,
            ["workspace", "register", "/srv/projects/demo"]);


        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/workspaces", request.RequestUri!.AbsolutePath);

        Assert.Contains(
            "\"path\":\"/srv/projects/demo\"",
            ReadBody(request),
            StringComparison.Ordinal);

    }


    [Fact]

    public void Workspace_register_without_a_path_registers_the_current_directory()
    {

        WorkspaceInfo workspace = Workspace();

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<WorkspaceInfo>(workspace, true, null),
            ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
            HttpStatusCode.Created));

        CliTestResult result = RunCommand(handler, ["workspace", "register"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        CreateWorkspaceRequest? body = JsonSerializer.Deserialize(
            ReadBody(request),
            ArcanumJsonContext.Default.CreateWorkspaceRequest);

        Assert.NotNull(body);

        Assert.Equal(
            global::System.Environment.CurrentDirectory,
            body.Path);

    }


    [Fact]

    public void Workspace_show_resolves_a_workspace_and_reads_its_detail()
    {

        WorkspaceInfo workspace = Workspace();

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath == "/api/workspaces")
            {

                return CreateResponse(
                    new ApiResponse<WorkspaceInfo[]>([workspace], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

            }


            return CreateResponse(
                new ApiResponse<WorkspaceInfo>(workspace, true, null),
                ArcanumJsonContext.Default.ApiResponseWorkspaceInfo);

        });


        CliTestResult result = RunCommand(handler, ["workspace", "show", "demo"]);


        Assert.Equal(0, result.ExitCode);

        Assert.Equal(3, handler.Requests.Count);

        Assert.Equal("/api/workspaces/ws-demo", handler.Requests[^1].RequestUri!.AbsolutePath);

    }


    [Fact]

    public void Workspace_file_search_and_index_commands_use_only_server_api_routes()
    {

        AssertRoute(
            ["workspace", "tree", "ws-demo"],
            HttpMethod.Get,
            "/api/workspaces/ws-demo/files",
            "recursive=true");

        AssertRoute(
            ["workspace", "info", "src/App.cs", "--workspace", "ws-demo"],
            HttpMethod.Get,
            "/api/workspaces/ws-demo/files/info",
            "relativePath=src%2FApp.cs");

        AssertRoute(
            ["workspace", "read", "src/App.cs", "--workspace", "ws-demo"],
            HttpMethod.Get,
            "/api/workspaces/ws-demo/files/contents",
            "relativePath=src%2FApp.cs");

        AssertRoute(
            ["workspace", "search", "find entry", "--workspace", "ws-demo"],
            HttpMethod.Post,
            "/api/workspaces/ws-demo/files/divine");

        AssertRoute(
            ["workspace", "index", "ws-demo"],
            HttpMethod.Post,
            "/api/workspaces/ws-demo/files/index");

        AssertRoute(
            ["workspace", "index-status", "ws-demo"],
            HttpMethod.Get,
            "/api/workspaces/ws-demo/files/index/status");

        AssertRoute(
            ["workspace", "chunks", "ws-demo", "--path", "src/App.cs"],
            HttpMethod.Get,
            "/api/workspaces/ws-demo/files/chunks",
            "relativePath=src%2FApp.cs");

        AssertRoute(
            ["workspace", "unregister", "ws-demo"],
            HttpMethod.Delete,
            "/api/workspaces/ws-demo");

    }


    [Fact]

    public void Workspace_tree_follows_opaque_file_pages()
    {

        int filePage = 0;

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath == "/api/workspaces")
            {

                return CreateResponse(
                    new ApiResponse<WorkspaceInfo[]>([Workspace()], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

            }

            filePage++;

            FileEntry entry = new(
                $"page-{filePage}.txt",
                $"page-{filePage}.txt",
                $"/srv/projects/demo/page-{filePage}.txt",
                FileEntryType.File,
                filePage,
                DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

            FileListResult page = new(
                [entry],
                null,
                filePage == 1 ? "opaque-checkpoint" : null);

            return CreateResponse(
                new ApiResponse<FileListResult>(page, true, null),
                ArcanumJsonContext.Default.ApiResponseFileListResult);

        });

        CliTestResult result = RunCommand(
            handler,
            ["workspace", "tree", "ws-demo"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage[] fileRequests = handler.Requests
            .Where(
                static request => request.RequestUri!.AbsolutePath.EndsWith(
                    "/files",
                    StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, fileRequests.Length);

        Assert.DoesNotContain(
            "cursor=",
            fileRequests[0].RequestUri!.Query,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "cursor=opaque-checkpoint",
            fileRequests[1].RequestUri!.Query,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("page-1.txt", result.Output, StringComparison.Ordinal);

        Assert.Contains("page-2.txt", result.Output, StringComparison.Ordinal);

    }


    [Fact]

    public void Workspace_read_emits_file_content_verbatim_without_console_reflow()
    {

        string content = string.Join(
            "\n",
            new string('a', 100),
            "{ \"key\": \"" + new string('b', 120) + "\" }",
            "short\tline\twith\ttabs")
            + "\r\ncrlf tail\r\n";

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath.EndsWith(
                "/files/contents",
                StringComparison.Ordinal))
            {

                FileReadResult read = new(
                    "src/App.cs",
                    content,
                    "utf-8",
                    content.Length,
                    DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

                return CreateResponse(
                    new ApiResponse<FileReadResult>(read, true, null),
                    ArcanumJsonContext.Default.ApiResponseFileReadResult);

            }


            return CreateWorkspaceApiResponse(request);

        });


        CliTestResult result = RunCommand(
            handler,
            ["workspace", "read", "src/App.cs", "--workspace", "ws-demo"]);


        Assert.Equal(0, result.ExitCode);

        Assert.Contains(content, result.Output, StringComparison.Ordinal);

    }


    /// <summary>
    /// `workspace read` promises the bytes the server read. Under `--output-format json` the legacy
    /// text wrapper used to reach them: it strips every ESC-introduced sequence out of the middle of
    /// the buffer and trims the trailing newlines off the end, so the file could not be reproduced
    /// from its own `--output-format json` output. Emitting a structured document instead keeps the
    /// content out of that wrapper entirely.
    /// </summary>
    [Fact]

    public void Workspace_read_json_reproduces_the_file_byte_for_byte()
    {

        string content = "\u001b[31mred\u001b[0m literal escape\nplain line\n\n";

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath.EndsWith(
                "/files/contents",
                StringComparison.Ordinal))
            {

                FileReadResult read = new(
                    "src/App.cs",
                    content,
                    "utf-8",
                    content.Length,
                    DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

                return CreateResponse(
                    new ApiResponse<FileReadResult>(read, true, null),
                    ArcanumJsonContext.Default.ApiResponseFileReadResult);

            }


            return CreateWorkspaceApiResponse(request);

        });


        CliTestResult result = RunCommand(
            handler,
            ["workspace", "read", "src/App.cs", "--workspace", "ws-demo", "--json"]);


        Assert.Equal(0, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(
            content,
            document.RootElement.GetProperty("content").GetString());

        Assert.Equal(
            "src/App.cs",
            document.RootElement.GetProperty("path").GetString());

    }


    [Fact]

    public void Workspace_current_reports_independent_campaign_and_workspace_mapping()
    {

        string currentDirectory = Path.GetFullPath(global::System.Environment.CurrentDirectory);

        WorkspaceInfo workspace = Workspace() with
        {

            Path = currentDirectory,

        };

        CampaignDto campaign = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "demo-campaign",
            currentDirectory,
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.Parse("2026-07-31T12:00:00Z"),
            DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath == "/api/workspaces")
            {

                return CreateResponse(
                    new ApiResponse<WorkspaceInfo[]>([workspace], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

            }

            return CreateResponse(
                new ApiResponse<ListPageResult<CampaignDto>>(
                    new ListPageResult<CampaignDto>([campaign], false),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto);

        });

        CliTestResult result = RunCommand(handler, ["workspace", "current"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("demo", result.Output, StringComparison.Ordinal);

        Assert.Contains("demo-campaign", result.Output, StringComparison.Ordinal);

        Assert.Contains("server host", result.Output, StringComparison.OrdinalIgnoreCase);

    }


    [Fact]

    public void Workspace_current_offers_campaign_registration_when_only_workspace_matches()
    {

        WorkspaceInfo workspace = Workspace() with
        {

            Path = Path.GetFullPath(global::System.Environment.CurrentDirectory),

        };

        RecordingHandler handler = new(request =>
        {

            if (request.RequestUri!.AbsolutePath == "/api/workspaces")
            {

                return CreateResponse(
                    new ApiResponse<WorkspaceInfo[]>([workspace], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

            }

            return CreateResponse(
                new ApiResponse<ListPageResult<CampaignDto>>(
                    new ListPageResult<CampaignDto>([], false),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto);

        });

        CliTestResult result = RunCommand(handler, ["workspace", "current"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("campaign create", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("server-path", result.Output, StringComparison.OrdinalIgnoreCase);

    }


    /// <summary>
    /// A host that keeps handing back the same continuation cursor must stop the walk, not make the
    /// command re-print the same page until the operator interrupts it.
    /// </summary>
    [Fact]

    public void Workspace_tree_stops_when_the_host_repeats_a_continuation_cursor()
    {

        const int safetyValve = 12;

        int calls = 0;

        RecordingHandler handler = new(request =>
        {

            string path = request.RequestUri!.AbsolutePath;

            if (path == "/api/workspaces")
            {

                return CreateResponse(
                    new ApiResponse<WorkspaceInfo[]>([Workspace()], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

            }

            calls++;

            if (calls > safetyValve)
            {

                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            }

            return CreateResponse(
                new ApiResponse<FileListResult>(
                    new FileListResult([], null, "stuck-cursor"),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseFileListResult);

        });

        CliTestResult result = RunCommand(handler, ["workspace", "tree", "ws-demo"]);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Equal(2, calls);

    }


    /// <summary>
    /// The same invariant for offset paging: a non-advancing offset must fail rather than grow the
    /// accumulated list until the process runs out of memory.
    /// </summary>
    [Fact]

    public void Workspace_current_stops_when_the_host_stops_advancing_the_campaign_offset()
    {

        const int safetyValve = 12;

        int calls = 0;

        RecordingHandler handler = new(request =>
        {

            string path = request.RequestUri!.AbsolutePath;

            if (path == "/api/workspaces")
            {

                return CreateResponse(
                    new ApiResponse<WorkspaceInfo[]>([Workspace()], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

            }

            calls++;

            if (calls > safetyValve)
            {

                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            }

            return CreateResponse(
                new ApiResponse<ListPageResult<CampaignDto>>(
                    new ListPageResult<CampaignDto>([], true, 0),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto);

        });

        CliTestResult result = RunCommand(handler, ["workspace", "current"]);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Equal(1, calls);

    }


    private static void AssertRoute(
        string[] args,
        HttpMethod method,
        string absolutePath,
        string? queryFragment = null)
    {

        RecordingHandler handler = new(CreateWorkspaceApiResponse);

        CliTestResult result = RunCommand(handler, args);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = handler.Requests.Last();

        Assert.Equal(method, request.Method);

        Assert.Equal(absolutePath, request.RequestUri!.AbsolutePath);

        if (queryFragment is not null)
        {

            Assert.Contains(
                queryFragment,
                request.RequestUri.Query,
                StringComparison.OrdinalIgnoreCase);

        }

    }


    private static HttpResponseMessage CreateWorkspaceApiResponse(
        HttpRequestMessage request)
    {

        string path = request.RequestUri!.AbsolutePath;

        if (path == "/api/workspaces")
        {

            return CreateResponse(
                new ApiResponse<WorkspaceInfo[]>([Workspace()], true, null),
                ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);

        }

        if (path.EndsWith("/files/info", StringComparison.Ordinal))
        {

            FileEntry entry = new(
                "App.cs",
                "src/App.cs",
                "/srv/projects/demo/src/App.cs",
                FileEntryType.File,
                10,
                DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

            return CreateResponse(
                new ApiResponse<FileEntry>(entry, true, null),
                ArcanumJsonContext.Default.ApiResponseFileEntry);

        }

        if (path.EndsWith("/files/contents", StringComparison.Ordinal))
        {

            FileReadResult read = new(
                "src/App.cs",
                "content",
                "utf-8",
                7,
                DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

            return CreateResponse(
                new ApiResponse<FileReadResult>(read, true, null),
                ArcanumJsonContext.Default.ApiResponseFileReadResult);

        }

        if (path.EndsWith("/files/divine", StringComparison.Ordinal))
        {

            return CreateResponse(
                new ApiResponse<WorkspaceSearchResult[]>([], true, null),
                ArcanumJsonContext.Default.ApiResponseWorkspaceSearchResultArray);

        }

        if (path.EndsWith("/files/index/status", StringComparison.Ordinal))
        {

            WorkspaceIndexStatusDto status = new(
                "ws-demo",
                "demo",
                "/srv/projects/demo",
                "blob",
                "ready",
                true,
                1,
                1,
                null,
                null,
                3,
                "not persisted");

            return CreateResponse(
                new ApiResponse<WorkspaceIndexStatusDto>(status, true, null),
                ArcanumJsonContext.Default.ApiResponseWorkspaceIndexStatusDto);

        }

        if (path.EndsWith("/files/index", StringComparison.Ordinal))
        {

            return CreateResponse(
                new ApiResponse<bool>(true, true, null),
                ArcanumJsonContext.Default.ApiResponseBoolean,
                HttpStatusCode.Accepted);

        }

        if (path.EndsWith("/files/chunks", StringComparison.Ordinal))
        {

            WorkspaceFileChunkPage page = new([], 0, 50, 0, false, null);

            return CreateResponse(
                new ApiResponse<WorkspaceFileChunkPage>(page, true, null),
                ArcanumJsonContext.Default.ApiResponseWorkspaceFileChunkPage);

        }

        if (path.EndsWith("/files", StringComparison.Ordinal))
        {

            return CreateResponse(
                new ApiResponse<FileListResult>(
                    new FileListResult([], null),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseFileListResult);

        }

        if (request.Method == HttpMethod.Delete)
        {

            return new HttpResponseMessage(HttpStatusCode.NoContent);

        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);

    }


    private static WorkspaceInfo Workspace() =>
        new(
            "ws-demo",
            "demo",
            "/srv/projects/demo",
            WorkspaceType.Custom,
            DateTimeOffset.Parse("2026-07-31T12:00:00Z"));


    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(
            new FakeSecretStore("test-key"));

        return CliTestHarness.Run(services, args);

    }


    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(status)
        {

            Content = new ByteArrayContent(json),

        };

    }


    private static string ReadBody(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()
        ?? string.Empty;


    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }


    private sealed class FakeHttpClientFactory(
        RecordingHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

            };

    }


    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];


        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {

                byte[] body = request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();

                snapshot.Content = new ByteArrayContent(body);

            }


            Requests.Add(snapshot);

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);

        }

    }

}

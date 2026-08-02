using System.Net;

using System.Text;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Chronosync;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Pattern;

using RetroDownfall.Arcanum.Core.Pattern.Entities;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class RunCommandTests
{

    [Fact]

    public async Task Run_command_exposes_unified_input_routing_and_context_options()
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["run", "--help"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("--research", result.Output, StringComparison.Ordinal);

        Assert.Contains("--spell", result.Output, StringComparison.Ordinal);

        Assert.Contains("--with", result.Output, StringComparison.Ordinal);

        Assert.Contains("--dry-run", result.Output, StringComparison.Ordinal);

        Assert.Contains("--campaign", result.Output, StringComparison.Ordinal);

        Assert.Contains("--workspace", result.Output, StringComparison.Ordinal);

        Assert.Contains("--session", result.Output, StringComparison.Ordinal);

        Assert.Contains("--model", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public async Task RunAsync_preserves_positional_and_piped_input_with_active_context()
    {

        Guid campaignId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        CliEffectiveContext context = EffectiveContext(
            campaignId,
            "/active/workspace",
            "provider/model",
            sessionId);

        FakeRunInputReader input = new(
            SuccessInput(
                "explain this",
                "piped context"));

        FakeRunAttachmentStager stager = new(
            SuccessStage(
                [new AttachedFileDto("stdin.txt", "piped context")]));

        FakeContextResolver resolver = new(
            CliInferenceContextResult.Success(
                context,
                ["active context warning"]));

        FakeRunExecutionDispatcher execution = new();

        RecordingConsole console = new();

        NoopGrimoireInitialization grimoire = new();

        NoopServeLauncher serve = new();

        RunCommand command = new(
            input,
            stager,
            resolver,
            execution,
            grimoire,
            serve,
            console);

        int exitCode = await command.RunAsync(
            Request(
                prompt: ["explain", "this"],
                with: ["@notes.unusual"]),
            CancellationToken.None);

        Assert.Equal(0, exitCode);

        Assert.Equal("explain this", input.PositionalInstruction);

        Assert.True(input.HasExplicitFileContext);

        Assert.NotNull(resolver.Request);

        Assert.Equal("/active/workspace", stager.WorkingDirectory);

        Assert.Equal("piped context", stager.PipedContent);

        Assert.Equal(["@notes.unusual"], stager.WithValues);

        RunExecutionRequest forwarded = Assert.IsType<RunExecutionRequest>(
            execution.Request);

        Assert.Equal(RunRoute.Agent, forwarded.Route);

        Assert.Equal("explain this", forwarded.Prompt);

        Assert.Same(context, forwarded.Context);

        Assert.Equal("piped context", Assert.Single(forwarded.AttachedFiles).Content);

        Assert.Contains(
            console.Diagnostics,
            value => value.Contains("active context warning", StringComparison.Ordinal));

        Assert.Equal(1, grimoire.CallCount);

        Assert.Equal(1, serve.CallCount);

    }

    [Fact]

    public async Task RunAsync_uses_the_current_directory_when_no_workspace_is_selected()
    {

        FakeRunExecutionDispatcher execution = new();

        RunCommand command = CreateCommand(
            SuccessInput("inspect this", null),
            SuccessStage([]),
            execution: execution);

        int exitCode = await command.RunAsync(
            Request(prompt: ["inspect", "this"]),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.Success, exitCode);

        RunExecutionRequest forwarded = Assert.IsType<RunExecutionRequest>(
            execution.Request);

        Assert.Equal(
            global::System.Environment.CurrentDirectory,
            forwarded.Context.Workspace.Value);

        Assert.Equal(
            CliContextSource.CurrentDirectory,
            forwarded.Context.Workspace.Source);

    }

    [Theory]

    [InlineData(false, null, false, 0)]

    [InlineData(true, null, false, 1)]

    [InlineData(false, "Review Changes", false, 2)]

    [InlineData(false, null, true, 0)]

    public async Task RunAsync_routes_complete_request_without_dropping_input(
        bool research,
        string? spell,
        bool dryRun,
        int expectedRoute)
    {

        FakeRunExecutionDispatcher execution = new();

        RunCommand command = CreateCommand(
            SuccessInput("do the work", "supporting context"),
            SuccessStage(
                [new AttachedFileDto("stdin.txt", "supporting context")]),
            execution: execution);

        RunCommandRequest request = Request(
            research: research,
            spell: spell,
            dryRun: dryRun);

        int exitCode = await command.RunAsync(
            request,
            CancellationToken.None);

        Assert.Equal(0, exitCode);

        RunExecutionRequest forwarded = Assert.IsType<RunExecutionRequest>(
            execution.Request);

        Assert.Equal((RunRoute)expectedRoute, forwarded.Route);

        Assert.Equal("do the work", forwarded.Prompt);

        Assert.Equal("supporting context", Assert.Single(forwarded.AttachedFiles).Content);

        Assert.Equal(dryRun, forwarded.Options.DryRun);

        Assert.Equal(spell, forwarded.Options.Spell);

    }

    [Fact]

    public async Task RunAsync_uses_a_neutral_instruction_for_attachment_only_input()
    {

        FakeRunExecutionDispatcher execution = new();

        RunCommand command = CreateCommand(
            SuccessInput(string.Empty, "attachment-only context"),
            SuccessStage(
                [new AttachedFileDto("stdin.txt", "attachment-only context")]),
            execution: execution);

        int exitCode = await command.RunAsync(
            Request(prompt: []),
            CancellationToken.None);

        Assert.Equal(0, exitCode);

        RunExecutionRequest forwarded = Assert.IsType<RunExecutionRequest>(
            execution.Request);

        Assert.Equal("Analyze the attached context.", forwarded.Prompt);

    }

    [Fact]

    public async Task RunAsync_rejects_incompatible_route_options_before_startup()
    {

        FakeRunInputReader input = new(
            SuccessInput("prompt", null));

        NoopGrimoireInitialization grimoire = new();

        NoopServeLauncher serve = new();

        RecordingConsole console = new();

        RunCommand command = CreateCommand(
            input.Result,
            SuccessStage([]),
            input: input,
            grimoire: grimoire,
            serve: serve,
            console: console);

        int exitCode = await command.RunAsync(
            Request(
                research: true,
                spell: "Named Spell"),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

        Assert.Null(input.PositionalInstruction);

        Assert.Equal(0, grimoire.CallCount);

        Assert.Equal(0, serve.CallCount);

        Assert.NotEmpty(console.Diagnostics);

    }

    [Fact]

    public async Task RunAsync_new_session_permissively_ignores_an_explicit_continuation_session()
    {

        FakeContextResolver resolver = new(
            CliInferenceContextResult.Success(
                EffectiveContext(null, null, null, null),
                []));

        RunCommand command = CreateCommand(
            SuccessInput("start fresh", null),
            SuccessStage([]),
            resolver: resolver);

        int exitCode = await command.RunAsync(
            Request(
                newSession: true,
                session: "11111111-1111-1111-1111-111111111111"),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.Success, exitCode);

        Assert.NotNull(resolver.Request);

        Assert.True(resolver.Request.NewSession);

        Assert.Null(resolver.Request.Session);

    }

    [Fact]

    public async Task RunAsync_writes_a_typed_json_error_for_invalid_input()
    {

        RecordingConsole console = new();

        RunCommand command = CreateCommand(
            new RunInputReadResult(
                false,
                "prompt",
                null,
                RunInputReader.MaxRedirectedInputBytes + 1L,
                true,
                false,
                true,
                [],
                "Redirected standard input is too large."),
            SuccessStage([]),
            console: console);

        using IDisposable invocation = CliInvocationContext.Push(
            new CliInvocationOptions(
                Json: true,
                Plain: false,
                Yes: false));

        int exitCode = await command.RunAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

        CliErrorPayload error = Assert.IsType<CliErrorPayload>(
            Assert.Single(console.JsonValues));

        Assert.Equal(exitCode, error.ExitCode);

        Assert.Contains("too large", error.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Run_parser_collects_repeated_with_values_around_the_positional_prompt()
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        FakeRunInputReader input = new(
            SuccessInput("do this", null));

        FakeRunAttachmentStager stager = new(
            SuccessStage(
                [
                    new AttachedFileDto("one.data", "one"),
                    new AttachedFileDto("two.unusual", "two"),
                ]));

        FakeRunExecutionDispatcher execution = new();

        services.RemoveAll<IRunInputReader>();

        services.AddSingleton<IRunInputReader>(input);

        services.RemoveAll<IRunAttachmentStager>();

        services.AddSingleton<IRunAttachmentStager>(stager);

        services.RemoveAll<IRunExecutionDispatcher>();

        services.AddSingleton<IRunExecutionDispatcher>(execution);

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            new FakeContextResolver(
                CliInferenceContextResult.Success(
                    EffectiveContext(null, null, null, null),
                    [])));

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(
            new NoopGrimoireInitialization());

        services.RemoveAll<IArcanumServeLauncher>();

        services.AddSingleton<IArcanumServeLauncher>(
            new NoopServeLauncher());

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [
                "run",
                "--with",
                "@one.data",
                "do",
                "this",
                "--with",
                "@two.unusual",
            ]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(["@one.data", "@two.unusual"], stager.WithValues);

        Assert.Equal("do this", input.PositionalInstruction);

        Assert.NotNull(execution.Request);

    }

    [Fact]

    public async Task Run_parser_preserves_arguments_after_the_option_terminator()
    {

        FakeRunInputReader input = new(
            SuccessInput(
                "explain --configuration Release",
                null));

        FakeRunExecutionDispatcher execution = new();

        ServiceCollection services = ConfigureRunParserServices(
            input,
            execution: execution);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [
                "run",
                "explain",
                "--",
                "--configuration",
                "Release",
            ]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(
            "explain --configuration Release",
            input.PositionalInstruction);

        Assert.NotNull(execution.Request);

    }

    [Fact]

    public async Task Run_parser_stop_option_does_not_consume_the_positional_prompt()
    {

        FakeRunInputReader input = new(
            SuccessInput("explain this", null));

        FakeRunExecutionDispatcher execution = new();

        ServiceCollection services = ConfigureRunParserServices(
            input,
            execution: execution);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [
                "run",
                "--stop",
                "END",
                "explain",
                "this",
            ]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal("explain this", input.PositionalInstruction);

        RunExecutionRequest request = Assert.IsType<RunExecutionRequest>(
            execution.Request);

        Assert.Equal(["END"], request.Options.Stop);

    }

    [Fact]

    public async Task Run_parser_writes_typed_json_for_invalid_inference_flags()
    {

        FakeRunInputReader input = new(
            SuccessInput("explain this", null));

        ServiceCollection services = ConfigureRunParserServices(input);

        services.RemoveAll<IRunExecutionDispatcher>();

        services.AddTransient<IRunExecutionDispatcher, RunExecutionDispatcher>();

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["--json", "run", "--temperature", "NaN", "explain", "this"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        CliErrorPayload? error = JsonSerializer.Deserialize(
            result.Output,
            CliJsonContext.Default.CliErrorPayload);

        Assert.NotNull(error);

        Assert.Equal(result.ExitCode, error.ExitCode);

        Assert.Contains("--temperature", error.Error, StringComparison.Ordinal);

        Assert.Contains("--temperature", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public async Task RunExecutionDispatcher_treats_cancelled_spell_selection_as_a_clean_exit()
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<ICliResourceCatalog>();

        services.AddSingleton<ICliResourceCatalog>(
            new FakeResourceCatalog(
                ResourceSelectionResult<SpellSummary>.Cancelled()));

        await using ServiceProvider provider = services.BuildServiceProvider();

        IRunExecutionDispatcher dispatcher = provider
            .GetRequiredService<IRunExecutionDispatcher>();

        int exitCode = await dispatcher.ExecuteAsync(
            new RunExecutionRequest(
                Request(spell: "review"),
                RunRoute.Spell,
                "review this",
                EffectiveContext(null, null, null, null),
                [],
                []),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.Success, exitCode);

    }

    [Theory]

    [InlineData(null)]

    [InlineData("Review Changes")]

    public async Task Run_production_agent_and_spell_routes_send_the_complete_turn_to_ping_stream(
        string? spell)
    {

        Guid campaignId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        CapturingNdjsonHandler handler = new();

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IRunInputReader>();

        services.AddSingleton<IRunInputReader>(
            new FakeRunInputReader(
                SuccessInput(
                    "explain this",
                    "piped context")));

        services.RemoveAll<IRunAttachmentStager>();

        services.AddSingleton<IRunAttachmentStager>(
            new FakeRunAttachmentStager(
                SuccessStage(
                    [new AttachedFileDto("stdin.txt", "piped context")],
                    [new ScryingFocusDto("iVBORw0KGgo=", "image/png")])));

        services.RemoveAll<ICliInferenceContextResolver>();

        FakeContextResolver resolver = new(
            CliInferenceContextResult.Success(
                EffectiveContext(
                    campaignId,
                    "/active/workspace",
                    "provider/model",
                    sessionId),
                []));

        services.AddSingleton<ICliInferenceContextResolver>(resolver);

        FakeResourceCatalog resources = new(
            ResourceSelectionResult<SpellSummary>.Selected(
                new SpellSummary(
                    "Review Changes",
                    "Review a change set.",
                    SpellSource.Workspace,
                    [])));

        services.RemoveAll<ICliResourceCatalog>();

        services.AddSingleton<ICliResourceCatalog>(resources);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        services.RemoveAll<IApiKeyDigestCache>();

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        services.RemoveAll<IEyeOfTheWorld>();

        services.AddSingleton<IEyeOfTheWorld>(new FakeEye());

        services.RemoveAll<IChronosyncEngine>();

        services.AddSingleton<IChronosyncEngine>(
            new NoopChronosyncEngine());

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(
            new NoopGrimoireInitialization());

        services.RemoveAll<IArcanumServeLauncher>();

        services.AddSingleton<IArcanumServeLauncher>(
            new NoopServeLauncher());

        List<string> arguments =
        [
            "run",
            "explain",
            "this",
            "--temperature",
            "0.25",
        ];

        if (spell is not null)
        {

            arguments.Add("--spell");

            arguments.Add(spell);

        }

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [.. arguments]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(1, resolver.CallCount);

        Assert.Equal("/api/intelligence/ping-stream", handler.RequestPath);

        PingRequest ping = Assert.IsType<PingRequest>(
            JsonSerializer.Deserialize(
                handler.Body,
                ArcanumJsonContext.Default.PingRequest));

        Assert.Equal("explain this", ping.Prompt);

        Assert.Equal("provider/model", ping.Model);

        Assert.Equal("/active/workspace", ping.WorkingDirectory);

        Assert.Equal(campaignId, ping.CampaignId);

        Assert.Equal(sessionId, ping.SessionId);

        Assert.Equal(0.25f, ping.Temperature);

        Assert.Equal("piped context", Assert.Single(ping.AttachedFiles!).Content);

        Assert.Equal("image/png", Assert.Single(ping.ScryingFoci!).MimeType);

        Assert.Equal(spell, resources.SpellIdentifier);

        Assert.Equal(
            spell is null
                ? null
                : "Review Changes",
            ping.OverrideSpellName);

    }

    [Fact]

    public async Task RunExecutionDispatcher_research_route_forwards_context_and_inference_without_dropped_input()
    {

        Guid campaignId = Guid.NewGuid();

        ResearchCaptureHandler handler = new();

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        ReplaceApiTransport(services, handler);

        await using ServiceProvider provider = services.BuildServiceProvider();

        IRunExecutionDispatcher dispatcher = provider
            .GetRequiredService<IRunExecutionDispatcher>();

        RunCommandRequest options = Request(research: true) with
        {

            Temperature = "0.4",

            TopP = "0.8",

            MaxSources = 7,

            MaxHops = 3,

            TokenBudget = 1_500,

            CostBudget = 0.75m,

            Unattended = true,

        };

        int exitCode = await dispatcher.ExecuteAsync(
            new RunExecutionRequest(
                options,
                RunRoute.Research,
                "research this",
                EffectiveContext(
                    campaignId,
                    "/active/workspace",
                    "provider/research-model",
                    null),
                [new AttachedFileDto("stdin.txt", "piped research context")],
                [new ScryingFocusDto("iVBORw0KGgo=", "image/png")]),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.Success, exitCode);

        Assert.Equal("/api/web/research", handler.RequestPath);

        WebResearchWorkflowRequest request = Assert.IsType<WebResearchWorkflowRequest>(
            JsonSerializer.Deserialize(
                handler.Body,
                ArcanumJsonContext.Default.WebResearchWorkflowRequest));

        Assert.Equal("research this", request.Question);

        Assert.Equal("/active/workspace", request.WorkingDirectory);

        Assert.Equal(campaignId, request.CampaignId);

        Assert.Equal("provider/research-model", request.Model);

        Assert.Equal(7, request.MaxSources);

        Assert.Equal(3, request.MaxHops);

        Assert.Equal(1_500, request.TokenBudget);

        Assert.Equal(0.75m, request.CostBudgetUsd);

        Assert.Equal(0.4f, request.Temperature);

        Assert.Equal(0.8f, request.TopP);

        Assert.True(request.UnattendedMode);

        Assert.Equal(
            "piped research context",
            Assert.Single(request.AttachedFiles!).Content);

        Assert.Equal(
            "image/png",
            Assert.Single(request.ScryingFoci!).MimeType);

    }

    [Fact]

    public async Task RunExecutionDispatcher_research_dry_run_calls_only_read_only_context_preview()
    {

        Guid campaignId = Guid.NewGuid();

        PreviewCaptureHandler handler = new();

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {

                ["Arcanum:Security:Ward:UnattendedMode"] = "true",

            });

        CliApplicationFactory.ConfigureCliServices(
            services,
            configuration);

        ReplaceApiTransport(services, handler);

        services.RemoveAll<ICliInferenceContextResolver>();

        FakeContextResolver resolver = new(
            CliInferenceContextResult.Success(
                EffectiveContext(
                    campaignId,
                    "/active/workspace",
                    "provider/preview-model",
                    null),
                []));

        services.AddSingleton<ICliInferenceContextResolver>(resolver);

        await using ServiceProvider provider = services.BuildServiceProvider();

        IRunExecutionDispatcher dispatcher = provider
            .GetRequiredService<IRunExecutionDispatcher>();

        RunCommandRequest options = Request(
            research: true,
            dryRun: true,
            newSession: true) with
        {

            ShowContent = true,

            TokenBudget = 1_750,

            Temperature = "0.3",

        };

        int exitCode = await dispatcher.ExecuteAsync(
            new RunExecutionRequest(
                options,
                RunRoute.Research,
                "preview research",
                EffectiveContext(
                    campaignId,
                    "/active/workspace",
                    "provider/preview-model",
                    null),
                [new AttachedFileDto("stdin.txt", "preview context")],
                [new ScryingFocusDto("iVBORw0KGgo=", "image/png")]),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.Success, exitCode);

        Assert.Equal(0, resolver.CallCount);

        Assert.Equal(
            ["/api/intelligence/context/inspect"],
            handler.RequestPaths);

        ContextPreviewRequest request = Assert.IsType<ContextPreviewRequest>(
            JsonSerializer.Deserialize(
                handler.Body,
                ArcanumJsonContext.Default.ContextPreviewRequest));

        Assert.Equal("preview research", request.Prompt);

        Assert.True(request.ShowContent);

        Assert.True(request.NoRetrieval);

        Assert.True(request.UnattendedMode);

        Assert.True(request.DisableAllTools);

        Assert.Contains(
            "untrusted research material",
            request.AdditionalSystemPrompt,
            StringComparison.Ordinal);

        Assert.Equal(1_750, request.MaxOutputTokens);

        Assert.Equal(0.3f, request.Temperature);

        Assert.Equal("preview context", Assert.Single(request.AttachedFiles!).Content);

        Assert.Equal("image/png", Assert.Single(request.ScryingFoci!).MimeType);

    }

    [Theory]

    [InlineData(0, 2, 2_000)]

    [InlineData(5, 0, 2_000)]

    [InlineData(5, 2, 63)]

    public async Task RunExecutionDispatcher_research_dry_run_rejects_invalid_research_bounds_before_preview(
        int maxSources,
        int maxHops,
        int tokenBudget)
    {

        PreviewCaptureHandler handler = new();

        RecordingConsole console = new();

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        ReplaceApiTransport(services, handler);

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            new FakeContextResolver(
                CliInferenceContextResult.Success(
                    EffectiveContext(null, "/active/workspace", null, null),
                    [])));

        services.RemoveAll<IConsoleDispatcher>();

        services.AddSingleton<IConsoleDispatcher>(console);

        await using ServiceProvider provider = services.BuildServiceProvider();

        IRunExecutionDispatcher dispatcher = provider
            .GetRequiredService<IRunExecutionDispatcher>();

        RunCommandRequest options = Request(
            research: true,
            dryRun: true) with
        {

            MaxSources = maxSources,

            MaxHops = maxHops,

            TokenBudget = tokenBudget,

        };

        int exitCode = await dispatcher.ExecuteAsync(
            new RunExecutionRequest(
                options,
                RunRoute.Research,
                "preview research",
                EffectiveContext(null, "/active/workspace", null, null),
                [],
                []),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

        Assert.Empty(handler.RequestPaths);

        Assert.Contains(
            console.Diagnostics,
            static diagnostic => diagnostic.Contains(
                "1-20 sources",
                StringComparison.Ordinal));

    }

    [Fact]

    public async Task RunExecutionDispatcher_research_dry_run_rejects_negative_cost_before_preview()
    {

        PreviewCaptureHandler handler = new();

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        ReplaceApiTransport(services, handler);

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            new FakeContextResolver(
                CliInferenceContextResult.Success(
                    EffectiveContext(null, "/active/workspace", null, null),
                    [])));

        await using ServiceProvider provider = services.BuildServiceProvider();

        IRunExecutionDispatcher dispatcher = provider
            .GetRequiredService<IRunExecutionDispatcher>();

        RunCommandRequest options = Request(
            research: true,
            dryRun: true) with
        {

            CostBudget = -0.01m,

        };

        int exitCode = await dispatcher.ExecuteAsync(
            new RunExecutionRequest(
                options,
                RunRoute.Research,
                "preview research",
                EffectiveContext(null, "/active/workspace", null, null),
                [],
                []),
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

        Assert.Empty(handler.RequestPaths);

    }

    private static void ReplaceApiTransport(
        ServiceCollection services,
        HttpMessageHandler handler)
    {

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        services.RemoveAll<IApiKeyDigestCache>();

        services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

    }

    private static ServiceCollection ConfigureRunParserServices(
        FakeRunInputReader input,
        FakeRunAttachmentStager? stager = null,
        FakeRunExecutionDispatcher? execution = null,
        FakeContextResolver? resolver = null)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IRunInputReader>();

        services.AddSingleton<IRunInputReader>(input);

        services.RemoveAll<IRunAttachmentStager>();

        services.AddSingleton<IRunAttachmentStager>(
            stager ?? new FakeRunAttachmentStager(SuccessStage([])));

        services.RemoveAll<IRunExecutionDispatcher>();

        services.AddSingleton<IRunExecutionDispatcher>(
            execution ?? new FakeRunExecutionDispatcher());

        services.RemoveAll<ICliInferenceContextResolver>();

        services.AddSingleton<ICliInferenceContextResolver>(
            resolver ?? new FakeContextResolver(
                CliInferenceContextResult.Success(
                    EffectiveContext(null, null, null, null),
                    [])));

        services.RemoveAll<IGrimoireCliInitialization>();

        services.AddSingleton<IGrimoireCliInitialization>(
            new NoopGrimoireInitialization());

        services.RemoveAll<IArcanumServeLauncher>();

        services.AddSingleton<IArcanumServeLauncher>(
            new NoopServeLauncher());

        return services;

    }

    private static RunCommand CreateCommand(
        RunInputReadResult inputResult,
        RunAttachmentStageResult stageResult,
        FakeRunInputReader? input = null,
        FakeRunExecutionDispatcher? execution = null,
        FakeContextResolver? resolver = null,
        NoopGrimoireInitialization? grimoire = null,
        NoopServeLauncher? serve = null,
        RecordingConsole? console = null) =>
        new(
            input ?? new FakeRunInputReader(inputResult),
            new FakeRunAttachmentStager(stageResult),
            resolver ?? new FakeContextResolver(
                CliInferenceContextResult.Success(
                    EffectiveContext(null, null, null, null),
                    [])),
            execution ?? new FakeRunExecutionDispatcher(),
            grimoire ?? new NoopGrimoireInitialization(),
            serve ?? new NoopServeLauncher(),
            console ?? new RecordingConsole());

    private static RunCommandRequest Request(
        bool research = false,
        string? spell = null,
        bool dryRun = false,
        bool newSession = false,
        string? session = null,
        string[]? with = null,
        string[]? prompt = null) =>
        new(
            prompt ?? ["prompt"],
            EscapedArguments: [],
            research,
            spell,
            with ?? [],
            dryRun,
            ShowContent: false,
            Model: null,
            newSession,
            Unattended: false,
            Campaign: null,
            Workspace: null,
            session,
            Temperature: null,
            TopP: null,
            MaxTokens: null,
            Seed: null,
            Stop: [],
            ResponseFormat: null,
            PresencePenalty: null,
            FrequencyPenalty: null,
            MaxSources: 5,
            MaxHops: 2,
            TokenBudget: 2_000,
            CostBudget: null);

    private static RunInputReadResult SuccessInput(
        string instruction,
        string? pipedContent) =>
        new(
            true,
            instruction,
            pipedContent,
            pipedContent?.Length ?? 0,
            pipedContent is not null,
            false,
            false,
            [],
            null);

    private static RunAttachmentStageResult SuccessStage(
        List<AttachedFileDto> attachedFiles,
        List<ScryingFocusDto>? scryingFoci = null) =>
        new(
            true,
            attachedFiles,
            scryingFoci ?? [],
            [],
            [],
            null);

    private static CliEffectiveContext EffectiveContext(
        Guid? campaignId,
        string? workspace,
        string? model,
        Guid? sessionId) =>
        new(
            new CliContextValue<Guid?>(
                campaignId,
                CliContextSource.ActiveContext),
            new CliContextValue<string?>(
                workspace,
                CliContextSource.ActiveContext),
            new CliContextValue<string?>(
                model,
                CliContextSource.ActiveContext),
            new CliContextValue<Guid?>(
                sessionId,
                CliContextSource.ActiveContext));

    private sealed class FakeRunInputReader(
        RunInputReadResult result) : IRunInputReader
    {

        public RunInputReadResult Result { get; } = result;

        public string? PositionalInstruction { get; private set; }

        public Task<RunInputReadResult> ReadAsync(
            string? positionalInstruction,
            CancellationToken cancellationToken,
            bool hasExplicitFileContext = false)
        {

            PositionalInstruction = positionalInstruction;

            HasExplicitFileContext = hasExplicitFileContext;

            return Task.FromResult(Result);

        }

        public bool HasExplicitFileContext { get; private set; }

    }

    private sealed class FakeRunAttachmentStager(
        RunAttachmentStageResult result) : IRunAttachmentStager
    {

        public IReadOnlyList<string>? WithValues { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public string? PipedContent { get; private set; }

        public Task<RunAttachmentStageResult> StageAsync(
            IReadOnlyList<string> withValues,
            string workingDirectory,
            string? pipedContent,
            CancellationToken cancellationToken)
        {

            WithValues = withValues;

            WorkingDirectory = workingDirectory;

            PipedContent = pipedContent;

            return Task.FromResult(result);

        }

    }

    private sealed class FakeContextResolver(
        CliInferenceContextResult result) : ICliInferenceContextResolver
    {

        public CliInferenceContextRequest? Request { get; private set; }

        public int CallCount { get; private set; }

        public Task<CliInferenceContextResult> ResolveAsync(
            CliInferenceContextRequest request,
            CancellationToken cancellationToken)
        {

            CallCount++;

            Request = request;

            return Task.FromResult(result);

        }

    }

    private sealed class FakeRunExecutionDispatcher : IRunExecutionDispatcher
    {

        public RunExecutionRequest? Request { get; private set; }

        public Task<int> ExecuteAsync(
            RunExecutionRequest request,
            CancellationToken cancellationToken)
        {

            Request = request;

            return Task.FromResult(0);

        }

    }

    private sealed class NoopGrimoireInitialization : IGrimoireCliInitialization
    {

        public int CallCount { get; private set; }

        public Task EnsureInitializedAsync(
            CancellationToken cancellationToken)
        {

            CallCount++;

            return Task.CompletedTask;

        }

    }

    private sealed class NoopServeLauncher : IArcanumServeLauncher
    {

        public int CallCount { get; private set; }

        public Task<ServeLaunchResult> EnsureRunningAsync(
            CancellationToken cancellationToken)
        {

            CallCount++;

            return Task.FromResult(
                new ServeLaunchResult(
                    ServeLaunchStatus.AlreadyRunning,
                    HealthProbeState.Healthy,
                    TimeSpan.Zero,
                    null,
                    null));

        }

    }

    private sealed class RecordingConsole : IConsoleDispatcher
    {

        public List<string> Diagnostics { get; } = [];

        public List<object> JsonValues { get; } = [];

        public void WritePayload(string value)
        {

        }

        public void WriteDiagnostic(string value) =>
            Diagnostics.Add(value);

        public void WriteJson<T>(
            T value,
            JsonTypeInfo<T> typeInfo)
        {

            JsonValues.Add(value!);

        }

        public void WriteJson(JsonElement value) =>
            JsonValues.Add(value);

        public void BeginJsonStream()
        {

        }

    }

    private sealed class FakeResourceCatalog(
        ResourceSelectionResult<SpellSummary> spellResult) : ICliResourceCatalog
    {

        public string? SpellIdentifier { get; private set; }

        public string? SpellWorkspace { get; private set; }

        public Task<ResourceSelectionResult<CampaignDto>> SelectCampaignAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<SessionSummaryDto>> SelectSessionAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<EntryDto>> SelectSessionEntryAsync(
            Guid sessionId,
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<WorkspaceInfo>> SelectWorkspaceAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<PromptSummaryDto>> SelectPromptAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<SpellSummary>> SelectSpellAsync(
            string? identifier,
            string? workspace,
            CancellationToken cancellationToken)
        {

            SpellIdentifier = identifier;

            SpellWorkspace = workspace;

            return Task.FromResult(spellResult);

        }

        public Task<ResourceSelectionResult<ApprenticeSummaryDto>> SelectApprenticeAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<ModelInfoDto>> SelectModelAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<ProviderInfoDto>> SelectProviderAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResourceSelectionResult<McpServerInfo>> SelectMcpServerAsync(
            string? identifier,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class FakeHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001"),

                Timeout = Timeout.InfiniteTimeSpan,

            };

    }

    private sealed class CapturingNdjsonHandler : HttpMessageHandler
    {

        public string? RequestPath { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            RequestPath = request.RequestUri?.AbsolutePath;

            Body = request.Content is null
                ? string.Empty
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            string ndjson = string.Join(
                '\n',
                new[]
                {

                    new IntelligenceEvent(
                        IntelligenceEventType.Token,
                        string.Empty,
                        "complete"),

                    new IntelligenceEvent(
                        IntelligenceEventType.Result,
                        "complete",
                        "complete"),

                }.Select(
                    static frame => JsonSerializer.Serialize(
                        frame,
                        ArcanumJsonContext.Default.IntelligenceEvent)))
                + "\n";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new StringContent(
                    ndjson,
                    Encoding.UTF8,
                    "application/x-ndjson"),

            };

        }

    }

    private sealed class ResearchCaptureHandler : HttpMessageHandler
    {

        public string? RequestPath { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            RequestPath = request.RequestUri?.AbsolutePath;

            Body = request.Content is null
                ? string.Empty
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            const string ndjson =
                "{\"type\":\"result\",\"result\":{\"answer\":\"complete\",\"citations\":[],\"provider\":\"test\",\"model\":\"test\",\"truncated\":false,\"usage\":{}}}\n";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new StringContent(
                    ndjson,
                    Encoding.UTF8,
                    "application/x-ndjson"),

            };

        }

    }

    private sealed class PreviewCaptureHandler : HttpMessageHandler
    {

        public List<string> RequestPaths { get; } = [];

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);

            Body = request.Content is null
                ? string.Empty
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            byte[] response = JsonSerializer.SerializeToUtf8Bytes(
                new ApiResponse<ContextPreviewResult>(
                    ContextPreviewTestData.Create(showContent: true),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseContextPreviewResult);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new ByteArrayContent(response),

            };

        }

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(
            string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class FakeEye : IEyeOfTheWorld
    {

        public Task<PatternSnapshot> PerceivePatternAsync(
            string directoryPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new PatternSnapshot(
                    DomainType.Unknown,
                    directoryPath,
                    []));

    }

    private sealed class NoopChronosyncEngine : IChronosyncEngine
    {

        public Task<ChronosyncReport> AnalyzeAndSyncAsync(
            PatternSnapshot currentSnapshot,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new ChronosyncReport(
                    null,
                    [],
                    [],
                    false));

    }

}

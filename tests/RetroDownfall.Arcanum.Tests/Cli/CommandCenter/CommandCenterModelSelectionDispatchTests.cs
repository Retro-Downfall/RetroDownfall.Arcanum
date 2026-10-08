using System.Collections.ObjectModel;

using System.Net;

using System.Text.Json;

using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// What <c>/model</c> does end to end through the dispatcher, against a host that answers
/// <c>GET /api/providers</c> and <c>GET /api/models</c>. A refused name must leave the session model
/// exactly as it was: the header label and the drop-down's marker both read it, so a name that was
/// stored and then failed the next turn showed as the current model until then.
/// </summary>
public sealed class CommandCenterModelSelectionDispatchTests
{
    private const string Current = "ornith1.5:35b";

    private static readonly ProviderInfoDto[] OllamaProviders =
    [
        new("Ollama", nameof(AiProviderKind.OpenAICompatible), "***", string.Empty, ["ornith1.5:35b", "gemma4:e4b"], 32_768, []),
    ];

    private static readonly ModelInfoDto[] OllamaModels =
    [
        new("ornith1.5:35b", "Ollama", nameof(AiProviderKind.OpenAICompatible), "***", 32_768),
        new("gemma4:e4b", "Ollama", nameof(AiProviderKind.OpenAICompatible), "***", 32_768),
    ];

    [Fact]
    public async Task An_unknown_model_is_refused_and_the_session_model_is_unchanged()
    {
        CatalogHandler handler = new(OllamaProviders, OllamaModels);

        CommandCenterState state = NewState();

        _ = await CreateDispatcher(handler).DispatchAsync("/model bogus-model", state, CancellationToken.None);

        Assert.Equal(Current, state.Model);

        string transcript = state.Log.RenderPlainText();

        Assert.Contains("Unknown model `bogus-model`; still using ornith1.5:35b.", transcript, StringComparison.Ordinal);

        Assert.Contains("Available: ornith1.5:35b (Ollama), gemma4:e4b (Ollama).", transcript, StringComparison.Ordinal);

        Assert.DoesNotContain("Model set to", transcript, StringComparison.Ordinal);

        Assert.Equal(SessionLogEntryKind.Error, state.Log.Snapshot()[^1].Kind);

        Assert.Equal(["/api/providers"], handler.Paths);
    }

    [Fact]
    public async Task A_listed_model_is_selected_in_the_provider_casing()
    {
        CommandCenterState state = NewState();

        _ = await CreateDispatcher(new CatalogHandler(OllamaProviders, OllamaModels))
            .DispatchAsync("/model GEMMA4:E4B", state, CancellationToken.None);

        Assert.Equal("gemma4:e4b", state.Model);

        Assert.Contains(
            "Model set to gemma4:e4b (Ollama) for this session.",
            state.Log.RenderPlainText(),
            StringComparison.Ordinal);

        Assert.Equal(SessionLogEntryKind.Status, state.Log.Snapshot()[^1].Kind);
    }

    /// <summary>
    /// An unchecked name is exactly what used to fail the next turn, so a host that cannot answer
    /// refuses the selection instead of taking the name on trust.
    /// </summary>
    [Fact]
    public async Task A_provider_list_that_cannot_be_read_refuses_the_name()
    {
        CommandCenterState state = NewState();

        _ = await CreateDispatcher(new CatalogHandler(OllamaProviders, OllamaModels, HttpStatusCode.ServiceUnavailable))
            .DispatchAsync("/model gemma4:e4b", state, CancellationToken.None);

        Assert.Equal(Current, state.Model);

        string transcript = state.Log.RenderPlainText();

        Assert.Contains("Could not check `gemma4:e4b` against the configured providers:", transcript, StringComparison.Ordinal);

        Assert.Contains("Still using ornith1.5:35b.", transcript, StringComparison.Ordinal);

        Assert.Equal(SessionLogEntryKind.Error, state.Log.Snapshot()[^1].Kind);
    }

    /// <summary>A Familiar's catalogue belongs to the vendor, so a name no row lists still works.</summary>
    [Fact]
    public async Task A_familiar_accepts_a_model_no_provider_lists_and_is_named()
    {
        ProviderInfoDto[] providers =
        [
            .. OllamaProviders,
            new("claude-sub", nameof(AiProviderKind.ClaudeCodeCli), string.Empty, string.Empty, ["sonnet"], 200_000, ["opus"]),
        ];

        CommandCenterState state = NewState();

        _ = await CreateDispatcher(new CatalogHandler(providers, OllamaModels))
            .DispatchAsync("/model claude-next", state, CancellationToken.None);

        Assert.Equal("claude-next", state.Model);

        Assert.Contains(
            "Model set to claude-next for this session. No provider lists it, so claude-sub (ClaudeCodeCli) will be asked for it.",
            state.Log.RenderPlainText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hidden_model_is_selected()
    {
        ProviderInfoDto[] providers =
        [
            new("claude-sub", nameof(AiProviderKind.ClaudeCodeCli), string.Empty, string.Empty, ["sonnet"], 200_000, ["opus"]),
        ];

        CommandCenterState state = NewState();

        _ = await CreateDispatcher(new CatalogHandler(providers, []))
            .DispatchAsync("/model opus", state, CancellationToken.None);

        Assert.Equal("opus", state.Model);

        Assert.Contains(
            "Model set to opus (claude-sub, hidden from model lists) for this session.",
            state.Log.RenderPlainText(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/model list</c> is not a model name. Taken as one it became the session model, and with a
    /// Familiar configured it would have been forwarded to the vendor as a model id.
    /// </summary>
    [Fact]
    public async Task Model_list_is_denied_without_asking_the_host()
    {
        ParsedShellCommand parsed = new ShellCommandParser().Parse("/model list");

        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);

        CatalogHandler handler = new(OllamaProviders, OllamaModels);

        CommandCenterState state = NewState();

        _ = await CreateDispatcher(handler).DispatchAsync("/model LIST", state, CancellationToken.None);

        Assert.Empty(handler.Paths);

        Assert.Equal(Current, state.Model);

        Assert.Contains(
            "`/model list` is not a model name. Use `/model` to list models, or `/model <name>` to choose one.",
            state.Log.RenderPlainText(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The denial is the only place the operator learns how to spell the two real forms, so every
    /// form it recommends is replayed through the parser like every other piece of guidance.
    /// </summary>
    [Fact]
    public void Every_slash_form_the_model_list_denial_recommends_parses()
    {
        ShellCommandParser parser = new();

        string message = parser.Parse("/model list").DenialMessage!;

        string[] recommended =
        [
            .. Regex.Matches(message, "`(/[^`]+)`")
                .Select(static match => match.Groups[1].Value)
                .Where(static form => !form.Equals("/model list", StringComparison.Ordinal)),
        ];

        Assert.Equal(["/model", "/model <name>"], recommended);

        foreach (string usage in recommended)
        {
            foreach (string form in SlashUsageExpander.Expand(usage, "gemma4:e4b"))
            {
                ParsedShellCommand parsed = parser.Parse(form);

                Assert.True(
                    parsed.Kind is ShellCommandKind.Model or ShellCommandKind.ModelSelect,
                    $"`{usage}` documents `{form}`, which the parser reads as {parsed.Kind}: {parsed.DenialMessage}");
            }
        }
    }

    [Fact]
    public async Task Model_with_no_name_marks_this_sessions_model_and_says_how_to_choose()
    {
        CatalogHandler handler = new(OllamaProviders, OllamaModels);

        CommandCenterState state = NewState();

        _ = await CreateDispatcher(handler).DispatchAsync("/model", state, CancellationToken.None);

        string listing = state.Log.Snapshot()[^1].Text;

        Assert.Equal(
            string.Join(
                System.Environment.NewLine,
                [
                    "Models (● this session):",
                    "● ornith1.5:35b (Ollama)",
                    "  gemma4:e4b (Ollama)",
                    "Choose with /model <name>, or Shift+Tab to the model control and press Enter.",
                ]),
            listing);

        Assert.Equal(["/api/models"], handler.Paths);
    }

    /// <summary>
    /// With no session model the host uses the configured default, so that is the one marked —
    /// compared without regard to case, as the host resolves it.
    /// </summary>
    [Fact]
    public async Task Model_with_no_name_marks_the_default_model_when_the_session_has_none()
    {
        CommandCenterState state = new(new SessionLogBuffer());

        _ = await CreateDispatcher(
                new CatalogHandler(OllamaProviders, OllamaModels),
                new ArcanumSettings { DefaultModel = "GEMMA4:E4B" })
            .DispatchAsync("/model", state, CancellationToken.None);

        string listing = state.Log.Snapshot()[^1].Text;

        Assert.Contains("● gemma4:e4b (Ollama)", listing, StringComparison.Ordinal);

        Assert.Contains("  ornith1.5:35b (Ollama)", listing, StringComparison.Ordinal);
    }

    private static CommandCenterState NewState() => new(new SessionLogBuffer()) { Model = Current };

    private static ShellCommandDispatcher CreateDispatcher(HttpMessageHandler handler, ArcanumSettings? settings = null)
    {
        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            ArcanumApiCredentialLeaseTestFactory.Create("test-key"));

        SessionWorkspaceService workspace = new(
            client,
            new NoopLastSessionStore(),
            NullLogger<SessionWorkspaceService>.Instance);

        return new ShellCommandDispatcher(
            client,
            new ShellCommandParser(),
            new TestOptionsMonitor(settings ?? new ArcanumSettings()),
            workspace,
            NullLogger<ShellCommandDispatcher>.Instance);
    }

    /// <summary>Serves the provider and model listings, and records which ones were asked for.</summary>
    private sealed class CatalogHandler(
        ProviderInfoDto[] providers,
        ModelInfoDto[] models,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Collection<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string path = request.RequestUri!.AbsolutePath;

            Paths.Add(path);

            byte[]? payload = status != HttpStatusCode.OK
                ? null
                : path switch
                {
                    "/api/providers" => JsonSerializer.SerializeToUtf8Bytes(
                        new ApiResponse<ProviderInfoDto[]>(providers, true, null),
                        ArcanumJsonContext.Default.ApiResponseProviderInfoDtoArray),
                    "/api/models" => JsonSerializer.SerializeToUtf8Bytes(
                        new ApiResponse<ModelInfoDto[]>(models, true, null),
                        ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray),
                    _ => null,
                };

            return Task.FromResult(
                payload is null
                    ? new HttpResponseMessage(status == HttpStatusCode.OK ? HttpStatusCode.NotFound : status)
                    {
                        Content = new StringContent(
                            """{"isSuccess":false,"error":{"code":"Test.Down","message":"down"}}"""),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload),
                    });
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5001/") };
    }

    private sealed class NoopLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public Task<ArcanumClientMutationResult<CliContextDocument>> SaveSessionIdAsync(
            Guid id,
            Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ArcanumClientMutationResult<CliContextDocument>.Completed(
                    CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}

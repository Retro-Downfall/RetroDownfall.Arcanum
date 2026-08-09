using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// End to end through the seam the rest of Arcanum actually uses: resolve a model, get a lease from
/// <see cref="IChatClientFactory"/>, spawn a real child process, and read a real answer back. The
/// unit tests pin the mapping; this pins that the pieces are wired to each other, which is the part
/// a mocked runner can never prove.
/// </summary>
/// <remarks>
/// POSIX only. The adapter owns the whole argument list, so the stub has to be directly executable
/// — a Windows stub needs a shell host in front of it, and prepending one would mean asserting
/// against an invocation Arcanum never builds. The spawn itself is covered on every platform by
/// <see cref="FamiliarProcessRunnerTests"/>, which drives the runner directly.
/// </remarks>
[Collection("ChildProcess")]
public sealed class FamiliarTransportIntegrationTests
{

    private static void RequirePosix() =>
        Skip.If(
            OperatingSystem.IsWindows(),
            "The stub Familiar is a shell script; Windows needs a host process in front of it.");

    [SkippableFact]
    public async Task A_configured_familiar_serves_a_buffered_turn_through_the_factory()
    {

        RequirePosix();

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            FamiliarFixtures.ReadLines(FamiliarFixtures.ClaudeSuccess));

        ChatClientFactory factory = CreateFactory(
            AiProviderKind.ClaudeCodeCli,
            stub,
            declaredModel: "claude-sonnet");

        using ChatClientLease lease = await factory.ResolveClientAsync(
            "claude-sonnet",
            CancellationToken.None);

        ChatResponse response = await lease.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with exactly: PONG")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("PONG", response.Text);

        Assert.Equal("claude-sonnet", lease.ResolvedModel);

    }

    [SkippableFact]
    public async Task A_configured_familiar_serves_a_streaming_turn_through_the_factory()
    {

        RequirePosix();

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            FamiliarFixtures.ReadLines(FamiliarFixtures.ClaudePartialMessages));

        ChatClientFactory factory = CreateFactory(
            AiProviderKind.ClaudeCodeCli,
            stub,
            declaredModel: "claude-haiku");

        using ChatClientLease lease = await factory.ResolveClientAsync(
            "claude-haiku",
            CancellationToken.None);

        List<string> deltas = [];

        await foreach (ChatResponseUpdate update in lease.ChatClient.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None))
        {

            foreach (AIContent content in update.Contents)
            {

                if (content is TextContent { Text.Length: > 0 } text)
                {
                    deltas.Add(text.Text);
                }

            }

        }

        Assert.Equal("PONG PONG PONG", string.Concat(deltas));

    }

    [SkippableFact]
    public async Task Codex_serves_a_turn_end_to_end_including_reported_usage()
    {

        RequirePosix();

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            FamiliarFixtures.ReadLines(FamiliarFixtures.CodexSuccess));

        ChatClientFactory factory = CreateFactory(
            AiProviderKind.CodexCli,
            stub,
            declaredModel: "gpt-5.6");

        using ChatClientLease lease = await factory.ResolveClientAsync("gpt-5.6", CancellationToken.None);

        ChatResponse response = await lease.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("PONG", response.Text);

        Assert.Equal(16318L, Assert.IsType<UsageDetails>(response.Usage).InputTokenCount);

    }

    /// <summary>
    /// The whole point of the kind: a model the operator never wrote into <c>arcanum.json</c> still
    /// reaches the Familiar, verbatim.
    /// </summary>
    [SkippableFact]
    public async Task An_undeclared_model_reaches_the_familiar_through_the_factory()
    {

        RequirePosix();

        using StubFamiliarCli stub = StubFamiliarCli.Create(
            FamiliarFixtures.ReadLines(FamiliarFixtures.ClaudeSuccess));

        ChatClientFactory factory = CreateFactory(AiProviderKind.ClaudeCodeCli, stub, declaredModel: null);

        using ChatClientLease lease = await factory.ResolveClientAsync(
            "claude-shipped-yesterday",
            CancellationToken.None);

        _ = await lease.ChatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            cancellationToken: CancellationToken.None);

        Assert.Equal("claude-shipped-yesterday", lease.ResolvedModel);

        Assert.Contains("claude-shipped-yesterday", stub.ReadRecordedArgv());

    }

    [SkippableFact]
    public async Task A_missing_binary_produces_an_actionable_transport_failure_not_an_empty_turn()
    {

        RequirePosix();

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    Command = StubFamiliarCli.MissingExecutablePath(),
                    Models = ["claude-sonnet"],
                },
            ],
        };

        ChatClientFactory factory = new(
            new StubHttpClientFactory(),
            new StaticOptionsMonitor(settings));

        using ChatClientLease lease = await factory.ResolveClientAsync(
            "claude-sonnet",
            CancellationToken.None);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            async () => await lease.ChatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: CancellationToken.None));

        Assert.Contains("was not found", failure.Message, StringComparison.Ordinal);

        // Arcanum never installs a Familiar, so the remediation has to name the operator's own step.
        Assert.Contains("install", failure.Message, StringComparison.OrdinalIgnoreCase);

    }

    private static ChatClientFactory CreateFactory(
        AiProviderKind kind,
        StubFamiliarCli stub,
        string? declaredModel)
    {

        ProviderSettings provider = new()
        {

            Name = $"{kind}-subscription",

            Type = kind,

            Command = stub.FileName,

            Models = declaredModel is null ? [] : [declaredModel],

        };

        ArcanumSettings settings = new() { Providers = [provider] };

        return new ChatClientFactory(
            new StubHttpClientFactory(),
            new StaticOptionsMonitor(settings));

    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new();

    }

    private sealed class StaticOptionsMonitor(ArcanumSettings settings) : IOptionsMonitor<ArcanumSettings>
    {

        public ArcanumSettings CurrentValue => settings;

        public ArcanumSettings Get(string? name) => settings;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;

    }

}

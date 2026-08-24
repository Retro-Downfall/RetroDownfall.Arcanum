using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands.Tower;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Tower;

using Spectre.Console;

using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// What <c>arcanum memory status</c> puts in front of an operator about their Covenant.
/// </summary>
/// <remarks>
/// Driven through the command object against a recorded response rather than through the command
/// host: the host redirects standard input and never closes it, which hangs the run rather than
/// failing it.
/// </remarks>
public sealed class MemoryStatusCovenantCommandTests : IDisposable
{

    private readonly IAnsiConsole _priorConsole = AnsiConsole.Console;

    private readonly TestConsole _console = new();

    private static CancellationToken Token => CancellationToken.None;

    public MemoryStatusCovenantCommandTests() => AnsiConsole.Console = _console;

    public void Dispose() => AnsiConsole.Console = _priorConsole;

    [Fact]
    public async Task The_counts_the_server_measured_are_the_counts_the_operator_reads()
    {

        RecordingDispatcher dispatcher = await StatusAsync(Covenant(
        [
            new CovenantScopeCountDto(CovenantScope.Global, CovenantLane.Confirmed, CovenantLifecycle.Set, 3),
            new CovenantScopeCountDto(CovenantScope.Campaign, CovenantLane.Proposed, CovenantLifecycle.Set, 7),
            new CovenantScopeCountDto(CovenantScope.Global, CovenantLane.Confirmed, CovenantLifecycle.Retired, 2),
        ]));

        string table = _console.Output;

        Assert.Contains("Global", table, StringComparison.Ordinal);

        Assert.Contains("Proposed", table, StringComparison.Ordinal);

        // Retired is its own row rather than folded into the Set count: "three standing preferences"
        // and "three standing plus two withdrawn" are different facts about the same entries.
        Assert.Contains("Retired", table, StringComparison.Ordinal);

        Assert.Contains("3", table, StringComparison.Ordinal);

        Assert.Contains("7", table, StringComparison.Ordinal);

        string rendered = string.Join("\n", dispatcher.Payloads);

        Assert.Contains("Global Confirmed 1200", rendered, StringComparison.Ordinal);

        Assert.Contains("Campaign Proposed 900", rendered, StringComparison.Ordinal);

        // The ceiling travels with the totals. A byte count with nothing to compare it against tells
        // an operator nothing about whether they are near the limit.
        Assert.Contains("ceiling 4096", rendered, StringComparison.Ordinal);

    }

    [Fact]
    public async Task An_installation_holding_nothing_says_so_rather_than_printing_an_empty_table()
    {

        RecordingDispatcher dispatcher = await StatusAsync(Covenant([]));

        Assert.Contains(
            "No Covenant entries.",
            string.Join("\n", dispatcher.Payloads),
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task An_unavailable_arm_names_its_degradation_beside_the_word_unavailable()
    {

        RecordingDispatcher dispatcher = await StatusAsync(
            Covenant([]) with { Available = false, DegradationCode = "canonical-unavailable" });

        string rendered = string.Join("\n", dispatcher.Payloads);

        Assert.Contains("unavailable (canonical-unavailable)", rendered, StringComparison.Ordinal);

        // The zero beside an unavailable arm has to be readable as "not counted", which is what the
        // degradation code on the same line supplies.
        Assert.Contains("No Covenant entries.", rendered, StringComparison.Ordinal);

    }

    [Fact]
    public async Task An_installation_without_a_covenant_arm_prints_nothing_rather_than_zeroes()
    {

        RecordingDispatcher dispatcher = await StatusAsync(covenant: null);

        string rendered = string.Join("\n", dispatcher.Payloads);

        // A zero is a measurement. The honest rendering of something never measured is absence.
        Assert.DoesNotContain("Covenant", rendered, StringComparison.Ordinal);

    }

    private static CovenantStatusDto Covenant(CovenantScopeCountDto[] counts) =>
        new(
            Enabled: true,
            Available: true,
            counts,
            GlobalConfirmedRenderedBytes: 1200,
            CampaignConfirmedRenderedBytes: 0,
            CampaignProposedRenderedBytes: 900,
            RenderedByteCeilingPerSection: 4096,
            new CovenantSearchHealthDto(
                CovenantSearchHealthState.Healthy,
                CovenantSearchExecutionMode.Fts,
                CovenantSearchRebuildGuidance.None),
            "Retained until retired.",
            DegradationCode: null);

    private static async Task<RecordingDispatcher> StatusAsync(CovenantStatusDto? covenant)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(new StatusHandler(covenant)));

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        using ServiceProvider provider = services.BuildServiceProvider();

        RecordingDispatcher dispatcher = new();

        MemoryCommands commands = new(
            provider.GetRequiredService<ArcanumApiClient>(),
            provider.GetRequiredService<IThemePalette>(),
            dispatcher,
            new RefusingConfirmation());

        Assert.Equal(0, await commands.Status(sessionIdentifier: null, Token));

        return dispatcher;

    }

    private sealed class StatusHandler(CovenantStatusDto? covenant) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        ApiResponse<MemoryStatusDto>.FromResult(
                            Result<MemoryStatusDto>.Success(
                                new MemoryStatusDto(null, null, [], covenant))),
                        ArcanumJsonContext.Default.ApiResponseMemoryStatusDto),
                    Encoding.UTF8,
                    "application/json"),
            });

    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5000/") };

    }

    private sealed class RecordingDispatcher : IConsoleDispatcher
    {

        internal List<string> Payloads { get; } = [];

        internal List<string> Diagnostics { get; } = [];

        public void WritePayload(string value) => Payloads.Add(value);

        public void WriteDiagnostic(string value) => Diagnostics.Add(value);

        public void WriteVerbose(string value) => Diagnostics.Add(value);

        public void WriteLine(string value) => Payloads.Add(value);

        public void WriteJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            Payloads.Add(JsonSerializer.Serialize(value, typeInfo));

        public void WriteJson(JsonElement value) => Payloads.Add(value.GetRawText());

        public void BeginJsonStream()
        {
        }

    }

    private sealed class RefusingConfirmation : IConfirmationPrompt
    {

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reading status asks the operator nothing.");

    }

    private sealed class FixedSecretStore : ISecretStore
    {

        private const string Key = "arc_test_0123456789abcdef0123456789abcdef";

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(Key);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(Key));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

}

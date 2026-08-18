using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.Arsenal;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class DiagnosticMcpInvocationViewModelTests
{

    [Fact]
    public async Task Refresh_FiltersInternalServerAndStoppedServers()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto(
            ActiveSpells: [],
            NativeTools: [],
            McpServers:
            [
                new(DiagnosticMcpInvocationViewModel.InternalServerName, "running", 3, ["execute_command", "write_file", "read_file_chunk"], null),
                new("external-a", "running", 1, ["echo"], null),
                new("external-b", "stopped", 0, [], "not started"),
            ],
            Spells: []);

        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Null(vm.LastError);
        Assert.Single(vm.Servers);
        Assert.Equal("external-a", vm.Servers[0].Name);
        Assert.Contains("1 running external server", vm.StatusText);

    }

    [Fact]
    public async Task SelectingServer_PopulatesTools()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 2, ["echo", "ping"], null)], []);

        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource);

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SelectedServer = vm.Servers[0];

        Assert.Equal(2, vm.Tools.Count);
        Assert.Contains("echo", vm.Tools);
        Assert.Contains("ping", vm.Tools);

    }

    [Fact]
    public async Task Invoke_RequiresConfirm_AndSurfacesBlockedToolErrorFromServer()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 1, ["echo"], null)], []);
        dataSource.DiagnosticError = "Mcp.DiagnosticBlocked: This tool cannot be invoked from the diagnostic endpoint because it is a Forbidden Art.";

        ConfirmingDialogService confirmation = new(confirm: true);
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, confirmation: confirmation);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedTool = "echo";

        await vm.InvokeCommand.ExecuteAsync(null);

        Assert.True(confirmation.WasCalled);
        Assert.NotNull(vm.LastError);
        Assert.Contains("Forbidden Art", vm.LastError);
        Assert.Equal("Invocation failed.", vm.StatusText);

    }

    [Fact]
    public async Task Invoke_CancelledConfirm_DoesNotCallApi()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 1, ["echo"], null)], []);

        ConfirmingDialogService confirmation = new(confirm: false);
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, confirmation: confirmation);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedTool = "echo";

        await vm.InvokeCommand.ExecuteAsync(null);

        Assert.True(confirmation.WasCalled);
        Assert.False(dataSource.DiagnosticInvokeCalled);
        Assert.Equal("Invocation cancelled.", vm.StatusText);

    }

    [Fact]
    public async Task Invoke_HappyPath_DisplaysResultAndDuration()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 1, ["echo"], null)], []);
        dataSource.DiagnosticResponse = new McpToolInvokeResponse
        {
            Result = JsonDocument.Parse("""{"ok":true}""").RootElement.Clone(),
            ServerName = "external-a",
            ToolName = "echo",
            DurationMs = 17,
            Truncated = false,
        };

        ConfirmingDialogService confirmation = new(confirm: true);
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, confirmation: confirmation);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedTool = "echo";

        await vm.InvokeCommand.ExecuteAsync(null);

        Assert.Null(vm.LastError);
        Assert.Contains("17ms", vm.StatusText);
        Assert.Equal("external-a", vm.ResolvedServerName);
        Assert.False(vm.LastTruncated);
        Assert.Contains("\"ok\":true", vm.ResultText);

    }

    [Fact]
    public async Task Invoke_TruncatedResult_SetsFlag()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 1, ["echo"], null)], []);
        dataSource.DiagnosticResponse = new McpToolInvokeResponse
        {
            Result = JsonDocument.Parse("\"big\"").RootElement.Clone(),
            ServerName = "external-a",
            ToolName = "echo",
            DurationMs = 5,
            Truncated = true,
        };

        ConfirmingDialogService confirmation = new(confirm: true);
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, confirmation: confirmation);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedTool = "echo";

        await vm.InvokeCommand.ExecuteAsync(null);

        Assert.True(vm.LastTruncated);
        Assert.Contains("truncated", vm.StatusText);

    }

    [Fact]
    public async Task Invoke_InvalidArgumentsJson_SetsLastError_NoApiCall()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 1, ["echo"], null)], []);

        ConfirmingDialogService confirmation = new(confirm: true);
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, confirmation: confirmation);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedTool = "echo";
        vm.ArgumentsText = "{ not json";

        await vm.InvokeCommand.ExecuteAsync(null);

        // Confirmation is gated behind JSON parsing in the VM, so the dialog must not be shown.
        Assert.False(confirmation.WasCalled);
        Assert.False(dataSource.DiagnosticInvokeCalled);
        Assert.NotNull(vm.LastError);
        Assert.Contains("Invalid arguments JSON", vm.LastError);

    }

    [Fact]
    public async Task SaveFixture_PersistsWithNameAndLoads()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 1, ["echo"], null)], []);
        InMemoryDiagnosticMcpFixtureStore store = new();
        FixedAnswerTextInput textInput = new("my-fixture");
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, store: store, textInput: textInput);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedTool = "echo";
        vm.ArgumentsText = "{\"path\":\"/tmp\"}";

        await vm.SaveFixtureCommand.ExecuteAsync(null);

        Assert.Equal(1, store.SaveCount);
        await vm.LoadFixturesCommand.ExecuteAsync(null);
        Assert.Single(vm.Fixtures);
        Assert.Equal("my-fixture", vm.Fixtures[0].Name);
        Assert.Equal("echo", vm.Fixtures[0].ToolName);

    }

    [Fact]
    public async Task SaveFixture_DedupesByName_KeepingNewest()
    {

        FakeArsenalDataSource dataSource = new();
        dataSource.Arsenal = new WorkspaceArsenalDto([], [], [new("external-a", "running", 1, ["echo"], null)], []);
        InMemoryDiagnosticMcpFixtureStore store = new();
        FixedAnswerTextInput textInput = new("dup");
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, store: store, textInput: textInput);

        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedTool = "echo";
        vm.ArgumentsText = "{\"v\":1}";
        await vm.SaveFixtureCommand.ExecuteAsync(null);

        vm.ArgumentsText = "{\"v\":2}";
        await vm.SaveFixtureCommand.ExecuteAsync(null);

        await vm.LoadFixturesCommand.ExecuteAsync(null);

        Assert.Single(vm.Fixtures);
        Assert.Contains("\"v\":2", vm.Fixtures[0].ArgumentsJson);

    }

    [Fact]
    public async Task LoadFixtureIntoEditor_RestoresFields()
    {

        FakeArsenalDataSource dataSource = new();
        InMemoryDiagnosticMcpFixtureStore store = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DiagnosticMcpFixtureRecord seed = new(Guid.NewGuid(), "seed", now, now, "echo", "external-a", "/ws", "{\"x\":1}", null, null);
        await store.SaveAsync(new DiagnosticMcpFixtureStoreDocument(DiagnosticMcpFixtureStore.CurrentSchemaVersion, now, now, [seed]), CancellationToken.None);

        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, store: store);
        await vm.LoadFixturesCommand.ExecuteAsync(null);

        vm.SelectedFixture = vm.Fixtures[0];
        vm.LoadFixtureIntoEditorCommand.Execute(null);

        Assert.Equal("echo", vm.SelectedTool);
        Assert.Equal("{\"x\":1}", vm.ArgumentsText);
        Assert.Equal("/ws", vm.WorkingDirectory);

    }

    [Fact]
    public async Task DeleteFixture_RequiresConfirm_AndRemovesEntry()
    {

        FakeArsenalDataSource dataSource = new();
        InMemoryDiagnosticMcpFixtureStore store = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DiagnosticMcpFixtureRecord seed = new(Guid.NewGuid(), "seed", now, now, "echo", null, null, "{}", null, null);
        await store.SaveAsync(new DiagnosticMcpFixtureStoreDocument(DiagnosticMcpFixtureStore.CurrentSchemaVersion, now, now, [seed]), CancellationToken.None);

        ConfirmingDialogService confirmation = new(confirm: true);
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, confirmation: confirmation, store: store);
        await vm.LoadFixturesCommand.ExecuteAsync(null);
        vm.SelectedFixture = vm.Fixtures[0];

        await vm.DeleteFixtureCommand.ExecuteAsync(null);

        Assert.True(confirmation.WasCalled);
        await vm.LoadFixturesCommand.ExecuteAsync(null);
        Assert.Empty(vm.Fixtures);

    }

    [Fact]
    public async Task ClearFixtures_RequiresConfirm_AndEmptiesStore()
    {

        FakeArsenalDataSource dataSource = new();
        InMemoryDiagnosticMcpFixtureStore store = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await store.SaveAsync(
            new DiagnosticMcpFixtureStoreDocument(
                DiagnosticMcpFixtureStore.CurrentSchemaVersion, now, now,
                [new(Guid.NewGuid(), "a", now, now, "echo", null, null, "{}", null, null)]),
            CancellationToken.None);

        ConfirmingDialogService confirmation = new(confirm: true);
        DiagnosticMcpInvocationViewModel vm = CreateViewModel(dataSource, confirmation: confirmation, store: store);
        await vm.LoadFixturesCommand.ExecuteAsync(null);

        await vm.ClearFixturesCommand.ExecuteAsync(null);

        Assert.True(confirmation.WasCalled);
        Assert.Empty(vm.Fixtures);

    }

    [Fact]
    public void PolicyBanner_AndSensitiveWarning_AreExposedForBinding()
    {

        DiagnosticMcpInvocationViewModel vm = CreateViewModel(new FakeArsenalDataSource());

        Assert.Contains("Diagnostic MCP Invocation", vm.PolicyText);
        Assert.Contains("Forbidden Arts", vm.PolicyText);
        Assert.Contains("locally on this machine", vm.SensitiveWarning);

    }

    [Fact]
    public async Task ExportResultAsync_WhenTheWriteFails_ReportsInsteadOfEscaping()
    {

        DiagnosticMcpInvocationViewModel vm = CreateViewModel(
            new FakeArsenalDataSource(),
            fileDialog: new FixedPathArtifactFileDialogService(FixedPathArtifactFileDialogService.UnwritablePath()));

        vm.ResultText = "{\"ok\":true}";

        await vm.ExportResultAsync(CancellationToken.None);

        Assert.NotEqual("Result exported.", vm.StatusText);

        Assert.Contains("export", vm.StatusText, StringComparison.OrdinalIgnoreCase);

    }

    private static DiagnosticMcpInvocationViewModel CreateViewModel(
        FakeArsenalDataSource dataSource,
        ConfirmingDialogService? confirmation = null,
        InMemoryDiagnosticMcpFixtureStore? store = null,
        FixedAnswerTextInput? textInput = null,
        IArtifactFileDialogService? fileDialog = null) =>
        new(
            dataSource,
            store ?? new InMemoryDiagnosticMcpFixtureStore(),
            new FoundryFloorViewModel(new NullLogService()),
            new FakeWhispersService(),
            confirmation ?? new ConfirmingDialogService(confirm: true),
            fileDialog ?? new NullArtifactFileDialogService(),
            textInput ?? new FixedAnswerTextInput(null));

    private sealed class FakeArsenalDataSource : IArsenalDataSource
    {

        public WorkspaceArsenalDto? Arsenal { get; set; }

        public McpToolInvokeResponse? DiagnosticResponse { get; set; }

        public string? DiagnosticError { get; set; }

        public bool DiagnosticInvokeCalled { get; private set; }

        public Task<(IReadOnlyList<McpServerInfo>? Servers, string? Error)> ListMcpServersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(IReadOnlyList<McpServerInfo>? Servers, string? Error)>(([], null));

        public Task<(bool Ok, string? Error)> StartServerAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult((true, (string?)null));

        public Task<(bool Ok, string? Error)> StopServerAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult((true, (string?)null));

        public Task<(bool Ok, string? Error)> RestartServerAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult((true, (string?)null));

        public Task<(bool Success, string? Error)> ReloadMcpAsync(string? workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult((true, (string?)null));

        public Task<(WorkspaceArsenalDto? Arsenal, string? Error)> GetArsenalAsync(string? workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult<(WorkspaceArsenalDto? Arsenal, string? Error)>((Arsenal, null));

        public Task<(ToolInvokeResponse? Response, string? Error)> InvokeToolAsync(ToolInvokeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<(ToolInvokeResponse? Response, string? Error)>((null, "not used"));

        public Task<(McpToolInvokeResponse? Response, string? Error)> InvokeDiagnosticMcpAsync(McpToolInvokeRequest request, CancellationToken cancellationToken)
        {

            DiagnosticInvokeCalled = true;

            if (DiagnosticError is not null)
            {

                return Task.FromResult<(McpToolInvokeResponse? Response, string? Error)>((null, DiagnosticError));

            }

            return Task.FromResult<(McpToolInvokeResponse? Response, string? Error)>((DiagnosticResponse, null));

        }

    }

    private sealed class ConfirmingDialogService : IConfirmationDialogService
    {

        private readonly bool _confirm;

        public ConfirmingDialogService(bool confirm)
        {

            _confirm = confirm;

        }

        public bool WasCalled { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken, bool confirmIsDefault = true)
        {

            WasCalled = true;

            return Task.FromResult(_confirm);

        }

    }

    private sealed class FixedAnswerTextInput : ITextInputDialogService
    {

        private readonly string? _answer;

        public FixedAnswerTextInput(string? answer)
        {

            _answer = answer;

        }

        public Task<string?> PromptAsync(string title, string prompt, string? defaultValue, CancellationToken cancellationToken) =>
            Task.FromResult(_answer);

    }

}

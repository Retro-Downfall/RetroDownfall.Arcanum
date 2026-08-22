using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.AuditBrowser;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class AuditBrowserViewModelTests
{

    private static readonly Guid SessionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task RefreshInference_PopulatesRecords()
    {

        InferenceAuditRecord record = new(
            DateTimeOffset.UtcNow.ToString("O"),
            SessionId.ToString("D"),
            "chat",
            "gpt-test",
            "openai",
            10,
            20,
            30,
            100,
            1,
            ["tool_a"],
            null,
            "stop",
            null,
            "spell-a",
            null);

        FakeAuditBrowserDataSource dataSource = new()
        {
            InferenceResult = new DataSourceResult<InferenceAuditRecord[]>([record], true, null, null),
        };

        AuditBrowserViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshInferenceAsync(CancellationToken.None);

        Assert.Single(viewModel.InferenceRecords);

        Assert.Equal("gpt-test", viewModel.InferenceRecords[0].Model);

        Assert.False(viewModel.HasNoInferenceRecords);

        Assert.Contains("1 inference", viewModel.StatusText, StringComparison.Ordinal);

    }

    [Fact]
    public async Task RefreshInference_Empty_SurfacesHonestEmptyState()
    {

        FakeAuditBrowserDataSource dataSource = new()
        {
            InferenceResult = new DataSourceResult<InferenceAuditRecord[]>([], true, null, null),
        };

        AuditBrowserViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshInferenceAsync(CancellationToken.None);

        Assert.True(viewModel.HasNoInferenceRecords);

        Assert.Equal(AuditBrowserViewModel.InferenceEmptyMessageText, viewModel.StatusText);

    }

    [Fact]
    public async Task RefreshGuardrails_PopulatesRecordsAndNeverImpliesRawPii()
    {

        GuardrailAuditRecord record = new(
            DateTimeOffset.UtcNow.ToString("O"),
            SessionId.ToString("D"),
            "input",
            "pii",
            "[REDACTED]",
            "gpt-test");

        FakeAuditBrowserDataSource dataSource = new()
        {
            GuardrailsResult = new DataSourceResult<GuardrailAuditRecord[]>([record], true, null, null),
        };

        AuditBrowserViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshGuardrailsAsync(CancellationToken.None);

        Assert.Single(viewModel.GuardrailRecords);

        Assert.Equal("[REDACTED]", viewModel.GuardrailRecords[0].MatchedTextRedacted);

        Assert.Contains("redacted", viewModel.GuardrailsRedactionNote, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("raw PII is available", viewModel.GuardrailsRedactionNote, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task RefreshGuardrails_Empty_SurfacesHonestEmptyState()
    {

        FakeAuditBrowserDataSource dataSource = new()
        {
            GuardrailsResult = new DataSourceResult<GuardrailAuditRecord[]>([], true, null, null),
        };

        AuditBrowserViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshGuardrailsAsync(CancellationToken.None);

        Assert.Equal(AuditBrowserViewModel.GuardrailsEmptyMessageText, viewModel.StatusText);

    }

    [Fact]
    public void OpenInferenceSession_OpensTome()
    {

        InferenceAuditRecord record = new(
            DateTimeOffset.UtcNow.ToString("O"),
            SessionId.ToString("D"),
            "chat",
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            [],
            null,
            null,
            null,
            null,
            null);

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        AuditBrowserViewModel viewModel = NewViewModel(new FakeAuditBrowserDataSource(), navigation);

        viewModel.OpenInferenceSessionCommand.Execute(record);

        Assert.Equal((DocumentKind.Session, SessionId.ToString("D")), opened);

    }

    [Fact]
    public async Task ExportInferenceJson_WritesFile()
    {

        string temp = Path.Combine(Path.GetTempPath(), $"audit-export-{Guid.NewGuid():N}.json");

        try
        {

            InferenceAuditRecord record = new(
                DateTimeOffset.UtcNow.ToString("O"),
                null,
                "chat",
                "m",
                "p",
                1,
                2,
                3,
                4,
                0,
                [],
                null,
                null,
                null,
                null,
                null);

            FakeAuditBrowserDataSource dataSource = new()
            {
                InferenceResult = new DataSourceResult<InferenceAuditRecord[]>([record], true, null, null),
            };

            ControllableFileDialog fileDialog = new(temp);

            AuditBrowserViewModel viewModel = NewViewModel(dataSource, fileDialog: fileDialog);

            await viewModel.RefreshInferenceAsync(CancellationToken.None);

            await viewModel.ExportInferenceJsonAsync(CancellationToken.None);

            Assert.True(File.Exists(temp));

            Assert.Contains("inference record", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);

        }

        finally
        {

            if (File.Exists(temp))
            {

                File.Delete(temp);

            }

        }

    }

    [Fact]
    public async Task ExportInferenceJson_SnapshotsRecordsAfterMutationAdmission()
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            $"audit-admitted-export-{Guid.NewGuid():N}.json");

        InferenceAuditRecord record = new(
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "chat",
            "before-admission",
            "provider",
            1,
            2,
            3,
            4,
            0,
            [],
            null,
            null,
            null,
            null,
            null);

        FakeAuditBrowserDataSource dataSource = new()
        {
            InferenceResult = new DataSourceResult<InferenceAuditRecord[]>(
                [record],
                true,
                null,
                null),
        };

        AuditBrowserViewModel? viewModel = null;

        try
        {

            viewModel = NewViewModel(
                dataSource,
                fileDialog: new ControllableFileDialog(path),
                mutationRunner: new BeforeMutationTheForgeLocalMutationRunner(
                    () => viewModel!.InferenceRecords.Clear()));

            await viewModel.RefreshInferenceAsync(CancellationToken.None);

            await viewModel.ExportInferenceJsonAsync(CancellationToken.None);

            InferenceAuditRecord[]? written = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(path),
                TheForgeJsonContext.Default.InferenceAuditRecordArray);

            Assert.Empty(written!);

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

    [Fact]
    public async Task CopyDisabledPaths_Inference_CopiesPaths()
    {

        FakeClipboardService clipboard = new();

        AuditBrowserViewModel viewModel = NewViewModel(new FakeAuditBrowserDataSource(), clipboard: clipboard);

        await viewModel.CopyDisabledPathsCommand.ExecuteAsync("InferenceAudit");

        Assert.Equal(
            DisabledSettingPaths.JoinForClipboard(DisabledSettingPaths.InferenceAudit),
            clipboard.LastText);

    }

    private static AuditBrowserViewModel NewViewModel(
        FakeAuditBrowserDataSource dataSource,
        NavigationService? navigation = null,
        ControllableFileDialog? fileDialog = null,
        FakeClipboardService? clipboard = null,
        ITheForgeLocalMutationRunner? mutationRunner = null) =>
        new(
            dataSource,
            navigation ?? new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()),
            fileDialog ?? new ControllableFileDialog(null),
            clipboard ?? new FakeClipboardService(),
            new FakeWhispersService(),
            mutationRunner ?? ImmediateTheForgeLocalMutationRunner.Instance);

    private sealed class FakeAuditBrowserDataSource : IAuditBrowserDataSource
    {

        public DataSourceResult<InferenceAuditRecord[]> InferenceResult { get; set; } =
            new([], true, null, null);

        public DataSourceResult<GuardrailAuditRecord[]> GuardrailsResult { get; set; } =
            new([], true, null, null);

        public Task<DataSourceResult<InferenceAuditRecord[]>> QueryInferenceAsync(DateTimeOffset? from, DateTimeOffset? to, string? model, string? sessionId, int? limit, CancellationToken cancellationToken) =>
            Task.FromResult(InferenceResult);

        public Task<DataSourceResult<GuardrailAuditRecord[]>> QueryGuardrailsAsync(DateTimeOffset? from, DateTimeOffset? to, string? stage, string? violationType, string? sessionId, int? limit, CancellationToken cancellationToken) =>
            Task.FromResult(GuardrailsResult);

    }

    private sealed class ControllableFileDialog(string? path) : IArtifactFileDialogService
    {

        public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
            Task.FromResult(path);

    }

}

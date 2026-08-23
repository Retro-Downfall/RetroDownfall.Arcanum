using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

[Collection(TheForgeProcessEnvironmentCollection.Name)]
public class ArtifactImportExportHelperTests
{

    [Fact]
    public async Task WriteAndReadJson_RoundTripsCampaignExport()
    {

        CampaignDto campaign = new(
            Guid.NewGuid(),
            "RoundTrip",
            "/campaigns/roundtrip",
            RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        CampaignExportDto export = new(campaign, [], []);

        string path = Path.Combine(Path.GetTempPath(), $"forge-helper-{Guid.NewGuid():N}.json");

        try
        {

            await ArtifactImportExportHelper.WriteJsonAsync(
                ImmediateTheForgeLocalMutationRunner.Instance,
                path,
                export,
                TheForgeJsonContext.Default.CampaignExportDto,
                CancellationToken.None);

            (CampaignExportDto? roundTrip, string? error) = await ArtifactImportExportHelper.ReadJsonAsync(
                path,
                TheForgeJsonContext.Default.CampaignExportDto,
                CancellationToken.None);

            Assert.Null(error);

            Assert.NotNull(roundTrip);

            Assert.Equal(campaign.Id, roundTrip!.Campaign.Id);

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
    public async Task PickSavePathOrNull_WhenCancelled_ReturnsNull()
    {

        ControllableFileDialog dialog = new(null);

        string? path = await ArtifactImportExportHelper.PickSavePathOrNullAsync(
            dialog,
            "x.json",
            CancellationToken.None);

        Assert.Null(path);

    }

    [Fact]
    public async Task WriteJsonAsync_SnapshotsTheValueAfterMutationAdmission()
    {

        CampaignDto campaign = new(
            Guid.NewGuid(),
            "Admission",
            "/campaigns/admission",
            RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        List<CampaignExportSpellDto> spells = [];

        CampaignExportDto export = new(campaign, spells, []);

        string path = Path.Combine(
            Path.GetTempPath(),
            $"forge-admitted-export-{Guid.NewGuid():N}.json");

        try
        {

            await ArtifactImportExportHelper.WriteJsonAsync(
                new BeforeMutationRunner(
                    () => spells.Add(new CampaignExportSpellDto(
                        "admitted",
                        null,
                        "# admitted",
                        []))),
                path,
                export,
                TheForgeJsonContext.Default.CampaignExportDto,
                CancellationToken.None);

            (CampaignExportDto? roundTrip, string? error) =
                await ArtifactImportExportHelper.ReadJsonAsync(
                    path,
                    TheForgeJsonContext.Default.CampaignExportDto,
                    CancellationToken.None);

            Assert.Null(error);

            CampaignExportSpellDto written = Assert.Single(roundTrip!.Spells);

            Assert.Equal("admitted", written.Name);

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

    [Theory]
    [InlineData(false, ErrorCodes.Data.FileLocked)]
    [InlineData(true, ErrorCodes.Data.ControlPathUnavailable)]
    public async Task Managed_root_export_refusal_returns_error_without_creating_a_file(
        bool unsafeDisposition,
        string expectedCode)
    {

        using TheForgeTestHomeScope home = new("forge-artifact-refusal");

        string managedRoot = ArcanumPaths.GrimoireDirectory;

        string path = Path.Combine(managedRoot, "exports", "campaign.json");

        Error error = new(expectedCode, "refused for test");

        RecordingBoundary boundary = new(error, unsafeDisposition);

        TheForgeLocalMutationRunner runner = new(boundary);

        CampaignDto campaign = new(
            Guid.NewGuid(),
            "Refused",
            "/campaigns/refused",
            RetroDownfall.Arcanum.Core.Workspaces.WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        string? writeError = await ArtifactImportExportHelper.WriteJsonAsync(
            runner,
            path,
            new CampaignExportDto(campaign, [], []),
            TheForgeJsonContext.Default.CampaignExportDto,
            CancellationToken.None);

        Assert.Contains(expectedCode, writeError, StringComparison.Ordinal);

        Assert.Equal(1, boundary.CallCount);

        Assert.False(File.Exists(path));

        Assert.False(Directory.Exists(managedRoot));

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

    private sealed class BeforeMutationRunner(Action beforeMutation) : ITheForgeLocalMutationRunner
    {

        public Task RunAsync(
            string path,
            Func<CancellationToken, Task> mutation,
            CancellationToken cancellationToken = default)
        {

            beforeMutation();

            return mutation(cancellationToken);

        }

    }

    private sealed class RecordingBoundary(
        Error error,
        bool unsafeDisposition) : IArcanumClientMutationBoundary
    {

        public int CallCount { get; private set; }

        public Task<ArcanumClientMutationResult<T>> RunAsync<T>(
            Func<T> mutation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ArcanumClientMutationResult<T>> RunAsync<T>(
            Func<CancellationToken, Task<T>> mutation,
            CancellationToken cancellationToken = default)
        {

            CallCount++;

            ArcanumClientMutationResult<T> result = unsafeDisposition
                ? ArcanumClientMutationResult<T>.Unsafe(error)
                : ArcanumClientMutationResult<T>.Blocked(error);

            return Task.FromResult(result);

        }

    }

}

public class SpellPromptImportFlowTests
{

    [Fact]
    public async Task SpellImport_WhenCancelled_IsNoOp()
    {

        FakeSpellImportDataSource dataSource = new();

        SpellEditorViewModel viewModel = new(
            "heal",
            dataSource,
            new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()),
            new AlwaysCancelConfirmation(),
            new ControllableFileDialog(null),
            new AlwaysNullTextInput(),
            new FakeWhispersService(),
            ImmediateTheForgeLocalMutationRunner.Instance);

        await viewModel.ImportAsync(CancellationToken.None);

        Assert.False(dataSource.ImportCalled);

    }

    [Fact]
    public async Task SpellImport_WhenNameCollision_SurfacesErrorCode()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-spell-import-{Guid.NewGuid():N}.json");

        SpellExportDto payload = new(null, "# heal", []);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, TheForgeJsonContext.Default.SpellExportDto));

        try
        {

            FakeSpellImportDataSource dataSource = new()
            {
                ImportResult = new DataSourceResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary>(
                    null,
                    false,
                    ErrorCodes.Spell.NameCollision,
                    "A spell with that name already exists."),
            };

            FakeWhispersService whispers = new();

            SpellEditorViewModel viewModel = new(
                "heal",
                dataSource,
                new NavigationService(),
                new FoundryFloorViewModel(new NullLogService()),
                new AlwaysCancelConfirmation(),
                new ControllableFileDialog(path),
                new AlwaysNullTextInput(),
                whispers,
                ImmediateTheForgeLocalMutationRunner.Instance);

            await viewModel.ImportAsync(CancellationToken.None);

            Assert.True(dataSource.ImportCalled);

            Assert.Contains("Spell.NameCollision", viewModel.LastError, StringComparison.Ordinal);

            Assert.Contains(whispers.Calls, static c => c.Message.Contains("collision", StringComparison.OrdinalIgnoreCase));

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
    public async Task PromptImport_WhenDuplicateVersion_SurfacesErrorCode()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-prompt-import-{Guid.NewGuid():N}.json");

        PromptExportDto payload = new(
            "greeting",
            "v1",
            null,
            [],
            "Hello",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, TheForgeJsonContext.Default.PromptExportDto));

        try
        {

            FakePromptImportDataSource dataSource = new()
            {
                ImportResult = new DataSourceResult<PromptSummaryDto>(
                    null,
                    false,
                    ErrorCodes.Prompt.DuplicateVersion,
                    "A prompt with this name and version already exists."),
            };

            FakeWhispersService whispers = new();

            ScriptoriumViewModel viewModel = new(
                Guid.NewGuid(),
                dataSource,
                new NavigationService(),
                new FoundryFloorViewModel(new NullLogService()),
                new AlwaysCancelConfirmation(),
                new ControllableFileDialog(path),
                new AlwaysNullTextInput(),
                whispers,
                ImmediateTheForgeLocalMutationRunner.Instance);

            await viewModel.ImportAsync(CancellationToken.None);

            Assert.True(dataSource.ImportCalled);

            Assert.Contains("Prompt.DuplicateVersion", viewModel.LastError, StringComparison.Ordinal);

            Assert.Contains(whispers.Calls, static c => c.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

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

    private sealed class AlwaysCancelConfirmation : IConfirmationDialogService
    {

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken,
            bool confirmIsDefault = true) =>
            Task.FromResult(false);

    }

    private sealed class AlwaysNullTextInput : ITextInputDialogService
    {

        public Task<string?> PromptAsync(string title, string label, string? defaultValue, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

    }

    private sealed class FakeSpellImportDataSource : ISpellEditorDataSource
    {

        public bool ImportCalled { get; private set; }

        public DataSourceResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary> ImportResult { get; init; } =
            new(null, false, "test", "not used");

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellDetail?> LoadSpellAsync(string name, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellDetail?>(null);

        public Task<IReadOnlyList<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto>> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto>>([]);

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDetailDto?> GetVersionDetailAsync(string name, string version, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDetailDto?>(null);

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto?> CreateVersionAsync(string name, RetroDownfall.Arcanum.Core.Intelligence.Spells.CreateSpellVersionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto?>(null);

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto?> UpdateVersionAsync(string name, string version, RetroDownfall.Arcanum.Core.Intelligence.Spells.UpdateSpellVersionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto?>(null);

        public Task<bool> SaveAsync(string name, RetroDownfall.Arcanum.Core.Intelligence.Spells.UpdateSpellRequest request, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellCastResult?> CastAsync(string name, RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellCastRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellCastResult?>(null);

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Models.ManaCountResult?> EstimateManaAsync(RetroDownfall.Arcanum.Core.Intelligence.Models.ManaCountRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Models.ManaCountResult?>(null);

        public async IAsyncEnumerable<RetroDownfall.Arcanum.Core.Intelligence.Models.IntelligenceEvent> ExecuteStreamAsync(
            string name,
            RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellExecuteRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {

            await Task.CompletedTask;

            yield break;

        }

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto?> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellVersionDto?>(null);

        public Task<SpellValidationResultDto?> ValidateAsync(string name, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<SpellValidationResultDto?>(null);

        public Task<SpellExportDto?> ExportAsync(string name, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<SpellExportDto?>(null);

        public Task<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary?> CloneAsync(string name, RetroDownfall.Arcanum.Core.Intelligence.Spells.CloneSpellRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary?>(null);

        public Task<DataSourceResult<RetroDownfall.Arcanum.Core.Intelligence.Spells.SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken cancellationToken)
        {

            ImportCalled = true;

            return Task.FromResult(ImportResult);

        }

        public Task<DeleteOutcome> DeleteAsync(string name, string workspace, CancellationToken cancellationToken) =>
            Task.FromResult(DeleteOutcome.Fail("Http.404", "not used"));

        public Task<IReadOnlyList<string>> ListSpellNamesAsync(string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListAvailableToolNamesAsync(string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

    }

    private sealed class FakePromptImportDataSource : IPromptEditorDataSource
    {

        public bool ImportCalled { get; private set; }

        public DataSourceResult<PromptSummaryDto> ImportResult { get; init; } =
            new(null, false, "test", "not used");

        public Task<PromptDetailDto?> LoadPromptAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PromptDetailDto?>(null);

        public Task<PromptDetailDto?> SaveAsync(Guid id, UpdatePromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptDetailDto?>(null);

        public Task<PromptRenderResultDto?> RenderAsync(Guid id, PromptRenderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptRenderResultDto?>(null);

        public Task<PromptTestResultDto?> TestAsync(Guid id, TestPromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptTestResultDto?>(null);

        public async IAsyncEnumerable<RetroDownfall.Arcanum.Core.Intelligence.Models.IntelligenceEvent> ExecuteStreamAsync(
            Guid id,
            PromptExecuteRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {

            await Task.CompletedTask;

            yield break;

        }

        public Task<IReadOnlyList<PromptVersionDto>> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PromptVersionDto>>([]);

        public Task<PromptDetailDto?> CloneAsync(Guid id, ClonePromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptDetailDto?>(null);

        public Task<PromptExportDto?> ExportAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PromptExportDto?>(null);

        public Task<DataSourceResult<PromptSummaryDto>> ImportAsync(PromptImportRequest request, CancellationToken cancellationToken)
        {

            ImportCalled = true;

            return Task.FromResult(ImportResult);

        }

        public Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(DeleteOutcome.Fail("Http.404", "not used"));

    }

}

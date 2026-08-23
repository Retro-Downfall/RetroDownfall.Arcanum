using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class CampaignManagementFlowTests
{

    [Fact]
    public async Task NewCampaignCommand_BuildsRegisterRequestAndRefreshes()
    {

        const string campaignPath = "/campaigns/winterhold";

        FakeCampaignManagementDataSource management = new()
        {
            CreateResult = DataSourceResult<CampaignDto>.FromResponse(
                new ApiResponse<CampaignDto>(NewCampaignDto("Winterhold"), true, null)),
        };

        FakeCampaignDialogService dialog = new()
        {
            NewInputs = new NewCampaignInputs("Winterhold", campaignPath, WorkspaceType.Campaign, "North hold"),
        };

        RecordingWhispers whispers = new();

        FoundryFloorViewModel floor = new(new NullLogService());

        FakeActiveCampaignService active = new();

        int refreshCount = 0;

        CampaignCommandCoordinator coordinator = NewCoordinator(management, dialog, whispers, floor, active);

        coordinator.FocusCampaignInAtelierAsync = async (_, _) =>
        {
            refreshCount++;
            await Task.CompletedTask;
        };

        CampaignsRootNodeViewModel root = new(
            coordinator,
            async _ => { refreshCount++; await Task.CompletedTask; });

        Assert.True(root.HasNewCampaign);

        await root.NewCampaignCommand!.ExecuteAsync(null);

        Assert.NotNull(management.LastCreateRequest);

        Assert.Equal("Winterhold", management.LastCreateRequest!.Name);

        Assert.Equal(Path.GetFullPath(campaignPath), management.LastCreateRequest.Path);

        Assert.Equal(WorkspaceType.Campaign, management.LastCreateRequest.Type);

        Assert.Equal("North hold", management.LastCreateRequest.Description);

        Assert.True(refreshCount >= 1);

        Assert.Contains(whispers.Messages, static m => m.Contains("Campaign created", StringComparison.Ordinal));

    }

    [Fact]
    public async Task NewCampaignCommand_WhenDialogCancelled_IsNoOp()
    {

        FakeCampaignManagementDataSource management = new();

        FakeCampaignDialogService dialog = new() { NewInputs = null };

        CampaignCommandCoordinator coordinator = NewCoordinator(management, dialog);

        CampaignsRootNodeViewModel root = new(coordinator, static _ => Task.CompletedTask);

        await root.NewCampaignCommand!.ExecuteAsync(null);

        Assert.Null(management.LastCreateRequest);

    }

    [Fact]
    public async Task NewCampaignCommand_WhenInvalidPath_SurfacesErrorOnFloorAndWhispers()
    {

        FakeCampaignManagementDataSource management = new()
        {
            CreateResult = new DataSourceResult<CampaignDto>(
                null,
                false,
                ErrorCodes.Campaign.InvalidPath,
                "The campaign path is invalid."),
        };

        FakeCampaignDialogService dialog = new()
        {
            NewInputs = new NewCampaignInputs("Broken", "/nope", WorkspaceType.Campaign, null),
        };

        RecordingWhispers whispers = new();

        FoundryFloorViewModel floor = new(new NullLogService());

        CampaignCommandCoordinator coordinator = NewCoordinator(management, dialog, whispers, floor);

        await coordinator.NewCampaignAsync(CancellationToken.None);

        Assert.Contains(floor.Lines, static line => line.Contains("Campaign.InvalidPath", StringComparison.Ordinal));

        Assert.Contains(whispers.Messages, static m => m.Contains("Campaign create failed", StringComparison.Ordinal));

    }

    [Fact]
    public async Task EditCampaignCommand_BuildsUpdateRequestWithoutSettings()
    {

        CampaignDto campaign = NewCampaignDto("Autumnfall");

        FakeCampaignManagementDataSource management = new()
        {
            UpdateResult = DataSourceResult<CampaignDto>.FromResponse(
                new ApiResponse<CampaignDto>(campaign with { Name = "Autumnfall Renamed", Description = "Updated" }, true, null)),
        };

        FakeCampaignDialogService dialog = new()
        {
            EditInputs = new EditCampaignInputs("Autumnfall Renamed", WorkspaceType.Campaign, "Updated"),
        };

        CampaignNodeViewModel node = NewCampaignNode(campaign, management, dialog);

        await node.EditCampaignCommand!.ExecuteAsync(null);

        Assert.NotNull(management.LastUpdateRequest);

        Assert.Equal("Autumnfall Renamed", management.LastUpdateRequest!.Name);

        Assert.Equal(WorkspaceType.Campaign, management.LastUpdateRequest.Type);

        Assert.Equal("Updated", management.LastUpdateRequest.Description);

        Assert.Null(management.LastUpdateRequest.Settings);

        Assert.Equal("Autumnfall Renamed", node.Label);

    }

    [Fact]
    public async Task DeleteCampaignCommand_WhenCancelled_DoesNotCallApi()
    {

        FakeCampaignManagementDataSource management = new();

        FakeConfirmationDialogService confirmation = new() { NextResult = false };

        CampaignNodeViewModel node = NewCampaignNode(
            NewCampaignDto("Autumnfall"),
            management,
            new FakeCampaignDialogService(),
            confirmation: confirmation);

        await node.DeleteCampaignCommand!.ExecuteAsync(null);

        Assert.False(management.DeleteCalled);

        Assert.Contains("unregister", confirmation.LastMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task DeleteCampaignCommand_WhenAccepted_UnregistersAndRefreshes()
    {

        FakeCampaignManagementDataSource management = new()
        {
            DeleteResult = new DataSourceResult<bool>(true, true, null, null),
        };

        FakeConfirmationDialogService confirmation = new() { NextResult = true };

        int refreshCount = 0;

        CampaignNodeViewModel node = NewCampaignNode(
            NewCampaignDto("Autumnfall"),
            management,
            new FakeCampaignDialogService(),
            confirmation: confirmation,
            refreshCampaigns: async _ => { refreshCount++; await Task.CompletedTask; });

        await node.DeleteCampaignCommand!.ExecuteAsync(null);

        Assert.True(management.DeleteCalled);

        Assert.Equal(1, refreshCount);

        Assert.False(confirmation.ConfirmWasDefault);

    }

    [Fact]
    public async Task ExportCampaignCommand_WritesJsonViaSourceGen()
    {

        CampaignDto campaign = NewCampaignDto("Autumnfall");

        CampaignExportDto export = new(campaign, [], []);

        FakeCampaignManagementDataSource management = new()
        {
            ExportResult = DataSourceResult<CampaignExportDto>.FromResponse(
                new ApiResponse<CampaignExportDto>(export, true, null)),
        };

        string path = Path.Combine(Path.GetTempPath(), $"forge-campaign-export-{Guid.NewGuid():N}.json");

        try
        {

            CampaignNodeViewModel node = NewCampaignNode(
                campaign,
                management,
                new FakeCampaignDialogService(),
                fileDialog: new ControllableFileDialog(path));

            await node.ExportCampaignCommand!.ExecuteAsync(null);

            Assert.True(File.Exists(path));

            string json = await File.ReadAllTextAsync(path);

            CampaignExportDto? roundTrip = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.CampaignExportDto);

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
    public async Task ExportCampaignCommand_WhenCancelled_IsNoOp()
    {

        FakeCampaignManagementDataSource management = new();

        CampaignNodeViewModel node = NewCampaignNode(
            NewCampaignDto("Autumnfall"),
            management,
            new FakeCampaignDialogService(),
            fileDialog: new ControllableFileDialog(null));

        await node.ExportCampaignCommand!.ExecuteAsync(null);

        Assert.False(management.ExportCalled);

    }

    [Fact]
    public async Task ExportCampaignCommand_ClientMutationRefusal_DoesNotFetchServerExport()
    {

        FakeCampaignManagementDataSource management = new();

        string path = Path.Combine(
            RetroDownfall.Arcanum.Core.Storage.ArcanumPaths.GrimoireDirectory,
            "campaign-export.json");

        CampaignNodeViewModel node = NewCampaignNode(
            NewCampaignDto("Autumnfall"),
            management,
            new FakeCampaignDialogService(),
            fileDialog: new ControllableFileDialog(path),
            mutationRunner: new RefusingTheForgeLocalMutationRunner());

        await node.ExportCampaignCommand!.ExecuteAsync(null);

        Assert.False(management.ExportCalled);

        Assert.False(File.Exists(path));

    }

    [Fact]
    public async Task ImportCampaignCommand_ReadsJsonAndCallsImportWithStrategy()
    {

        CampaignDto campaign = NewCampaignDto("Autumnfall");

        CampaignExportDto payload = new(campaign, [], []);

        string path = Path.Combine(Path.GetTempPath(), $"forge-campaign-import-{Guid.NewGuid():N}.json");

        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(payload, TheForgeJsonContext.Default.CampaignExportDto));

        try
        {

            FakeCampaignManagementDataSource management = new()
            {
                ImportResult = DataSourceResult<CampaignImportResultDto>.FromResponse(
                    new ApiResponse<CampaignImportResultDto>(
                        new CampaignImportResultDto(1, 2, 3, ["warn-a"]),
                        true,
                        null)),
            };

            FakeCampaignDialogService dialog = new() { ImportStrategy = "replace" };

            RecordingWhispers whispers = new();

            FoundryFloorViewModel floor = new(new NullLogService());

            CampaignNodeViewModel node = new(
                campaign,
                new NullAtelierDataSource(),
                new NavigationService(),
                new FakeActiveCampaignService(),
                new NullArtifactCreationDataSource(),
                new NullArtifactCreationDialogService(),
                floor,
                management,
                dialog,
                new FakeConfirmationDialogService(),
                new ControllableFileDialog(path),
                whispers,
                ImmediateTheForgeLocalMutationRunner.Instance,
                static _ => Task.CompletedTask);

            await node.ImportCampaignCommand!.ExecuteAsync(null);

            Assert.Equal("replace", management.LastImportStrategy);

            Assert.NotNull(management.LastImportPayload);

            Assert.Contains(whispers.Messages, static m => m.Contains("imported", StringComparison.OrdinalIgnoreCase));

            Assert.Contains(floor.Lines, static line => line.Contains("warn-a", StringComparison.Ordinal));

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
    public async Task ImportCampaignCommand_WhenCancelled_IsNoOp()
    {

        FakeCampaignManagementDataSource management = new();

        FakeCampaignDialogService dialog = new() { ImportStrategy = null };

        CampaignNodeViewModel node = NewCampaignNode(
            NewCampaignDto("Autumnfall"),
            management,
            dialog,
            fileDialog: new ControllableFileDialog("/tmp/unused.json"));

        await node.ImportCampaignCommand!.ExecuteAsync(null);

        Assert.False(management.ImportCalled);

    }

    [Fact]
    public async Task PathNotAllowed_SurfacesOnCreate()
    {

        FakeCampaignManagementDataSource management = new()
        {
            CreateResult = new DataSourceResult<CampaignDto>(
                null,
                false,
                ErrorCodes.Campaign.PathNotAllowed,
                "Path is outside allowed roots."),
        };

        FakeCampaignDialogService dialog = new()
        {
            NewInputs = new NewCampaignInputs("X", "/forbidden", WorkspaceType.Campaign, null),
        };

        RecordingWhispers whispers = new();

        FoundryFloorViewModel floor = new(new NullLogService());

        CampaignCommandCoordinator coordinator = NewCoordinator(management, dialog, whispers, floor);

        await coordinator.NewCampaignAsync(CancellationToken.None);

        Assert.Contains(
            floor.Lines,
            static line => line.Contains("Campaign.PathNotAllowed", StringComparison.Ordinal));

        Assert.Contains(
            whispers.Messages,
            static m => m.Contains("CampaignRoots", StringComparison.Ordinal));

    }

    private static CampaignCommandCoordinator NewCoordinator(
        FakeCampaignManagementDataSource management,
        FakeCampaignDialogService dialog,
        RecordingWhispers? whispers = null,
        FoundryFloorViewModel? floor = null,
        FakeActiveCampaignService? active = null)
    {

        floor ??= new FoundryFloorViewModel(new NullLogService());

        whispers ??= new RecordingWhispers();

        active ??= new FakeActiveCampaignService();

        return new CampaignCommandCoordinator(
            management,
            new NullAtelierDataSource(),
            dialog,
            new FakeConfirmationDialogService { NextResult = true },
            active,
            new NullArtifactCreationDataSource(),
            new NullArtifactCreationDialogService(),
            new NavigationService(),
            whispers,
            floor,
            new StaticTheForgeSettingsMonitor(new TheForgeSettings { BaseUrl = "http://127.0.0.1:5001" }),
            new AlwaysConnectedArcanumConnection());

    }

    private static CampaignNodeViewModel NewCampaignNode(
        CampaignDto campaign,
        FakeCampaignManagementDataSource management,
        FakeCampaignDialogService dialog,
        FakeConfirmationDialogService? confirmation = null,
        ControllableFileDialog? fileDialog = null,
        Func<CancellationToken, Task>? refreshCampaigns = null,
        ITheForgeLocalMutationRunner? mutationRunner = null) =>
        new(
            campaign,
            new NullAtelierDataSource(),
            new NavigationService(),
            new FakeActiveCampaignService(),
            new NullArtifactCreationDataSource(),
            new NullArtifactCreationDialogService(),
            new FoundryFloorViewModel(new NullLogService()),
            management,
            dialog,
            confirmation ?? new FakeConfirmationDialogService(),
            fileDialog ?? new ControllableFileDialog(null),
            new RecordingWhispers(),
            mutationRunner ?? ImmediateTheForgeLocalMutationRunner.Instance,
            refreshCampaigns ?? (static _ => Task.CompletedTask));

    private static CampaignDto NewCampaignDto(string name) =>
        new(
            Guid.NewGuid(),
            name,
            $"/campaigns/{name.ToLowerInvariant()}",
            WorkspaceType.Campaign,
            null,
            CampaignSettings.CreateDefault(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private sealed class AlwaysConnectedArcanumConnection : IArcanumConnection
    {

        public ConnectionState State => ConnectionState.Connected;

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode => null;

        public string? LastErrorMessage => null;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

    }

    private sealed class FakeCampaignManagementDataSource : ICampaignManagementDataSource
    {

        public DataSourceResult<CampaignDto> CreateResult { get; init; } =
            new(null, false, "test", "not configured");

        public DataSourceResult<CampaignDto> UpdateResult { get; init; } =
            new(null, false, "test", "not configured");

        public DataSourceResult<bool> DeleteResult { get; init; } =
            new(false, false, "test", "not configured");

        public DataSourceResult<CampaignExportDto> ExportResult { get; init; } =
            new(null, false, "test", "not configured");

        public DataSourceResult<CampaignImportResultDto> ImportResult { get; init; } =
            new(null, false, "test", "not configured");

        public RegisterCampaignRequest? LastCreateRequest { get; private set; }

        public UpdateCampaignRequest? LastUpdateRequest { get; private set; }

        public bool DeleteCalled { get; private set; }

        public bool ExportCalled { get; private set; }

        public bool ImportCalled { get; private set; }

        public string? LastImportStrategy { get; private set; }

        public CampaignExportDto? LastImportPayload { get; private set; }

        public Task<DataSourceResult<CampaignDto>> CreateAsync(RegisterCampaignRequest request, CancellationToken cancellationToken)
        {

            LastCreateRequest = request;

            return Task.FromResult(CreateResult);

        }

        public Task<DataSourceResult<CampaignDto>> UpdateAsync(Guid id, UpdateCampaignRequest request, CancellationToken cancellationToken)
        {

            LastUpdateRequest = request;

            return Task.FromResult(UpdateResult);

        }

        public Task<DataSourceResult<bool>> DeleteAsync(Guid id, CancellationToken cancellationToken)
        {

            DeleteCalled = true;

            return Task.FromResult(DeleteResult);

        }

        public Task<DataSourceResult<CampaignExportDto>> ExportAsync(Guid campaignId, CancellationToken cancellationToken)
        {

            ExportCalled = true;

            return Task.FromResult(ExportResult);

        }

        public Task<DataSourceResult<CampaignImportResultDto>> ImportAsync(
            Guid campaignId,
            string strategy,
            CampaignExportDto payload,
            CancellationToken cancellationToken)
        {

            ImportCalled = true;

            LastImportStrategy = strategy;

            LastImportPayload = payload;

            return Task.FromResult(ImportResult);

        }

    }

    private sealed class FakeCampaignDialogService : ICampaignDialogService
    {

        public NewCampaignInputs? NewInputs { get; init; }

        public EditCampaignInputs? EditInputs { get; init; }

        public string? ImportStrategy { get; init; }

        public Task<NewCampaignInputs?> PromptNewCampaignAsync(
            NewCampaignDialogOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NewInputs);

        public Task<string?> PromptOpenCampaignPathAsync(
            bool allowLocalFolderBrowse,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<EditCampaignInputs?> PromptEditCampaignAsync(CampaignDto existing, CancellationToken cancellationToken) =>
            Task.FromResult(EditInputs);

        public Task<string?> PromptImportStrategyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ImportStrategy);

    }

    private sealed class FakeConfirmationDialogService : IConfirmationDialogService
    {

        public bool NextResult { get; init; }

        public string? LastMessage { get; private set; }

        public bool ConfirmWasDefault { get; private set; } = true;

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken,
            bool confirmIsDefault = true)
        {

            LastMessage = message;

            ConfirmWasDefault = confirmIsDefault;

            return Task.FromResult(NextResult);

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

    private sealed class RecordingWhispers : IWhispersService
    {

        public List<string> Messages { get; } = [];

        public System.Collections.ObjectModel.ObservableCollection<WhisperNotification> Notifications { get; } = [];

        public void Show(WhisperSeverity severity, string message, string? title = null) => Messages.Add(message);

        public void Dismiss(Guid id)
        {
        }

        public void Clear()
        {
        }

        public void ExpireDue()
        {
        }

    }

}

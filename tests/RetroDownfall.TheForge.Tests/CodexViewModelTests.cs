using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class CodexViewModelTests
{
    private static readonly Guid CampaignId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task Load_Campaign()
    {
        CodexContentDto dto = new("campaigns/foo/CODEX.md", "# Campaign Codex", true);

        FakeCodexDataSource dataSource = new()
        {
            CampaignGetResult = new DataSourceResult<CodexContentDto>(dto, true, null, null),
        };

        CodexViewModel viewModel = NewViewModel(CampaignId, dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("# Campaign Codex", viewModel.Content);

        Assert.True(viewModel.Exists);

        Assert.Equal(CampaignId, dataSource.LastCampaignId);
    }

    [Fact]
    public async Task Load_Global()
    {
        CodexContentDto dto = new("CODEX.md", "# Global Codex", true);

        FakeCodexDataSource dataSource = new()
        {
            GlobalGetResult = new DataSourceResult<CodexContentDto>(dto, true, null, null),
        };

        CodexViewModel viewModel = NewViewModel(null, dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("# Global Codex", viewModel.Content);

        Assert.True(viewModel.IsGlobal);

        Assert.True(dataSource.GlobalGetCalled);
    }

    [Fact]
    public async Task Load_ExistsFalse_EmptyEditor()
    {
        CodexContentDto dto = new("CODEX.md", string.Empty, false);

        FakeCodexDataSource dataSource = new()
        {
            GlobalGetResult = new DataSourceResult<CodexContentDto>(dto, true, null, null),
        };

        CodexViewModel viewModel = NewViewModel(null, dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(string.Empty, viewModel.Content);

        Assert.False(viewModel.Exists);

        Assert.Contains("empty editor ready", viewModel.StatusText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_CallsPut()
    {
        CodexContentDto saved = new("CODEX.md", "# Saved", true);

        FakeCodexDataSource dataSource = new()
        {
            GlobalGetResult = new(new CodexContentDto("CODEX.md", string.Empty, false), true, null, null),

            GlobalPutResult = new DataSourceResult<CodexContentDto>(saved, true, null, null),
        };

        CodexViewModel viewModel = NewViewModel(null, dataSource);

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Content = "# Saved";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal("# Saved", dataSource.LastPutContent);

        Assert.True(dataSource.GlobalPutCalled);

        Assert.Equal("Saved.", viewModel.StatusText);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_load_blocks_save_until_successful_reload(bool campaign, bool previouslyLoaded)
    {
        CodexContentDto dto = new("CODEX.md", "original guidance", true);

        FakeCodexDataSource dataSource = new()
        {
            CampaignGetResult = new(dto, true, null, null),
            GlobalGetResult = new(dto, true, null, null),
        };

        CodexViewModel viewModel = NewViewModel(campaign ? CampaignId : null, dataSource);

        if (previouslyLoaded)
        {
            await viewModel.LoadAsync(CancellationToken.None);
        }

        dataSource.CampaignGetResult = new(null, false, "Codex.InvalidEncoding", "CODEX is not UTF-8.");

        dataSource.GlobalGetResult = dataSource.CampaignGetResult;

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Content = "replacement guidance";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Null(dataSource.LastPutContent);

        Assert.Equal(0, dataSource.PutCount);

        Assert.NotEqual("Loaded.", viewModel.StatusText);

        dataSource.CampaignGetResult = new(dto, true, null, null);

        dataSource.GlobalGetResult = dataSource.CampaignGetResult;

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Content = "safely edited guidance";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal(1, dataSource.PutCount);

        Assert.Equal("safely edited guidance", dataSource.LastPutContent);
    }

    [Fact]
    public async Task Delete_CallsDelete()
    {
        FakeCodexDataSource dataSource = new()
        {
            CampaignDeleteResult = new DataSourceResult<bool>(true, true, null, null),
        };

        CodexViewModel viewModel = NewViewModel(
            CampaignId,
            dataSource,
            confirmation: new ScriptedConfirmationDialogService(confirm: true));

        await viewModel.DeleteAsync(CancellationToken.None);

        Assert.Equal(CampaignId, dataSource.LastCampaignId);

        Assert.True(dataSource.CampaignDeleteCalled);

        Assert.Equal(string.Empty, viewModel.Content);

        Assert.False(viewModel.Exists);
    }

    [Fact]
    public async Task Delete_WhenDeclined_DoesNotDelete()
    {
        FakeCodexDataSource dataSource = new()
        {
            CampaignDeleteResult = new DataSourceResult<bool>(true, true, null, null),
        };

        ScriptedConfirmationDialogService confirmation = new(confirm: false);

        CodexViewModel viewModel = NewViewModel(CampaignId, dataSource, confirmation: confirmation);

        viewModel.Content = "# Hand-written campaign codex";

        // Deleting a whole CODEX.md destroys the operator's authored guidance; it must be confirmed.
        await viewModel.DeleteAsync(CancellationToken.None);

        Assert.False(dataSource.CampaignDeleteCalled);

        Assert.Single(confirmation.Prompts);

        Assert.Equal("# Hand-written campaign codex", viewModel.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_http_load_sends_no_put_until_successful_reload(bool campaign)
    {
        using CodexHandler handler = new();

        ArcanumApiClient client = new(
            new CodexHttpClientFactory(handler),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings { BaseUrl = "http://localhost:5001" }),
            new CodexApiKeyProvider(),
            NullLogger<ArcanumApiClient>.Instance);

        CodexViewModel viewModel = new(
            campaign ? CampaignId : null,
            new CodexDataSource(new CampaignService(client)),
            new FoundryFloorViewModel(new NullLogService()),
            new ScriptedConfirmationDialogService(confirm: true));

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Content = "replacement guidance";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal(0, handler.PutCount);

        Assert.Equal("CODEX is not UTF-8.", viewModel.LastError);

        handler.Readable = true;

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.Content = "safe edit";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.Equal(1, handler.PutCount);

        Assert.Equal("Saved.", viewModel.StatusText);
    }

    private sealed class CodexApiKeyProvider : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");

        public Task PersistPastedKeyAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }
    }

    private sealed class CodexHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CodexHandler : HttpMessageHandler
    {
        public bool Readable { get; set; }

        public int PutCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                PutCount++;
            }

            bool success = Readable || request.Method == HttpMethod.Put;

            return Task.FromResult(new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = new StringContent(success
                    ? """{"isSuccess":true,"data":{"path":"CODEX.md","content":"original guidance","exists":true}}"""
                    : """{"isSuccess":false,"error":{"code":"Codex.InvalidEncoding","message":"CODEX is not UTF-8."}}"""),
            });
        }
    }

    private static CodexViewModel NewViewModel(
        Guid? campaignId,
        FakeCodexDataSource dataSource,
        IConfirmationDialogService? confirmation = null) =>
        new(
            campaignId,
            dataSource,
            new FoundryFloorViewModel(new NullLogService()),
            confirmation ?? new ScriptedConfirmationDialogService(confirm: true));

    private sealed class FakeCodexDataSource : ICodexDataSource
    {
        public DataSourceResult<CodexContentDto> CampaignGetResult { get; set; } =
            new(null, true, null, null);

        public DataSourceResult<CodexContentDto> CampaignPutResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<bool> CampaignDeleteResult { get; init; } =
            new(true, true, null, null);

        public DataSourceResult<CodexContentDto> GlobalGetResult { get; set; } =
            new(null, true, null, null);

        public DataSourceResult<CodexContentDto> GlobalPutResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<bool> GlobalDeleteResult { get; init; } =
            new(true, true, null, null);

        public Guid? LastCampaignId { get; private set; }

        public string? LastPutContent { get; private set; }

        public int PutCount { get; private set; }

        public bool GlobalGetCalled { get; private set; }

        public bool GlobalPutCalled { get; private set; }

        public bool CampaignDeleteCalled { get; private set; }

        public Task<DataSourceResult<CodexContentDto>> GetCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            LastCampaignId = campaignId;

            return Task.FromResult(CampaignGetResult);
        }

        public Task<DataSourceResult<CodexContentDto>> PutCampaignCodexAsync(Guid campaignId, string content, CancellationToken cancellationToken)
        {
            LastCampaignId = campaignId;

            LastPutContent = content;

            PutCount++;

            return Task.FromResult(CampaignPutResult);
        }

        public Task<DataSourceResult<bool>> DeleteCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            LastCampaignId = campaignId;

            CampaignDeleteCalled = true;

            return Task.FromResult(CampaignDeleteResult);
        }

        public Task<DataSourceResult<CodexContentDto>> GetGlobalCodexAsync(CancellationToken cancellationToken)
        {
            GlobalGetCalled = true;

            return Task.FromResult(GlobalGetResult);
        }

        public Task<DataSourceResult<CodexContentDto>> PutGlobalCodexAsync(string content, CancellationToken cancellationToken)
        {
            GlobalPutCalled = true;

            LastPutContent = content;

            PutCount++;

            return Task.FromResult(GlobalPutResult);
        }

        public Task<DataSourceResult<bool>> DeleteGlobalCodexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(GlobalDeleteResult);
    }
}

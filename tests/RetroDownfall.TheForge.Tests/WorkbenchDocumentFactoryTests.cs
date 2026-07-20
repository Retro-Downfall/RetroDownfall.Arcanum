using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class WorkbenchDocumentFactoryTests
{

    [Fact]
    public void Create_PromptWithGuid_ReturnsScriptorium()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        Guid promptId = Guid.NewGuid();

        ViewModelBase doc = factory.Create(DocumentKind.Prompt, promptId.ToString());

        ScriptoriumViewModel scriptorium = Assert.IsType<ScriptoriumViewModel>(doc);

        Assert.Equal(DocumentKind.Prompt, scriptorium.Kind);

        Assert.Equal($"Scriptorium: {promptId:D}", scriptorium.Title);

        scriptorium.Dispose();

    }

    [Fact]
    public void Create_PromptWithNonGuid_ReturnsPlaceholder()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        ViewModelBase doc = factory.Create(DocumentKind.Prompt, "not-a-guid");

        Assert.IsType<WorkbenchDocumentPlaceholderViewModel>(doc);

    }

    [Fact]
    public void Create_Spell_ReturnsSpellEditor()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        ViewModelBase doc = factory.Create(DocumentKind.Spell, "greater-heal");

        SpellEditorViewModel editor = Assert.IsType<SpellEditorViewModel>(doc);

        Assert.Equal(DocumentKind.Spell, editor.Kind);

        Assert.Equal("Spell: greater-heal", editor.Title);

    }

    [Fact]
    public void Create_SessionWithGuid_ReturnsTome()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        Guid sessionId = Guid.NewGuid();

        ViewModelBase doc = factory.Create(DocumentKind.Session, sessionId.ToString());

        TomeViewModel tome = Assert.IsType<TomeViewModel>(doc);

        Assert.Equal(DocumentKind.Session, tome.Kind);

        tome.Dispose();

    }

    [Fact]
    public void Create_CodexWithCampaignGuid_ReturnsCodexViewModel()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        Guid campaignId = Guid.NewGuid();

        ViewModelBase doc = factory.Create(DocumentKind.Codex, campaignId.ToString("D"));

        CodexViewModel codex = Assert.IsType<CodexViewModel>(doc);

        Assert.Equal(DocumentKind.Codex, codex.Kind);

        Assert.Equal(campaignId, codex.CampaignId);

    }

    [Fact]
    public void Create_CodexGlobal_ReturnsGlobalCodexViewModel()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        ViewModelBase doc = factory.Create(DocumentKind.Codex, "global");

        CodexViewModel codex = Assert.IsType<CodexViewModel>(doc);

        Assert.Equal(DocumentKind.Codex, codex.Kind);

        Assert.Null(codex.CampaignId);

        Assert.True(codex.IsGlobal);

    }

    [Fact]
    public void Create_CodexWithInvalidId_ReturnsPlaceholder()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        ViewModelBase doc = factory.Create(DocumentKind.Codex, "not-a-guid");

        Assert.IsType<WorkbenchDocumentPlaceholderViewModel>(doc);

    }

    [Fact]
    public void Create_MarkdownWithPayload_ReturnsMarkdownDocument()
    {

        MarkdownDocumentContentStore store = new();

        store.Put("ws:1:readme.md", "readme.md", "# Hello");

        WorkbenchDocumentFactory factory = NewFactory(store);

        ViewModelBase doc = factory.Create(DocumentKind.Markdown, "ws:1:readme.md");

        MarkdownDocumentViewModel markdown = Assert.IsType<MarkdownDocumentViewModel>(doc);

        Assert.Equal(DocumentKind.Markdown, markdown.Kind);

        Assert.Equal("readme.md", markdown.Title);

        Assert.Equal("# Hello", markdown.MarkdownSource);

        Assert.Equal(MarkdownViewMode.Preview, markdown.ViewMode);

        markdown.Dispose();

    }

    [Fact]
    public void Create_MarkdownWithoutPayload_ReturnsPlaceholder()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        ViewModelBase doc = factory.Create(DocumentKind.Markdown, "ws:missing");

        WorkbenchDocumentPlaceholderViewModel placeholder = Assert.IsType<WorkbenchDocumentPlaceholderViewModel>(doc);

        Assert.Contains("no longer available", placeholder.EmptyState, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Create_Trial_ReturnsProvingGroundsViewModel()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        ViewModelBase doc = factory.Create(DocumentKind.Trial, ProvingGroundsViewModel.SingletonDocumentId);

        ProvingGroundsViewModel provingGrounds = Assert.IsType<ProvingGroundsViewModel>(doc);

        Assert.Equal(DocumentKind.Trial, provingGrounds.Kind);

        Assert.Equal("Proving Grounds", provingGrounds.Title);

        provingGrounds.Dispose();

    }

    [Fact]
    public void Create_Comparison_ReturnsComparisonWorkbench()
    {

        WorkbenchDocumentFactory factory = NewFactory();

        ViewModelBase doc = factory.Create(DocumentKind.Comparison, ComparisonWorkbenchViewModel.SingletonDocumentId);

        ComparisonWorkbenchViewModel comparison = Assert.IsType<ComparisonWorkbenchViewModel>(doc);

        Assert.Equal(DocumentKind.Comparison, comparison.Kind);

        Assert.Equal("Comparison Workbench", comparison.Title);

        comparison.Dispose();

    }

    private static WorkbenchDocumentFactory NewFactory(IMarkdownDocumentContentStore? store = null)
    {

        FoundryFloorViewModel foundryFloor = new(new NullLogService());

        NavigationService navigation = new();

        return new WorkbenchDocumentFactory(
            new NullSpellEditorDataSource(),
            new NullPromptEditorDataSource(),
            new NullTomeDataSource(),
            new NullCodexDataSource(),
            new NullTrialDataSource(),
            new InMemoryTrialSuiteStore(),
            new InMemoryComparisonRunStore(),
            new NullComparisonWorkbenchDataSource(),
            new InMemoryInferenceTraceStore(),
            store ?? new MarkdownDocumentContentStore(),
            navigation,
            foundryFloor,
            new NullConfirmationDialogService(),
            new NullArtifactFileDialogService(),
            new NullTextInputDialogService(),
            new FakeClipboardService(),
            new FakeWhispersService());

    }

}

internal sealed class NullTrialDataSource : ITrialDataSource
{

    public Task<DataSourceResult<RetroDownfall.Arcanum.Core.ProvingGrounds.TrialResult>> RunAsync(
        RetroDownfall.Arcanum.Core.ProvingGrounds.Trial trial,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<RetroDownfall.Arcanum.Core.ProvingGrounds.TrialResult>(null, true, null, null));

    public Task<DataSourceResult<IReadOnlyList<string>>> ListSpellNamesAsync(
        string? workspace,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<IReadOnlyList<string>>([], true, null, null));

    public Task<DataSourceResult<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.PromptSummaryDto>>> ListPromptsAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<IReadOnlyList<RetroDownfall.Arcanum.Core.TheForge.PromptSummaryDto>>([], true, null, null));

}

internal sealed class NullArtifactFileDialogService : IArtifactFileDialogService
{

    public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
    public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);


}

internal sealed class NullTextInputDialogService : ITextInputDialogService
{

    public Task<string?> PromptAsync(string title, string label, string? defaultValue, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

}

internal sealed class NullCodexDataSource : ICodexDataSource
{

    public Task<DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>> GetCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>(null, true, null, null));

    public Task<DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>> PutCampaignCodexAsync(Guid campaignId, string content, CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>(null, true, null, null));

    public Task<DataSourceResult<bool>> DeleteCampaignCodexAsync(Guid campaignId, CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

    public Task<DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>> GetGlobalCodexAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>(null, true, null, null));

    public Task<DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>> PutGlobalCodexAsync(string content, CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<RetroDownfall.Arcanum.Core.TheForge.CodexContentDto>(null, true, null, null));

    public Task<DataSourceResult<bool>> DeleteGlobalCodexAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

}

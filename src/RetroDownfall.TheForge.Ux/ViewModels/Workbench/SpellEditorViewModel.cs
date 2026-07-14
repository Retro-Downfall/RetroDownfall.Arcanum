using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Workbench editor for a Spell. Phase 5 loads and edits the spell's assembled body/metadata,
/// supports Save/Cast dry-run/Mana estimate/version activation, and streams Execute events through
/// the spell-specific NDJSON endpoint. The Tome UI that renders live execution transcripts arrives
/// in Phase 6; for now the editor records streamed events and opens the bound session tab when a
/// <c>sessionBound</c> frame arrives.
/// </summary>
public sealed partial class SpellEditorViewModel : ViewModelBase
{

    private readonly ISpellEditorDataSource _dataSource;

    private readonly INavigationService _navigation;

    [ObservableProperty]
    private SpellDetail? _spell;

    [ObservableProperty]
    private string _markdownBody = string.Empty;

    [ObservableProperty]
    private string _frontmatter = string.Empty;

    [ObservableProperty]
    private string _skillJson = string.Empty;

    [ObservableProperty]
    private SpellCastResult? _castPreview;

    [ObservableProperty]
    private int? _manaCount;

    [ObservableProperty]
    private string _executionPrompt = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private SpellVersionDto? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(IsPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsSplitterVisible))]
    private MarkdownViewMode _viewMode = MarkdownViewMode.Source;

    [ObservableProperty]
    private bool _loadRemoteImages;

    [ObservableProperty]
    private bool _syncScrollEnabled = true;

    public SpellEditorViewModel(string spellName, ISpellEditorDataSource dataSource, INavigationService navigation, string? workspace = null)
    {

        SpellName = spellName;

        Workspace = workspace;

        _dataSource = dataSource;

        _navigation = navigation;

        Title = $"Spell: {spellName}";

    }

    public override DocumentKind? Kind => DocumentKind.Spell;

    public string SpellName { get; }

    public string? Workspace { get; }

    public ObservableCollection<SpellVersionDto> Versions { get; } = [];

    public ObservableCollection<IntelligenceEvent> ExecutionEvents { get; } = [];

    public bool IsSourceVisible => MarkdownViewModeHelper.IsSourceVisible(ViewMode);

    public bool IsPreviewVisible => MarkdownViewModeHelper.IsPreviewVisible(ViewMode);

    public bool IsSplitterVisible => MarkdownViewModeHelper.IsSplitterVisible(ViewMode);

    public string ScriptsSummary => Spell is null || Spell.Tools.Length == 0
        ? "No attuned tools yet."
        : string.Join(Environment.NewLine, Spell.Tools.Select(static tool => $"- {tool}"));

    [RelayCommand]
    private void SetViewMode(MarkdownViewMode mode) => ViewMode = mode;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        try
        {

            Spell = await _dataSource.LoadSpellAsync(SpellName, Workspace, cancellationToken).ConfigureAwait(true);

            MarkdownBody = Spell?.Body ?? Spell?.SystemPrompt ?? string.Empty;

            Frontmatter = BuildFrontmatter(Spell);

            SkillJson = BuildSkillJson(Spell);

            await LoadVersionsAsync(cancellationToken).ConfigureAwait(true);

            OnPropertyChanged(nameof(ScriptsSummary));

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {

        if (Spell is null)
        {

            return;

        }

        UpdateSpellRequest request = new(
            Spell.Description,
            Spell.Tags,
            MarkdownBody,
            Spell.Template,
            Spell.Model,
            Spell.Provider,
            Spell.Tools,
            Spell.RequiredMcpServers,
            Spell.Version,
            Spell.InputSchema,
            Spell.OutputSchema,
            Spell.DeclaredTools,
            Spell.Dependencies);

        if (await _dataSource.SaveAsync(Spell.Name, request, cancellationToken).ConfigureAwait(true))
        {

            Spell = Spell with { SystemPrompt = MarkdownBody, Body = MarkdownBody };

        }

    }

    [RelayCommand]
    public async Task CastAsync(CancellationToken cancellationToken)
    {

        SpellCastRequest request = new(Workspace, SessionId: null, CampaignId: null);

        CastPreview = await _dataSource.CastAsync(SpellName, request, cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task EstimateManaAsync(CancellationToken cancellationToken)
    {

        string prompt = string.IsNullOrWhiteSpace(MarkdownBody) ? Spell?.SystemPrompt ?? string.Empty : MarkdownBody;

        ManaCountResult? result = await _dataSource
            .EstimateManaAsync(new ManaCountRequest(Prompt: prompt, Model: Spell?.Model, Tools: true), cancellationToken)
            .ConfigureAwait(true);

        ManaCount = result?.ManaCount;

    }

    [RelayCommand]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {

        string prompt = string.IsNullOrWhiteSpace(ExecutionPrompt) ? MarkdownBody : ExecutionPrompt;

        SpellExecuteRequest request = new(
            Prompt: prompt,
            Model: Spell?.Model,
            Workspace: Workspace ?? Spell?.WorkingDirectory);

        ExecutionEvents.Clear();

        await foreach (IntelligenceEvent ev in _dataSource.ExecuteStreamAsync(SpellName, request, cancellationToken).ConfigureAwait(true))
        {

            ExecutionEvents.Add(ev);

            if (ev.Type == IntelligenceEventType.SessionBound && Guid.TryParse(ev.Message, out Guid sessionId))
            {

                _navigation.OpenDocument(DocumentKind.Session, sessionId.ToString());

            }

        }

    }

    [RelayCommand]
    public async Task ActivateVersionAsync(SpellVersionDto? version, CancellationToken cancellationToken)
    {

        if (version is null)
        {

            return;

        }

        if (await _dataSource.ActivateVersionAsync(SpellName, version.Version, Workspace, cancellationToken).ConfigureAwait(true))
        {

            await LoadAsync(cancellationToken).ConfigureAwait(true);

        }

    }

    private async Task LoadVersionsAsync(CancellationToken cancellationToken)
    {

        Versions.Clear();

        foreach (SpellVersionDto version in await _dataSource.ListVersionsAsync(SpellName, Workspace, cancellationToken).ConfigureAwait(true))
        {

            Versions.Add(version);

        }

    }

    private static string BuildFrontmatter(SpellDetail? spell)
    {

        if (spell is null)
        {

            return string.Empty;

        }

        return string.Join(
            Environment.NewLine,
            $"Name: {spell.Name}",
            $"Description: {spell.Description ?? string.Empty}",
            $"Source: {spell.Source}",
            $"Model: {spell.Model ?? string.Empty}",
            $"Provider: {spell.Provider ?? string.Empty}",
            $"Tags: {string.Join(", ", spell.Tags)}");

    }

    private static string BuildSkillJson(SpellDetail? spell)
    {

        if (spell is null)
        {

            return "{}";

        }

        string toolJson = string.Join(", ", spell.Tools.Select(static tool => $"\"{tool}\""));

        string dependencyJson = string.Join(", ", (spell.Dependencies ?? []).Select(static dependency => $"\"{dependency}\""));

        return $$"""
{
  "name": "{{spell.Name}}",
  "tags": [{{string.Join(", ", spell.Tags.Select(static tag => $"\"{tag}\""))}}],
  "tools": [{{toolJson}}],
  "dependencies": [{{dependencyJson}}]
}
""";

    }

}

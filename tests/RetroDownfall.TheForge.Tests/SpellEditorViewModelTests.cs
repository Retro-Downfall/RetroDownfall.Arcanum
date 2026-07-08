using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class SpellEditorViewModelTests
{

    [Fact]
    public async Task LoadAsync_PopulatesSpellBodyAndMetadata()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            Versions = [new SpellVersionDto("v1", true, DateTimeOffset.UtcNow, "Initial")],
        };

        SpellEditorViewModel viewModel = new("mend-armor", dataSource, new NavigationService());

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("mend-armor", viewModel.Spell?.Name);

        Assert.Equal("# Mend Armor", viewModel.MarkdownBody);

        Assert.Contains("Description: Repairs armor", viewModel.Frontmatter);

        Assert.Contains("repair", viewModel.SkillJson);

        Assert.Single(viewModel.Versions);

    }

    [Fact]
    public async Task SaveAsync_SendsUpdateRequestFromEditedState()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
        };

        SpellEditorViewModel viewModel = new("mend-armor", dataSource, new NavigationService());

        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.MarkdownBody = "# Updated Mend Armor";

        await viewModel.SaveAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastUpdateRequest);

        Assert.Equal("# Updated Mend Armor", dataSource.LastUpdateRequest.SystemPrompt);

        Assert.NotNull(dataSource.LastUpdateRequest.Tags);

        Assert.Equal(["repair", "armor"], dataSource.LastUpdateRequest.Tags);

    }

    [Fact]
    public async Task CastAsync_StoresDryRunPreview()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            CastResult = new SpellCastResult("mend-armor", "Repairs armor", "system prompt", ["smithing"], ["tool-a"], ["script.sh"], "codex"),
        };

        SpellEditorViewModel viewModel = new("mend-armor", dataSource, new NavigationService());

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.CastAsync(CancellationToken.None);

        Assert.NotNull(viewModel.CastPreview);

        Assert.Equal("system prompt", viewModel.CastPreview.SystemPrompt);

    }

    [Fact]
    public async Task EstimateManaAsync_StoresManaCount()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            ManaCount = new ManaCountResult(42, "o200k_base"),
        };

        SpellEditorViewModel viewModel = new("mend-armor", dataSource, new NavigationService());

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.EstimateManaAsync(CancellationToken.None);

        Assert.Equal(42, viewModel.ManaCount);

    }

    [Fact]
    public async Task ExecuteAsync_StreamsEventsAndOpensSessionWhenSessionBound()
    {

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id) => opened = (kind, id);

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            ExecutionEvents =
            [
                new IntelligenceEvent(IntelligenceEventType.Token, "", "hello"),
                new IntelligenceEvent(IntelligenceEventType.SessionBound, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa").ToString()),
            ],
        };

        SpellEditorViewModel viewModel = new("mend-armor", dataSource, navigation)
        {
            ExecutionPrompt = "repair this",
        };

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.ExecutionEvents.Count);

        Assert.Equal((DocumentKind.Session, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), opened);

        Assert.NotNull(dataSource.LastExecuteRequest);

        Assert.Equal("repair this", dataSource.LastExecuteRequest.Prompt);

    }

    [Fact]
    public async Task ActivateVersionAsync_SendsVersionAndReloads()
    {

        FakeSpellEditorDataSource dataSource = new()
        {
            Spell = NewSpellDetail("mend-armor"),
            Versions = [new SpellVersionDto("v2", false, DateTimeOffset.UtcNow, "Second")],
        };

        SpellEditorViewModel viewModel = new("mend-armor", dataSource, new NavigationService());

        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ActivateVersionAsync(viewModel.Versions.Single(), CancellationToken.None);

        Assert.Equal("v2", dataSource.ActivatedVersion);

        Assert.Equal(2, dataSource.LoadCallCount);

    }

    private static SpellDetail NewSpellDetail(string name) =>
        new(
            name,
            "Repairs armor",
            SpellSource.Workspace,
            ["repair", "armor"],
            "# Mend Armor",
            null,
            "# Mend Armor",
            "gpt-4o",
            "openai",
            ["tool-a"],
            [],
            "/tmp/workspace",
            "/tmp/workspace/SPELL.md");

    private sealed class FakeSpellEditorDataSource : ISpellEditorDataSource
    {

        public SpellDetail? Spell { get; init; }

        public IReadOnlyList<SpellVersionDto> Versions { get; init; } = [];

        public SpellCastResult? CastResult { get; init; }

        public ManaCountResult? ManaCount { get; init; }

        public IReadOnlyList<IntelligenceEvent> ExecutionEvents { get; init; } = [];

        public UpdateSpellRequest? LastUpdateRequest { get; private set; }

        public SpellExecuteRequest? LastExecuteRequest { get; private set; }

        public string? ActivatedVersion { get; private set; }

        public int LoadCallCount { get; private set; }

        public Task<SpellDetail?> LoadSpellAsync(string name, string? workspace, CancellationToken cancellationToken)
        {

            LoadCallCount++;

            return Task.FromResult(Spell);

        }

        public Task<IReadOnlyList<SpellVersionDto>> ListVersionsAsync(string name, string? workspace, CancellationToken cancellationToken) =>
            Task.FromResult(Versions);

        public Task<bool> SaveAsync(string name, UpdateSpellRequest request, CancellationToken cancellationToken)
        {

            LastUpdateRequest = request;

            return Task.FromResult(true);

        }

        public Task<SpellCastResult?> CastAsync(string name, SpellCastRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CastResult);

        public Task<ManaCountResult?> EstimateManaAsync(ManaCountRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ManaCount);

        public async IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(string name, SpellExecuteRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {

            LastExecuteRequest = request;

            foreach (IntelligenceEvent ev in ExecutionEvents)
            {

                yield return ev;

                await Task.Yield();

            }

        }

        public Task<bool> ActivateVersionAsync(string name, string version, string? workspace, CancellationToken cancellationToken)
        {

            ActivatedVersion = version;

            return Task.FromResult(true);

        }

    }

}

using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Diff;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ScriptoriumMirrorViewModelTests
{

    [Fact]
    public async Task RefreshMirrorDiff_ComparesPersistedSnapshotToVersionBody()
    {

        Guid currentId = Guid.NewGuid();

        Guid otherId = Guid.NewGuid();

        PromptDetailDto current = SamplePrompt(currentId, "v1", "Hello {{name}}");

        PromptDetailDto other = SamplePrompt(otherId, "v2", "Hi {{name}}");

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = current,
            Versions =
            [
                new PromptVersionDto(currentId, "v1", DateTimeOffset.UtcNow),
                new PromptVersionDto(otherId, "v2", DateTimeOffset.UtcNow),
            ],
            PromptsById =
            {
                [currentId] = current,
                [otherId] = other,
            },
        };

        ScriptoriumViewModel vm = Create(currentId, dataSource);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.ShowDirtyComparisonWarning);

        vm.Template = "Dirty buffer";

        Assert.True(vm.ShowDirtyComparisonWarning);

        vm.SelectedVersion = dataSource.Versions[1];

        await vm.RefreshMirrorDiffCommand.ExecuteAsync(null);

        Assert.NotNull(vm.MirrorComparedPrompt);

        Assert.Equal("v2", vm.MirrorComparedPrompt!.Version);

        Assert.Contains(vm.MirrorDiffLines, static l => l.Kind is LineDiffKind.Added or LineDiffKind.Removed);

        Assert.Contains("no activate-prompt", vm.MirrorStatusText, StringComparison.OrdinalIgnoreCase);

        vm.Dispose();

    }

    [Fact]
    public async Task RefreshMirrorDiff_RapidVersionSelection_KeepsOnlyTheLastSelectionsDiff()
    {

        Guid currentId = Guid.NewGuid();

        Guid alphaId = Guid.NewGuid();

        Guid betaId = Guid.NewGuid();

        PromptDetailDto current = SamplePrompt(currentId, "v1", "Hello {{name}}");

        PromptDetailDto alpha = SamplePrompt(alphaId, "v2", "AAA-unique-alpha");

        PromptDetailDto beta = SamplePrompt(betaId, "v3", "BBB-unique-beta");

        TaskCompletionSource alphaGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource betaGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakePromptEditorDataSource dataSource = new()
        {
            Prompt = current,
            Versions =
            [
                new PromptVersionDto(currentId, "v1", DateTimeOffset.UtcNow),
                new PromptVersionDto(alphaId, "v2", DateTimeOffset.UtcNow),
                new PromptVersionDto(betaId, "v3", DateTimeOffset.UtcNow),
            ],
            PromptsById =
            {
                [currentId] = current,
                [alphaId] = alpha,
                [betaId] = beta,
            },
        };

        ScriptoriumViewModel vm = Create(currentId, dataSource);

        await vm.LoadCommand.ExecuteAsync(null);

        dataSource.Gates[alphaId] = alphaGate.Task;

        dataSource.Gates[betaId] = betaGate.Task;

        // Two selections inside one round trip — the operator arrowing down the version list.
        vm.SelectedVersion = dataSource.Versions[1];

        vm.SelectedVersion = dataSource.Versions[2];

        alphaGate.SetResult();

        betaGate.SetResult();

        // Wait for the WHOLE post-condition, not just the version. The view model publishes
        // MirrorComparedPrompt and MirrorDiffLines in separate steps, so a loop that stops at the
        // version can observe the beta prompt with the alpha diff still rendered and fail spuriously
        // under load.
        await WaitUntilAsync(
            () => vm.MirrorComparedPrompt?.Version == "v3"
                && vm.MirrorDiffLines.Any(static l => l.Text.Contains("BBB-unique-beta", StringComparison.Ordinal))
                && !vm.MirrorDiffLines.Any(static l => l.Text.Contains("AAA-unique-alpha", StringComparison.Ordinal)),
            "the beta selection's diff to replace the alpha selection's");

        Assert.Equal("v3", vm.MirrorComparedPrompt!.Version);

        Assert.Contains(vm.MirrorDiffLines, static l => l.Text.Contains("BBB-unique-beta", StringComparison.Ordinal));

        Assert.DoesNotContain(vm.MirrorDiffLines, static l => l.Text.Contains("AAA-unique-alpha", StringComparison.Ordinal));

        vm.Dispose();

    }

    /// <summary>
    /// Polls until <paramref name="settled"/> holds, then returns. Fails with a description of what
    /// was being waited for rather than letting a later assertion report a confusing intermediate
    /// state. The generous ceiling is a deadlock guard, not a timing assumption: the happy path exits
    /// on the first satisfied poll.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> settled, string description)
    {

        for (int attempt = 0; attempt < 500; attempt++)
        {

            if (settled())
            {

                return;

            }

            await Task.Delay(10);

        }

        Assert.Fail($"Timed out after 5s waiting for {description}.");

    }

    private static ScriptoriumViewModel Create(Guid id, IPromptEditorDataSource dataSource) =>
        new(
            id,
            dataSource,
            new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()),
            new NullConfirmationDialogService(),
            new NullArtifactFileDialogService(),
            new NullTextInputDialogService(),
            new FakeWhispersService(),
            ImmediateTheForgeLocalMutationRunner.Instance);

    private static PromptDetailDto SamplePrompt(Guid id, string version, string template) =>
        new(
            Id: id,
            CampaignId: null,
            Name: "greeting",
            Version: version,
            Description: "Say hello",
            Tags: ["social"],
            Template: template,
            ParameterSchema: null,
            DefaultParameters: null,
            Model: "gpt-4o",
            Provider: "openai",
            Temperature: 0.7,
            TopP: 0.9,
            MaxOutputTokens: 512,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class FakePromptEditorDataSource : IPromptEditorDataSource
    {

        public PromptDetailDto? Prompt { get; init; }

        public IReadOnlyList<PromptVersionDto> Versions { get; init; } = [];

        public Dictionary<Guid, PromptDetailDto> PromptsById { get; } = [];

        /// <summary>Per-version gates so mirror fetches can be held in flight and released in order.</summary>
        public Dictionary<Guid, Task> Gates { get; } = [];

        public async Task<PromptDetailDto?> LoadPromptAsync(Guid id, CancellationToken cancellationToken)
        {

            if (Gates.TryGetValue(id, out Task? gate))
            {

                await gate.ConfigureAwait(false);

            }

            return PromptsById.TryGetValue(id, out PromptDetailDto? detail) ? detail : Prompt;

        }

        public Task<PromptDetailDto?> SaveAsync(Guid id, UpdatePromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Prompt);

        public Task<PromptRenderResultDto?> RenderAsync(Guid id, PromptRenderRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptRenderResultDto?>(null);

        public Task<PromptTestResultDto?> TestAsync(Guid id, TestPromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptTestResultDto?>(null);

        public async IAsyncEnumerable<IntelligenceEvent> ExecuteStreamAsync(
            Guid id,
            PromptExecuteRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {

            await Task.CompletedTask;

            yield break;

        }

        public Task<IReadOnlyList<PromptVersionDto>> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken) =>
            Task.FromResult(Versions);

        public Task<PromptDetailDto?> CloneAsync(Guid id, ClonePromptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PromptDetailDto?>(null);

        public Task<PromptExportDto?> ExportAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<PromptExportDto?>(null);

        public Task<DataSourceResult<PromptSummaryDto>> ImportAsync(PromptImportRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<PromptSummaryDto>(null, false, "test", "unused"));

        public Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(DeleteOutcome.Fail("Http.404", "not used"));

    }

}

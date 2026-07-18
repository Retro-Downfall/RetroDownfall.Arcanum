using RetroDownfall.TheForge.Core.Services;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ProvingGroundsViewModelTests
{

    [Fact]
    public void TryBuildTrial_SpellTarget_BuildsExpectedRequest()
    {

        ProvingGroundsViewModel vm = NewViewModel();

        vm.TargetKind = TrialTargetKind.Spell;

        vm.Target = "greater-heal";

        vm.Workspace = "/ws/a";

        vm.Model = "fast";

        vm.AddVariableCommand.Execute(null);

        vm.Variables[0].Key = "target";

        vm.Variables[0].Value = "ally";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.Inquisitors[0].Pattern = "healed";

        Assert.True(vm.TryBuildTrial(out Trial? trial, out string? error));

        Assert.Null(error);

        Assert.NotNull(trial);

        Assert.Equal(TrialTargetKind.Spell, trial.TargetKind);

        Assert.Equal("greater-heal", trial.Target);

        Assert.Equal("/ws/a", trial.Workspace);

        Assert.Equal("fast", trial.Model);

        Assert.Equal("ally", trial.Variables!["target"]);

        Assert.IsType<RegexInquisitor>(Assert.Single(trial.Inquisitors));

        vm.Dispose();

    }

    [Fact]
    public void TryBuildTrial_PromptAndApprenticeGoal_BuildExpectedTargets()
    {

        ProvingGroundsViewModel vm = NewViewModel();

        Guid promptId = Guid.NewGuid();

        vm.TargetKind = TrialTargetKind.Prompt;

        vm.Target = promptId.ToString("D");

        vm.AddSemanticInquisitorCommand.Execute(null);

        vm.Inquisitors[0].Question = "Is it helpful?";

        vm.Inquisitors[0].ExpectedAnswer = false;

        Assert.True(vm.TryBuildTrial(out Trial? promptTrial, out _));

        Assert.Equal(TrialTargetKind.Prompt, promptTrial!.TargetKind);

        Assert.Equal(promptId.ToString("D"), promptTrial.Target);

        SemanticInquisitor semantic = Assert.IsType<SemanticInquisitor>(Assert.Single(promptTrial.Inquisitors));

        Assert.False(semantic.ExpectedAnswer);

        vm.TargetKind = TrialTargetKind.ApprenticeGoal;

        vm.Target = "Build a REST API";

        Assert.True(vm.TryBuildTrial(out Trial? goalTrial, out _));

        Assert.Equal(TrialTargetKind.ApprenticeGoal, goalTrial!.TargetKind);

        Assert.Equal("Build a REST API", goalTrial.Target);

        vm.Dispose();

    }

    [Fact]
    public void Variables_AddRemove_AndEmptyKeyBlocksRun()
    {

        FakeTrialDataSource dataSource = new();

        ProvingGroundsViewModel vm = NewViewModel(dataSource);

        vm.Target = "goal";

        vm.TargetKind = TrialTargetKind.ApprenticeGoal;

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.Inquisitors[0].Pattern = "ok";

        vm.AddVariableCommand.Execute(null);

        Assert.Single(vm.Variables);

        vm.Variables[0].Key = " ";

        vm.Variables[0].Value = "x";

        Assert.False(vm.TryBuildTrial(out _, out string? error));

        Assert.Equal("Variable keys must be non-empty.", error);

        vm.SelectedVariable = vm.Variables[0];

        vm.RemoveVariableCommand.Execute(null);

        Assert.Empty(vm.Variables);

        Assert.True(vm.TryBuildTrial(out _, out _));

        vm.Dispose();

    }

    [Fact]
    public void Inquisitors_AddEditRemove_AndInvalidBlocksRun()
    {

        ProvingGroundsViewModel vm = NewViewModel();

        vm.TargetKind = TrialTargetKind.ApprenticeGoal;

        vm.Target = "goal";

        vm.AddRegexInquisitorCommand.Execute(null);

        Assert.False(vm.TryBuildTrial(out _, out string? emptyRegex));

        Assert.Equal("Regex Inquisitor requires a pattern.", emptyRegex);

        vm.Inquisitors[0].Pattern = "ok";

        vm.AddJsonSchemaInquisitorCommand.Execute(null);

        vm.Inquisitors[1].SchemaJson = "{not-json";

        Assert.False(vm.TryBuildTrial(out _, out string? badSchema));

        Assert.StartsWith("Invalid JSON schema:", badSchema);

        vm.SelectedInquisitor = vm.Inquisitors[1];

        vm.RemoveInquisitorCommand.Execute(null);

        Assert.Single(vm.Inquisitors);

        vm.AddSemanticInquisitorCommand.Execute(null);

        vm.Inquisitors[1].Question = "Was the plan structured?";

        Assert.True(vm.TryBuildTrial(out Trial? trial, out _));

        Assert.Equal(2, trial!.Inquisitors.Count);

        vm.Dispose();

    }

    [Fact]
    public async Task Run_Passed_SetsResultAndWhispersSuccess()
    {

        TrialResult result = new(
            "t",
            TrialTargetKind.ApprenticeGoal,
            "goal",
            true,
            "output text",
            [new InquisitorVerdict("regex", "r", true, "matched")],
            1,
            1,
            null);

        FakeTrialDataSource dataSource = new()
        {
            RunResult = new DataSourceResult<TrialResult>(result, true, null, null),
        };

        FakeWhispersService whispers = new();

        ProvingGroundsViewModel vm = NewViewModel(dataSource, whispers: whispers);

        vm.TargetKind = TrialTargetKind.ApprenticeGoal;

        vm.Target = "goal";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.Inquisitors[0].Pattern = "output";

        await vm.RunAsync(CancellationToken.None);

        Assert.NotNull(vm.LastResult);

        Assert.True(vm.LastResult.Passed);

        Assert.Contains("Passed", vm.ResultSummary, StringComparison.Ordinal);

        Assert.Contains(whispers.Calls, c => c.Severity == WhisperSeverity.Success);

        Assert.NotNull(dataSource.LastTrial);

        vm.Dispose();

    }

    [Fact]
    public async Task Run_Failed_WhispersWarning()
    {

        TrialResult result = new(
            "t",
            TrialTargetKind.Spell,
            "heal",
            false,
            "nope",
            [new InquisitorVerdict("regex", null, false, "no match")],
            0,
            1,
            null);

        FakeTrialDataSource dataSource = new()
        {
            RunResult = new DataSourceResult<TrialResult>(result, true, null, null),
        };

        FakeWhispersService whispers = new();

        ProvingGroundsViewModel vm = NewViewModel(dataSource, whispers: whispers);

        vm.Target = "heal";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.Inquisitors[0].Pattern = "yes";

        await vm.RunAsync(CancellationToken.None);

        Assert.False(vm.LastResult!.Passed);

        Assert.Contains(whispers.Calls, c => c.Severity == WhisperSeverity.Warning && c.Message == "Trial failed.");

        vm.Dispose();

    }

    [Fact]
    public async Task Run_ApiError_WhispersError()
    {

        FakeTrialDataSource dataSource = new()
        {
            RunResult = new DataSourceResult<TrialResult>(
                null,
                false,
                "ProvingGrounds.InferenceFailed",
                "boom"),
        };

        FakeWhispersService whispers = new();

        ProvingGroundsViewModel vm = NewViewModel(dataSource, whispers: whispers);

        vm.TargetKind = TrialTargetKind.ApprenticeGoal;

        vm.Target = "goal";

        vm.AddRegexInquisitorCommand.Execute(null);

        vm.Inquisitors[0].Pattern = "x";

        await vm.RunAsync(CancellationToken.None);

        Assert.Null(vm.LastResult);

        Assert.Equal("boom", vm.LastError);

        Assert.Contains(whispers.Calls, c => c.Severity == WhisperSeverity.Error);

        vm.Dispose();

    }

    [Fact]
    public void ApplyPrefill_BlockedWhenDirty_SucceedsWhenClean()
    {

        FakeWhispersService whispers = new();

        ProvingGroundsViewModel vm = NewViewModel(whispers: whispers);

        Assert.True(vm.ApplyPrefill(TrialTargetKind.Spell, "heal", "/ws", "fast", null));

        Assert.Equal("heal", vm.Target);

        Assert.False(vm.IsDirty);

        vm.Target = "changed";

        Assert.True(vm.IsDirty);

        Assert.False(vm.ApplyPrefill(TrialTargetKind.Prompt, Guid.NewGuid().ToString("D"), null, null, null));

        Assert.Equal("changed", vm.Target);

        Assert.Contains(
            whispers.Calls,
            c => c.Severity == WhisperSeverity.Warning
                 && c.Message == "Proving Grounds has unsaved draft changes.");

        vm.Dispose();

    }

    [Fact]
    public async Task Reset_RequiresConfirmation_ClearsDirtyDraft()
    {

        ConfirmingDialogService dialog = new(confirm: true);

        ProvingGroundsViewModel vm = NewViewModel(confirmation: dialog);

        vm.Target = "x";

        Assert.True(vm.IsDirty);

        await vm.ResetAsync(CancellationToken.None);

        Assert.Equal(string.Empty, vm.Target);

        Assert.False(vm.IsDirty);

        Assert.Equal(1, dialog.CallCount);

        vm.Dispose();

    }

    [Fact]
    public void JsonSchemaInquisitor_ValidSchema_BuildsJsonElement()
    {

        ProvingGroundsViewModel vm = NewViewModel();

        vm.TargetKind = TrialTargetKind.ApprenticeGoal;

        vm.Target = "goal";

        vm.AddJsonSchemaInquisitorCommand.Execute(null);

        vm.Inquisitors[0].SchemaJson = """{"type":"object","required":["name"]}""";

        Assert.True(vm.TryBuildTrial(out Trial? trial, out _));

        JsonSchemaInquisitor schema = Assert.IsType<JsonSchemaInquisitor>(Assert.Single(trial!.Inquisitors));

        Assert.Equal(JsonValueKind.Object, schema.Schema.ValueKind);

        vm.Dispose();

    }

    private static ProvingGroundsViewModel NewViewModel(
        FakeTrialDataSource? dataSource = null,
        FakeWhispersService? whispers = null,
        IConfirmationDialogService? confirmation = null)
    {

        return new ProvingGroundsViewModel(
            dataSource ?? new FakeTrialDataSource(),
            new FoundryFloorViewModel(new NullLogService()),
            whispers ?? new FakeWhispersService(),
            confirmation ?? new NullConfirmationDialogService(),
            new InMemoryTrialSuiteStore(),
            new NullArtifactFileDialogService());

    }

    private sealed class FakeTrialDataSource : ITrialDataSource
    {

        public DataSourceResult<TrialResult> RunResult { get; init; } =
            new(null, true, null, null);

        public Trial? LastTrial { get; private set; }

        public Task<DataSourceResult<TrialResult>> RunAsync(Trial trial, CancellationToken cancellationToken)
        {

            LastTrial = trial;

            return Task.FromResult(RunResult);

        }

        public Task<DataSourceResult<IReadOnlyList<string>>> ListSpellNamesAsync(
            string? workspace,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<IReadOnlyList<string>>([], true, null, null));

        public Task<DataSourceResult<IReadOnlyList<PromptSummaryDto>>> ListPromptsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<IReadOnlyList<PromptSummaryDto>>([], true, null, null));

    }

    private sealed class ConfirmingDialogService(bool confirm) : IConfirmationDialogService
    {

        public int CallCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken, bool confirmIsDefault = true)
        {

            CallCount++;

            return Task.FromResult(confirm);

        }

    }

}

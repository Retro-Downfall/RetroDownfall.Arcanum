using System.ComponentModel;
using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;

public sealed class TrialRunCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<TrialRunCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        TrialTargetKind? targetKind = settings.Target?.Trim().ToLowerInvariant() switch
        {
            "spell" => TrialTargetKind.Spell,
            "prompt" => TrialTargetKind.Prompt,
            "apprenticegoal" => TrialTargetKind.ApprenticeGoal,
            _ => null,
        };

        if (targetKind is null)
        {
            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("--target must be one of: spell, prompt, apprenticeGoal.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.TargetValue))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--target-value is required.")));

            return 1;
        }

        List<Inquisitor> inquisitors = new();

        foreach (string raw in settings.Inquisitor ?? [])
        {

            if (!CliArgReader.TryReadInlineOrFile(raw, out string json, out string? readError))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(readError!)));

                return 1;
            }

            Inquisitor? inquisitor;

            try
            {
                inquisitor = JsonSerializer.Deserialize(json, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.Inquisitor);
            }
            catch (JsonException ex)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid inquisitor JSON: {ex.Message}")));

                return 1;
            }

            if (inquisitor is null)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Inquisitor JSON parsed to null.")));

                return 1;
            }

            inquisitors.Add(inquisitor);

        }

        if (!CliArgReader.TryParseKeyValuePairs(settings.Var, out Dictionary<string, string> variables, out string? varError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(varError!)));

            return 1;
        }

        Trial trial = new(
            targetKind.Value,
            settings.TargetValue.Trim(),
            inquisitors,
            variables.Count == 0 ? null : variables,
            settings.Model,
            settings.Workspace,
            settings.Name);

        Result<TrialResult> result = await apiClient.RunTrialAsync(trial, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        WriteTrialResult(result.Value, themePalette);

        return result.Value.Passed ? 0 : 1;

    }

    private static void WriteTrialResult(TrialResult trial, IThemePalette themePalette)
    {

        string passedText = trial.Passed ? "\u2713 Passed" : "\u2717 Failed";

        Table summary = new();

        summary.Border(TableBorder.None);

        summary.HideHeaders();

        summary.AddColumn(new TableColumn(string.Empty).NoWrap());

        summary.AddColumn(new TableColumn(string.Empty));

        summary.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Trial:")),
            themePalette.HighlightMarkup(Markup.Escape(trial.TrialName)));

        summary.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Result:")),
            trial.Passed
                ? themePalette.HighlightMarkup(Markup.Escape(passedText))
                : themePalette.ErrorMarkup(Markup.Escape(passedText)));

        summary.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Inquisitors:")),
            themePalette.TextMarkup(Markup.Escape($"{trial.InquisitorsPassed}/{trial.InquisitorsTotal} passed")));

        Panel panel = new(summary)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("The Proving Grounds"))),
            Border = BoxBorder.Rounded,
            BorderStyle = trial.Passed ? themePalette.HighlightStyle() : themePalette.ErrorStyle(),
        };

        AnsiConsole.Write(panel);

        if (trial.Verdicts.Count > 0)
        {

            Table verdictsTable = new();

            verdictsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Kind")));

            verdictsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Passed")));

            verdictsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Detail")));

            foreach (InquisitorVerdict verdict in trial.Verdicts)
            {

                string verdictLabel = verdict.Label is null ? verdict.Kind : $"{verdict.Kind} ({verdict.Label})";

                AddVerdictRow(verdictsTable, themePalette, verdictLabel, verdict.Passed, verdict.Detail);

            }

            AnsiConsole.Write(verdictsTable);

        }

        const int outputPreviewChars = 500;

        string outputPreview = trial.Output.Length > outputPreviewChars
            ? trial.Output[..outputPreviewChars] + "\u2026"
            : trial.Output;

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Output:")));

        AnsiConsole.WriteLine(outputPreview);

    }

    private static void AddVerdictRow(Table table, IThemePalette themePalette, string kind, bool passed, string detail)
    {

        table.AddRow(
            new Markup(themePalette.TextMarkup(Markup.Escape(kind))),
            new Markup(
                passed
                    ? themePalette.HighlightMarkup(Markup.Escape("yes"))
                    : themePalette.ErrorMarkup(Markup.Escape("no"))),
            new Markup(themePalette.TextMarkup(Markup.Escape(detail))));

    }

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--target <KIND>")]
        [Description("Trial target kind: spell, prompt, apprenticeGoal.")]
        public string? Target { get; init; }

        [CommandOption("--target-value <VALUE>")]
        [Description("Spell name, prompt GUID, or apprentice goal text.")]
        public string? TargetValue { get; init; }

        [CommandOption("--model <MODEL>")]
        public string? Model { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Trial display name; defaults to '{targetKind}:{target}'.")]
        public string? Name { get; init; }

        [CommandOption("--inquisitor <JSON_OR_FILE>")]
        [Description("Inquisitor spec: inline JSON, or @filename. Pass multiple times for several inquisitors.")]
        public string[]? Inquisitor { get; init; }

        [CommandOption("--var <KEY=VALUE>")]
        [Description("Trial variable as key=value; pass multiple times for several variables.")]
        public string[]? Var { get; init; }

    }

}

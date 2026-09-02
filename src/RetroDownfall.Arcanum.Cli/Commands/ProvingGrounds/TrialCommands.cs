using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;

/// <summary>
/// The Proving Grounds: run Trials against spells, prompts, or Apprentice goals (requires arcanum serve).
/// </summary>
public sealed class TrialCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// Run a Trial with Inquisitors (POST /api/proving-grounds/trials/run).
    /// </summary>
    /// <param name="target">Trial target kind: spell, prompt, apprenticeGoal.</param>
    /// <param name="targetValue">Spell name, prompt GUID, or apprentice goal text.</param>
    /// <param name="model">Model override for the Trial.</param>
    /// <param name="workspace">Workspace root to scope the Trial.</param>
    /// <param name="name">Trial display name; defaults to '{targetKind}:{target}'.</param>
    /// <param name="inquisitor">Inquisitor spec: inline JSON, or @filename. Pass multiple times for several inquisitors.</param>
    /// <param name="var">Trial variable as key=value; pass multiple times for several variables.</param>
    public async Task<int> Run(
        string? target = null,
        string? targetValue = null,
        string? model = null,
        string? workspace = null,
        string? name = null,
        string[]? inquisitor = null,
        string[]? var = null,
        CancellationToken cancellationToken = default)
    {

        TrialTargetKind? targetKind = target?.Trim().ToLowerInvariant() switch
        {
            "spell" => TrialTargetKind.Spell,
            "prompt" => TrialTargetKind.Prompt,
            "apprenticegoal" => TrialTargetKind.ApprenticeGoal,
            _ => null,
        };

        if (targetKind is null)
        {
            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("--target must be one of: spell, prompt, apprenticeGoal.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (string.IsNullOrWhiteSpace(targetValue))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--target-value is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        List<Inquisitor> inquisitors = new();

        foreach (string raw in inquisitor ?? [])
        {

            if (!CliArgReader.TryReadInlineOrFile(raw, out string json, out string? readError))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(readError!)));

                return 1;
            }

            Inquisitor? parsedInquisitor;

            try
            {
                parsedInquisitor = JsonSerializer.Deserialize(json, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.Inquisitor);
            }
            catch (JsonException ex)
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid inquisitor JSON: {ex.Message}")));

                return 1;
            }

            if (parsedInquisitor is null)
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("Inquisitor JSON parsed to null.")));

                return (int)CliExitCode.ConfigurationError;
            }

            inquisitors.Add(parsedInquisitor);

        }

        if (!CliArgReader.TryParseKeyValuePairs(var, out Dictionary<string, string> variables, out string? varError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(varError!)));

            return 1;
        }

        Trial trial = new(
            targetKind.Value,
            targetValue.Trim(),
            inquisitors,
            variables.Count == 0 ? null : variables,
            model,
            workspace,
            name);

        Result<TrialResult> result = await apiClient.RunTrialAsync(trial, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

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
            ? trial.Output[..Utf8Truncation.SafeCharSliceLength(trial.Output, outputPreviewChars)] + "\u2026"
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

}

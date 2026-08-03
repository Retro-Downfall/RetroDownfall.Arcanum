using System.CommandLine;

using System.CommandLine.Parsing;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands.Configuration;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildPreset(IServiceProvider services)
    {

        PresetCommands handler = services.GetRequiredService<PresetCommands>();

        Command preset = new(
            "preset",
            "Inspect, preview, apply, and reset transparent onboarding presets.");

        Command list = new(
            "list",
            "List the built-in versioned workflow presets and their effective state.");

        list.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler.List(cancellationToken).ConfigureAwait(false));

        Command show = new(
            "show",
            "Explain one preset, its owned settings, prerequisites, and disclosures.");

        Argument<string> showName = PresetNameArgument();

        show.Add(showName);

        show.SetAction(
            async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await handler.Show(
                        parseResult.GetValue(showName)!,
                        cancellationToken)
                    .ConfigureAwait(false));

        Command diff = new(
            "diff",
            "Preview persisted and effective changes without writing configuration.");

        Argument<string> diffName = PresetNameArgument();

        diff.Add(diffName);

        diff.SetAction(
            async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await handler.Diff(
                        parseResult.GetValue(diffName)!,
                        cancellationToken)
                    .ConfigureAwait(false));

        Command apply = new(
            "apply",
            "Validate and atomically apply only the settings owned by one preset.");

        Argument<string> applyName = PresetNameArgument();

        apply.Add(applyName);

        apply.SetAction(
            async (ParseResult parseResult, CancellationToken cancellationToken) =>
                await handler.Apply(
                        parseResult.GetValue(applyName)!,
                        cancellationToken)
                    .ConfigureAwait(false));

        Command reset = new(
            "reset",
            "Restore the active preset baseline without changing unrelated settings.");

        reset.SetAction(
            async (ParseResult _, CancellationToken cancellationToken) =>
                await handler.Reset(cancellationToken).ConfigureAwait(false));

        preset.Add(list);

        preset.Add(show);

        preset.Add(diff);

        preset.Add(apply);

        preset.Add(reset);

        return preset;

    }

    private static Argument<string> PresetNameArgument() =>
        new("name")
        {

            Description = "Preset ID or display name.",

        };

}

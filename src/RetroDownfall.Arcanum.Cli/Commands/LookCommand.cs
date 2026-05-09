using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class LookCommand(IEyeOfTheWorld eye, IThemePalette palette) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        PatternSnapshot snapshot = await eye
            .PerceivePatternAsync(Environment.CurrentDirectory, cancellationToken)
            .ConfigureAwait(false);

        PatternSnapshotMarkup.WritePatternSnapshot(snapshot, palette);

        return 0;
    }
}

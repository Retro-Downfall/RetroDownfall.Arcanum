using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class LookCommand(IEyeOfTheWorld eye, IThemePalette palette)
{

    /// <summary>
    /// Eye of the World: situational snapshot of the current directory (domain + TOC).
    /// </summary>
    [Command("")]
    public async Task<int> Run(CancellationToken cancellationToken)
    {
        PatternSnapshot snapshot = await eye
            .PerceivePatternAsync(Environment.CurrentDirectory, cancellationToken)
            .ConfigureAwait(false);

        PatternSnapshotMarkup.WritePatternSnapshot(snapshot, palette);

        return 0;
    }

}

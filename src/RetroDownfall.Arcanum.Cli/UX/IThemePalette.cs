using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

public interface IThemePalette
{

    Color Text { get; }

    Color Heading { get; }

    Color Highlight { get; }

    Color Error { get; }

    Color Muted { get; }

}

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CliSettings
{

    public ArcanumTheme Theme { get; set; } = ArcanumTheme.SystemDefault;

    public bool ShowManaBar { get; set; } = true;

}



namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record WardSettings
{

    public bool Enabled { get; init; } = true;

    public List<string> ForbiddenArts { get; init; } = new()
    {
        "execute_command",
        "write_file",
        "replace_text_block",
        "delete_lore",
        "run_spell_script",
    };

    public int TimeoutSeconds { get; init; } = 120;

    public int MaxActiveWards { get; init; } = 50;

    public bool AutoDenyInUnattendedMode { get; init; } = true;

}

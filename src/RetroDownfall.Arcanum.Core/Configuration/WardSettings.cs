namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record WardSettings
{

    public bool Enabled { get; init; } = true;

    private readonly List<string> _forbiddenArts = new()
    {
        "execute_command",
        "write_file",
        "replace_text_block",
        "delete_lore",
        "run_spell_script",
    };

    public IReadOnlyList<string> ForbiddenArts
    {

        get => _forbiddenArts;

        init => _forbiddenArts = new List<string>(value);

    }

    public int TimeoutSeconds { get; init; } = 120;

    public int MaxActiveWards { get; init; } = 50;

    public bool AutoDenyInUnattendedMode { get; init; } = true;

}

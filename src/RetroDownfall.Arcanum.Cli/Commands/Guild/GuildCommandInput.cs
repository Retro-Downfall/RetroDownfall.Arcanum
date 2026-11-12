namespace RetroDownfall.Arcanum.Cli.Commands.Guild;

internal sealed record GuildCommandInput(string?[] Arguments, IReadOnlyDictionary<string, object?> Values)
{
    public string? Argument(int index = 0) => index < Arguments.Length ? Arguments[index] : null;

    public string? Text(string name) => Values.GetValueOrDefault(name) as string;

    public long? Number(string name) => Values.GetValueOrDefault(name) is long number ? number : null;

    public bool Flag(string name) => Values.GetValueOrDefault(name) is true;

    public string[] Many(string name) => Values.GetValueOrDefault(name) as string[] ?? [];
}

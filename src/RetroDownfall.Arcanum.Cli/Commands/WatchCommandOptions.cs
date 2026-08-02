namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed record WatchCommandOptions
{

    public WatchCommandOptions(
        bool reconnect,
        string[] eventTypes,
        string[] toolNames)
    {

        Reconnect = reconnect;

        EventTypes = Normalize(eventTypes);

        ToolNames = Normalize(toolNames);

    }

    public bool Reconnect { get; }

    public string[] EventTypes { get; }

    public string[] ToolNames { get; }

    private static string[] Normalize(string[] values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();

}

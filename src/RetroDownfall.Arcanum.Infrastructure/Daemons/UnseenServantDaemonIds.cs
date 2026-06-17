namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public static class UnseenServantDaemonIds
{

    public const string Prefix = "unseen-servant:";

    public static string ForJobName(string jobName) => $"{Prefix}{jobName}";

    public static string? JobNameFromId(string daemonId)
    {
        if (!daemonId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string name = daemonId[Prefix.Length..];

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

}

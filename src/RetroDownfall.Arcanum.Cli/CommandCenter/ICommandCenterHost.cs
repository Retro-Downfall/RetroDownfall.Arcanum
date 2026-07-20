namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal interface ICommandCenterHost
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

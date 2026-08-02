namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal interface ICommandCenterHost
{
    Task<int> RunAsync(CancellationToken cancellationToken);

    Task<int> RunAsync(
        Guid? startupSessionId,
        CancellationToken cancellationToken) =>
        RunAsync(cancellationToken);
}

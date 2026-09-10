namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal interface IMcpGlobalInitializationCoordinator
{
    Task InitializeGlobalAsync(
        McpGlobalInitializationAuthority authority,
        CancellationToken cancellationToken);

    Task StopAllAsync(CancellationToken cancellationToken);
}

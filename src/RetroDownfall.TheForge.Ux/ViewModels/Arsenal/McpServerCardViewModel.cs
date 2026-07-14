using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>One row in The Arsenal's MCP servers list — a friendly projection of <see cref="McpServerInfo"/>.</summary>
public sealed class McpServerCardViewModel
{

    public McpServerCardViewModel(McpServerInfo info)
    {

        Info = info;

    }

    public McpServerInfo Info { get; }

    public string Name => Info.Name;

    public string StateText => Info.State switch
    {
        McpServerState.Running => "Running",
        McpServerState.Starting => "Starting",
        McpServerState.Restarting => "Restarting",
        McpServerState.Error => "Error",
        _ => "Stopped",
    };

    public string Transport => Info.Transport switch
    {
        McpServerTransport.Stdio => "stdio",
        McpServerTransport.Sse => "sse",
        McpServerTransport.Http => "http",
        _ => Info.Transport.ToString(),
    };

    public string ToolsText => Info.Tools is { Length: > 0 } tools ? string.Join(", ", tools) : "—";

    public string? ErrorMessage => Info.ErrorMessage;

    public bool IsRunning => Info.State == McpServerState.Running;

    public bool IsStopped => Info.State == McpServerState.Stopped;

    public bool IsError => Info.State == McpServerState.Error;

}

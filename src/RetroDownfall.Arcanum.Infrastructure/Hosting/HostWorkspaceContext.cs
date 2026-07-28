using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class HostWorkspaceContext : IHostWorkspaceContext
{

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public HostWorkspaceContext(IOptionsMonitor<ArcanumSettings> settings)
    {
        _settings = settings;
    }

    public string? WorkspacePath
    {
        get
        {
            string? configured = _settings.CurrentValue.ResolveDefaultWorkspace();

            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(configured.Trim());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

}

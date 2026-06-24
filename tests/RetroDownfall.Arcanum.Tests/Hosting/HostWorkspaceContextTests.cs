using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class HostWorkspaceContextTests
{

    [Fact]
    public void WorkspacePath_returns_null_when_unconfigured()
    {

        HostWorkspaceContext context = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        Assert.Null(context.WorkspacePath);

    }

    [Fact]
    public void WorkspacePath_returns_full_path_for_configured_workspace()
    {

        string root = Path.Combine(Path.GetTempPath(), "arcanum-host-ws", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {

            ArcanumSettings settings = new()
            {
                Host = new HostSettings { Workspace = root },
            };

            HostWorkspaceContext context = new(new TestOptionsMonitor<ArcanumSettings>(settings));

            string? resolved = context.WorkspacePath;

            Assert.NotNull(resolved);

            Assert.Equal(Path.GetFullPath(root), resolved);

        }
        finally
        {

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

        }

    }

}

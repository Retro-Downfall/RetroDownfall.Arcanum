using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolExecutionPipelinePathPreflightTests
{

    [Fact]
    public void TryResolvePathUnderWorkspace_AllowsChildUnderRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            bool ok = ToolExecutionPipeline.TryResolvePathUnderWorkspace(root, "notes/a.txt", out string absolute);
            Assert.True(ok);
            Assert.StartsWith(Path.GetFullPath(root), absolute, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void TryResolvePathUnderWorkspace_RejectsDotDotEscape()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            bool ok = ToolExecutionPipeline.TryResolvePathUnderWorkspace(
                root,
                Path.Combine("..", "outside.txt"),
                out string absolute);

            Assert.False(ok);
            Assert.Equal(string.Empty, absolute);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void TryResolvePathUnderWorkspace_RejectsAbsoluteOutsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcanum-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string outside = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            bool ok = ToolExecutionPipeline.TryResolvePathUnderWorkspace(root, outside, out string absolute);
            Assert.False(ok);
            Assert.Equal(string.Empty, absolute);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

}

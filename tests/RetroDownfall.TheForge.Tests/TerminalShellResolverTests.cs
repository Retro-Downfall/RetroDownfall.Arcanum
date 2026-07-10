using RetroDownfall.TheForge.Ux.Services.Terminal;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TerminalShellResolverTests
{

    [Fact]
    public void Resolve_ReturnsPlatformShellAndPrefix()
    {

        TerminalShellResolver resolver = new();

        TerminalShellSpec spec = resolver.Resolve();

        Assert.False(string.IsNullOrWhiteSpace(spec.FileName));

        if (OperatingSystem.IsWindows())
        {

            Assert.Equal("cmd.exe", spec.FileName);

            Assert.Equal(["/C"], spec.ArgumentPrefix);

        }
        else
        {

            Assert.Equal(["-lc"], spec.ArgumentPrefix);

        }

    }

}

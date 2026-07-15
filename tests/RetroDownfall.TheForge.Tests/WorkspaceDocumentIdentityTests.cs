using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class WorkspacePathHelperTests
{

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("/tmp/ws", "/tmp/ws")]
    [InlineData("  /tmp/ws  ", "/tmp/ws")]
    public void ForApi_TrimsAndEmptyToNull(string? input, string? expected) =>
        Assert.Equal(expected, WorkspacePathHelper.ForApi(input));

    [Fact]
    public void ForApi_DoesNotStripTrailingSeparator()
    {

        string withSlash = "/tmp/ws" + Path.DirectorySeparatorChar;

        Assert.Equal(withSlash, WorkspacePathHelper.ForApi(withSlash));

    }

    [Fact]
    public void ForIdentity_CollapsesTrailingSeparators()
    {

        string withSlash = "/tmp/ws" + Path.DirectorySeparatorChar;

        Assert.Equal("/tmp/ws", WorkspacePathHelper.ForIdentity(withSlash));

        Assert.Equal("/tmp/ws", WorkspacePathHelper.ForIdentity("/tmp/ws"));

    }

}

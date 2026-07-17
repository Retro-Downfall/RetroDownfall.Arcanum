using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ForgeApiErrorTests
{

    [Fact]
    public void From_FormatsCodeAndMessage()
    {

        ApiResponse<string> response = new(null, false, new Error("Hub.Error", "boom"), null);

        Assert.Equal("[Hub.Error] boom", ForgeApiError.From(response));

    }

    [Fact]
    public void From_CuratesConnectionFailed()
    {

        ApiResponse<bool> response = new(
            false,
            false,
            new Error("Connection.Failed", "No connection could be made because the target machine actively refused it."),
            null);

        Assert.Equal("[Connection.Failed] Arcanum is unreachable. Is the API running?", ForgeApiError.From(response));

    }

    [Fact]
    public void From_NullResponse_UsesFallback()
    {

        Assert.Equal("Failed to list MCP servers.", ForgeApiError.From<string>(null, "Failed to list MCP servers."));

    }

}

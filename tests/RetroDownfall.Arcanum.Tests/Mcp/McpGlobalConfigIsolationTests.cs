using System.Reflection;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("ProcessEnvironment")]
public sealed class McpGlobalConfigIsolationTests
{

    [Fact]
    public void GlobalConfigPath_UsesTestingIsolatedArcanumRoot()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(testHome);

        MethodInfo resolver = typeof(McpConnectionManager).GetMethod(
            "GetGlobalMcpConfigPath",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Global MCP config path resolver was not found.");

        string actual = Assert.IsType<string>(resolver.Invoke(null, null));

        string expected = Path.Combine(testHome, ".config", "arcanum", "mcp.json");

        Assert.Equal(expected, actual);

    }

    private sealed class TestingEnvironmentScope : IDisposable
    {

        private readonly Dictionary<string, string?> _original = new();

        public TestingEnvironmentScope(string testHome)
        {

            Set("ASPNETCORE_ENVIRONMENT", "Testing");

            Set("DOTNET_ENVIRONMENT", "Testing");

            Set("ARCANUM_TEST_HOME", testHome);

        }

        public void Dispose()
        {

            foreach (KeyValuePair<string, string?> entry in _original)
            {

                global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

            }

        }

        private void Set(string name, string value)
        {

            _original[name] = global::System.Environment.GetEnvironmentVariable(name);

            global::System.Environment.SetEnvironmentVariable(name, value);

        }

    }

}

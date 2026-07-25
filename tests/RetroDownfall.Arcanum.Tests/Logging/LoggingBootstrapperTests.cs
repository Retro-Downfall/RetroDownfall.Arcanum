using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Tests.Logging;

[Collection("ProcessEnvironment")]
public sealed class LoggingBootstrapperTests
{

    [Fact]
    public void ResolveLogDirectory_UsesTestingIsolatedRoot()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        using TestingEnvironmentScope scope = new(testHome);

        string expected = Path.Combine(testHome, ".config", "arcanum", "logs");

        Assert.Equal(expected, LoggingBootstrapper.ResolveLogDirectory());

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

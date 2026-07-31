using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("ProcessEnvironment")]

public sealed class ConfigurationEnvironmentOverridesTests
{

    [Fact]

    public void Apply_reports_effective_typed_values_without_mutating_file_snapshot()
    {

        const string portVariable = "ARCANUM_Arcanum__Host__Port";

        const string hostAnyVariable = "ARCANUM_HOST_ANY";

        string? originalPort = global::System.Environment.GetEnvironmentVariable(portVariable);

        string? originalHostAny = global::System.Environment.GetEnvironmentVariable(hostAnyVariable);

        try
        {

            global::System.Environment.SetEnvironmentVariable(portVariable, "6124");

            global::System.Environment.SetEnvironmentVariable(hostAnyVariable, "1");

            ArcanumSettings fileSettings = new();

            ArcanumSettings effective = ConfigurationEnvironmentOverrides.Apply(fileSettings);

            Assert.Equal(6124, effective.Host.Port);

            Assert.True(effective.Host.ListenAny);

            Assert.NotEqual(6124, fileSettings.Host.Port);

            Assert.False(fileSettings.Host.ListenAny);

            IReadOnlyList<string> overrides = ConfigurationEnvironmentOverrides.Inspect(fileSettings);

            Assert.Contains($"host.port <- {portVariable}", overrides);

            Assert.Contains($"host.listenAny <- {hostAnyVariable}", overrides);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(portVariable, originalPort);

            global::System.Environment.SetEnvironmentVariable(hostAnyVariable, originalHostAny);

        }

    }

}

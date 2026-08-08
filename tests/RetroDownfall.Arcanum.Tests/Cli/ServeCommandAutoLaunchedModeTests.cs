using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("ProcessEnvironment")]
public sealed class ServeCommandAutoLaunchedModeTests
{

    [Fact]
    public void IsAutoLaunched_true_when_env_is_1()
    {

        string? original = global::System.Environment.GetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar);

        try
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, "1");

            Assert.True(ServeCommand.IsAutoLaunched());

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, original);

        }

    }

    [Fact]
    public void IsAutoLaunched_true_when_env_is_true()
    {

        string? original = global::System.Environment.GetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar);

        try
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, "true");

            Assert.True(ServeCommand.IsAutoLaunched());

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, original);

        }

    }

    [Fact]
    public void IsAutoLaunched_false_when_env_cleared()
    {

        string? original = global::System.Environment.GetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar);

        try
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, null);

            Assert.False(ServeCommand.IsAutoLaunched());

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, original);

        }

    }

    [Fact]
    public void IsAutoLaunched_false_when_env_is_0()
    {

        string? original = global::System.Environment.GetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar);

        try
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, "0");

            Assert.False(ServeCommand.IsAutoLaunched());

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar, original);

        }

    }

}

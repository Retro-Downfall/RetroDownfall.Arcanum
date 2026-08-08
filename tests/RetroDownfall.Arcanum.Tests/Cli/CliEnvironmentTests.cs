using RetroDownfall.Arcanum.Cli.UX;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("ProcessEnvironment")]
public sealed class CliEnvironmentTests
{

    [Fact]
    public void Constructor_InteractiveTerminal_IsInteractiveIsTrue()
    {

        ICliEnvironment env = new CliEnvironment(showManaBarConfigured: true);

        bool expected = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        Assert.Equal(expected, env.IsInteractive);

    }

    [Fact]
    public void Constructor_ColorDisabledByEnvVariable_ColorEnabledIsFalse()
    {

        string? originalNoColor = System.Environment.GetEnvironmentVariable("NO_COLOR");

        string? originalArcanumNoColor = System.Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR");

        try
        {

            System.Environment.SetEnvironmentVariable("NO_COLOR", "1");

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", null);

            ICliEnvironment env = new CliEnvironment(showManaBarConfigured: true);

            Assert.False(env.ColorEnabled);

        }
        finally
        {

            System.Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", originalArcanumNoColor);

        }

    }

    [Fact]
    public void Constructor_ArcanumNoColorVariable_ColorEnabledIsFalse()
    {

        string? originalNoColor = System.Environment.GetEnvironmentVariable("NO_COLOR");

        string? originalArcanumNoColor = System.Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR");

        try
        {

            System.Environment.SetEnvironmentVariable("NO_COLOR", null);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", "true");

            ICliEnvironment env = new CliEnvironment(showManaBarConfigured: true);

            Assert.False(env.ColorEnabled);

        }
        finally
        {

            System.Environment.SetEnvironmentVariable("NO_COLOR", originalNoColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", originalArcanumNoColor);

        }

    }

    [Fact]
    public void Constructor_ShowManaBarEnabledAndInteractive_ShouldShowManaBarIsTrue()
    {

        ICliEnvironment env = new CliEnvironment(showManaBarConfigured: true);

        bool expected = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        Assert.Equal(expected, env.ShouldShowManaBar);

    }

    [Fact]
    public void Constructor_ShowManaBarDisabled_ShouldShowManaBarIsFalse()
    {

        ICliEnvironment env = new CliEnvironment(showManaBarConfigured: false);

        Assert.False(env.ShouldShowManaBar);

    }

}

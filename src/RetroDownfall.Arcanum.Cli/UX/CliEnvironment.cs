namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Process-wide signals about whether the CLI is running attached to an interactive terminal,
/// whether color output is desired, and whether UX chrome (mana bar, prompts) should render.
/// </summary>
public interface ICliEnvironment
{

    /// <summary>
    /// <c>true</c> when neither stdout nor stdin is redirected and the runtime detects a TTY.
    /// </summary>
    bool IsInteractive { get; }

    /// <summary>
    /// <c>true</c> when <see cref="IsInteractive"/> AND neither the standard <c>NO_COLOR</c>
    /// (see <c>https://no-color.org</c>) nor the Arcanum-specific <c>ARCANUM_NO_COLOR</c>
    /// environment variable is set to a non-empty value.
    /// </summary>
    bool ColorEnabled { get; }

    /// <summary>
    /// <c>true</c> when the mana bar should render: configured on (<c>Arcanum:Cli:ShowManaBar</c>)
    /// AND running interactively (otherwise the bar pollutes piped output and scripts).
    /// </summary>
    bool ShouldShowManaBar { get; }

}

public sealed class CliEnvironment : ICliEnvironment
{

    public CliEnvironment(bool showManaBarConfigured)
    {

        bool stdoutRedirected = Console.IsOutputRedirected;

        bool stdinRedirected = Console.IsInputRedirected;

        IsInteractive = !stdinRedirected && !stdoutRedirected;

        string? noColor = Environment.GetEnvironmentVariable("NO_COLOR");

        string? arcanumNoColor = Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR");

        bool noColorRequested = !string.IsNullOrEmpty(noColor) || !string.IsNullOrEmpty(arcanumNoColor);

        ColorEnabled = IsInteractive && !noColorRequested;

        ShouldShowManaBar = showManaBarConfigured && IsInteractive;

    }

    public bool IsInteractive { get; }

    public bool ColorEnabled { get; }

    public bool ShouldShowManaBar { get; }

}

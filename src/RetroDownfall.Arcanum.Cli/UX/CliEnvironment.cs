using RetroDownfall.Arcanum.Cli.Infrastructure;

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

    private readonly bool _isInteractive;

    private readonly bool _colorEnabled;

    private readonly bool _showManaBarConfigured;

    private readonly ICliInvocationContext? _invocationContext;

    public CliEnvironment(bool showManaBarConfigured)
        : this(showManaBarConfigured, invocationContext: null)
    {

    }

    internal CliEnvironment(
        bool showManaBarConfigured,
        ICliInvocationContext? invocationContext)
    {

        bool stdoutRedirected = Console.IsOutputRedirected;

        bool stdinRedirected = Console.IsInputRedirected;

        _isInteractive = !stdinRedirected && !stdoutRedirected;

        string? noColor = Environment.GetEnvironmentVariable("NO_COLOR");

        string? arcanumNoColor = Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR");

        bool noColorRequested = !string.IsNullOrEmpty(noColor) || !string.IsNullOrEmpty(arcanumNoColor);

        _colorEnabled = _isInteractive && !noColorRequested;

        _showManaBarConfigured = showManaBarConfigured;

        _invocationContext = invocationContext;

    }

    public bool IsInteractive =>
        _isInteractive && !(_invocationContext?.Options.Json ?? false);

    public bool ColorEnabled =>
        _colorEnabled
        && !(_invocationContext?.Options.Plain ?? false)
        && !(_invocationContext?.Options.Json ?? false);

    public bool ShouldShowManaBar =>
        _showManaBarConfigured
        && IsInteractive
        && ColorEnabled;

}

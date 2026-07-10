namespace RetroDownfall.TheForge.Ux.Services.Terminal;

/// <summary>
/// Platform default shell: <c>$SHELL</c> (else zsh/sh) with <c>-lc</c> on Unix;
/// <c>cmd.exe /C</c> on Windows.
/// </summary>
public sealed class TerminalShellResolver : ITerminalShellResolver
{

    public TerminalShellSpec Resolve()
    {

        if (OperatingSystem.IsWindows())
        {

            return new TerminalShellSpec("cmd.exe", ["/C"]);

        }

        string? shell = Environment.GetEnvironmentVariable("SHELL");

        if (string.IsNullOrWhiteSpace(shell))
        {

            shell = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/sh";

        }

        return new TerminalShellSpec(shell, ["-lc"]);

    }

}

using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Generates and installs shell completion. Generation is pure: it reads the command tree and
/// writes a script, touching no network and no state. Installation is the only mutation, and it
/// names its exact target and confirms first.
/// </summary>
internal sealed class CompletionCommands(
    ICliCompletionResolver resolver,
    IConfirmationPrompt confirmationPrompt,
    IConsoleDispatcher dispatcher)
{

    public int Generate(string? shell, CliSurfaceMap map)
    {

        if (string.IsNullOrWhiteSpace(shell))
        {

            dispatcher.WriteDiagnostic(
                $"A shell is required. Supported: {string.Join(", ", CliCompletionShells.Names)}.");

            return (int)CliExitCode.ConfigurationError;

        }

        if (!CliCompletionShells.IsSupported(shell))
        {

            return UnsupportedShell(shell);

        }

        dispatcher.WritePayload(CliCompletionScriptWriter.Write(shell, map).TrimEnd('\n'));

        return (int)CliExitCode.Success;

    }

    public async Task<int> InstallAsync(
        string shell,
        string? explicitTarget,
        CliSurfaceMap map,
        CancellationToken cancellationToken)
    {

        if (!CliCompletionShells.IsSupported(shell))
        {

            return UnsupportedShell(shell);

        }

        string target = explicitTarget ?? DefaultTarget(shell);

        dispatcher.WriteDiagnostic($"Completion target: {target}");

        if (File.Exists(target))
        {

            dispatcher.WriteDiagnostic("A file already exists at that path and will be replaced.");

        }

        bool confirmed;

        try
        {

            confirmed = await confirmationPrompt
                .PromptForConfirmationAsync(
                    $"Write arcanum {shell} completion to {target}?",
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch (NonInteractiveConfirmationException)
        {

            dispatcher.WriteDiagnostic(
                "Installation needs confirmation. Re-run with --yes, or use "
                + $"`arcanum completion {shell}` and redirect the script yourself.");

            return (int)CliExitCode.ConfigurationError;

        }

        if (!confirmed)
        {

            dispatcher.WriteDiagnostic("Installation declined. Nothing was written.");

            return (int)CliExitCode.Success;

        }

        try
        {

            WriteAtomically(target, CliCompletionScriptWriter.Write(shell, map));

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            dispatcher.WriteDiagnostic($"Completion could not be written to {target}.");

            return (int)CliExitCode.GenericError;

        }

        dispatcher.WriteDiagnostic($"Installed {shell} completion to {target}.");

        dispatcher.WriteDiagnostic(SourcingHint(shell, target));

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Resolves one safe dynamic completion source. This is the command the generated scripts call,
    /// so it must never print a diagnostic, never start the host, and always exit successfully —
    /// an unavailable host simply yields no suggestions.
    /// </summary>
    public async Task<int> ResolveAsync(string provider, CancellationToken cancellationToken)
    {

        IReadOnlyList<string> values = await resolver
            .ResolveAsync(provider, cancellationToken)
            .ConfigureAwait(false);

        if (values.Count > 0)
        {

            dispatcher.WritePayload(string.Join(' ', values));

        }

        return (int)CliExitCode.Success;

    }

    private int UnsupportedShell(string shell)
    {

        dispatcher.WriteDiagnostic(
            $"`{shell}` is not a supported shell. Supported: {string.Join(", ", CliCompletionShells.Names)}.");

        return (int)CliExitCode.ConfigurationError;

    }

    /// <summary>
    /// Per-shell conventional completion location under the operator's own configuration directory.
    /// Nothing is written outside the user's home, and no system-wide path is ever chosen.
    /// </summary>
    private static string DefaultTarget(string shell)
    {

        string home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

        return shell switch
        {
            CliCompletionShells.Bash =>
                Path.Combine(home, ".local", "share", "bash-completion", "completions", "arcanum"),
            CliCompletionShells.Zsh =>
                Path.Combine(home, ".zfunc", "_arcanum"),
            CliCompletionShells.Fish =>
                Path.Combine(home, ".config", "fish", "completions", "arcanum.fish"),
            _ =>
                Path.Combine(home, ".config", "powershell", "arcanum.completion.ps1"),
        };

    }

    private static string SourcingHint(string shell, string target) =>
        shell switch
        {
            CliCompletionShells.Bash =>
                $"Add `source {target}` to ~/.bashrc if your shell does not load bash-completion automatically.",
            CliCompletionShells.Zsh =>
                $"Ensure `fpath+=({Path.GetDirectoryName(target)})` precedes `compinit` in ~/.zshrc.",
            CliCompletionShells.Fish =>
                "fish loads this on the next shell start; no further configuration is needed.",
            _ =>
                $"Add `. {target}` to your PowerShell profile ($PROFILE).",
        };

    /// <summary>
    /// Writes through a sibling temp file and an atomic replace, so an interrupted install leaves
    /// either the previous script or the new one — never a half-written file the shell would source.
    /// </summary>
    private static void WriteAtomically(string target, string content)
    {

        string directory = Path.GetDirectoryName(target)
            ?? throw new IOException("Completion target has no directory.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        string temporary = target + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {

            File.WriteAllText(temporary, content);

            File.Move(temporary, target, overwrite: true);

        }
        catch
        {

            try
            {

                File.Delete(temporary);

            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;

        }

    }

}

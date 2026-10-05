using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands.Daemon;

/// <summary>
/// Collects the account a Windows Service runs under and that account's password. Neither value is ever a command
/// line argument: they come from the terminal prompt, with the password hidden, or from redirected standard input.
/// </summary>
public interface IDaemonServiceAccountPrompt
{
    /// <summary>
    /// Reads the account and password, or fails with <see cref="DaemonServiceAccountPrompt.UnavailableCode"/> when
    /// this invocation has no way to ask, or <see cref="DaemonServiceAccountPrompt.InvalidCode"/> when either value
    /// is empty.
    /// </summary>
    Task<Result<DaemonServiceCredential>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The terminal-backed <see cref="IDaemonServiceAccountPrompt"/>. With standard input redirected the account is the
/// first line and the password the second; otherwise the account is asked for in the clear, offering the invoking
/// user's own account, and the password through the hidden prompt. <c>--print</c> and <c>--json</c> promise that no
/// prompt blocks, so under either one without redirected input there is no route and the read fails.
/// </summary>
internal sealed class DaemonServiceAccountPrompt(
    ICliInvocationContext invocationContext,
    ISensitiveValueConsole console) : IDaemonServiceAccountPrompt
{
    internal const string UnavailableCode = "DaemonServiceAccountUnavailable";

    internal const string InvalidCode = "DaemonServiceAccountInvalid";

    internal const string UnavailableMessage =
        "The service account and its password must be supplied on redirected stdin (account on the first line, "
        + "password on the second) or at the terminal prompt when --print or --output-format json is in effect; "
        + "neither is accepted on the command line. A per-user Task Scheduler task runs the host as you with no "
        + "stored password: see docs/Arcanum.Command.Reference.md, 'arcanum daemon install'.";

    public async Task<Result<DaemonServiceCredential>> ReadAsync(CancellationToken cancellationToken)
    {
        CliInvocationOptions options = invocationContext.Options;

        string? account;

        if (console.IsInputRedirected)
        {
            account = await console.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (options.Print || options.Json)
        {
            return Result<DaemonServiceCredential>.Failure(new Error(UnavailableCode, UnavailableMessage));
        }
        else
        {
            account = console.PromptVisible("Service account (DOMAIN\\user):", CurrentUserAccount(), options);
        }

        string trimmedAccount = account?.Trim() ?? string.Empty;

        if (trimmedAccount.Length == 0)
        {
            return Result<DaemonServiceCredential>.Failure(
                new Error(InvalidCode, "The service account must not be empty."));
        }

        SensitiveValueRead password = await SensitiveValueInput
            .ReadAsync(console, options, $"Password for {trimmedAccount}:", cancellationToken)
            .ConfigureAwait(false);

        if (!password.IsAvailable)
        {
            return Result<DaemonServiceCredential>.Failure(new Error(UnavailableCode, UnavailableMessage));
        }

        // Not trimmed: unlike an API key, surrounding whitespace can be part of a password.
        if (string.IsNullOrEmpty(password.Value))
        {
            return Result<DaemonServiceCredential>.Failure(
                new Error(InvalidCode, "The service account's password must not be empty."));
        }

        return Result<DaemonServiceCredential>.Success(new DaemonServiceCredential(trimmedAccount, password.Value));
    }

    private static string CurrentUserAccount() => $"{Environment.UserDomainName}\\{Environment.UserName}";
}

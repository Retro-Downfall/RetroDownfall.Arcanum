using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

/// <summary>
/// Reads authored text from <c>--file</c> or from piped standard input, never from an argument.
/// </summary>
/// <remarks>
/// Shared by every verb that writes operator-authored prose into a memory store. Text that arrives as
/// a command-line argument lands in shell history and in the process list of a shared machine, and a
/// standing preference or a corrected memory is exactly the kind of text an operator would not want
/// left in either place. Keeping the read in one place is what keeps that property from depending on
/// which verb the operator reached for.
///
/// <para>The subject noun travels with the call so a refusal names the store the operator was writing
/// to rather than whichever one this helper was extracted from.</para>
///
/// <para>Content that is empty or whitespace-only is refused here, and that is a property of this
/// surface rather than of the stores behind it. The two layers answer different questions. A path can
/// lie about what it holds: an operator who mistyped <c>--file</c> did not ask to write nothing, and
/// nothing reaching this method can tell that apart from a deliberate blank, so it refuses both and
/// costs the deliberate one a single direct request. A caller that sends empty content to a route sent
/// it on purpose — there is no path between them and the request to have gone wrong — so the routes
/// stay permissive, and <b>a Saga memory really can be set to blank text that way</b>. Nothing here
/// makes a store unable to hold nothing; it makes this verb unwilling to guess.</para>
///
/// <para><paramref name="emptyContentRemedy"/> is where a caller says what an operator who meant it
/// should do instead, because that answer is not the same for every store: Saga's routes accept blank
/// content and the Covenant's refuse it at their own preflight, so a single shared sentence would be
/// false for one of them.</para>
/// </remarks>
internal static class AuthoredContentReader
{

    public static async Task<Result<string>> ReadAsync(
        string? file,
        string subject,
        string? emptyContentRemedy,
        CancellationToken cancellationToken)
    {

        Result<string> authored = await ReadSourceAsync(file, subject, cancellationToken).ConfigureAwait(false);

        if (authored.IsFailure)
        {

            return authored;

        }

        // Both sources meet this, and they must: the same payload was refused through the pipe and sent
        // through a file, so which source an operator reached for decided whether a mistyped path was
        // caught. The remarks say why this surface refuses what the routes behind it may accept.
        if (!string.IsNullOrWhiteSpace(authored.Value))
        {

            return authored;

        }

        string refusal = emptyContentRemedy is { Length: > 0 } remedy
            ? $"{subject} content was empty or whitespace-only, so nothing was sent. {remedy}"
            : $"{subject} content was empty or whitespace-only, so nothing was sent.";

        return Result<string>.Failure(new Error(ErrorCodes.Validation.InvalidBody, refusal));

    }

    /// <summary>
    /// The text as its source hands it over, before it is judged.
    /// </summary>
    private static async Task<Result<string>> ReadSourceAsync(
        string? file,
        string subject,
        CancellationToken cancellationToken)
    {

        if (file is { Length: > 0 })
        {

            return File.Exists(file)
                ? Result<string>.Success(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false))
                : Result<string>.Failure(new Error(
                    ErrorCodes.Validation.InvalidBody,
                    $"No file exists at '{file}'."));

        }

        if (!Console.IsInputRedirected)
        {

            return Result<string>.Failure(new Error(
                ErrorCodes.Validation.InvalidBody,
                $"{subject} content comes from --file or piped standard input, not from a command-line argument."));

        }

        return Result<string>.Success(
            await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false));

    }

}

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
/// </remarks>
internal static class AuthoredContentReader
{

    public static async Task<Result<string>> ReadAsync(
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

        string piped = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(piped)
            ? Result<string>.Failure(new Error(
                ErrorCodes.Validation.InvalidBody,
                $"{subject} content was empty."))
            : Result<string>.Success(piped);

    }

}

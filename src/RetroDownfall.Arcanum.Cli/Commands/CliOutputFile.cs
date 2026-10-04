using System.Text;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.UX;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Destination handling shared by every verb that writes a file the operator named: resolve and vet the
/// destination before any work is done, ask before replacing a file that is already there, and replace
/// it through a temporary sibling so a failed or interrupted write never leaves a truncated file.
/// </summary>
/// <remarks>
/// The overwrite question goes through <see cref="IConfirmationPrompt"/>, so <c>--yes</c> approves it
/// and a non-interactive run (<c>--json</c>, redirected stdin or stdout) is refused with exit 2 rather
/// than overwriting silently; a script that rewrites the same file on every run passes <c>--yes</c>.
/// </remarks>
internal static class CliOutputFile
{
    /// <summary>
    /// Resolves <paramref name="path"/> to a full path and reports why it cannot be written, without
    /// creating the destination.
    /// </summary>
    /// <returns><see langword="null"/> when the destination is usable, otherwise the reason.</returns>
    internal static string? TryResolve(string path, out string fullPath)
    {
        fullPath = string.Empty;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return $"the path is not valid ({exception.Message})";
        }

        if (Directory.Exists(fullPath))
        {
            return "the destination is a directory, not a file";
        }

        string? directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return "the destination directory does not exist";
        }

        try
        {
            // A write probe in the directory, never at the destination itself: a read-only directory
            // is found before the work is done rather than after.
            string probe = Path.Combine(directory, $".{Guid.NewGuid():N}.probe");

            using FileStream stream = new(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return $"the destination directory is not writable ({exception.Message})";
        }

        return null;
    }

    /// <summary>
    /// Asks before replacing an existing file. A destination that does not exist yet needs no answer.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the destination is free or the operator approved replacing it.
    /// </returns>
    internal static async Task<bool> ConfirmOverwriteAsync(
        IConfirmationPrompt confirmationPrompt,
        string fullPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fullPath))
        {
            return true;
        }

        return await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Overwrite existing file {fullPath}?",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers an export document: to stdout when no <paramref name="output"/> is named, otherwise to
    /// that file after the overwrite question and through a temporary sibling.
    /// </summary>
    /// <param name="json">The serialized export.</param>
    /// <param name="output">The operator's <c>--output</c> value, or blank for stdout.</param>
    /// <param name="exportedLabel">The label for the confirmation line, such as "Spell exported to:".</param>
    /// <param name="confirmationPrompt">Asked before an existing file is replaced.</param>
    /// <param name="themePalette">Themes the confirmation and failure lines.</param>
    /// <param name="cancellationToken">Cancels the prompt and the write.</param>
    /// <returns>0 when written or declined, 1 when the file could not be written.</returns>
    internal static async Task<int> WriteExportAsync(
        string json,
        string? output,
        string exportedLabel,
        IConfirmationPrompt confirmationPrompt,
        IThemePalette themePalette,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);

            return 0;
        }

        try
        {
            string fullPath = Path.GetFullPath(output);

            if (!await ConfirmOverwriteAsync(confirmationPrompt, fullPath, cancellationToken)
                    .ConfigureAwait(false))
            {
                CliErrorOutput.WriteMarkupLine(
                    themePalette.MutedMarkup(
                        Markup.Escape("Export cancelled; the existing file was not changed.")));

                return 0;
            }

            await WriteAllTextAsync(fullPath, json, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape($"Could not write '{output}': {exception.Message}")));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape(exportedLabel),
                Markup.Escape(output)));

        return 0;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a temporary sibling and moves it over
    /// <paramref name="fullPath"/>, so the destination is either the old file or the complete new one.
    /// </summary>
    /// <param name="fullPath">The resolved destination.</param>
    /// <param name="content">The text to write.</param>
    /// <param name="encoding">The encoding each verb already used; UTF-8 without a byte-order mark by default.</param>
    /// <param name="cancellationToken">Cancels the write; the temporary sibling is removed either way.</param>
    internal static async Task WriteAllTextAsync(
        string fullPath,
        string content,
        Encoding? encoding,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(fullPath) ?? ".";

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

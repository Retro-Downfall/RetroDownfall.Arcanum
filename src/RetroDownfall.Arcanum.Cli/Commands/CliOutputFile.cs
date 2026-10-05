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
/// <para>The overwrite question goes through <see cref="IConfirmationPrompt"/>, so <c>--yes</c> approves
/// it and a non-interactive run (<c>--json</c>, <c>--print</c>, redirected stdin or redirected stdout) is
/// refused with exit 2 rather than overwriting silently; a script that rewrites the same file on every
/// run passes <c>--yes</c>.</para>
/// <para>Replacing through a sibling must not change what the destination already was. A symbolic link
/// is written through to its target rather than replaced by a regular file, and the permission bits of
/// an existing file are carried onto the replacement on Unix (an export the operator made owner-only
/// stays owner-only); on Windows <see cref="File.Replace(string, string, string?)"/> keeps the replaced
/// file's attributes. On Unix, ownership and extended attributes are not carried over: they belong to the
/// new file.</para>
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
    /// Settles an export verb's <c>--output</c> before the export is fetched: the destination is vetted
    /// and an existing file is asked about, so a refusal costs no request, like <c>--save</c> on the web
    /// verbs.
    /// </summary>
    /// <param name="output">The operator's <c>--output</c> value, or blank for stdout.</param>
    /// <param name="confirmationPrompt">Asked before an existing file is replaced.</param>
    /// <param name="themePalette">Themes the cancellation and failure lines.</param>
    /// <param name="cancellationToken">Cancels the prompt.</param>
    internal static async Task<ExportDestination> PlanExportAsync(
        string? output,
        IConfirmationPrompt confirmationPrompt,
        IThemePalette themePalette,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ExportDestination.Stdout;
        }

        string? problem = TryResolve(output, out string fullPath);

        if (problem is not null)
        {
            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape($"Could not write '{output}': {problem}.")));

            return new ExportDestination(false, 1, null, output);
        }

        if (!await ConfirmOverwriteAsync(confirmationPrompt, fullPath, cancellationToken)
                .ConfigureAwait(false))
        {
            CliErrorOutput.WriteMarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape("Export cancelled; the existing file was not changed.")));

            return new ExportDestination(false, 0, null, output);
        }

        return new ExportDestination(true, 0, fullPath, output);
    }

    /// <summary>
    /// Delivers an export document to the destination <see cref="PlanExportAsync"/> settled: to stdout when
    /// none was named, otherwise to that file through a temporary sibling.
    /// </summary>
    /// <param name="json">The serialized export.</param>
    /// <param name="destination">The settled destination.</param>
    /// <param name="exportedLabel">The label for the confirmation line, such as "Spell exported to:".</param>
    /// <param name="themePalette">Themes the confirmation and failure lines.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>0 when written, 1 when the file could not be written.</returns>
    internal static async Task<int> WriteExportAsync(
        string json,
        ExportDestination destination,
        string exportedLabel,
        IThemePalette themePalette,
        CancellationToken cancellationToken)
    {
        if (destination.FullPath is not string fullPath)
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);

            return 0;
        }

        try
        {
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
                    Markup.Escape($"Could not write '{destination.Display}': {exception.Message}")));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape(exportedLabel),
                Markup.Escape(destination.Display ?? fullPath)));

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
    internal static Task WriteAllTextAsync(
        string fullPath,
        string content,
        Encoding? encoding,
        CancellationToken cancellationToken) =>
        WriteAllTextAsync(fullPath, content, encoding, File.Delete, cancellationToken);

    /// <summary>
    /// <see cref="WriteAllTextAsync(string, string, Encoding?, CancellationToken)"/> with the removal of the
    /// temporary sibling supplied, so a test can make that removal fail.
    /// </summary>
    internal static async Task WriteAllTextAsync(
        string fullPath,
        string content,
        Encoding? encoding,
        Action<string> removeTemporary,
        CancellationToken cancellationToken)
    {
        // A symbolic link is the operator's name for its target, so the target is what gets replaced.
        // Replacing the link would leave an unrelated regular file where the link was.
        string destination = ResolveWriteTarget(fullPath);

        string directory = Path.GetDirectoryName(destination) ?? ".";

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            ReplaceFile(temporaryPath, destination);
        }
        finally
        {
            RemoveTemporary(temporaryPath, removeTemporary);
        }
    }

    /// <summary>
    /// The path a write to <paramref name="fullPath"/> must replace: the final target when it is a symbolic
    /// link (existing or dangling), otherwise the path itself.
    /// </summary>
    private static string ResolveWriteTarget(string fullPath)
    {
        FileInfo file = new(fullPath);

        // LinkTarget is null for a path that is not a link, including one that does not exist yet; asking a
        // path that does not exist to resolve itself throws instead.
        if (file.LinkTarget is null)
        {
            return fullPath;
        }

        return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
    }

    /// <summary>
    /// Moves the completed temporary file over the destination, keeping what the destination already had:
    /// its permission bits on Unix, its attributes on Windows.
    /// </summary>
    private static void ReplaceFile(string temporaryPath, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(temporaryPath, destination);

            return;
        }

        if (OperatingSystem.IsWindows())
        {
            File.Replace(temporaryPath, destination, destinationBackupFileName: null);

            return;
        }

        File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(destination));

        File.Move(temporaryPath, destination, overwrite: true);
    }

    /// <summary>
    /// Removes the temporary sibling without letting a failure to do so replace the outcome of the write.
    /// </summary>
    private static void RemoveTemporary(string temporaryPath, Action<string> removeTemporary)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                removeTemporary(temporaryPath);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray dot-file is a lesser harm than hiding why the write failed, and this
            // runs while the write's own exception, if any, is still unwinding.
        }
    }
}

/// <summary>
/// Where an export verb's output goes, settled before the export is fetched.
/// </summary>
/// <param name="Proceed">False when the destination was refused or the overwrite declined and the verb must stop.</param>
/// <param name="ExitCode">The exit code to stop with when <paramref name="Proceed"/> is false.</param>
/// <param name="FullPath">The resolved file, or <see langword="null"/> for stdout.</param>
/// <param name="Display">The operator's own spelling of the destination, for messages.</param>
internal readonly record struct ExportDestination(
    bool Proceed,
    int ExitCode,
    string? FullPath,
    string? Display)
{
    /// <summary>No <c>--output</c>: the export goes to stdout.</summary>
    internal static ExportDestination Stdout { get; } = new(true, 0, null, null);
}

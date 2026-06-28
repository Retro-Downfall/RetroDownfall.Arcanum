using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

/// <summary>
/// Durable, atomic text-file writes for spell artifacts (<c>SPELL.md</c>, <c>SKILL.json</c>).
/// Writes to a same-directory temporary file (flushed to disk) then atomically replaces the
/// destination via <see cref="File.Move(string, string, bool)"/>, so a crash mid-write never
/// leaves a partially written or truncated destination file.
/// </summary>
internal static class SpellAtomicFile
{

    public static async Task WriteAllTextAsync(string destinationPath, string contents, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);

        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Destination path must include a directory.", nameof(destinationPath));
        }

        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);

            await using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, destinationPath, overwrite: true);

            tempPath = string.Empty;
        }
        finally
        {
            if (tempPath.Length > 0)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

}

using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

/// <summary>
/// Durable, atomic text-file writes for spell artifacts (<c>SPELL.md</c>, <c>SPELL.json</c>).
/// Delegates to <see cref="AtomicFile.ReplaceAsync"/>, which writes to a same-directory temporary
/// file (flushed to disk) then atomically replaces the destination via
/// <see cref="File.Move(string, string, bool)"/>, so a crash mid-write never leaves a partially
/// written or truncated destination file.
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

        byte[] bytes = Encoding.UTF8.GetBytes(contents);

        AtomicReplaceStatus replaceStatus = await AtomicFile.ReplaceAsync(
            destinationPath,
            tempPath,
            (stream, ct) => stream.WriteAsync(bytes, ct).AsTask(),
            cancellationToken).ConfigureAwait(false);

        if (replaceStatus != AtomicReplaceStatus.Succeeded)
        {

            throw new IOException(
                $"Atomic spell replacement did not succeed ({replaceStatus}).");

        }

    }

}

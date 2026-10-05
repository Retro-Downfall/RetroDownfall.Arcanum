using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The length and SHA-256 of the exact bytes a read-modify-write caller read. A caller hands it to
/// <see cref="AtomicFile.ReplaceAsync"/> so the replace aborts, leaving the destination untouched, when
/// the bytes it is about to replace are not the bytes the edit was computed from.
/// </summary>
internal readonly record struct FileContentBaseline(
    long Length,
    string Sha256Hex)
{
    /// <summary>
    /// What both read-modify-write writers (the PATCH route and the in-process <c>replace_text_block</c>
    /// tool) tell the caller when the replace aborted against a baseline, so the two cannot drift.
    /// </summary>
    internal const string ChangedAfterReadMessage =
        "The file changed after it was read, or its state could not be verified, so nothing was written. Re-read the file and retry.";

    internal static FileContentBaseline Of(ReadOnlySpan<byte> bytes) =>
        new(
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)));

    internal bool Matches(
        long length,
        string sha256Hex) =>
        Length == length
        && string.Equals(
            Sha256Hex,
            sha256Hex,
            StringComparison.OrdinalIgnoreCase);
}

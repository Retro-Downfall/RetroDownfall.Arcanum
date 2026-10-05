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

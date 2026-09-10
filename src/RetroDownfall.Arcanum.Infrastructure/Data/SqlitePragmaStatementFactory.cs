using System.Globalization;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Builds the two runtime-valued SQLite PRAGMAs whose assignment grammar does not accept parameters.
/// </summary>
/// <remarks>
/// Application query and DML values are always parameters. These two statements are narrower:
/// <c>busy_timeout</c> accepts one nonnegative decimal integer, while SQLCipher <c>rekey</c> accepts
/// the canonical 32-byte Base64 token produced by <see cref="Security.GrimoireKeyDerivation"/>.
/// Validation closes each grammar before a value is composed into SQL text.
/// </remarks>
internal static class SqlitePragmaStatementFactory
{
    internal static string BusyTimeout(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);

        return string.Concat(
            "PRAGMA busy_timeout=",
            milliseconds.ToString(CultureInfo.InvariantCulture),
            ";");
    }

    internal static string Rekey(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        Span<byte> decoded = stackalloc byte[32];
        Span<char> canonical = stackalloc char[44];

        try
        {
            if (passphrase.Length != canonical.Length
                || !Convert.TryFromBase64String(passphrase, decoded, out int bytesWritten)
                || bytesWritten != decoded.Length
                || !Convert.TryToBase64Chars(decoded, canonical, out int charsWritten)
                || charsWritten != canonical.Length
                || !passphrase.AsSpan().SequenceEqual(canonical))
            {
                throw new ArgumentException(
                    "The SQLCipher rekey value must be one canonical 32-byte Base64 token.",
                    nameof(passphrase));
            }

            return string.Concat("PRAGMA rekey = '", passphrase, "';");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
            canonical.Clear();
        }
    }
}

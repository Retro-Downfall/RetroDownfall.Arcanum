using System.Globalization;

namespace RetroDownfall.Arcanum.Core.Backup;

/// <summary>
/// The documented floor on a recovery passphrase chosen for a new archive.
/// </summary>
/// <remarks>
/// The recovery passphrase alone protects an archive: it derives the key that encrypts the Grimoire
/// secret and every file-encryption key the archive carries, and an archive can be copied off the
/// machine and attacked offline. The floor is applied when a passphrase is chosen, at creation, and
/// never when an existing archive is opened, verified, inspected, migrated or restored: an archive
/// written before the floor existed has to stay readable by the passphrase it was written under.
///
/// <para>A length floor, not a strength meter. It stops the passphrases that are trivially guessable
/// by length and says so in the plan; choosing a long, unique one remains the operator's job.</para>
///
/// <para>Length is counted in the characters a person sees (Unicode text elements), not in the UTF-16
/// code units a string is stored in, so a rule that says twelve characters is not met by six emoji or
/// by six letters each written as a base letter plus a combining mark.</para>
/// </remarks>
public static class BackupPassphrasePolicy
{
    /// <summary>The fewest user-perceived characters a passphrase may have when an archive is created.</summary>
    public const int MinimumCreateCharacters = 12;

    public static string CreateMinimumMessage =>
        $"A backup recovery passphrase must be at least {MinimumCreateCharacters} characters when an archive is created.";

    public static string CreateWarning =>
        "The recovery passphrase alone protects the Grimoire encryption secret and every file-encryption key in this archive; "
        + $"it must be at least {MinimumCreateCharacters} characters, and a long, unique passphrase is strongly recommended.";

    public static bool MeetsCreateMinimum(ReadOnlySpan<char> passphrase)
    {
        // Text elements are never shorter than one code unit, so a passphrase under the floor in code
        // units cannot reach it in characters.
        if (passphrase.Length < MinimumCreateCharacters)
        {
            return false;
        }

        int characters = 0;

        while (!passphrase.IsEmpty)
        {
            passphrase = passphrase[StringInfo.GetNextTextElementLength(passphrase)..];

            characters++;

            if (characters >= MinimumCreateCharacters)
            {
                return true;
            }
        }

        return false;
    }
}

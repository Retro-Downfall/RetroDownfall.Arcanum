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
/// </remarks>
public static class BackupPassphrasePolicy
{
    /// <summary>The fewest UTF-16 characters a passphrase may have when an archive is created.</summary>
    public const int MinimumCreateCharacters = 12;

    public static string CreateMinimumMessage =>
        $"A backup recovery passphrase must be at least {MinimumCreateCharacters} characters when an archive is created.";

    public static string CreateWarning =>
        "The recovery passphrase alone protects the Grimoire encryption secret and every file-encryption key in this archive; "
        + $"it must be at least {MinimumCreateCharacters} characters, and a long, unique passphrase is strongly recommended.";

    public static bool MeetsCreateMinimum(ReadOnlySpan<char> passphrase) =>
        passphrase.Length >= MinimumCreateCharacters;
}

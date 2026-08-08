using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Diagnostics;

/// <summary>
/// Whether this process can talk to the OS credential backend at all.
///
/// <para>Pre-#33, doctor could tell an operator that a credential was "not set" but not why — a
/// missing secret and an unreachable Keychain or Secret Service produced the same line. That
/// distinction is the whole diagnosis on a Linux box with no keyring daemon, where every credential
/// looks absent and storing one again changes nothing.</para>
/// </summary>
public sealed class CredentialStoreAvailabilityCheck(IOsCredentialStore osCredentialStore) : IDoctorCheck
{

    public const string CheckId = "credentials.os_store";

    public string Id => CheckId;

    public string Name => "OS credential store";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Credentials;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        if (osCredentialStore.IsAvailable)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Healthy,
                "The OS credential store is reachable. No credential value is read by this check."));

        }

        return Task.FromResult(new DoctorFinding(
            DoctorOutcome.Degraded,
            "The OS credential store is not reachable, so Arcanum falls back to its encrypted file "
            + "mirror. Credentials still work, but they are protected by the Data Protection key "
            + "ring rather than by the OS."
            + (OperatingSystem.IsLinux()
                ? " On Linux this usually means libsecret is not installed or no keyring daemon is running."
                : string.Empty),
            [
                new DoctorRemedy(
                    "credentials.restore_os_store",
                    null,
                    DoctorRemedyCommands.KeyList,
                    "Confirm which store each credential resolved from; values are never displayed."),
            ]));

    }

}

/// <summary>
/// The Data Protection key ring that decrypts every <c>.dat</c> credential mirror. Its absence is
/// the difference between "no credential is stored" and "every stored credential is unreadable", so
/// it fails closed with restore guidance and never suggests regenerating a key over existing
/// ciphertext.
/// </summary>
public sealed class DataProtectionKeyRingCheck : IDoctorCheck
{

    public const string CheckId = "credentials.key_ring";

    public string Id => CheckId;

    public string Name => "Data Protection key ring";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Credentials;

    public bool RequiresNetwork => false;

    public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        string keyRing = DataProtectionKeyPaths.Directory;

        IReadOnlyList<string> mirrors =
            [
                .. new[]
                {
                    ArcanumPaths.ApiKeyStoreFile,
                    ArcanumPaths.GrimoireKeyStoreFile,
                    ArcanumPaths.FileEncryptionKeyStoreFile,
                    ArcanumPaths.PerplexityApiKeyStoreFile,
                }.Where(File.Exists),
            ];

        bool ringExists = Directory.Exists(keyRing);

        if (!ringExists && mirrors.Count == 0)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Skipped,
                "No encrypted credential mirror exists yet, so there is no key ring to check."));

        }

        if (!ringExists)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Unhealthy,
                $"{mirrors.Count} encrypted credential mirror(s) exist but the Data Protection key ring "
                + "is missing, so none of them can be decrypted. Arcanum will not generate a replacement "
                + "key over existing ciphertext.",
                [
                    new DoctorRemedy(
                        "credentials.restore_key_ring",
                        null,
                        DoctorRemedyCommands.KeyList,
                        "The key ring is machine-local and is never carried by a backup archive. Confirm which store each "
                        + "credential resolves from, then re-store any credential that has become unreadable."),
                ]));

        }

        int keyCount;

        try
        {

            keyCount = Directory.EnumerateFiles(keyRing, "*.xml").Count();

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Unavailable,
                "The Data Protection key ring could not be enumerated. Check the permissions on the Arcanum secret directory."));

        }

        if (keyCount == 0 && mirrors.Count == 0)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Skipped,
                "The key-ring directory exists but nothing has been encrypted with it yet."));

        }

        if (keyCount == 0 && mirrors.Count > 0)
        {

            return Task.FromResult(new DoctorFinding(
                DoctorOutcome.Unhealthy,
                $"The Data Protection key ring is empty while {mirrors.Count} encrypted credential "
                + "mirror(s) exist, so none of them can be decrypted.",
                [
                    new DoctorRemedy(
                        "credentials.restore_key_ring",
                        null,
                        DoctorRemedyCommands.KeyList,
                        "The key ring is machine-local and is never carried by a backup archive. Re-store each credential "
                        + "the inventory reports as unreadable."),
                ]));

        }

        SensitivePathPosture posture = SecureFilePermissions
            .InspectOwnerOnlyPosture()
            .FirstOrDefault(candidate => candidate.Path == keyRing);

        return Task.FromResult(new DoctorFinding(
            DoctorOutcome.Healthy,
            $"The Data Protection key ring holds {keyCount} key file(s) protecting "
            + $"{mirrors.Count} credential mirror(s). No key material is read by this check."
            + (posture.Exists && !posture.IsOwnerOnly
                ? " Its permissions are reported separately by 'permissions.posture'."
                : string.Empty)));

    }

}

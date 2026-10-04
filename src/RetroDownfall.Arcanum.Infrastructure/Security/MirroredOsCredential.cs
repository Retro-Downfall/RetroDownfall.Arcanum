using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// How one mirrored credential departs from the shared policy. Everything not named here is the
/// same for every credential that keeps an OS copy and an encrypted file mirror.
/// </summary>
/// <param name="Description">Names the credential in messages, e.g. "the master API key".</param>
/// <param name="RecoveryHint">Appended to an OS failure that carried no message of its own.</param>
/// <param name="SynchronizeMirrorFromOs">
/// Rewrites the mirror whenever an OS read returns a value the mirror does not hold.
/// </param>
/// <param name="MirrorRestoreFailureIsCorrupt">
/// A mirror that cannot be restored into an empty OS store is refused rather than served.
/// </param>
/// <param name="OsReadFailureWithoutMirrorIsCorrupt">
/// A failed OS read with no mirror reports <see cref="SecretStoreReadStatus.Corrupted"/>, because
/// <see cref="SecretStoreReadStatus.Missing"/> would authorize minting over a credential that may
/// still exist.
/// </param>
/// <param name="RequireOsWrite">
/// A save the OS store refuses throws instead of degrading to the mirror alone.
/// </param>
internal sealed record MirroredCredentialPolicy(
    string Description,
    string RecoveryHint,
    bool SynchronizeMirrorFromOs = false,
    bool MirrorRestoreFailureIsCorrupt = false,
    bool OsReadFailureWithoutMirrorIsCorrupt = false,
    bool RequireOsWrite = false);

/// <summary>
/// The single read/peek/save/delete policy for a credential kept in the OS credential store with an
/// owner-only, Data Protection-encrypted file mirror (DESIGN §11.2). Each instance owns one account
/// and one gate; operations on that account are serialized through it.
/// </summary>
/// <remarks>
/// The gate is a <see cref="SemaphoreSlim"/> whose wait handle is never observed, so it is never
/// disposed: disposing it under an in-flight caller would turn that caller's own release into an
/// <see cref="ObjectDisposedException"/> that replaced its real result.
/// </remarks>
internal sealed class MirroredOsCredential(
    IOsCredentialStore osStore,
    string account,
    CredentialMirror mirror,
    MirroredCredentialPolicy policy,
    ILogger? logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Reads the credential, preferring the OS copy and promoting a mirror into an empty OS store.
    /// </summary>
    internal async Task<SecretStoreReadResult> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = osStore.TryGet(ArcanumCredentialIdentity.Service, account);

            if (os.Status == OsCredentialStoreStatus.Ok && !string.IsNullOrWhiteSpace(os.Value))
            {
                if (policy.SynchronizeMirrorFromOs)
                {
                    await SynchronizeMirrorAsync(os.Value).ConfigureAwait(false);
                }

                return SecretStoreReadResult.Ok(os.Value);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                logger?.LogWarning(
                    "OS credential store read failed for {Credential}: {Message}",
                    policy.Description,
                    os.Message);
            }

            SecretStoreReadResult fromMirror = await mirror.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (fromMirror.Status == SecretStoreReadStatus.Ok)
            {
                if (os.Status is OsCredentialStoreStatus.NotFound or OsCredentialStoreStatus.Ok)
                {
                    SecretStoreReadResult? refused = RestoreMirrorIntoOsStore(fromMirror.Value!);

                    if (refused is not null)
                    {
                        return refused;
                    }
                }
                else if (os.Status == OsCredentialStoreStatus.Unavailable)
                {
                    logger?.LogWarning(
                        "OS credential store unavailable ({Message}); using the encrypted mirror for {Credential}.",
                        os.Message,
                        policy.Description);
                }

                return fromMirror;
            }

            if (fromMirror.Status != SecretStoreReadStatus.Missing)
            {
                return fromMirror;
            }

            // A read that FAILED leaves the credential's existence unknown, so it must not collapse to
            // Missing where Missing is what authorises minting a replacement: minting overwrites — or,
            // when the write fails too, deletes — whatever credential is still there. Unavailable is
            // different: the backend is absent, so nothing of ours can be living in it.
            if (os.Status == OsCredentialStoreStatus.Failed && policy.OsReadFailureWithoutMirrorIsCorrupt)
            {
                return SecretStoreReadResult.Corrupted(
                    $"OS key storage failed while reading {policy.Description}. "
                    + (os.Message ?? policy.RecoveryHint));
            }

            return fromMirror;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Reads the credential without promoting, repairing, or persisting anything. A failed OS read is
    /// not interchangeable with absence: its hidden value may supersede the mirror, so the peek fails
    /// closed rather than returning a potentially stale credential.
    /// </summary>
    internal async Task<SecretStoreReadResult> PeekAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = osStore.TryGet(ArcanumCredentialIdentity.Service, account);

            if (os.Status == OsCredentialStoreStatus.Ok && !string.IsNullOrWhiteSpace(os.Value))
            {
                return SecretStoreReadResult.Ok(os.Value);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                return SecretStoreReadResult.Corrupted(
                    $"OS key storage failed while peeking at {policy.Description}. "
                    + (os.Message ?? policy.RecoveryHint));
            }

            return await mirror.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Stores the credential in the OS store and its mirror. Once the OS store has been changed the
    /// mirror write is bookkeeping for a committed change, so it no longer observes the caller's token.
    /// </summary>
    internal async Task SaveAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = osStore.Set(ArcanumCredentialIdentity.Service, account, value);

            if (os.Status == OsCredentialStoreStatus.Ok)
            {
                try
                {
                    await mirror.WriteAsync(value, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // The OS credential is safely stored and authoritative. Keep serving while making
                    // the failed mirror visible without disclosing the credential.
                    logger?.LogWarning(
                        exception,
                        "OS credential save succeeded for {Credential}, but its encrypted mirror failed.",
                        policy.Description);
                }

                return;
            }

            if (policy.RequireOsWrite)
            {
                throw new InvalidOperationException(
                    $"{Capitalized(policy.Description)} could not be saved to OS key storage: "
                    + (os.Message ?? os.Status.ToString()));
            }

            logger?.LogWarning(
                "OS credential store save failed for {Credential} ({Status}: {Message}); using the encrypted mirror.",
                policy.Description,
                os.Status,
                os.Message);

            PurgeSupersededOsCredential(os);

            await mirror.WriteAsync(value, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>Deletes the OS copy and the mirror file.</summary>
    internal async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = osStore.Delete(ArcanumCredentialIdentity.Service, account);

            string path = mirror.Path;

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (os.Status == OsCredentialStoreStatus.Failed)
            {
                throw new InvalidOperationException(
                    "The encrypted mirror was deleted, but the OS credential store could not delete "
                    + $"{policy.Description}.");
            }

            if (os.Status == OsCredentialStoreStatus.Unavailable)
            {
                logger?.LogWarning(
                    "The encrypted mirror for {Credential} was deleted while the OS credential store was unavailable.",
                    policy.Description);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Promotes a mirror into an OS store that holds nothing of ours. Returns the refusal to serve
    /// when the policy treats a failed promotion as fatal, otherwise null.
    /// </summary>
    private SecretStoreReadResult? RestoreMirrorIntoOsStore(string value)
    {
        OsCredentialStoreResult restore = osStore.Set(ArcanumCredentialIdentity.Service, account, value);

        if (restore.Status == OsCredentialStoreStatus.Ok)
        {
            logger?.LogInformation(
                "Migrated {Credential} from its encrypted mirror into the OS credential store ({Service}/{Account}).",
                policy.Description,
                ArcanumCredentialIdentity.Service,
                account);

            return null;
        }

        if (policy.MirrorRestoreFailureIsCorrupt)
        {
            return SecretStoreReadResult.Corrupted(
                $"The encrypted mirror of {policy.Description} exists, but it could not be restored "
                + $"to OS key storage: {restore.Message}");
        }

        logger?.LogWarning(
            "Could not migrate {Credential} into the OS credential store ({Status}: {Message}); using the encrypted mirror.",
            policy.Description,
            restore.Status,
            restore.Message);

        return null;
    }

    /// <summary>
    /// Makes the mirror agree with the canonical OS credential. A mirror failure must not make a
    /// healthy installation unavailable: the OS credential remains authoritative.
    /// </summary>
    private async Task SynchronizeMirrorAsync(string value)
    {
        try
        {
            SecretStoreReadResult current = await mirror.ReadAsync(CancellationToken.None).ConfigureAwait(false);

            if (current.Status == SecretStoreReadStatus.Ok
                && string.Equals(current.Value, value, StringComparison.Ordinal))
            {
                return;
            }

            await mirror.WriteAsync(value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                exception,
                "{Credential} was read from OS storage, but its encrypted mirror could not be synchronized.",
                Capitalized(policy.Description));
        }
    }

    /// <summary>
    /// Reads prefer the OS credential over the mirror, so a failed OS write has to take the superseded
    /// credential with it — otherwise the replaced credential keeps being used and the newly stored one
    /// never takes effect. When the credential can neither be replaced nor removed the save fails
    /// closed, before the mirror is rewritten, rather than reporting a replacement it did not perform.
    /// </summary>
    private void PurgeSupersededOsCredential(OsCredentialStoreResult save)
    {
        OsCredentialStoreResult purge = osStore.Delete(ArcanumCredentialIdentity.Service, account);

        if (purge.Status is OsCredentialStoreStatus.Ok or OsCredentialStoreStatus.NotFound)
        {
            return;
        }

        if (!osStore.IsAvailable)
        {
            // No reachable backend at all: the mirror is the documented operating mode here, and
            // every read in this state resolves through it.
            logger?.LogWarning(
                "OS key storage is unavailable ({Message}); the mirror for {Credential} was written without "
                + "reconciling any earlier OS credential.",
                purge.Message,
                policy.Description);

            return;
        }

        throw new InvalidOperationException(
            $"{Capitalized(policy.Description)} could not be written to OS key storage ({save.Status}), "
            + $"and the superseded OS credential could not be removed ({purge.Status}): "
            + (purge.Message ?? "no detail reported.")
            + " The previous value would keep being used, so nothing was changed.");
    }

    private static string Capitalized(string description) =>
        description.Length == 0
            ? description
            : char.ToUpperInvariant(description[0]) + description[1..];
}

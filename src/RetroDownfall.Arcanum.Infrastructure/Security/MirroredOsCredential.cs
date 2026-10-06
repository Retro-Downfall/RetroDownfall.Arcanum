using System.Diagnostics;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// How one mirrored credential departs from the shared policy. Everything not named here is the
/// same for every credential that keeps an OS copy and an encrypted file mirror.
/// </summary>
/// <param name="Description">Names the credential in messages, e.g. "the master API key".</param>
/// <param name="RecoveryHint">
/// Appended to an OS failure that carried no message of its own, and to the refusal of a stale mirror
/// that OS key storage holds no copy of.
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
    bool MirrorRestoreFailureIsCorrupt = false,
    bool OsReadFailureWithoutMirrorIsCorrupt = false,
    bool RequireOsWrite = false);

/// <summary>
/// The single read/peek/save/delete policy for a credential kept in the OS credential store with an
/// owner-only, Data Protection-encrypted file mirror (DESIGN §11.2). Each instance owns one account
/// and one gate; operations on that account are serialized through it.
/// </summary>
/// <remarks>
/// <para>The gate is a <see cref="SemaphoreSlim"/> whose wait handle is never observed, so it is never
/// disposed: disposing it under an in-flight caller would turn that caller's own release into an
/// <see cref="ObjectDisposedException"/> that replaced its real result.</para>
/// <para>The mirror follows the OS copy for every credential: an OS read that returns a value the
/// mirror does not hold rewrites the mirror, so a credential rotated in OS key storage by another tool
/// never leaves its superseded value to be served at the next locked-keychain read.</para>
/// <para>Every OS read is bounded. The platform call is synchronous and cannot be cancelled — a
/// Keychain dialog nobody answers parks it indefinitely — so it runs on its own thread, and a caller
/// stops waiting after <c>osReadTimeout</c> and treats the read as failed. The abandoned call stays
/// the one outstanding read: later readers join it rather than raise a second prompt (and fail at
/// once when it is already older than the timeout), and a write waits for it to return before
/// touching the OS store.</para>
/// </remarks>
internal sealed class MirroredOsCredential(
    IOsCredentialStore osStore,
    string account,
    CredentialMirror mirror,
    MirroredCredentialPolicy policy,
    ILogger? logger,
    TimeSpan? osReadTimeout = null)
{
    /// <summary>
    /// Long enough to answer a one-off OS prompt, short enough that a parked call cannot wedge
    /// startup or request authentication.
    /// </summary>
    internal static readonly TimeSpan DefaultOsReadTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly TimeSpan _osReadTimeout = osReadTimeout ?? DefaultOsReadTimeout;

    private readonly Lock _osReadSync = new();

    private Task<OsCredentialStoreResult>? _osRead;

    private long _osReadStartedAt;

    private volatile bool _servingMirrorDuringOsFailure;

    /// <summary>
    /// True while the startup read (<see cref="GetAtStartupAsync"/>) was answered from the mirror
    /// because the OS read failed, and no OS read has answered since — an answer that the store holds
    /// nothing counts. That read adopted the mirror's value for this process; <see cref="PeekAsync"/>
    /// still fails closed for it (DESIGN §11.2 item 4). An ordinary runtime <see cref="GetAsync"/>
    /// served from the mirror never sets it, and a save clears it only once the save has committed.
    /// </summary>
    internal bool ServingMirrorDuringOsFailure => _servingMirrorDuringOsFailure;

    private string StaleMarkerPath => StaleMarkerPathFor(mirror.Path);

    /// <summary>
    /// True while the mirror at <paramref name="mirrorPath"/> is marked as possibly holding a superseded
    /// value. Every reader that can answer from a mirror honours it: this helper's own reads, and the
    /// backup snapshot, which reads the mirrors without healing them and so without an instance.
    /// </summary>
    internal static bool IsMirrorMarkedStale(string mirrorPath) => File.Exists(StaleMarkerPathFor(mirrorPath));

    private static string StaleMarkerPathFor(string mirrorPath) => mirrorPath + ".stale";

    /// <summary>
    /// Reads the credential, preferring the OS copy and promoting a mirror into an empty OS store.
    /// When the OS store cannot answer, a mirror is served — unless a mirror write after a committed
    /// OS change failed and left it marked stale, in which case it is refused.
    /// </summary>
    internal Task<SecretStoreReadResult> GetAsync(CancellationToken cancellationToken) =>
        GetCoreAsync(adoptMirrorServedOverOsFailure: false, cancellationToken);

    /// <summary>
    /// <see cref="GetAsync"/> for the host's one startup read. A mirror served because the OS read
    /// failed is adopted for this process (<see cref="ServingMirrorDuringOsFailure"/>), so a
    /// locked-keychain boot keeps authenticating its key once the startup digest's TTL lapses.
    /// </summary>
    internal Task<SecretStoreReadResult> GetAtStartupAsync(CancellationToken cancellationToken) =>
        GetCoreAsync(adoptMirrorServedOverOsFailure: true, cancellationToken);

    private async Task<SecretStoreReadResult> GetCoreAsync(
        bool adoptMirrorServedOverOsFailure,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            OsCredentialStoreResult os = await ReadOsAsync(cancellationToken).ConfigureAwait(false);

            NoteOsAnswer(os);

            if (os.Status == OsCredentialStoreStatus.Ok && !string.IsNullOrWhiteSpace(os.Value))
            {
                await SynchronizeMirrorAsync(os.Value).ConfigureAwait(false);

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

            if (fromMirror.Status == SecretStoreReadStatus.Ok && MirrorIsMarkedStale())
            {
                return StaleMirrorRefusal(os);
            }

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
                else if (adoptMirrorServedOverOsFailure)
                {
                    // A locked keychain at startup: serve the current mirror (item 4) and remember that
                    // this process adopted it without the OS store being able to confirm it.
                    _servingMirrorDuringOsFailure = true;
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
        // A peek never queues longer than one read may take: the holder may itself be waiting out a
        // parked OS call, and the request path must fail closed rather than stack behind it.
        if (!await _gate.WaitAsync(_osReadTimeout, cancellationToken).ConfigureAwait(false))
        {
            return SecretStoreReadResult.Corrupted(
                $"OS key storage failed while peeking at {policy.Description}. " + TimedOutMessage());
        }

        try
        {
            OsCredentialStoreResult os = await ReadOsAsync(cancellationToken).ConfigureAwait(false);

            NoteOsAnswer(os);

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

            SecretStoreReadResult fromMirror = await mirror.ReadAsync(cancellationToken).ConfigureAwait(false);

            return fromMirror.Status == SecretStoreReadStatus.Ok && MirrorIsMarkedStale()
                ? StaleMirrorRefusal(os)
                : fromMirror;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Asks OS key storage alone whether it holds the credential, joining the one outstanding read
    /// rather than starting a second: a probe made after a startup read that timed out on a parked
    /// prompt fails at once instead of stacking another prompt beside it. Bounded by the same OS-read
    /// timeout as every other read, gate wait included.
    /// </summary>
    internal async Task<OsCredentialStoreResult> ProbeOsAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(_osReadTimeout, cancellationToken).ConfigureAwait(false))
        {
            return OsCredentialStoreResult.Failed(TimedOutMessage());
        }

        try
        {
            OsCredentialStoreResult os = await ReadOsAsync(cancellationToken).ConfigureAwait(false);

            NoteOsAnswer(os);

            return os;
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
            await WaitForOutstandingOsReadAsync(cancellationToken).ConfigureAwait(false);

            OsCredentialStoreResult os = osStore.Set(ArcanumCredentialIdentity.Service, account, value);

            if (os.Status == OsCredentialStoreStatus.Ok)
            {
                // Committed: whatever this process adopted at startup is superseded by the saved value.
                _servingMirrorDuringOsFailure = false;

                try
                {
                    await mirror.WriteAsync(value, CancellationToken.None).ConfigureAwait(false);

                    ClearStaleMarker();
                }
                catch (Exception exception)
                {
                    // The OS credential is safely stored and authoritative. Keep serving, but the
                    // mirror still holds the superseded value: mark it so no locked-keychain read can
                    // ever serve it.
                    logger?.LogWarning(
                        exception,
                        "OS credential save succeeded for {Credential}, but its encrypted mirror failed.",
                        policy.Description);

                    MarkMirrorStale();
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

            // A refused save changes nothing, so it leaves any startup adoption in place: the adopted
            // key is still the live one. Only the mirror write below commits the new value.
            PurgeSupersededOsCredential(os);

            await mirror.WriteAsync(value, CancellationToken.None).ConfigureAwait(false);

            _servingMirrorDuringOsFailure = false;

            ClearStaleMarker();
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
            await WaitForOutstandingOsReadAsync(cancellationToken).ConfigureAwait(false);

            OsCredentialStoreResult os = osStore.Delete(ArcanumCredentialIdentity.Service, account);

            string path = mirror.Path;

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(StaleMarkerPath))
            {
                File.Delete(StaleMarkerPath);
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
    /// One bounded OS read. Joins the outstanding read when there is one, so a parked call is never
    /// stacked with another; a read already older than the timeout fails at once.
    /// </summary>
    private async Task<OsCredentialStoreResult> ReadOsAsync(CancellationToken cancellationToken)
    {
        Task<OsCredentialStoreResult> read;

        long startedAt;

        lock (_osReadSync)
        {
            if (_osRead is null || _osRead.IsCompleted)
            {
                _osReadStartedAt = Stopwatch.GetTimestamp();

                _osRead = Task.Run(
                    () => osStore.TryGet(ArcanumCredentialIdentity.Service, account),
                    CancellationToken.None);
            }

            read = _osRead;

            startedAt = _osReadStartedAt;
        }

        TimeSpan remaining = _osReadTimeout - Stopwatch.GetElapsedTime(startedAt);

        if (remaining > TimeSpan.Zero)
        {
            try
            {
                return await read.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Abandoned, not cancelled: the call keeps its thread until the OS returns, and it
                // stays the outstanding read that every later caller joins.
            }
        }

        logger?.LogWarning(
            "OS key storage did not answer within {Timeout} while reading {Credential}; failing closed.",
            _osReadTimeout,
            policy.Description);

        return OsCredentialStoreResult.Failed(TimedOutMessage());
    }

    /// <summary>
    /// A write must not run alongside a parked read: each would raise its own OS prompt. Waits for the
    /// outstanding read to return; its outcome was already reported to the readers that joined it.
    /// </summary>
    private async Task WaitForOutstandingOsReadAsync(CancellationToken cancellationToken)
    {
        Task<OsCredentialStoreResult>? read;

        lock (_osReadSync)
        {
            read = _osRead;
        }

        if (read is null || read.IsCompleted)
        {
            return;
        }

        try
        {
            _ = await read.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The read's own failure belongs to its readers; the write proceeds once it has returned.
        }
    }

    /// <summary>
    /// Any answer from OS key storage other than a failure — a value, an empty store, an absent
    /// backend — ends a startup adoption: from then on the ordinary peek can answer for itself.
    /// </summary>
    private void NoteOsAnswer(OsCredentialStoreResult os)
    {
        if (os.Status != OsCredentialStoreStatus.Failed)
        {
            _servingMirrorDuringOsFailure = false;
        }
    }

    private string TimedOutMessage() =>
        $"OS key storage did not answer within {_osReadTimeout.TotalSeconds:0.###} seconds. Answer or "
        + "dismiss any pending OS prompt, then retry.";

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
    /// Makes the mirror agree with the canonical OS credential, for every mirrored credential. A mirror
    /// failure must not make a healthy installation unavailable: the OS credential remains
    /// authoritative, and the mirror is marked stale instead.
    /// </summary>
    private async Task SynchronizeMirrorAsync(string value)
    {
        try
        {
            SecretStoreReadResult current = await mirror.ReadAsync(CancellationToken.None).ConfigureAwait(false);

            if (current.Status != SecretStoreReadStatus.Ok
                || !string.Equals(current.Value, value, StringComparison.Ordinal))
            {
                await mirror.WriteAsync(value, CancellationToken.None).ConfigureAwait(false);
            }

            // The mirror now holds the canonical value, whether it already did or was just rewritten.
            ClearStaleMarker();
        }
        catch (Exception exception)
        {
            // The OS credential was rotated out of band (or the mirror was lost) and the copy could
            // not follow it: the mirror may be stale.
            logger?.LogWarning(
                exception,
                "{Credential} was read from OS storage, but its encrypted mirror could not be synchronized.",
                Capitalized(policy.Description));

            MarkMirrorStale();
        }
    }

    private bool MirrorIsMarkedStale() => IsMirrorMarkedStale(mirror.Path);

    /// <summary>
    /// The refusal of a mirror marked stale, with the remedy that applies. While OS key storage cannot
    /// answer, the next read it does answer re-synchronizes the mirror and clears the marker. When it
    /// answers that it holds no copy, there is nothing to re-synchronize from: only storing the
    /// credential again (or restoring it) replaces the mirror.
    /// </summary>
    private SecretStoreReadResult StaleMirrorRefusal(OsCredentialStoreResult os)
    {
        logger?.LogWarning(
            "The encrypted mirror of {Credential} is marked stale and is not served while OS key storage cannot confirm it ({Status}).",
            policy.Description,
            os.Status);

        const string Stale = "may be older than the OS credential (a mirror write after a change failed)";

        return SecretStoreReadResult.Corrupted(
            os.Status == OsCredentialStoreStatus.Failed
                ? $"The encrypted mirror of {policy.Description} {Stale}, so it is not used while OS key "
                    + "storage cannot answer. Unlock or repair OS key storage and retry; the next read it "
                    + "answers re-synchronizes the mirror."
                : $"The encrypted mirror of {policy.Description} {Stale}, and OS key storage holds no copy "
                    + "to confirm it, so it is not used. Store the credential again to replace it. "
                    + policy.RecoveryHint);
    }

    /// <summary>
    /// Durably records that the mirror may hold a superseded value. When even the marker cannot be
    /// written the stale mirror is deleted instead: a missing mirror fails closed, a stale one would
    /// be served.
    /// </summary>
    private void MarkMirrorStale()
    {
        try
        {
            string markerPath = StaleMarkerPath;

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(
                Path.GetDirectoryName(markerPath) ?? throw new InvalidOperationException("Invalid mirror path."));

            OwnerOnlyAtomicFile.Write(markerPath, StaleMarkerContent);

            return;
        }
        catch (Exception markerFailure)
        {
            logger?.LogWarning(
                markerFailure,
                "Could not mark the encrypted mirror of {Credential} stale; removing the mirror instead.",
                policy.Description);
        }

        try
        {
            File.Delete(mirror.Path);
        }
        catch (Exception deleteFailure) when (deleteFailure is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(
                deleteFailure,
                "The encrypted mirror of {Credential} may be stale and could be neither marked nor removed.",
                policy.Description);
        }
    }

    private void ClearStaleMarker()
    {
        try
        {
            if (File.Exists(StaleMarkerPath))
            {
                File.Delete(StaleMarkerPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left in place the marker keeps a now-current mirror refused, which fails closed.
            logger?.LogWarning(
                exception,
                "Could not clear the stale marker of the encrypted mirror of {Credential}.",
                policy.Description);
        }
    }

    private static ReadOnlySpan<byte> StaleMarkerContent =>
        "The encrypted mirror beside this file may be older than the OS credential.\n"u8;

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

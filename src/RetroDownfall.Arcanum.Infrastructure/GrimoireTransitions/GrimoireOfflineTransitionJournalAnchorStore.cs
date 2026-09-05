using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal enum GrimoireOfflineTransitionAnchorWriteStage : byte
{

    Opening,

    Advance,

    Closed,

}

internal sealed partial class GrimoireOfflineTransitionJournalAnchorStore
{

    private readonly IOsCredentialStore _credentials;

    private readonly Action<string>? _afterStep;

    private readonly Func<string, bool>? _failBeforeStep;

    internal GrimoireOfflineTransitionJournalAnchorStore(
        IOsCredentialStore credentials,
        Action<string>? afterStep = null,
        Func<string, bool>? failBeforeStep = null)
    {

        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

        _afterStep = afterStep;

        _failBeforeStep = failBeforeStep;

    }

    internal Result<GrimoireOfflineTransitionAnchorV1?> Read(
        GrimoireOfflineTransitionJournalLocation location)
    {

        ArgumentNullException.ThrowIfNull(location);

        OsCredentialStoreResult stored;

        try
        {

            stored = _credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                Account(location));

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Unavailable<GrimoireOfflineTransitionAnchorV1?>();

        }

        if (stored.Status is OsCredentialStoreStatus.NotFound)
        {

            return Result<GrimoireOfflineTransitionAnchorV1?>.Success(null);

        }

        if (stored.Status is not OsCredentialStoreStatus.Ok)
        {

            return Unavailable<GrimoireOfflineTransitionAnchorV1?>();

        }

        Result<GrimoireOfflineTransitionAnchorV1> decoded =
            GrimoireOfflineTransitionJournalAuthenticator.DecodeAnchor(stored.Value);

        if (decoded.IsFailure || !FixedBindingsMatch(location, decoded.Value))
        {

            return Integrity<GrimoireOfflineTransitionAnchorV1?>();

        }

        return decoded.Value;

    }

    internal Result WriteGenesisAndVerify(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        Guid installationId)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        if (installationId == Guid.Empty)
        {

            return Integrity();

        }

        Result<GrimoireOfflineTransitionAnchorV1?> stored = Read(location);

        if (stored.IsFailure)
        {

            return stored.Error;

        }

        if (stored.Value is not null)
        {

            return RevisionConflict();

        }

        GrimoireOfflineTransitionAnchorV1 genesis = new(
            GrimoireOfflineTransitionJournalAuthenticator.AnchorVersion,
            location.ProfileNamespace.Digest,
            installationId,
            SlotEpoch: 0,
            GrimoireOfflineTransitionAnchorState.Closed,
            OperationId: null,
            Kind: null,
            PayloadVersion: null,
            Revision: 0,
            EnvelopeDigest: null,
            location.JournalLocationDigest);

        return WriteAndVerify(location, genesis, "genesis");

    }

    internal Result CompareWriteAndVerify(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionAnchorV1 expected,
        GrimoireOfflineTransitionAnchorV1 next,
        GrimoireOfflineTransitionAnchorWriteStage stage)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        ArgumentNullException.ThrowIfNull(expected);

        ArgumentNullException.ThrowIfNull(next);

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        if (!Enum.IsDefined(stage)
            || !FixedBindingsMatch(location, expected)
            || !FixedBindingsMatch(location, next)
            || GrimoireOfflineTransitionJournalAuthenticator.ValidateAnchor(expected).IsFailure
            || GrimoireOfflineTransitionJournalAuthenticator.ValidateAnchor(next).IsFailure)
        {

            return Integrity();

        }

        Result matches = RequireMatches(location, expected);

        if (matches.IsFailure)
        {

            return matches;

        }

        string label = stage switch
        {
            GrimoireOfflineTransitionAnchorWriteStage.Opening => "opening",
            GrimoireOfflineTransitionAnchorWriteStage.Advance => "advance",
            GrimoireOfflineTransitionAnchorWriteStage.Closed => "closed",
            _ => throw new InvalidOperationException("The anchor write stage was not defined."),
        };

        return WriteAndVerify(location, next, label);

    }

    internal Result RequireMatches(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionAnchorV1 expected)
    {

        ArgumentNullException.ThrowIfNull(location);

        ArgumentNullException.ThrowIfNull(expected);

        Result<GrimoireOfflineTransitionAnchorV1?> stored = Read(location);

        if (stored.IsFailure)
        {

            return stored.Error;

        }

        return stored.Value == expected
            ? Result.Success()
            : RevisionConflict();

    }

    private Result WriteAndVerify(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionAnchorV1 anchor,
        string stage)
    {

        Result<string> encoded =
            GrimoireOfflineTransitionJournalAuthenticator.EncodeAnchor(anchor);

        if (encoded.IsFailure)
        {

            return encoded.Error;

        }

        string writtenStep = $"anchor:{stage}-written";

        if (FailsBefore(writtenStep))
        {

            return Unavailable();

        }

        OsCredentialStoreResult written;

        try
        {

            written = _credentials.Set(
                ArcanumCredentialIdentity.Service,
                Account(location),
                encoded.Value);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return Unavailable();

        }

        if (written.Status is not OsCredentialStoreStatus.Ok)
        {

            return Unavailable();

        }

        Emit(writtenStep);

        string readbackStep = $"anchor:{stage}-readback";

        if (FailsBefore(readbackStep))
        {

            return Unavailable();

        }

        Result<GrimoireOfflineTransitionAnchorV1?> readback = Read(location);

        if (readback.IsFailure || readback.Value != anchor)
        {

            return Integrity();

        }

        Emit(readbackStep);

        return Result.Success();

    }

    private static bool FixedBindingsMatch(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionAnchorV1 anchor) =>
        anchor.ProfileNamespaceDigest == location.ProfileNamespace.Digest
        && anchor.JournalLocationDigest == location.JournalLocationDigest;

    private static string Account(GrimoireOfflineTransitionJournalLocation location) =>
        ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(
            location.ProfileNamespace.AccountSuffix);

    private bool FailsBefore(string step) => _failBeforeStep?.Invoke(step) is true;

    private void Emit(string step) => _afterStep?.Invoke(step);

    private static Result RevisionConflict() => new Error(
        ErrorCodes.Covenant.RevisionConflict,
        "The transition journal anchor changed before its checked write.");

    private static Result Integrity() => new Error(
        ErrorCodes.Covenant.IntegrityFailure,
        "The transition journal anchor did not satisfy its integrity contract.");

    private static Result<T> Integrity<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.IntegrityFailure,
        "The transition journal anchor did not satisfy its integrity contract."));

    private static Result Unavailable() => new Error(
        ErrorCodes.Covenant.Unavailable,
        "The transition journal anchor credential is unavailable.");

    private static Result<T> Unavailable<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.Unavailable,
        "The transition journal anchor credential is unavailable."));

}

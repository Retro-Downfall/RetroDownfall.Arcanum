using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Read-only access to the one composite Covenant runtime generation held by this process.
/// </summary>
internal interface ICovenantRuntimeGenerationProvider
{

    CovenantRuntimeGenerationState Current { get; }

}

/// <summary>
/// The sole publisher of the process's live Covenant keys, authority, and availability tuple.
/// </summary>
internal sealed class CovenantRuntimeGenerationProvider
    : ICovenantRuntimeGenerationProvider, IDisposable
{

    private readonly Lock _sync = new();

    private readonly ICovenantRuntimePublicationCheckpoint _publicationCheckpoint;

    private CovenantRuntimeGenerationState _current = CovenantRuntimeGenerationState.Initial;

    private bool _disposed;

    public CovenantRuntimeGenerationProvider()
        : this(CovenantRuntimePublicationCheckpoint.None)
    {
    }

    internal CovenantRuntimeGenerationProvider(
        ICovenantRuntimePublicationCheckpoint publicationCheckpoint) =>
        _publicationCheckpoint = publicationCheckpoint
            ?? throw new ArgumentNullException(nameof(publicationCheckpoint));

    internal CovenantRuntimeGenerationState Current => Volatile.Read(ref _current);

    CovenantRuntimeGenerationState ICovenantRuntimeGenerationProvider.Current => Current;

    internal Lock.Scope EnterScope() => _sync.EnterScope();

    internal Result Initialize(
        CovenantRuntimeGenerationState expected,
        CovenantPreparedEnvelopeKeyGeneration prepared,
        CovenantAuthoritySnapshot authority,
        CovenantAvailabilitySnapshot availability)
    {

        ArgumentNullException.ThrowIfNull(expected);

        ArgumentNullException.ThrowIfNull(prepared);

        ArgumentNullException.ThrowIfNull(authority);

        ArgumentNullException.ThrowIfNull(availability);

        lock (_sync)
        {

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!ReferenceEquals(_current, expected)
                || !ReferenceEquals(expected.Availability, availability))
            {

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "Covenant runtime state changed before bootstrap initialization.");

            }

            if (expected.Keys is not null || expected.AuthoritySlot is not null)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "Covenant runtime authority has already been initialized.");

            }

            if (!prepared.IsOwnedBy(this))
            {

                return new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "The prepared Covenant key generation belongs to another runtime holder.");

            }

            if (!prepared.Matches(authority, availability))
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The prepared Covenant key generation does not match its bootstrap authority and availability.");

            }

            CovenantEnvelopeKeyGeneration keys = prepared.Take();

            CovenantAuthoritySnapshot stamped = authority with
            {

                RuntimeAuthorityGeneration = expected.RuntimeAuthorityGeneration,

            };

            _current = new CovenantRuntimeGenerationState(
                expected.RuntimeAuthorityGeneration,
                keys,
                stamped,
                keys.Snapshot.CanonicalEnvelopeEpoch,
                AuthorityRetired: false,
                RecoveryOwner: null,
                availability);

            return Result.Success();

        }

    }

    internal Result PublishCommitted(
        CovenantRuntimeGenerationState expected,
        CovenantPreparedEnvelopeKeyGeneration prepared,
        CovenantCommittedAuthorityTransition transition,
        CovenantAvailabilitySnapshot availability)
    {

        ArgumentNullException.ThrowIfNull(expected);

        ArgumentNullException.ThrowIfNull(prepared);

        ArgumentNullException.ThrowIfNull(transition);

        ArgumentNullException.ThrowIfNull(availability);

        lock (_sync)
        {

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!ReferenceEquals(_current, expected))
            {

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "Covenant runtime state changed before final publication.");

            }

            if (!prepared.IsOwnedBy(this))
            {

                return new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "The prepared Covenant key generation belongs to another runtime holder.");

            }

            if (!prepared.Matches(transition))
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The prepared Covenant key generation does not match its committed transition.");

            }

            if (!ReferenceEquals(expected.Availability, _current.Availability)
                || transition.Capability.ExpectedGeneration != expected.Availability.Generation)
            {

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The committed capability no longer follows the captured runtime state.");

            }

            if (!MatchesCapability(availability, transition.Capability))
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The committed availability tuple does not match its capability transition.");

            }

            long runtimeGeneration = checked(expected.RuntimeAuthorityGeneration + 1);

            CovenantAuthoritySnapshot authority = new(
                runtimeGeneration,
                transition.InstallationIdentity,
                transition.AuthorityEpoch,
                transition.MasterKeyVersion,
                transition.RecoveryEnvelopeEpoch,
                transition.HostToolsState,
                transition.TransitionId);

            ObservePublication(CovenantRuntimePublicationStep.CommittedBeforeSwap);

            CovenantEnvelopeKeyGeneration keys = prepared.Take();

            _current = new CovenantRuntimeGenerationState(
                runtimeGeneration,
                keys,
                authority,
                transition.CanonicalEnvelopeEpoch,
                AuthorityRetired: false,
                RecoveryOwner: null,
                availability);

            try
            {

                ObservePublication(CovenantRuntimePublicationStep.CommittedAfterSwap);

            }
            finally
            {

                expected.Keys?.Dispose();

            }

            return Result.Success();

        }

    }

    internal CovenantAvailabilitySnapshot PublishAvailability(
        Func<CovenantAvailabilitySnapshot, CovenantAvailabilitySnapshot> mutate)
    {

        ArgumentNullException.ThrowIfNull(mutate);

        lock (_sync)
        {

            ObjectDisposedException.ThrowIf(_disposed, this);

            CovenantRuntimeGenerationState current = _current;

            CovenantAvailabilitySnapshot candidate = mutate(current.Availability);

            if (current.AuthoritySlot is not null
                && candidate.DatasetGeneration != current.Availability.DatasetGeneration)
            {

                throw new InvalidOperationException(
                    "An availability-only publication cannot change the initialized Covenant dataset generation.");

            }

            CovenantAvailabilitySnapshot next = candidate with
            {

                Generation = current.Availability.Generation + 1,

            };

            ObservePublication(CovenantRuntimePublicationStep.AvailabilityBeforeSwap);

            _current = current with { Availability = next };

            ObservePublication(CovenantRuntimePublicationStep.AvailabilityAfterSwap);

            return next;

        }

    }

    internal Result RetireAuthorityGeneration(
        long observedRuntimeAuthorityGeneration,
        CovenantExclusiveRecoveryOwner recoveryOwner)
    {

        if (!recoveryOwner.IsValid)
        {

            throw new ArgumentException("A retired runtime generation requires an exact recovery owner.", nameof(recoveryOwner));

        }

        lock (_sync)
        {

            ObjectDisposedException.ThrowIf(_disposed, this);

            CovenantRuntimeGenerationState current = _current;

            if (current.RuntimeAuthorityGeneration != observedRuntimeAuthorityGeneration)
            {

                return Result.Success();

            }

            long retiredGeneration = checked(current.RuntimeAuthorityGeneration + 1);

            CovenantAuthoritySnapshot? retained = current.AuthoritySlot is { } authority
                ? authority with { RuntimeAuthorityGeneration = retiredGeneration }
                : null;

            _current = new CovenantRuntimeGenerationState(
                retiredGeneration,
                Keys: null,
                retained,
                current.CanonicalEnvelopeEpoch,
                AuthorityRetired: true,
                recoveryOwner,
                current.Availability);

            current.Keys?.Dispose();

            return Result.Success();

        }

    }

    private static bool MatchesCapability(
        CovenantAvailabilitySnapshot availability,
        CovenantCommittedCapabilityTransition capability) =>
        availability.Generation == capability.Generation
        && availability.FeatureEnabled == capability.FeatureEnabled
        && availability.Canonical == capability.Canonical
        && availability.CanonicalSchemaVersion == capability.CanonicalSchemaVersion
        && string.Equals(
            availability.CanonicalInstalledFingerprint,
            capability.CanonicalInstalledFingerprint,
            StringComparison.Ordinal)
        && availability.Accelerator == capability.Accelerator
        && availability.AcceleratorSchemaVersion == capability.AcceleratorSchemaVersion
        && string.Equals(
            availability.AcceleratorInstalledFingerprint,
            capability.AcceleratorInstalledFingerprint,
            StringComparison.Ordinal)
        && availability.DatasetGeneration == capability.DatasetGeneration
        && availability.CanonicalSequence == capability.CanonicalSequence
        && availability.CoreCampaignDeletionSequence == capability.CoreCampaignDeletionSequence
        && availability.AppliedDatasetGeneration == capability.AppliedDatasetGeneration
        && availability.AppliedSequence == capability.AppliedSequence
        && availability.AppliedCampaignDeletionSequence == capability.AppliedCampaignDeletionSequence
        && availability.AcceleratorEpoch == capability.AcceleratorEpoch
        && availability.FtsSynchronization == capability.FtsSynchronization
        && availability.RebuildRequired == capability.RebuildRequired
        && string.Equals(
            availability.CanonicalDiagnosticCode,
            capability.CanonicalDiagnosticCode,
            StringComparison.Ordinal)
        && string.Equals(
            availability.AcceleratorDiagnosticCode,
            capability.AcceleratorDiagnosticCode,
            StringComparison.Ordinal);

    /// <summary>
    /// Delivers a test-only observation without allowing observer behavior to participate in the
    /// domain transaction.
    /// </summary>
    /// <remarks>
    /// This deliberately quarantines every ordinary observer exception. A checkpoint runs while the
    /// holder lock is owned, so propagating one before the swap could strand transferred keys and
    /// propagating one afterward could report failure for an already-published successor. Production
    /// always supplies the no-op implementation.
    /// </remarks>
    private void ObservePublication(CovenantRuntimePublicationStep step)
    {

        try
        {

            _publicationCheckpoint.Reached(step);

        }
        catch (Exception)
        {

            // Observation is non-authoritative and must never alter publication Result semantics.

        }

    }

    public void Dispose()
    {

        lock (_sync)
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            _current.Keys?.Dispose();

            _current = CovenantRuntimeGenerationState.Initial;

        }

    }

}

/// <summary>
/// Test-only observation seam at the two sides of a runtime-holder swap.
/// </summary>
/// <remarks>
/// Called synchronously while the holder lock is held. Implementations carry only the closed step
/// code and must never call the holder or operation gate. An observer is non-authoritative: the
/// holder quarantines its exceptions and never lets it select or alter a publication outcome.
/// Production construction always uses the no-op implementation.
/// </remarks>
internal interface ICovenantRuntimePublicationCheckpoint
{

    void Reached(CovenantRuntimePublicationStep step);

}

internal enum CovenantRuntimePublicationStep : byte
{

    CommittedBeforeSwap = 1,

    CommittedAfterSwap = 2,

    AvailabilityBeforeSwap = 3,

    AvailabilityAfterSwap = 4,

}

internal static class CovenantRuntimePublicationCheckpoint
{

    internal static ICovenantRuntimePublicationCheckpoint None { get; } = new NoOpCheckpoint();

    private sealed class NoOpCheckpoint : ICovenantRuntimePublicationCheckpoint
    {

        public void Reached(CovenantRuntimePublicationStep step)
        {
        }

    }

}

internal sealed record CovenantRuntimeGenerationState(
    long RuntimeAuthorityGeneration,
    CovenantEnvelopeKeyGeneration? Keys,
    CovenantAuthoritySnapshot? AuthoritySlot,
    long? CanonicalEnvelopeEpoch,
    bool AuthorityRetired,
    CovenantExclusiveRecoveryOwner? RecoveryOwner,
    CovenantAvailabilitySnapshot Availability)
{

    internal static CovenantRuntimeGenerationState Initial { get; } = new(
        RuntimeAuthorityGeneration: 1,
        Keys: null,
        AuthoritySlot: null,
        CanonicalEnvelopeEpoch: null,
        AuthorityRetired: false,
        RecoveryOwner: null,
        new CovenantAvailabilitySnapshot(
            Generation: 1,
            FeatureEnabled: false,
            Canonical: CovenantCapabilityState.Unavailable,
            CanonicalSchemaVersion: null,
            CanonicalInstalledFingerprint: null,
            Accelerator: CovenantCapabilityState.Unavailable,
            AcceleratorSchemaVersion: null,
            AcceleratorInstalledFingerprint: null,
            DatasetGeneration: null,
            CanonicalSequence: 0,
            CoreCampaignDeletionSequence: 0,
            AppliedDatasetGeneration: null,
            AppliedSequence: null,
            AppliedCampaignDeletionSequence: null,
            AcceleratorEpoch: 0,
            FtsSynchronization: CovenantFtsSynchronizationState.Unavailable,
            RebuildRequired: true,
            LastHealthTransition: CovenantHealthTransition.Bootstrap,
            CanonicalDiagnosticCode: null,
            AcceleratorDiagnosticCode: null));

    internal CovenantAuthoritySnapshot? ActiveAuthority => AuthorityRetired ? null : AuthoritySlot;

}

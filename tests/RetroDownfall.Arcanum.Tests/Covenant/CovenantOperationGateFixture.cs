using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Test doubles for the three facts the Covenant operation gate compares against, plus the builders
/// that make an exclusive acquisition readable at a call site.
/// </summary>
/// <remarks>
/// The gate's whole job is to notice when one of those facts moves underneath a live lease, so each
/// double is deliberately mutable: a test changes a dataset generation or an authority epoch between
/// acquisition and revalidation and asserts the gate refuses.
/// </remarks>
internal static class CovenantOperationGateFixture
{

    internal static readonly Guid DatasetGeneration = new("11111111-1111-4111-8111-111111111111");

    internal static readonly Guid CampaignOne = new("22222222-2222-4222-8222-222222222222");

    internal static readonly Guid CampaignTwo = new("33333333-3333-4333-8333-333333333333");

    internal static CovenantDigest Digest(byte seed)
    {

        byte[] bytes = new byte[CovenantLimits.DigestBytes];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = unchecked((byte)(seed + index));

        }

        return new CovenantDigest(bytes);

    }

    internal static CovenantExclusiveRecoveryOwner Owner(
        CovenantExclusiveOperation operation,
        Guid? operationId = null,
        byte effectSeed = 7) =>
        new(operationId ?? new Guid("44444444-4444-4444-8444-444444444444"), operation, Digest(effectSeed));

    internal static CanonicalCampaignContext CampaignContext(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 5,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 9,
            rootIdentityDigest: Digest(21));

    internal static CovenantOperationGate CreateGate(
        FakeCovenantAvailability? availability = null,
        FakeCovenantAuthorityProvider? authority = null,
        FakeCovenantCampaignScopeProbe? campaigns = null,
        TimeSpan? drainTimeout = null)
    {

        FakeCovenantAvailability resolvedAvailability = availability ?? new FakeCovenantAvailability();

        FakeCovenantAuthorityProvider resolvedAuthority = authority ?? new FakeCovenantAuthorityProvider();

        CovenantRuntimeGenerationProvider runtime = new();

        if (resolvedAuthority.Current is null)
        {

            _ = runtime.PublishAvailability(_ => resolvedAvailability.Current);

            resolvedAvailability.Bind(runtime);

            resolvedAuthority.Bind(runtime);

            return new CovenantOperationGate(
                runtime,
                campaigns ?? new FakeCovenantCampaignScopeProbe(),
                drainTimeout ?? TimeSpan.FromSeconds(5));

        }

        CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Encoding.UTF8.GetBytes("operation-gate-fixture-master-material"),
            new CovenantEnvelopeBootstrapKeyInput(
                resolvedAuthority.Current!.InstallationIdentity,
                resolvedAuthority.Current.MasterKeyVersion,
                canonicalEnvelopeEpoch: 1,
                resolvedAuthority.Current.RecoveryEnvelopeEpoch,
                resolvedAvailability.Current.DatasetGeneration));

        if (prepared.IsFailure)
        {

            throw new InvalidOperationException(prepared.Error.Message);

        }

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantAvailabilitySnapshot bootAvailability = runtime.PublishAvailability(
            _ => resolvedAvailability.Current);

        CovenantRuntimeGenerationState expected = runtime.Current;

        Result initialized = runtime.Initialize(
            expected,
            owned,
            resolvedAuthority.Current,
            bootAvailability);

        if (initialized.IsFailure)
        {

            throw new InvalidOperationException(initialized.Error.Message);

        }

        resolvedAvailability.Bind(runtime, keys);

        resolvedAuthority.Bind(runtime);

        return new CovenantOperationGate(
            runtime,
            campaigns ?? new FakeCovenantCampaignScopeProbe(),
            drainTimeout ?? TimeSpan.FromSeconds(5));

    }

}

internal sealed class FakeCovenantAvailability : ICovenantAvailability
{

    private CovenantRuntimeGenerationProvider? _runtime;

    private CovenantEnvelopeMasterKeyProvider? _keys;

    private CovenantAvailabilitySnapshot _current = new(
        Generation: 1,
        FeatureEnabled: true,
        Canonical: CovenantCapabilityState.Healthy,
        CanonicalSchemaVersion: 1,
        CanonicalInstalledFingerprint: "fingerprint",
        Accelerator: CovenantCapabilityState.Healthy,
        AcceleratorSchemaVersion: 1,
        AcceleratorInstalledFingerprint: "fingerprint",
        DatasetGeneration: CovenantOperationGateFixture.DatasetGeneration,
        CanonicalSequence: 12,
        CoreCampaignDeletionSequence: 3,
        AppliedDatasetGeneration: CovenantOperationGateFixture.DatasetGeneration,
        AppliedSequence: 12,
        AppliedCampaignDeletionSequence: 3,
        AcceleratorEpoch: 4,
        FtsSynchronization: CovenantFtsSynchronizationState.Synchronized,
        RebuildRequired: false,
        LastHealthTransition: CovenantHealthTransition.Bootstrap,
        CanonicalDiagnosticCode: null,
        AcceleratorDiagnosticCode: null);

    public CovenantAvailabilitySnapshot Current => _runtime?.Current.Availability ?? Volatile.Read(ref _current);

    internal void Bind(
        CovenantRuntimeGenerationProvider runtime,
        CovenantEnvelopeMasterKeyProvider? keys = null)
    {

        _runtime = runtime;

        _keys = keys;

    }

    internal void PublishCommittedDataset(Guid datasetGeneration)
    {

        CovenantRuntimeGenerationProvider runtime = _runtime
            ?? throw new InvalidOperationException("The availability fixture is not runtime-bound.");

        CovenantEnvelopeMasterKeyProvider keys = _keys
            ?? throw new InvalidOperationException("The availability fixture has no live key preparer.");

        CovenantRuntimeGenerationState expected = runtime.Current;

        CovenantAuthoritySnapshot authority = expected.ActiveAuthority
            ?? throw new InvalidOperationException("The availability fixture has no active authority.");

        CovenantAvailabilitySnapshot current = expected.Availability;

        CovenantCommittedCapabilityTransition capability = new(
            current.Generation,
            checked(current.Generation + 1),
            current.FeatureEnabled,
            current.Canonical,
            current.CanonicalSchemaVersion,
            current.CanonicalInstalledFingerprint,
            current.Accelerator,
            current.AcceleratorSchemaVersion,
            current.AcceleratorInstalledFingerprint,
            datasetGeneration,
            CanonicalSequence: 0,
            CoreCampaignDeletionSequence: 0,
            CanonicalAppliedCampaignDeletionSequence: 0,
            CanonicalAppliedSessionDeletionSequence: 0,
            AppliedDatasetGeneration: null,
            AppliedSequence: null,
            AppliedCampaignDeletionSequence: null,
            current.AcceleratorEpoch,
            CovenantFtsSynchronizationState.Dirty,
            RebuildRequired: true,
            CleanupAppliedCampaignSequence: 0,
            CleanupAppliedSessionSequence: 0,
            CleanupFullSweepRequired: true,
            current.CanonicalDiagnosticCode,
            current.AcceleratorDiagnosticCode);

        CovenantCommittedAuthorityTransition transition = new(
            authority.InstallationIdentity,
            authority.AuthorityEpoch,
            authority.MasterKeyVersion,
            checked(expected.CanonicalEnvelopeEpoch!.Value + 1),
            authority.RecoveryEnvelopeEpoch,
            authority.HostToolsState,
            authority.TransitionId,
            capability);

        CovenantAvailability availability = new(runtime);

        Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
            current,
            capability,
            CovenantHealthTransition.Reset);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(transition);

        if (built.IsFailure || prepared.IsFailure)
        {

            throw new InvalidOperationException(
                built.IsFailure ? built.Error.Message : prepared.Error.Message);

        }

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        Result published = runtime.PublishCommitted(
            expected,
            owned,
            transition,
            built.Value);

        if (published.IsFailure)
        {

            throw new InvalidOperationException(published.Error.Message);

        }

    }

    internal void Mutate(Func<CovenantAvailabilitySnapshot, CovenantAvailabilitySnapshot> change)
    {

        if (_runtime is { } runtime)
        {

            _ = runtime.PublishAvailability(change);

            return;

        }

        CovenantAvailabilitySnapshot current = Volatile.Read(ref _current);

        Volatile.Write(ref _current, change(current) with { Generation = current.Generation + 1 });

    }

}

internal sealed class FakeCovenantAuthorityProvider : ICovenantAuthoritySnapshotProvider
{

    private CovenantRuntimeGenerationProvider? _runtime;

    private CovenantAuthoritySnapshot? _current = new(
        RuntimeAuthorityGeneration: 1,
        InstallationIdentity: "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90",
        AuthorityEpoch: 1,
        MasterKeyVersion: 1,
        RecoveryEnvelopeEpoch: 1,
        HostToolsState: CovenantHostToolsState.Clean,
        TransitionId: null);

    public CovenantAuthoritySnapshot? Current => _runtime is { } runtime
        ? runtime.Current.ActiveAuthority
        : Volatile.Read(ref _current);

    internal void Bind(CovenantRuntimeGenerationProvider runtime) => _runtime = runtime;

    internal void Advance()
    {

        if (_runtime is { } runtime)
        {

            _ = runtime.RetireAuthorityGeneration(
                runtime.Current.RuntimeAuthorityGeneration,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair));

            return;

        }

        Volatile.Write(
            ref _current,
            Current! with { AuthorityEpoch = Current.AuthorityEpoch + 1 });

    }

    internal void Clear()
    {

        if (_runtime is { } runtime)
        {

            _ = runtime.RetireAuthorityGeneration(
                runtime.Current.RuntimeAuthorityGeneration,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair));

            return;

        }

        Volatile.Write(ref _current, null);

    }

}

internal sealed class FakeCovenantCampaignScopeProbe : ICovenantCampaignScopeProbe
{

    private readonly Dictionary<Guid, CovenantCampaignScopeState> _states = new()
    {
        [CovenantOperationGateFixture.CampaignOne] = CovenantCampaignScopeState.Live,

        [CovenantOperationGateFixture.CampaignTwo] = CovenantCampaignScopeState.Live,
    };

    internal void Set(Guid campaignId, CovenantCampaignScopeState state)
    {

        lock (_states)
        {

            _states[campaignId] = state;

        }

    }

    public ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        lock (_states)
        {

            return ValueTask.FromResult(
                Result<CovenantCampaignScopeState>.Success(
                    _states.TryGetValue(campaignId, out CovenantCampaignScopeState state)
                        ? state
                        : CovenantCampaignScopeState.Deleted));

        }

    }

}

/// <summary>
/// A one-shot finalizer that records whether the gate invoked it, and can be told to fail.
/// </summary>
internal sealed class RecordingPostDispositionFinalizer(bool succeed = true)
    : ICovenantExclusivePostDispositionFinalizer
{

    private int _invocations;

    internal int Invocations => Volatile.Read(ref _invocations);

    internal CovenantExclusiveLeaseDisposition? ObservedDisposition { get; private set; }

    public ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken)
    {

        _ = Interlocked.Increment(ref _invocations);

        ObservedDisposition = disposition;

        return ValueTask.FromResult(
            succeed
                ? Result.Success()
                : Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, "The durable journal did not advance.")));

    }

}

/// <summary>
/// The real <see cref="CovenantCampaignScopeProbe"/>, over a real bootstrapped Grimoire, against rows
/// the Campaign deletion trigger actually wrote — never the <see cref="FakeCovenantCampaignScopeProbe"/>
/// every gate suite substitutes.
/// </summary>
/// <remarks>
/// Replacing <see cref="CovenantCampaignScopeProbe.HasDeletionEventAsync"/> with a hard-coded
/// answer keeps every other suite green, because no test anywhere constructs the real probe. This is
/// that test.
/// </remarks>
[Collection("Grimoire")]
public sealed class CovenantCampaignScopeProbeTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _workspaceRoot = string.Empty;

    private ArcanumDbContext? _db;

    public CovenantCampaignScopeProbeTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        _workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-scope-probe",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_workspaceRoot);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

        if (Directory.Exists(_workspaceRoot))
        {

            Directory.Delete(_workspaceRoot, recursive: true);

        }

    }

    [SkippableFact]
    public async Task ResolveAsync_reports_deleted_for_a_campaign_the_deletion_trigger_recorded()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CampaignRepository campaigns = new(
            _db!,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Result<Campaign> added = await campaigns.AddAsync(
            new Campaign
            {
                Id = Guid.NewGuid(),
                Name = "probe scope test",
                Path = _workspaceRoot,
                Type = WorkspaceType.Campaign,
                Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(
                    CampaignRepository.DefaultSanctumConfig()),
                CreatedAt = now,
                UpdatedAt = now,
            },
            CancellationToken.None);

        Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Message : string.Empty);

        Guid campaignId = added.Value.Id;

        bool deleted = await campaigns.DeleteAsync(campaignId, CancellationToken.None);

        Assert.True(deleted);

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(
            new RecordingScopedOrdinaryConnectionFactory());

        await using ServiceProvider provider = services.BuildServiceProvider();

        CovenantCampaignScopeProbe probe = new(provider.GetRequiredService<IServiceScopeFactory>());

        Result<CovenantCampaignScopeState> resolved = await probe.ResolveAsync(
            campaignId,
            CancellationToken.None);

        Assert.True(resolved.IsSuccess, resolved.IsFailure ? resolved.Error.Message : string.Empty);

        Assert.Equal(CovenantCampaignScopeState.Deleted, resolved.Value);

    }

}

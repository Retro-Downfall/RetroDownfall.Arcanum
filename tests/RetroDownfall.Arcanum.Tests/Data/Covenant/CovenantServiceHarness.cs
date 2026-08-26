using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Composes the real mutation service over a real encrypted canonical tier, and drives it the way a
/// route does: acquire the lease the operation needs, prepare, then commit under a write lease.
/// </summary>
/// <remarks>
/// Deliberately not written against fakes. The whole contract is that a prepared measurement and a
/// committed change describe the same installation, and a fake store would let both halves agree about
/// a world neither of them read.
///
/// <para>Two seams are substituted and they are the only two. The envelope codec keeps the exact token
/// shape — purpose, timestamps, payload — and skips the cryptography, which has its own vectors and its
/// own suite; the authority snapshot states an operator authority epoch, because a test process has no
/// runtime generation to derive one from. Neither decides, measures, or stores anything.</para>
/// </remarks>
internal sealed class CovenantServiceHarness : IAsyncDisposable
{

    private readonly CovenantCanonicalFixture _fixture;

    private readonly HarnessClock _clock;

    private CovenantServiceHarness(
        CovenantCanonicalFixture fixture,
        CovenantMutationService service,
        CovenantOperationGate gate,
        HarnessClock clock)
    {

        _fixture = fixture;

        _clock = clock;

        Service = service;

        Gate = gate;

    }

    internal CovenantMutationService Service { get; }

    internal CovenantOperationGate Gate { get; }

    internal CovenantCanonicalFixture Fixture => _fixture;

    internal static async Task<CovenantServiceHarness> StartAsync(CancellationToken cancellationToken)
    {

        CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(cancellationToken);

        HarnessClock clock = new();

        CovenantMutationService service = new(
            fixture.Store,
            new CovenantCompiler(),
            new HarnessEnvelopeCodec(),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantMutationKernel(),
            new CovenantCurationKernel(),
            new HarnessAuthority(),
            clock);

        return new CovenantServiceHarness(fixture, service, CovenantOperationGateFixture.CreateGate(), clock);

    }

    internal void Advance(TimeSpan amount) => _clock.Advance(amount);

    internal Task AddCampaignAsync(Guid campaignId, CancellationToken cancellationToken) =>
        _fixture.AddCampaignAsync(campaignId, "Harness Campaign", cancellationToken);

    /// <summary>Writes one entry through the production prepare-and-commit path.</summary>
    internal async Task SetAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        string content,
        CancellationToken cancellationToken)
    {

        Guid mutationId = Guid.CreateVersion7();

        Result<CovenantMutationPreflightDto> prepared;

        await using (ICovenantSnapshotReadLease read = await AcquireReadAsync(scope, campaignId, cancellationToken))
        {

            prepared = await Service.PrepareSetAsync(
                new CovenantSetPrepareRequest(scope, campaignId, key, content, 0, mutationId, false),
                read,
                cancellationToken);

        }

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        await using CovenantWriteLease write = (await Gate.AcquireWriteAsync(Scope(scope, campaignId), cancellationToken)).Value;

        Result<CovenantMutationResultDto> committed = await Service.SetAsync(
            new CovenantSetRequest(scope, campaignId, key, content, 0, mutationId, false, prepared.Value.PreflightToken),
            write,
            cancellationToken);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

    }

    /// <summary>Retires one lane head through the production prepare-and-commit path.</summary>
    internal async Task RetireAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        long expectedRevision,
        CancellationToken cancellationToken)
    {

        Guid mutationId = Guid.CreateVersion7();

        Result<CovenantMutationPreflightDto> prepared;

        await using (ICovenantSnapshotReadLease read = await AcquireReadAsync(scope, campaignId, cancellationToken))
        {

            prepared = await Service.PrepareRetireAsync(
                new CovenantRetirePrepareRequest(
                    scope,
                    campaignId,
                    key,
                    CovenantLane.Confirmed,
                    expectedRevision,
                    mutationId),
                read,
                cancellationToken);

        }

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        await using CovenantWriteLease write =
            (await Gate.AcquireWriteAsync(Scope(scope, campaignId), cancellationToken)).Value;

        Result<CovenantMutationResultDto> committed = await Service.RetireAsync(
            new CovenantRetireRequest(
                scope,
                campaignId,
                key,
                CovenantLane.Confirmed,
                expectedRevision,
                mutationId,
                prepared.Value.PreflightToken),
            write,
            cancellationToken);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

    }

    internal async Task<Result<CovenantMutationPreflightDto>> PrepareCorrectAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        string content,
        Guid targetVersionId,
        string targetRenderedHash,
        long expectedRevision,
        CancellationToken cancellationToken,
        Guid? mutationId = null,
        CovenantLane targetLane = CovenantLane.Confirmed)
    {

        await using ICovenantSnapshotReadLease read = await AcquireReadAsync(scope, campaignId, cancellationToken);

        return await Service.PrepareCorrectAsync(
            new CovenantCorrectPrepareRequest(
                scope,
                campaignId,
                key,
                content,
                targetVersionId,
                targetLane,
                expectedRevision,
                targetRenderedHash,
                mutationId ?? Guid.CreateVersion7()),
            read,
            cancellationToken);

    }

    internal async Task<Result<CovenantMutationResultDto>> CommitCorrectAsync(
        CovenantCorrectRequest request,
        CancellationToken cancellationToken)
    {

        await using CovenantWriteLease write =
            (await Gate.AcquireWriteAsync(Scope(request.Scope, request.CampaignId), cancellationToken)).Value;

        return await Service.CorrectAsync(request, write, cancellationToken);

    }

    internal async Task<Result<CovenantCurationPreflightDto>> PrepareCurationAsync(
        CovenantCurationKind kind,
        CovenantScope scope,
        Guid? campaignId,
        string key,
        CancellationToken cancellationToken,
        long expectedRevision = 0,
        Guid? mutationId = null,
        CovenantLane lane = CovenantLane.Confirmed)
    {

        await using ICovenantSnapshotReadLease read = await AcquireReadAsync(scope, campaignId, cancellationToken);

        return await Service.PrepareCurationAsync(
            new CovenantCurationPrepareRequest(
                kind,
                scope,
                campaignId,
                key,
                lane,
                expectedRevision,
                mutationId ?? Guid.CreateVersion7()),
            read,
            cancellationToken);

    }

    internal async Task<Result<CovenantCurationResultDto>> CommitCurationAsync(
        CovenantCurationRequest request,
        CancellationToken cancellationToken)
    {

        await using CovenantWriteLease write =
            (await Gate.AcquireWriteAsync(Scope(request.Scope, request.CampaignId), cancellationToken)).Value;

        return await Service.CurateAsync(request, write, cancellationToken);

    }

    /// <summary>Prepares and commits one curation change, the way a CLI verb does.</summary>
    internal async Task<Result<CovenantCurationResultDto>> CurateAsync(
        CovenantCurationKind kind,
        CovenantScope scope,
        Guid? campaignId,
        string key,
        CancellationToken cancellationToken,
        long expectedRevision = 0,
        CovenantLane lane = CovenantLane.Confirmed)
    {

        Guid mutationId = Guid.CreateVersion7();

        Result<CovenantCurationPreflightDto> prepared = await PrepareCurationAsync(
            kind,
            scope,
            campaignId,
            key,
            cancellationToken,
            expectedRevision,
            mutationId,
            lane);

        if (prepared.IsFailure)
        {

            return prepared.Error;

        }

        return await CommitCurationAsync(
            new CovenantCurationRequest(
                kind,
                scope,
                campaignId,
                key,
                lane,
                expectedRevision,
                mutationId,
                prepared.Value.PreflightToken),
            cancellationToken);

    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private async Task<ICovenantSnapshotReadLease> AcquireReadAsync(
        CovenantScope scope,
        Guid? campaignId,
        CancellationToken cancellationToken) =>
        scope == CovenantScope.Global
            ? (await Gate.AcquireInstallationReadAsync(cancellationToken)).Value
            : (await Gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(campaignId!.Value), cancellationToken)).Value;

    private static CovenantOperationScope Scope(CovenantScope kind, Guid? campaignId) =>
        kind == CovenantScope.Global
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(campaignId!.Value);

    /// <summary>A clock the suite can move, so token expiry is stated rather than waited for.</summary>
    private sealed class HarnessClock : TimeProvider
    {

        private long _ticks = DateTimeOffset.UnixEpoch.UtcTicks + TimeSpan.TicksPerDay;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond), TimeSpan.Zero);

        internal void Advance(TimeSpan amount) => _ = Interlocked.Add(ref _ticks, amount.Ticks);

    }

    private sealed class HarnessEnvelopeCodec : ICovenantEnvelopeCodec
    {

        private readonly Dictionary<string, CovenantEnvelopeBody> _issued = new(StringComparer.Ordinal);

        public CovenantEnvelopeKeySnapshot KeySnapshot { get; } =
            new(1, 1, 1, Guid.NewGuid().ToString("D"), Guid.NewGuid());

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime,
            DateTimeOffset? issuedAtUtc = null)
        {

            string token = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

            // Honoured, not ignored: a stand-in that stamped its own clock would let the body and the
            // header disagree, and the suite would rediscover that as a flake rather than a bug.
            DateTimeOffset now = issuedAtUtc ?? DateTimeOffset.UtcNow;

            _issued[token] = new CovenantEnvelopeBody(
                purpose,
                1,
                1,
                (ulong)_issued.Count + 1,
                DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds((now + lifetime).ToUnixTimeMilliseconds()),
                payload.ToArray());

            return Result<string>.Success(token);

        }

        public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token) =>
            token is not null && _issued.TryGetValue(token, out CovenantEnvelopeBody? body)
                && body.Purpose == expectedPurpose
                ? Result<CovenantEnvelopeBody>.Success(body)
                : Result<CovenantEnvelopeBody>.Failure(new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This Covenant token is not valid for this purpose."));

    }

    private sealed class HarnessAuthority : ICovenantAuthoritySnapshotProvider
    {

        public CovenantAuthoritySnapshot? Current { get; } = new(
            1,
            Guid.Parse("11111111-2222-3333-4444-555555555555").ToString("D"),
            AuthorityEpoch: 11,
            MasterKeyVersion: 1,
            RecoveryEnvelopeEpoch: 1,
            CovenantHostToolsState.Clean,
            null);

    }

}

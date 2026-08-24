using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The operator's write path, end to end against a real encrypted canonical tier.
/// </summary>
/// <remarks>
/// Deliberately not written against fakes. The whole contract is that a prepared measurement and a
/// committed mutation describe the same installation, and a fake store would let both halves agree
/// about a world neither of them read.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantMutationServiceTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_prepared_set_commits_and_becomes_the_confirmed_head()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantMutationService service = Service(fixture);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Guid mutationId = Guid.CreateVersion7();

        // A Global mutation's effect reaches every Campaign, so measuring it needs installation-wide
        // read authority rather than a scoped lease. A scoped one is refused, which is the contract.
        await using CovenantInstallationReadLease read =
            (await gate.AcquireInstallationReadAsync(Token)).Value;

        Result<CovenantMutationPreflightDto> prepared = await service.PrepareSetAsync(
            new CovenantSetPrepareRequest(
                CovenantScope.Global,
                null,
                "preference.builds",
                "Run build commands from the repository root.",
                ExpectedRevision: 0,
                mutationId,
                Reactivate: false),
            read,
            Token);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Assert.Equal(CovenantLane.Confirmed, prepared.Value.Lane);

        Assert.Equal(0, prepared.Value.CurrentLaneRevision);

        Assert.NotEmpty(prepared.Value.PreflightToken);

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationResultDto> committed = await service.SetAsync(
            new CovenantSetRequest(
                CovenantScope.Global,
                null,
                "preference.builds",
                "Run build commands from the repository root.",
                ExpectedRevision: 0,
                mutationId,
                Reactivate: false,
                prepared.Value.PreflightToken),
            write,
            Token);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

        Assert.False(committed.Value.Replayed);

        Assert.Equal(CovenantMutationOutcome.Applied, committed.Value.Outcome);

        Assert.Equal(1, committed.Value.ResultingLaneRevision);

        Assert.Equal(1L, await CountAsync(fixture, "SELECT COUNT(*) FROM covenant_heads;"));

    }

    [Fact]
    public async Task An_exact_retry_replays_the_committed_answer_rather_than_writing_twice()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantMutationService service = Service(fixture);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Guid mutationId = Guid.CreateVersion7();

        CovenantSetRequest request = await PrepareThenBuildAsync(service, gate, mutationId, Token);

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationResultDto> first = await service.SetAsync(request, write, Token);

        Result<CovenantMutationResultDto> second = await service.SetAsync(request, write, Token);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : string.Empty);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : string.Empty);

        Assert.False(first.Value.Replayed);

        Assert.True(second.Value.Replayed);

        Assert.Equal(first.Value.ResultingLaneRevision, second.Value.ResultingLaneRevision);

        Assert.Equal(1L, await CountAsync(fixture, "SELECT COUNT(*) FROM covenant_versions;"));

    }

    [Fact]
    public async Task Reusing_one_mutation_identity_for_different_content_is_an_idempotency_conflict()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantMutationService service = Service(fixture);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Guid mutationId = Guid.CreateVersion7();

        CovenantSetRequest request = await PrepareThenBuildAsync(service, gate, mutationId, Token);

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        _ = await service.SetAsync(request, write, Token);

        Result<CovenantMutationResultDto> reused = await service.SetAsync(
            request with { Content = "Something else entirely." },
            write,
            Token);

        Assert.True(reused.IsFailure);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, reused.Error.Code);

    }

    [Fact]
    public async Task A_token_prepared_for_one_key_cannot_commit_another()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantMutationService service = Service(fixture);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Guid mutationId = Guid.CreateVersion7();

        CovenantSetRequest request = await PrepareThenBuildAsync(service, gate, mutationId, Token);

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationResultDto> swapped = await service.SetAsync(
            request with { Key = "preference.other", MutationId = Guid.CreateVersion7() },
            write,
            Token);

        Assert.True(swapped.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, swapped.Error.Code);

    }

    [Fact]
    public async Task A_commit_with_no_token_at_all_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantMutationService service = Service(fixture);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantWriteLease write =
            (await gate.AcquireWriteAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantMutationResultDto> refused = await service.SetAsync(
            new CovenantSetRequest(
                CovenantScope.Global,
                null,
                "preference.builds",
                "Run build commands from the repository root.",
                ExpectedRevision: 0,
                Guid.CreateVersion7(),
                Reactivate: false,
                "not-a-token"),
            write,
            Token);

        Assert.True(refused.IsFailure);

    }

    private static async Task<CovenantSetRequest> PrepareThenBuildAsync(
        CovenantMutationService service,
        CovenantOperationGate gate,
        Guid mutationId,
        CancellationToken cancellationToken)
    {

        await using CovenantInstallationReadLease read =
            (await gate.AcquireInstallationReadAsync(cancellationToken)).Value;

        Result<CovenantMutationPreflightDto> prepared = await service.PrepareSetAsync(
            new CovenantSetPrepareRequest(
                CovenantScope.Global,
                null,
                "preference.builds",
                "Run build commands from the repository root.",
                ExpectedRevision: 0,
                mutationId,
                Reactivate: false),
            read,
            cancellationToken);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        return new CovenantSetRequest(
            CovenantScope.Global,
            null,
            "preference.builds",
            "Run build commands from the repository root.",
            ExpectedRevision: 0,
            mutationId,
            Reactivate: false,
            prepared.Value.PreflightToken);

    }

    private static CovenantMutationService Service(CovenantCanonicalFixture fixture) =>
        new(
            fixture.Store,
            new CovenantCompiler(),
            new StubEnvelopeCodec(),
            new FixedCovenantConnectionSource(fixture.Connection),
            new CovenantMutationKernel(),
            new StubAuthority(),
            TimeProvider.System);

    private static async Task<long> CountAsync(CovenantCanonicalFixture fixture, string sql)
    {

        await using Microsoft.Data.Sqlite.SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// A codec that authenticates by construction rather than by key material.
    /// </summary>
    /// <remarks>
    /// The real envelope protocol has its own vectors and its own suite. What this suite is about is
    /// what the service does with a body once one has been authenticated, so the stand-in keeps the
    /// exact shape — purpose, timestamps, payload — and skips only the cryptography.
    /// </remarks>
    private sealed class StubEnvelopeCodec : ICovenantEnvelopeCodec
    {

        private readonly Dictionary<string, CovenantEnvelopeBody> _issued = new(StringComparer.Ordinal);

        public CovenantEnvelopeKeySnapshot KeySnapshot { get; } = new(1, 1, 1, Guid.NewGuid().ToString("D"), Guid.NewGuid());

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime)
        {

            string token = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

            DateTimeOffset now = DateTimeOffset.UtcNow;

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

        public Result<CovenantEnvelopeBody> Decode(
            CovenantEnvelopePurpose expectedPurpose,
            string? token) =>
            token is not null && _issued.TryGetValue(token, out CovenantEnvelopeBody? body)
                && body.Purpose == expectedPurpose
                ? Result<CovenantEnvelopeBody>.Success(body)
                : Result<CovenantEnvelopeBody>.Failure(new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This Covenant token is not valid for this purpose."));

    }

    private sealed class StubAuthority : ICovenantAuthoritySnapshotProvider
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

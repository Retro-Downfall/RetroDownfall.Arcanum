using System.Data;
using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The Covenant erasure chokepoint: agent authorship of an erased key is refused inside the write
/// authority, and a store whose evidence cannot be verified refuses every agent write.
/// </summary>
/// <remarks>
/// <para>Every agent write here reaches the production kernel over a real canonical tier that carries
/// the erasure fingerprint table, with fingerprints computed by a real key. The kernel is handed the
/// gate its callers capture before <c>BEGIN</c>, and each refusal is measured inside the write's own
/// transaction before it rolls back, so "nothing was written" is a fact about the kernel rather than
/// about the rollback that follows it.</para>
///
/// <para>A fingerprinted refusal carries the pin's own code and text, so an agent cannot tell a key the
/// operator erased from one the operator pinned. Key trouble carries the same content-free errors the
/// Saga and Lexicon chokepoints answer with.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantErasureSuppressionTests
{

    private const string Key = "preference.builds";

    private const string OtherKey = "preference.tests";

    private const string ErasedKey = "erased.key";

    private const string OperatorManaged = "This Covenant key is managed by the operator in this scope.";

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task An_agent_proposal_of_a_fingerprinted_Campaign_key_is_refused_as_operator_managed()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, Key, Token);

        CovenantMutationKernel kernel = Kernel(harness);

        long keyEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Applied refused = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, keyEpoch));

        AssertRefused(refused, ErrorCodes.Covenant.ForbiddenAuthority, OperatorManaged);

    }

    /// <summary>
    /// A fingerprint refusal and a pin refusal are the same answer, code and text, so the tool result
    /// an agent sees cannot tell an erased key from a pinned one.
    /// </summary>
    [Fact]
    public async Task A_pin_refusal_and_a_fingerprint_refusal_are_indistinguishable()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, OtherKey, "Run the tests quietly.", Token);

        Result<CovenantCurationResultDto> pinned = await harness.CurateAsync(
            CovenantCurationKind.Pin,
            CovenantScope.Campaign,
            CampaignOne,
            Key,
            Token,
            lane: CovenantLane.Proposed);

        Assert.True(pinned.IsSuccess, pinned.IsFailure ? pinned.Error.Message : string.Empty);

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, OtherKey, Token);

        CovenantMutationKernel kernel = Kernel(harness);

        long pinnedEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        long erasedEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, OtherKey)).KeyEpoch;

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Applied pin = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, pinnedEpoch));

        Applied fingerprint = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, OtherKey, "The model suggests loud tests.", 0, erasedEpoch));

        AssertRefused(pin, ErrorCodes.Covenant.ForbiddenAuthority, OperatorManaged);

        AssertRefused(fingerprint, ErrorCodes.Covenant.ForbiddenAuthority, OperatorManaged);

        Assert.Equal(
            (pin.Result.Error.Code, pin.Result.Error.Message),
            (fingerprint.Result.Error.Code, fingerprint.Result.Error.Message));

    }

    /// <summary>
    /// An approved agent retirement is agent authorship too. The operator's own write that re-created
    /// the key after the erasure does not make the erased identity the agent's to retire.
    /// </summary>
    /// <remarks>
    /// A live head and its fingerprint coexist when the operator re-created the key while this process's
    /// latch held no erasure key: the set cannot release what it cannot check, so it commits and leaves
    /// the fingerprint. An operator re-probe then latches the key, and the agent write meets it.
    /// </remarks>
    [Fact]
    public async Task An_approved_agent_retirement_of_a_fingerprinted_key_is_refused()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await SeedFingerprintWithoutLatchingAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Build from the root.", Token);

        Assert.Equal(1, await ScalarAsync(harness, "SELECT COUNT(*) FROM memory_erasure_fingerprints;"));

        harness.Fixture.ErasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe).Key?.Dispose();

        Assert.Equal(MemoryErasureKeyState.Present, harness.Fixture.ErasureKeys.Latch.State);

        CovenantMutationKernel kernel = Kernel(harness);

        CovenantLaneHeadProbe head = await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Confirmed, Key);

        Assert.Equal(CovenantLaneHeadPresence.Present, head.Presence);

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Applied refused = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentRetire(
                CovenantOperationScope.ForCampaign(CampaignOne),
                Key,
                CovenantLane.Confirmed,
                head.LaneRevision,
                head.KeyEpoch));

        AssertRefused(refused, ErrorCodes.Covenant.ForbiddenAuthority, OperatorManaged);

    }

    /// <summary>
    /// A fingerprint binds an identity, not a key epoch. Writes to the key in another scope move the
    /// dependency epoch the proposal proves current, and the refusal is unchanged.
    /// </summary>
    [Fact]
    public async Task The_refusal_survives_the_key_epoch_moving()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, Key, Token);

        long before = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        CovenantMutationKernel kernel = Kernel(harness);

        long keyEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        Assert.NotEqual(before, keyEpoch);

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Applied refused = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, keyEpoch));

        AssertRefused(refused, ErrorCodes.Covenant.ForbiddenAuthority, OperatorManaged);

    }

    /// <summary>
    /// The exact-scope boundary: agents author only Campaign Proposed lanes, and a Global erasure is a
    /// different identity from the same key inside a Campaign.
    /// </summary>
    [Fact]
    public async Task A_Global_fingerprint_does_not_block_a_Campaign_proposal_of_the_same_key()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Global, null, Key, Token);

        CovenantMutationKernel kernel = Kernel(harness);

        long keyEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Applied applied = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, keyEpoch));

        AssertApplied(applied);

    }

    /// <summary>
    /// With Covenant evidence present and no key in hand, the kernel refuses every agent write, even of
    /// a key nobody erased, and says why from the latch state it was handed (R16).
    /// </summary>
    /// <remarks>
    /// An absent key with evidence is a lost key; every state that might still resolve is an
    /// unavailable one. "none" is the gate a caller that captured nothing passes.
    /// </remarks>
    [Theory]
    [InlineData("none", ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData(nameof(MemoryErasureKeyState.Unresolved), ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData(nameof(MemoryErasureKeyState.Absent), ErrorCodes.MemoryErasure.KeyLost)]
    [InlineData(nameof(MemoryErasureKeyState.Unavailable), ErrorCodes.MemoryErasure.KeyUnavailable)]
    [InlineData(nameof(MemoryErasureKeyState.Malformed), ErrorCodes.MemoryErasure.KeyUnavailable)]
    public async Task Agent_writes_without_a_key_map_the_latch_state_to_its_refusal(string state, string code)
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, ErasedKey, Token);

        CovenantMutationKernel kernel = Kernel(harness);

        long keyEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        using MemoryErasureKeyring? keyring = state == "none"
            ? null
            : harness.KeyringInState(Enum.Parse<MemoryErasureKeyState>(state));

        using CovenantAgentErasureGate gate = keyring is null
            ? CovenantAgentErasureGate.None
            : CovenantAgentErasureGate.FromLatch(keyring);

        Assert.Null(gate.Key);

        Applied refused = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, keyEpoch));

        Error expected = code == ErrorCodes.MemoryErasure.KeyLost
            ? MemoryErasureGuard.KeyLostError
            : MemoryErasureGuard.KeyUnavailableError;

        AssertRefused(refused, expected.Code, expected.Message);

    }

    /// <summary>
    /// Evidence that commits after the gate was captured, while the latch held no key, is met inside
    /// the transaction with no key in hand, and the write is refused rather than let through.
    /// </summary>
    /// <remarks>
    /// This is the first-fingerprint race: a caller reads an unresolved latch before <c>BEGIN</c>, and
    /// an erasure commits in between. Nothing may read the credential store inside the transaction, so
    /// the write fails closed as unavailable.
    /// </remarks>
    [Fact]
    public async Task Evidence_committed_after_the_gate_was_captured_refuses_an_agent_write_it_cannot_verify()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        CovenantMutationKernel kernel = Kernel(harness);

        long keyEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Assert.Null(gate.Key);

        Assert.Equal(MemoryErasureKeyState.Unresolved, gate.LatchState);

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, ErasedKey, Token);

        Applied refused = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, keyEpoch));

        AssertRefused(refused, ErrorCodes.MemoryErasure.KeyUnavailable, MemoryErasureGuard.KeyUnavailableError.Message);

    }

    /// <summary>
    /// A Covenant fingerprint recorded under another key is evidence this key can never match, so it
    /// fails every agent write closed as key loss, even with a present key in hand.
    /// </summary>
    /// <remarks>
    /// The foreign row commits after the gate was captured, beside a valid one: the key in hand changed
    /// out from under the store between the latch read and the classification.
    /// </remarks>
    [Fact]
    public async Task Agent_writes_fail_closed_when_a_Covenant_fingerprint_carries_another_key_id()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, ErasedKey, Token);

        CovenantMutationKernel kernel = Kernel(harness);

        long keyEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Assert.Equal(MemoryErasureKeyState.Present, gate.LatchState);

        Assert.NotNull(gate.Key);

        await InsertForeignFingerprintAsync(harness);

        Applied refused = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, keyEpoch));

        AssertRefused(refused, ErrorCodes.MemoryErasure.KeyLost, MemoryErasureGuard.KeyLostError.Message);

    }

    /// <summary>
    /// An installation that has erased nothing never needs the key and never asks the credential store.
    /// </summary>
    [Fact]
    public async Task Without_Covenant_evidence_agent_writes_need_no_key()
    {

        await using CovenantServiceHarness harness = await StartAsync();

        CovenantMutationKernel kernel = Kernel(harness);

        long keyEpoch = (await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key)).KeyEpoch;

        Applied applied = await ApplyAsync(
            harness,
            kernel,
            CovenantAgentErasureGate.None,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, keyEpoch));

        AssertApplied(applied);

        using (CovenantAgentErasureGate captured = kernel.CaptureErasureGate())
        {

            Assert.Null(captured.Key);

        }

        Assert.Equal(0, harness.Fixture.Credentials.Calls);

    }

    /// <summary>
    /// Operator writes are never refused, with a key in hand or without one. The production commit path
    /// re-creating an erased key releases its fingerprint in the same transaction when its gate holds the
    /// key, and otherwise commits, keeps the fingerprint and says it could not check.
    /// </summary>
    /// <remarks>
    /// The kernel itself never consults the gate for an operator intent: the release is the commit
    /// path's own step, read from the gate it captured before <c>BEGIN</c> and handed to the kernel. A
    /// commit path that handed the kernel any other gate would leave the fingerprint behind.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_operator_set_of_a_fingerprinted_key_still_commits(bool keyInHand)
    {

        await using CovenantServiceHarness harness = await StartAsync();

        if (keyInHand)
        {

            await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, Key, Token);

        }
        else
        {

            await SeedFingerprintWithoutLatchingAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        }

        using (MemoryErasureKey? latched = harness.Fixture.ErasureKeys.TryCopyLatched())
        {

            Assert.Equal(keyInHand, latched is not null);

        }

        CovenantMutationResultDto result = await OperatorSetAsync(harness, Key, "Build from the root.");

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);

        Assert.False(result.Replayed);

        Assert.Equal(keyInHand ? true : null, result.ReleasedErasureFingerprint);

        Assert.Equal(keyInHand ? 0 : 1, await ScalarAsync(harness, "SELECT COUNT(*) FROM memory_erasure_fingerprints;"));

    }

    /// <summary>
    /// The staging probe classifies with the kernel's own mapping, inside its own read transaction, and
    /// reads only the latch: it never touches the credential store, because the turn lease is held.
    /// </summary>
    [Theory]
    [InlineData("present-fingerprinted", CovenantAgentErasureState.Withheld)]
    [InlineData("present-other", CovenantAgentErasureState.Clear)]
    [InlineData("present-foreign", CovenantAgentErasureState.KeyLost)]
    [InlineData(nameof(MemoryErasureKeyState.Unresolved), CovenantAgentErasureState.KeyUnavailable)]
    [InlineData(nameof(MemoryErasureKeyState.Absent), CovenantAgentErasureState.KeyLost)]
    [InlineData(nameof(MemoryErasureKeyState.Unavailable), CovenantAgentErasureState.KeyUnavailable)]
    [InlineData(nameof(MemoryErasureKeyState.Malformed), CovenantAgentErasureState.KeyUnavailable)]
    public async Task The_lane_head_probe_maps_the_latch_state_like_the_kernel(
        string state,
        CovenantAgentErasureState expected)
    {

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, OtherKey, "Run the tests quietly.", Token);

        await harness.SeedCovenantFingerprintAsync(CovenantScope.Campaign, CampaignOne, Key, Token);

        if (state == "present-foreign")
        {

            await InsertForeignFingerprintAsync(harness);

        }

        using MemoryErasureKeyring keyring = harness.KeyringInState(
            state.StartsWith("present-", StringComparison.Ordinal)
                ? MemoryErasureKeyState.Present
                : Enum.Parse<MemoryErasureKeyState>(state));

        CovenantStore store = new(new FixedCovenantConnectionSource(harness.Fixture.Connection), keyring);

        int calls = harness.Fixture.Credentials.Calls;

        CovenantLaneHeadProbe probe = await ProbeAsync(
            harness,
            store,
            CovenantLane.Proposed,
            state == "present-other" ? OtherKey : Key);

        Assert.Equal(calls, harness.Fixture.Credentials.Calls);

        Assert.Equal(expected, probe.AgentErasure);

        Assert.Equal(expected == CovenantAgentErasureState.Withheld, probe.IsAgentWithheld);

    }

    /// <summary>
    /// A Covenant-only catalog, with neither the Core metadata table nor the fingerprint table, holds
    /// no evidence: agent writes need no key, the probe reports clear, and nothing reads Core metadata
    /// that is not there.
    /// </summary>
    [Fact]
    public async Task A_catalog_without_Core_metadata_or_evidence_answers_clear()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.AddCampaignAsync(CampaignOne, Token);

        CovenantMutationKernel kernel = Kernel(harness);

        CovenantLaneHeadProbe probe = await ProbeAsync(harness, harness.Fixture.Store, CovenantLane.Proposed, Key);

        Assert.Equal(CovenantAgentErasureState.Clear, probe.AgentErasure);

        using CovenantAgentErasureGate gate = kernel.CaptureErasureGate();

        Applied applied = await ApplyAsync(
            harness,
            kernel,
            gate,
            CovenantMutationFixture.AgentPropose(CampaignOne, Key, "The model suggests building from tools.", 0, probe.KeyEpoch));

        AssertApplied(applied);

        Assert.Equal(0, harness.Fixture.Credentials.Calls);

    }

    /// <summary>
    /// A gate is read from the latch alone: a key the latch holds is copied, and nothing is probed.
    /// A latch that turned Present between the copy and the state read is read as unresolved, because
    /// with no key in hand the gate must not claim a present one.
    /// </summary>
    [Fact]
    public void A_latch_that_turns_present_between_its_two_reads_is_read_as_unresolved()
    {

        using CovenantAgentErasureGate gate = CovenantAgentErasureGate.FromLatch(new RacingPresentProvider());

        Assert.Null(gate.Key);

        Assert.Equal(MemoryErasureKeyState.Unresolved, gate.LatchState);

    }

    /// <summary>
    /// A gate owns a private copy of the latched key and zeroes it on disposal, more than once safely,
    /// without touching the latch it was read from. <see cref="CovenantAgentErasureGate.None"/> owns no key.
    /// </summary>
    [Fact]
    public void A_gate_owns_a_private_key_copy_and_zeroes_only_that_copy()
    {

        using MemoryErasureKeyring keyring = MemoryErasureTestKeys.Isolated();

        keyring.OpenOrCreate(evidenceRowsExist: false).Key!.Dispose();

        MemoryErasureIdentity identity = MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, CampaignOne, Key);

        CovenantAgentErasureGate gate = CovenantAgentErasureGate.FromLatch(keyring);

        Assert.Equal(MemoryErasureKeyState.Present, gate.LatchState);

        MemoryErasureKey copy = Assert.IsType<MemoryErasureKey>(gate.Key);

        byte[] expected = copy.Fingerprint(identity);

        gate.Dispose();

        gate.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => copy.Fingerprint(identity));

        using MemoryErasureKey latched = keyring.TryCopyLatched()!;

        Assert.Equal(expected, latched.Fingerprint(identity));

        CovenantAgentErasureGate.None.Dispose();

        Assert.Null(CovenantAgentErasureGate.None.Key);

    }

    private static async Task<CovenantServiceHarness> StartAsync()
    {

        CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token, withErasureEvidence: true);

        try
        {

            await harness.AddCampaignAsync(CampaignOne, Token);

            return harness;

        }
        catch
        {

            await harness.DisposeAsync();

            throw;

        }

    }

    private static CovenantMutationKernel Kernel(CovenantServiceHarness harness) =>
        new(new CovenantQuotaGuard(), harness.Fixture.ErasureKeys);

    /// <summary>
    /// Records one Covenant fingerprint under the installation's key, created and read by a keyring of
    /// its own, so the fixture's latch is left unresolved: the state of a process that has not read the
    /// key since the erase recorded it.
    /// </summary>
    private static async Task SeedFingerprintWithoutLatchingAsync(
        CovenantServiceHarness harness,
        CovenantScope scope,
        Guid? campaignId,
        string key)
    {

        using MemoryErasureKey erasureKey = MemoryErasureTestKeys.CreateKey(harness.Fixture.Credentials);

        await MemoryErasureTestKeys.SeedFingerprintAsync(
            harness.Fixture.Connection,
            erasureKey,
            MemoryErasureIdentity.ForCovenant(scope, campaignId, key),
            Token);

        Assert.Equal(MemoryErasureKeyState.Unresolved, harness.Fixture.ErasureKeys.Latch.State);

    }

    /// <summary>Sets one Campaign key through the production prepare-and-commit path and returns the result.</summary>
    private static async Task<CovenantMutationResultDto> OperatorSetAsync(CovenantServiceHarness harness, string key, string content)
    {

        CovenantOperationScope scope = CovenantOperationScope.ForCampaign(CampaignOne);

        CovenantSetPrepareRequest prepare = new(CovenantScope.Campaign, CampaignOne, key, content, 0, Guid.CreateVersion7(), false);

        Result<CovenantMutationPreflightDto> prepared;

        await using (ICovenantSnapshotReadLease read = (await harness.Gate.AcquireReadAsync(scope, Token)).Value)
        {

            prepared = await harness.Service.PrepareSetAsync(prepare, read, Token);

        }

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        await using CovenantWriteLease write = (await harness.Gate.AcquireWriteAsync(scope, Token)).Value;

        Result<CovenantMutationResultDto> committed = await harness.Service.SetAsync(
            new CovenantSetRequest(
                prepare.Scope,
                prepare.CampaignId,
                prepare.Key,
                prepare.Content,
                prepare.ExpectedRevision,
                prepare.MutationId,
                prepare.Reactivate,
                prepared.Value.PreflightToken),
            write,
            Token);

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

        return committed.Value;

    }

    /// <summary>
    /// Applies one batch in its own serializable transaction and measures, inside that transaction,
    /// what the kernel wrote before the transaction commits or rolls back.
    /// </summary>
    private static async Task<Applied> ApplyAsync(
        CovenantServiceHarness harness,
        CovenantMutationKernel kernel,
        CovenantAgentErasureGate gate,
        CovenantMutationIntent intent)
    {

        CovenantMutationBatch batch = await CovenantMutationFixture.LiveBatchAsync(harness.Fixture, Token, intent);

        SqliteConnection connection = harness.Fixture.Connection;

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        (long receipts, long versions) before = await CountAsync(connection, transaction);

        Result<IReadOnlyList<CovenantMutationReceipt>> result = await kernel.ApplyBatchAsync(
            batch,
            new CovenantMutationTransaction(connection, transaction),
            gate,
            Token);

        (long receipts, long versions) after = await CountAsync(connection, transaction);

        if (result.IsSuccess)
        {

            await transaction.CommitAsync(Token);

        }
        else
        {

            await transaction.RollbackAsync(Token);

        }

        return new Applied(result, after.receipts - before.receipts, after.versions - before.versions);

    }

    private static void AssertRefused(Applied applied, string code, string message)
    {

        Assert.True(applied.Result.IsFailure, "The agent write was applied.");

        Assert.Equal(code, applied.Result.Error.Code);

        Assert.Equal(message, applied.Result.Error.Message);

        Assert.Equal(0, applied.ReceiptsWritten);

        Assert.Equal(0, applied.VersionsWritten);

    }

    private static void AssertApplied(Applied applied)
    {

        Assert.True(applied.Result.IsSuccess, applied.Result.IsFailure ? applied.Result.Error.Message : string.Empty);

        CovenantMutationReceipt receipt = Assert.Single(applied.Result.Value);

        Assert.Equal(CovenantMutationOutcome.Applied, receipt.Outcome);

        Assert.Equal(1, applied.ReceiptsWritten);

    }

    private static async Task<(long Receipts, long Versions)> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM covenant_mutation_receipts), (SELECT COUNT(*) FROM covenant_versions);
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        return (reader.GetInt64(0), reader.GetInt64(1));

    }

    /// <summary>A Covenant fingerprint recorded under a key identifier no key in this test holds.</summary>
    private static async Task InsertForeignFingerprintAsync(CovenantServiceHarness harness)
    {

        await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO memory_erasure_fingerprints (Fingerprint, StoreCode, KeyId)
            VALUES (randomblob(32), 1, randomblob(16));
            """;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

    private static async Task<CovenantLaneHeadProbe> ProbeAsync(
        CovenantServiceHarness harness,
        CovenantStore store,
        CovenantLane lane,
        string key)
    {

        await using ICovenantSnapshotReadLease read =
            (await harness.Gate.AcquireReadAsync(CovenantOperationScope.ForCampaign(CampaignOne), Token)).Value;

        Result<CovenantLaneHeadProbe> probe = await store.ProbeLaneHeadAsync(
            CovenantCanonicalFixture.CampaignContext(CampaignOne),
            lane,
            key,
            read,
            Token);

        Assert.True(probe.IsSuccess, probe.IsFailure ? probe.Error.Message : string.Empty);

        return probe.Value;

    }

    private static async Task<long> ScalarAsync(CovenantServiceHarness harness, string sql)
    {

        await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// A latch observed mid-publish: no key to copy, then a Present state. Probing it is a failure.
    /// </summary>
    private sealed class RacingPresentProvider : IMemoryErasureKeyProvider
    {

        public MemoryErasureKeyLatch Latch => new(MemoryErasureKeyState.Present, new byte[16]);

        public MemoryErasureKeyOpenResult OpenExisting(MemoryErasureKeyProbe probe) =>
            throw new InvalidOperationException("A gate reads the latch; it never probes the credential store.");

        public MemoryErasureKey? TryCopyLatched() => null;

    }

    private sealed record Applied(
        Result<IReadOnlyList<CovenantMutationReceipt>> Result,
        long ReceiptsWritten,
        long VersionsWritten);

}

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// Proving a profile's offline-transition slot is over, and removing what could have resumed it.
/// </summary>
/// <remarks>
/// The two accounts are the only handle anything has on an interrupted transition, so they outlive
/// every ordinary cleanup by design. What this covers is the one exception: a full installation reset
/// may take them, and only after the journal file is provably gone and the anchor has actually closed.
/// A key with no anchor is the case worth naming — genesis writes its anchor before it mints the key and
/// refuses to start beside an existing key, so no genesis leaves a key alone and that combination is
/// durable evidence, not a tidy installation with a stray secret. The converse, a closed epoch-0 anchor
/// with no key, is exactly what a genesis that failed before minting leaves, and it is terminal.
/// </remarks>
[Collection("WorkspacePathPolicy")]
public sealed class GrimoireOfflineTransitionFullResetTerminalTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();

    private static readonly Guid Installation =
        Guid.Parse("70707070-7070-4070-8070-707070707070");

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public void An_untouched_slot_proves_terminal_by_absence()
    {
        using Harness harness = Create("absence");

        GrimoireOfflineTransitionFullResetTerminalProjectionV1 projection = Value(
            harness.Anchors.ProveFullResetTerminal(
                harness.Lock,
                harness.Location,
                Installation));

        Assert.Equal(
            GrimoireOfflineTransitionFullResetTerminalArm.NeverTransitionedAbsence,
            projection.Arm);

        Assert.Null(projection.AnchorAccountValueDigest);

        Assert.Null(projection.JournalKeyAccountValueDigest);

        Assert.True(projection.TerminalEvidenceDigest.IsValid);

        Assert.True(
            harness.Anchors.VerifyTerminalPairAbsent(harness.Lock, harness.Location).IsSuccess);
    }

    [Fact]
    public void A_key_with_no_anchor_is_residue_and_proves_nothing()
    {
        using Harness harness = Create("residue");

        // Genesis writes its anchor before it mints the key and refuses to start when a key is already
        // there, so a key standing alone is durable evidence rather than genesis residue. Removing it
        // would destroy something no proof here can account for.
        _ = harness.Credentials.Set(
            ArcanumCredentialIdentity.Service,
            KeyAccount(harness),
            "stored-key");

        Assert.True(
            harness.Anchors.ProveFullResetTerminal(
                harness.Lock,
                harness.Location,
                Installation).IsFailure);

        Assert.True(
            harness.Anchors.VerifyTerminalPairAbsent(harness.Lock, harness.Location).IsFailure);
    }

    /// <summary>
    /// A genesis that wrote its closed epoch-0 anchor and failed before minting the key never sealed a
    /// transition. Begin and recovery both accept that shape, so a full reset must too: otherwise every
    /// reset that runs no nested transition first would stay blocked behind it.
    /// </summary>
    [Fact]
    public void An_unsealed_genesis_with_no_key_proves_terminal_and_is_removed()
    {
        using Harness harness = Create("unsealed-genesis");

        Assert.True(
            harness.Anchors.WriteGenesisAndVerify(harness.Lock, harness.Location, Installation).IsSuccess);

        GrimoireOfflineTransitionFullResetTerminalProjectionV1 projection = Value(
            harness.Anchors.ProveFullResetTerminal(
                harness.Lock,
                harness.Location,
                Installation));

        Assert.Equal(GrimoireOfflineTransitionFullResetTerminalArm.ClosedAnchor, projection.Arm);

        Assert.Equal(0UL, projection.ClosedSlotEpoch);

        Assert.Null(projection.JournalKeyAccountValueDigest);

        CovenantDigest anchorDigest = Assert.IsType<CovenantDigest>(projection.AnchorAccountValueDigest);

        Assert.True(
            harness.Anchors.RemoveAnchorForFullReset(
                harness.Lock,
                harness.Location,
                anchorDigest).IsSuccess);

        // The key step has no projected digest because the key was never there; it observes the
        // absence and advances.
        Assert.True(
            harness.Anchors.RemoveJournalKeyForFullReset(
                harness.Lock,
                harness.Location,
                default).IsSuccess);

        Assert.True(
            harness.Anchors.VerifyTerminalPairAbsent(harness.Lock, harness.Location).IsSuccess);
    }

    /// <summary>
    /// A key that appears after an unsealed genesis was proven terminal is something that wrote to the
    /// slot since; with no projected digest to reproduce, it is refused rather than deleted.
    /// </summary>
    [Fact]
    public void A_key_minted_after_an_unsealed_genesis_proof_is_not_removed()
    {
        using Harness harness = Create("unsealed-genesis-late-key");

        Assert.True(
            harness.Anchors.WriteGenesisAndVerify(harness.Lock, harness.Location, Installation).IsSuccess);

        Assert.Null(Value(
            harness.Anchors.ProveFullResetTerminal(
                harness.Lock,
                harness.Location,
                Installation)).JournalKeyAccountValueDigest);

        _ = harness.Credentials.Set(
            ArcanumCredentialIdentity.Service,
            KeyAccount(harness),
            "late-key");

        Assert.True(
            harness.Anchors.RemoveJournalKeyForFullReset(
                harness.Lock,
                harness.Location,
                default).IsFailure);
    }

    /// <summary>
    /// Only the unsealed genesis tolerates a missing key. A closed anchor above epoch 0 sealed a
    /// transition whose key is now gone, which is not a slot anything here can call finished.
    /// </summary>
    [Fact]
    public void A_closed_anchor_above_epoch_zero_with_no_key_proves_nothing()
    {
        using Harness harness = Create("sealed-no-key");

        Assert.True(
            harness.Anchors.WriteGenesisAndVerify(harness.Lock, harness.Location, Installation).IsSuccess);

        GrimoireOfflineTransitionAnchorV1 genesis = Assert.IsType<GrimoireOfflineTransitionAnchorV1>(
            Value(harness.Anchors.Read(harness.Location)));

        _ = harness.Credentials.Set(
            ArcanumCredentialIdentity.Service,
            AnchorAccount(harness),
            Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeAnchor(
                genesis with
                {
                    SlotEpoch = 1,
                    OperationId = Guid.Parse("71717171-7171-4171-8171-717171717171"),
                    Kind = GrimoireOfflineTransitionKind.CovenantReset,
                    PayloadVersion = 1,
                })));

        Assert.Equal(
            1UL,
            Assert.IsType<GrimoireOfflineTransitionAnchorV1>(
                Value(harness.Anchors.Read(harness.Location))).SlotEpoch);

        Result<GrimoireOfflineTransitionFullResetTerminalProjectionV1> proven =
            harness.Anchors.ProveFullResetTerminal(
                harness.Lock,
                harness.Location,
                Installation);

        Assert.True(proven.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, proven.Error.Code);
    }

    [Fact]
    public void A_terminal_pair_is_compare_removed_anchor_first_and_the_pass_is_resumable()
    {
        using Harness harness = Create("removal");

        _ = harness.Credentials.Set(
            ArcanumCredentialIdentity.Service,
            KeyAccount(harness),
            "stored-key");

        _ = harness.Credentials.Set(
            ArcanumCredentialIdentity.Service,
            AnchorAccount(harness),
            "stored-anchor");

        CovenantDigest anchorDigest =
            GrimoireOfflineTransitionJournalAnchorStore.TerminalAccountValueDigest(
                AnchorAccount(harness),
                "stored-anchor");

        CovenantDigest keyDigest =
            GrimoireOfflineTransitionJournalAnchorStore.TerminalAccountValueDigest(
                KeyAccount(harness),
                "stored-key");

        // A value that changed since the proof was taken means something wrote to the slot after it
        // was declared terminal. Deleting whatever is there now would be removing evidence nobody
        // proved anything about.
        Assert.True(
            harness.Anchors.RemoveAnchorForFullReset(
                harness.Lock,
                harness.Location,
                keyDigest).IsFailure);

        Assert.True(
            harness.Anchors.RemoveAnchorForFullReset(
                harness.Lock,
                harness.Location,
                anchorDigest).IsSuccess);

        // Idempotent: a pass resumed after a crash between the deletion and the record of it reads the
        // absence and advances rather than refusing.
        Assert.True(
            harness.Anchors.RemoveAnchorForFullReset(
                harness.Lock,
                harness.Location,
                anchorDigest).IsSuccess);

        Assert.True(
            harness.Anchors.VerifyTerminalPairAbsent(harness.Lock, harness.Location).IsFailure);

        Assert.True(
            harness.Anchors.RemoveJournalKeyForFullReset(
                harness.Lock,
                harness.Location,
                keyDigest).IsSuccess);

        Assert.True(
            harness.Anchors.VerifyTerminalPairAbsent(harness.Lock, harness.Location).IsSuccess);
    }

    [Fact]
    public void An_account_value_digest_is_bound_to_its_own_account_name()
    {
        // Otherwise one account's projected digest would authorize removing the other, and the pair
        // would only ever be as strong as whichever of the two an attacker could reproduce.
        Assert.NotEqual(
            GrimoireOfflineTransitionJournalAnchorStore.TerminalAccountValueDigest("a", "value"),
            GrimoireOfflineTransitionJournalAnchorStore.TerminalAccountValueDigest("b", "value"));

        Assert.NotEqual(
            GrimoireOfflineTransitionJournalAnchorStore.TerminalAccountValueDigest("a", "one"),
            GrimoireOfflineTransitionJournalAnchorStore.TerminalAccountValueDigest("a", "two"));
    }

    private static string AnchorAccount(Harness harness) =>
        GrimoireOfflineTransitionJournalAnchorStore
            .TerminalAccounts(harness.Location.ProfileNamespace).AnchorAccount;

    private static string KeyAccount(Harness harness) =>
        GrimoireOfflineTransitionJournalAnchorStore
            .TerminalAccounts(harness.Location.ProfileNamespace).KeyAccount;

    private Harness Create(string name)
    {
        string guardedRoot = _workspace.CreateSubdir("transition-terminal-" + name);

        ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InMemoryOsCredentialStore credentials = new();

        GrimoireOfflineTransitionJournalLocation location = Value(
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(guardedRoot));

        return new Harness(
            held,
            credentials,
            location,
            new GrimoireOfflineTransitionJournalAnchorStore(credentials));
    }

    private static T Value<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;
    }

    private sealed record Harness(
        ArcanumMaintenanceLock Lock,
        InMemoryOsCredentialStore Credentials,
        GrimoireOfflineTransitionJournalLocation Location,
        GrimoireOfflineTransitionJournalAnchorStore Anchors) : IDisposable
    {
        public void Dispose() => Lock.Dispose();
    }
}

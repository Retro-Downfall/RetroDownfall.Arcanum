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
/// A key with no anchor is the case worth naming — genesis mints the key first, so that combination is
/// the residue of a slot that opened, not a tidy installation with a stray secret.
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

        // Genesis mints the key before it writes an anchor and refuses to start when one is already
        // there, so a key standing alone is durable evidence that a slot opened. Removing it would
        // destroy the only thing that could ever have finished the transition.
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

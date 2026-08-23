using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The schema invariants behind a full installation reset's per-Campaign cleanup children.
/// </summary>
/// <remarks>
/// These rows are written once, before either host-tools marker is touched, and are the only record
/// of what the reset observed at that moment. Everything downstream — replay, rehydration,
/// reconciliation — compares against them rather than re-observing, so an invariant that lives only
/// in the caller is an invariant a direct write can walk around. Each one is asserted here against
/// the real installed objects rather than against a fake store.
/// </remarks>
public sealed class CampaignPathFullInstallationResetCleanupSchemaTests
{

    private const int PathMutation = 1;

    private const int RestoreCleanup = 3;

    private const int FullInstallationResetCleanup = 4;

    private const int Opened = 1;

    private const int Unavailable = 2;

    private const int Mismatch = 3;

    private const int Prepared = 1;

    private const int Completed = 12;

    private const int ManualBlocker = 14;

    private const int TempCreated = 2;

    [Fact]
    public async Task Full_reset_companion_is_one_to_one_with_a_kind_four_parent_and_cascades_with_it()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string intent = await journal.InsertKindFourAsync(observation: Opened);

        Assert.Equal(1, await journal.CountCompanionsAsync(intent));

        // A second companion for the same intent is the shape that would let two observations claim
        // one child; the primary key is what refuses it.
        SqliteException duplicate = await journal.ExpectCompanionInsertFailureAsync(intent, Opened);

        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        // Terminalize first: the parent's own delete guard refuses a nonterminal intent, and this
        // test is about the cascade rather than about retention.
        await journal.AdvancePhaseAsync(intent, Completed);

        await journal.DeleteParentAsync(intent);

        Assert.Equal(0, await journal.CountCompanionsAsync(intent));

    }

    [Fact]
    public async Task Full_reset_companion_rejects_non_kind_four_parent_or_missing_parent()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string restore = await journal.InsertParentAsync(
            kind: RestoreCleanup,
            gateOwner: 5,
            displayPath: "/tmp/restore",
            phase: Prepared);

        SqliteException wrongKind = await journal.ExpectCompanionInsertFailureAsync(restore, Opened);

        Assert.Contains("kind four", wrongKind.Message, StringComparison.OrdinalIgnoreCase);

        SqliteException missing = await journal.ExpectCompanionInsertFailureAsync(
            Guid.NewGuid().ToString("D"),
            Opened);

        Assert.True(
            missing.Message.Contains("kind four", StringComparison.OrdinalIgnoreCase)
            || missing.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase),
            missing.Message);

    }

    [Fact]
    public async Task Full_reset_companion_requires_every_32_byte_inventory_digest()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        foreach (string column in ScratchJournal.RequiredDigestColumns)
        {

            string intent = await journal.InsertParentKindFourAsync(displayPath: "/tmp/one");

            SqliteException nulled = await journal.ExpectCompanionInsertFailureAsync(
                intent,
                Opened,
                mutate: values => values[column] = DBNull.Value);

            Assert.Contains("NOT NULL", nulled.Message, StringComparison.OrdinalIgnoreCase);

            SqliteException short31 = await journal.ExpectCompanionInsertFailureAsync(
                intent,
                Opened,
                mutate: values => values[column] = new byte[31]);

            Assert.Contains("CHECK", short31.Message, StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]
    public async Task Full_reset_companion_accepts_only_opened_1_unavailable_2_or_mismatch_3()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        foreach (int observation in new[] { Opened, Unavailable, Mismatch })
        {

            string intent = observation == Opened
                ? await journal.InsertParentKindFourAsync(displayPath: "/tmp/one")
                : await journal.InsertParentKindFourAsync(displayPath: null);

            await journal.InsertCompanionAsync(intent, observation);

            Assert.Equal(1, await journal.CountCompanionsAsync(intent));

        }

        foreach (int rejected in new[] { 0, 4, -1 })
        {

            string intent = await journal.InsertParentKindFourAsync(displayPath: null);

            SqliteException failure = await journal.ExpectCompanionInsertFailureAsync(intent, rejected);

            Assert.Contains("CHECK", failure.Message, StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]
    public async Task Opened_requires_equal_opened_and_authenticated_ownership_digests()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string intent = await journal.InsertParentKindFourAsync(displayPath: "/tmp/one");

        SqliteException different = await journal.ExpectCompanionInsertFailureAsync(
            intent,
            Opened,
            mutate: values =>
                values["OpenedSameHandleOwnershipEvidenceDigest"] = ScratchJournal.Digest(0xEE));

        Assert.Contains("CHECK", different.Message, StringComparison.OrdinalIgnoreCase);

        SqliteException absent = await journal.ExpectCompanionInsertFailureAsync(
            intent,
            Opened,
            mutate: values =>
                values["OpenedSameHandleOwnershipEvidenceDigest"] = DBNull.Value);

        Assert.Contains("CHECK", absent.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Blocked_observations_require_no_opened_ownership_digest()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        foreach (int blocked in new[] { Unavailable, Mismatch })
        {

            string intent = await journal.InsertParentKindFourAsync(displayPath: null);

            SqliteException failure = await journal.ExpectCompanionInsertFailureAsync(
                intent,
                blocked,
                mutate: values =>
                    values["OpenedSameHandleOwnershipEvidenceDigest"] = ScratchJournal.Digest(0x11));

            Assert.Contains("CHECK", failure.Message, StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]
    public async Task Full_reset_companion_evidence_is_immutable_after_insert()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string intent = await journal.InsertKindFourAsync(observation: Opened);

        SqliteException rewritten = await Assert.ThrowsAsync<SqliteException>(
            () => journal.UpdateCompanionObservationDigestAsync(intent, ScratchJournal.Digest(0x99)));

        Assert.Contains("immutable", rewritten.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Full_reset_companion_rejects_direct_delete_but_allows_parent_driven_cascade()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string intent = await journal.InsertKindFourAsync(observation: Mismatch);

        SqliteException direct = await Assert.ThrowsAsync<SqliteException>(
            () => journal.DeleteCompanionAsync(intent));

        Assert.Contains("cannot be deleted", direct.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await journal.CountCompanionsAsync(intent));

        // ManualBlocker is terminal for kind four and deliberately not retainable — the parent's
        // delete guard keeps blocked evidence for an operator — so the cascade is exercised on a
        // second child through the one phase retention does admit.
        string completed = await journal.InsertKindFourAsync(observation: Opened);

        await journal.AdvancePhaseAsync(completed, Completed);

        await journal.DeleteParentAsync(completed);

        Assert.Equal(0, await journal.CountCompanionsAsync(completed));

        Assert.Equal(1, await journal.CountCompanionsAsync(intent));

    }

    [Fact]
    public async Task Kind_four_parent_requires_null_gate_apply_request_payload_temp_and_pending_disposition()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        foreach ((string column, object value) in ScratchJournal.ForbiddenKindFourColumns)
        {

            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
                () => journal.InsertParentAsync(
                    kind: FullInstallationResetCleanup,
                    gateOwner: null,
                    displayPath: "/tmp/one",
                    phase: Prepared,
                    mutate: values => values[column] = value));

            Assert.Contains("CHECK", failure.Message, StringComparison.OrdinalIgnoreCase);

        }

        SqliteException zeroRevision = await Assert.ThrowsAsync<SqliteException>(
            () => journal.InsertParentAsync(
                kind: FullInstallationResetCleanup,
                gateOwner: null,
                displayPath: "/tmp/one",
                phase: Prepared,
                mutate: values => values["PriorRevision"] = 0L));

        Assert.Contains("CHECK", zeroRevision.Message, StringComparison.OrdinalIgnoreCase);

        SqliteException disallowedPhase = await Assert.ThrowsAsync<SqliteException>(
            () => journal.InsertParentAsync(
                kind: FullInstallationResetCleanup,
                gateOwner: null,
                displayPath: "/tmp/one",
                phase: TempCreated));

        Assert.Contains("CHECK", disallowedPhase.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Kind_four_parent_allows_a_null_path_hint_only_for_blocked_full_reset_cleanup()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string blocked = await journal.InsertParentKindFourAsync(displayPath: null);

        await journal.InsertCompanionAsync(blocked, Unavailable);

        Assert.Equal(1, await journal.CountCompanionsAsync(blocked));

        // The null path is legal on the parent for kind four, but an opened observation may not
        // claim it: rehydration derives the root from that hint, so an opened child without one has
        // no way back to the Campaign it says it opened.
        string openedWithoutPath = await journal.InsertParentKindFourAsync(displayPath: null);

        SqliteException failure = await journal.ExpectCompanionInsertFailureAsync(
            openedWithoutPath,
            Opened);

        Assert.Contains("target display path", failure.Message, StringComparison.OrdinalIgnoreCase);

        // And the mirror: a blocked observation must not carry one.
        string blockedWithPath = await journal.InsertParentKindFourAsync(displayPath: "/tmp/one");

        SqliteException carried = await journal.ExpectCompanionInsertFailureAsync(
            blockedWithPath,
            Mismatch);

        Assert.Contains("target display path", carried.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Kind_four_parent_rejects_target_path_change_in_both_null_to_value_and_value_to_null_directions()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string withPath = await journal.InsertParentKindFourAsync(displayPath: "/tmp/one");

        SqliteException cleared = await Assert.ThrowsAsync<SqliteException>(
            () => journal.UpdateTargetDisplayPathAsync(withPath, newPath: null));

        Assert.Contains("target display path", cleared.Message, StringComparison.OrdinalIgnoreCase);

        string withoutPath = await journal.InsertParentKindFourAsync(displayPath: null);

        SqliteException filled = await Assert.ThrowsAsync<SqliteException>(
            () => journal.UpdateTargetDisplayPathAsync(withoutPath, newPath: "/tmp/two"));

        Assert.Contains("target display path", filled.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Kinds_one_through_three_still_require_a_nonnull_target_display_path()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        foreach ((int kind, long gate) in new[] { (PathMutation, 1L), (2, 2L), (RestoreCleanup, 5L) })
        {

            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
                () => journal.InsertParentAsync(
                    kind: kind,
                    gateOwner: gate,
                    displayPath: null,
                    phase: Prepared,
                    mutate: kind == PathMutation
                        ? values => values["ApplyRequestDigest"] = ScratchJournal.Digest(0x21)
                        : null));

            Assert.Contains("CHECK", failure.Message, StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]
    public async Task Kind_four_parent_advances_only_prepared_to_completed_or_manual_blocker()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        foreach (int destination in new[] { Completed, ManualBlocker })
        {

            string intent = await journal.InsertParentKindFourAsync(displayPath: "/tmp/one");

            await journal.AdvancePhaseAsync(intent, destination);

            Assert.Equal(destination, await journal.ReadPhaseAsync(intent));

        }

        foreach (int rejected in new[] { TempCreated, 7, 13, 16 })
        {

            string intent = await journal.InsertParentKindFourAsync(displayPath: "/tmp/one");

            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
                () => journal.AdvancePhaseAsync(intent, rejected));

            Assert.True(
                failure.Message.Contains("CHECK", StringComparison.OrdinalIgnoreCase)
                || failure.Message.Contains("full installation reset", StringComparison.OrdinalIgnoreCase),
                failure.Message);

        }

    }

    [Fact]
    public async Task Kind_four_completed_and_manual_blocker_rows_are_terminal()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        foreach (int terminal in new[] { Completed, ManualBlocker })
        {

            string intent = await journal.InsertParentKindFourAsync(displayPath: "/tmp/one");

            await journal.AdvancePhaseAsync(intent, terminal);

            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(
                () => journal.AdvancePhaseAsync(intent, terminal == Completed ? ManualBlocker : Completed));

            Assert.Contains("terminal", failure.Message, StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]
    public async Task Covenant_family_erasure_retains_kind_four_parent_and_companion_evidence()
    {

        await using ScratchJournal journal = await ScratchJournal.CreateAsync();

        string intent = await journal.InsertKindFourAsync(observation: Mismatch);

        await journal.AdvancePhaseAsync(intent, ManualBlocker);

        // Family maintenance is the widest authorization Covenant erasure ever holds. A blocked
        // full-reset child is the operator's only record that a Campaign marker was left behind, so
        // it has to survive that authorization exactly as a restore intent does.
        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(
            () => journal.DeleteParentUnderFamilyMaintenanceAsync(intent));

        Assert.Contains("completed or compensated", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await journal.CountCompanionsAsync(intent));

        Assert.Equal(ManualBlocker, await journal.ReadPhaseAsync(intent));

    }

}

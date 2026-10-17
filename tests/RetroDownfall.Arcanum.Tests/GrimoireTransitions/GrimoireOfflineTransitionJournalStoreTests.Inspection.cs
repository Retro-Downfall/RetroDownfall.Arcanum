using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// Inspecting a journal slot answers what recovery would answer, and writes nothing.
/// </summary>
/// <remarks>
/// The inspection exists so that a read which promises to change nothing (the dry run of a global
/// reset) can ask what recovery would find. Each case builds a slot the way a crash leaves it, asks
/// the inspection first and then recovery, and requires that the inspection moved no file and no
/// credential and that both gave the same verdict at the same revision.
/// </remarks>
public sealed partial class GrimoireOfflineTransitionJournalStoreTests
{
    [Theory]
    [InlineData("anchor:opening-written", false, ExpectedCrashRecovery.NoActive)]
    [InlineData("anchor:opening-readback", false, ExpectedCrashRecovery.Manual)]
    [InlineData("file:temporary-created", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:temporary-written", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:temporary-flushed", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:atomic-replace", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:previous-retained", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:permissions-verified", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:parent-flushed", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:secure-reread", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-retiring", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-retiring-verified", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-unlinked", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-zero-link-verified", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-delete-parent-flushed", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:residue-absence-proved", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("anchor:advance-written", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("anchor:advance-readback", true, ExpectedCrashRecovery.AuthenticatedNext)]
    public async Task Inspection_agrees_with_recovery_on_every_publication_boundary_and_changes_nothing(
        string boundary,
        bool advance,
        ExpectedCrashRecovery expected)
    {
        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalStore interrupted = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(failBeforeStep: step => step == boundary),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials, failBeforeStep: step => step == boundary));

        Result<GrimoireOfflineTransitionJournalPublication> interruptedResult;

        if (advance)
        {
            GrimoireOfflineTransitionJournalPublication current = await BeginAsync(initial);

            interruptedResult = await interrupted.AdvanceAsync(_lock, current, Bytes("second"), CancellationToken.None);
        }
        else
        {
            interruptedResult = await interrupted.BeginAsync(
                _lock,
                _guarded,
                Installation,
                Operation,
                GrimoireOfflineTransitionKind.CovenantReset,
                1,
                Bytes("first"),
                CancellationToken.None);
        }

        Assert.True(interruptedResult.IsFailure, boundary);

        await AssertInspectionMatchesRecoveryAsync(boundary, expected);
    }

    [Theory]
    [InlineData("anchor:closed-written", ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("anchor:closed-readback", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-moved", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-verified", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-parent-flushed", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-unlinked", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-zero-link-verified", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:delete-parent-flushed", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:absence-parent-flushed", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:absence-proved", ExpectedCrashRecovery.NoActive)]
    public async Task Inspection_agrees_with_recovery_on_every_retirement_boundary_and_changes_nothing(
        string boundary,
        ExpectedCrashRecovery expected)
    {
        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalStore interrupted = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(failBeforeStep: step => step == boundary),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials, failBeforeStep: step => step == boundary));

        Assert.True((await interrupted.RetireAsync(_lock, terminal, CancellationToken.None)).IsFailure, boundary);

        await AssertInspectionMatchesRecoveryAsync(boundary, expected);
    }

    [Theory]
    [InlineData("previous")]
    [InlineData("working")]
    public async Task Inspection_agrees_with_recovery_on_an_exchange_that_left_a_predecessor_beside_the_canonical_file(
        string predecessorState)
    {
        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(ReadyStore());

        GrimoireOfflineTransitionJournalStore failing = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(failBeforeStep: step => step == "file:previous-retiring"),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Assert.True((await failing.AdvanceAsync(_lock, current, Bytes("second"), CancellationToken.None)).IsFailure);

        if (predecessorState is "working")
        {
            File.Move(current.Location.PreviousPath, current.Location.WorkingPath);
        }

        await AssertInspectionMatchesRecoveryAsync(predecessorState, ExpectedCrashRecovery.AuthenticatedNext);
    }

    [Theory]
    [InlineData("with-working", ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("previous-only", ExpectedCrashRecovery.AuthenticatedPrior)]
    public async Task Inspection_agrees_with_recovery_when_the_canonical_file_is_absent_and_a_predecessor_remains(
        string shape,
        ExpectedCrashRecovery expected)
    {
        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(ReadyStore());

        GrimoireOfflineTransitionJournalLocation location = current.Location;

        if (shape is "with-working")
        {
            GrimoireOfflineTransitionEnvelopeV1 next = SealForTest(
                location,
                current.Envelope,
                revision: 2,
                current.EnvelopeDigest,
                Bytes("second"));

            WriteOwnerOnly(
                location.WorkingPath,
                Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(next)));
        }

        File.Move(location.JournalPath, location.PreviousPath);

        await AssertInspectionMatchesRecoveryAsync(shape, expected);
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("stale-temp")]
    [InlineData("unknown-residue")]
    public async Task Inspection_refuses_what_recovery_refuses_and_changes_nothing(string topology)
    {
        SeedIdentity(Installation);

        GrimoireOfflineTransitionJournalLocation location = Location();

        GrimoireOfflineTransitionEnvelopeV1 envelope = CreateUnanchoredEnvelope(location);

        byte[] bytes = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(envelope));

        WriteOwnerOnly(
            topology switch
            {
                "canonical" => location.JournalPath,
                "stale-temp" => location.JournalPath + ".tmp.interrupted",
                _ => location.JournalPath + ".unexpected",
            },
            bytes);

        await AssertInspectionMatchesRecoveryAsync(topology, ExpectedCrashRecovery.Manual);
    }

    [Fact]
    public async Task Inspection_of_an_empty_slot_is_no_active_journal_and_changes_nothing()
    {
        SeedIdentity(Installation);

        await AssertInspectionMatchesRecoveryAsync("empty", ExpectedCrashRecovery.NoActive);
    }

    [Fact]
    public async Task Inspection_refuses_an_absent_anchor_beside_a_present_key_without_creating_either()
    {
        GrimoireOfflineTransitionJournalLocation location = Location();

        using (Value(new GrimoireOfflineTransitionJournalKeyProvider(_credentials).CreateOrOpen(
                   _lock,
                   _guarded,
                   location.ProfileNamespace)))
        {
        }

        await AssertInspectionMatchesRecoveryAsync("key without anchor", ExpectedCrashRecovery.Manual);
    }

    /// <summary>
    /// Inspects the slot as it stands, requires that nothing moved, then recovers it and requires the
    /// same verdict.
    /// </summary>
    private async Task AssertInspectionMatchesRecoveryAsync(string shape, ExpectedCrashRecovery expected)
    {
        string before = Snapshot();

        Result<GrimoireOfflineTransitionJournalRecoveryState> inspected =
            await Store().InspectAsync(_guarded, CancellationToken.None);

        Assert.Equal(before, Snapshot());

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered =
            await Store().RecoverAsync(_lock, _guarded, CancellationToken.None);

        if (expected is ExpectedCrashRecovery.Manual)
        {
            Assert.True(inspected.IsFailure, shape);

            Assert.True(recovered.IsFailure, shape);

            Assert.Equal(recovered.Error.Code, inspected.Error.Code);

            Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, inspected.Error.Code);

            return;
        }

        Assert.True(inspected.IsSuccess, shape + ": " + (inspected.IsFailure ? inspected.Error.Message : string.Empty));

        Assert.True(recovered.IsSuccess, shape);

        Assert.Equal(recovered.Value.Outcome, inspected.Value.Outcome);

        Assert.Equal(recovered.Value.OperationId, inspected.Value.OperationId);

        if (expected is ExpectedCrashRecovery.NoActive)
        {
            Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal, inspected.Value.Outcome);

            Assert.Null(inspected.Value.Publication);

            return;
        }

        Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated, inspected.Value.Outcome);

        GrimoireOfflineTransitionJournalPublication seen = Assert.IsType<GrimoireOfflineTransitionJournalPublication>(
            inspected.Value.Publication);

        GrimoireOfflineTransitionJournalPublication left = Assert.IsType<GrimoireOfflineTransitionJournalPublication>(
            recovered.Value.Publication);

        Assert.Equal(expected is ExpectedCrashRecovery.AuthenticatedPrior ? 1UL : 2UL, seen.Envelope.Revision);

        Assert.Equal(left.Envelope.Revision, seen.Envelope.Revision);

        Assert.Equal(left.EnvelopeDigest, seen.EnvelopeDigest);

        Assert.True(left.PayloadBytes.AsSpan().SequenceEqual(seen.PayloadBytes));
    }

    /// <summary>
    /// Every file beside the journal with its digest, and the three credentials of the slot, as one text.
    /// </summary>
    private string Snapshot()
    {
        GrimoireOfflineTransitionJournalLocation location = Location();

        string directory = Path.GetDirectoryName(location.JournalPath)!;

        IEnumerable<string> files = Directory.EnumerateFileSystemEntries(directory)
            .Where(path => Path.GetFileName(path).StartsWith(location.JournalLeaf, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + "=" + (File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                : "directory"));

        string suffix = location.ProfileNamespace.AccountSuffix;

        IEnumerable<string> credentials =
        [
            Credential(ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(suffix)),
            Credential(ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(suffix)),
            Credential(ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(suffix)),
        ];

        return string.Join("\n", files.Concat(credentials));
    }

    private string Credential(string account)
    {
        OsCredentialStoreResult stored = _credentials.TryGet(ArcanumCredentialIdentity.Service, account);

        return account + "=" + stored.Status + ":" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stored.Value ?? string.Empty)));
    }
}

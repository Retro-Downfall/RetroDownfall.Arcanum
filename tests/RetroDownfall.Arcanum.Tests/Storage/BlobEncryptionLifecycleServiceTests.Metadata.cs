using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Operations;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed partial class BlobEncryptionLifecycleServiceTests
{
    [Fact]
    public async Task Status_includes_invalid_metadata_and_does_not_invent_unknown_bytes()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate healthy = await WriteEncryptedAsync(blobs, "metadata-status");
        MutableMetadataInventory metadata = new([healthy], [InvalidRow(-17, null), InvalidRow(21, 7)]);
        ObservedBlobStore observed = new(blobs);
        BlobEncryptionLifecycleService service = InventoryService(metadata, observed, keys, new(TimeProvider.System));

        BlobEncryptionStatus result = await service.GetStatusAsync();

        Assert.Equal(3, result.TotalFiles);
        Assert.Equal(2, result.InvalidFiles);
        AssertUnknownBytes(result.TotalBytes);
        Assert.Equal(healthy.ExpectedPlaintextLength, result.EncryptedBytes);
        Assert.Equal(0, observed.OpenReadCalls);
        Assert.Equal(0, observed.InspectCalls);
    }

    [Fact]
    public async Task Verify_includes_invalid_metadata_once_and_verifies_healthy_neighbors()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate healthy = await WriteEncryptedAsync(blobs, "metadata-verify");
        MutableMetadataInventory metadata = new([healthy], [InvalidRow(-17, null), InvalidRow(21, 7)]);
        BlobEncryptionLifecycleService service = InventoryService(metadata, blobs, keys, new(TimeProvider.System));

        BlobEncryptionOperationResult result = await service.VerifyAsync(1, long.MaxValue);

        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(2, result.RemainingFiles);
        Assert.Equal(2, result.FailedFiles);
        AssertUnknownBytes(result.RemainingBytes);
        Assert.Equal(2, Assert.Single(result.Issues).Value);
        Assert.Equal("InvalidMetadata", Assert.Single(result.Issues).Key.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_pass_keeps_invalid_metadata_visible_after_migrating_neighbors(bool rotate)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("metadata-durable");
        MutableMetadataInventory metadata = new([legacy], [InvalidRow(-17, 7)]);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        BlobEncryptionLifecycleService service = InventoryService(metadata, blobs, keys, operations);

        BlobEncryptionOperationResult result = rotate
            ? await service.RotateKeyAsync(1, long.MaxValue)
            : await service.MigrateAsync(1, long.MaxValue);

        Assert.True(blobs.HasEnvelope(legacy.Path));
        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(1, result.RemainingFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(7, result.RemainingBytes);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, Assert.Single(operations.Operations).State);
        Assert.Empty(keys.Retired);
    }

    [Theory]
    [InlineData(LongRunningOperationKinds.BlobEncryptionMigration)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionKeyRotation)]
    public async Task Recovery_keeps_invalid_metadata_visible_after_migrating_neighbors(string kind)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("metadata-recovery");
        MutableMetadataInventory metadata = new([legacy], [InvalidRow(-17, 7)]);
        LongRunningOperation operation = Operation(kind);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        operations.Add(operation);
        BlobEncryptionLifecycleService service = InventoryService(metadata, blobs, keys, operations);

        LongRunningOperationRecoveryResult result = await service.RecoverAsync(operation, CancellationToken.None);

        Assert.True(blobs.HasEnvelope(legacy.Path));
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);
        Assert.Empty(keys.Retired);
    }

    [Fact]
    public async Task Durable_pass_counts_one_missing_record_once_across_attempt_and_verification()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate missing = Candidate(Path.Combine(_root, "missing-dedup"), 7, null, 0, null)
            with
        { CatalogRowId = -17 };
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("dedup-neighbor");
        MutableMetadataInventory metadata = new([missing, legacy], []);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        BlobEncryptionLifecycleService service = InventoryService(metadata, blobs, keys, operations);

        BlobEncryptionOperationResult result = await service.MigrateAsync(1, long.MaxValue);

        Assert.True(blobs.HasEnvelope(legacy.Path));
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(1, result.Issues[BlobEncryptionVerificationIssue.MissingFile]);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, Assert.Single(operations.Operations).State);
    }

    [Fact]
    public async Task Rotation_rejects_new_invalid_metadata_at_final_relist_before_retiring_any_key()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate healthy = await WriteEncryptedAsync(blobs, "late-invalid");
        MutableMetadataInventory metadata = new([healthy], []);
        metadata.BeforeList = calls =>
        {
            if (calls == 3)
            {
                metadata.InvalidRows = [InvalidRow(-17, null)];
            }
        };
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        BlobEncryptionLifecycleService service = InventoryService(metadata, blobs, keys, operations);

        BlobEncryptionOperationResult result = await service.RotateKeyAsync(1, long.MaxValue);

        Assert.Empty(keys.Retired);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, Assert.Single(operations.Operations).State);
    }

    [Fact]
    public async Task Status_reports_an_unrepresentable_byte_total_as_unknown_without_wrapping()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        MutableMetadataInventory metadata = new([], [InvalidRow(-17, long.MaxValue), InvalidRow(21, 1)]);
        BlobEncryptionStatus result = await InventoryService(metadata, blobs, keys, new(TimeProvider.System)).GetStatusAsync();

        AssertUnknownBytes(result.TotalBytes);
        Assert.Equal(2, result.InvalidFiles);
    }

    [Fact]
    public async Task Verify_does_not_schedule_corrupt_catalog_lengths_as_physical_io()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate oversized = (await WriteLegacyAsync("oversized-metadata"))
            with
        { ExpectedPlaintextLength = long.MaxValue };
        BlobEncryptionCandidate healthy = await WriteEncryptedAsync(blobs, "oversized-neighbor");
        MutableMetadataInventory metadata = new([oversized, healthy], []);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        BlobEncryptionOperationResult result = await InventoryService(metadata, blobs, keys, new(TimeProvider.System))
            .VerifyAsync(1, 1024 * 1024, deadline.Token);

        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(1, result.Issues[BlobEncryptionVerificationIssue.PlaintextLengthMismatch]);
        Assert.Equal(long.MaxValue, result.RemainingBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_write_failure_survives_successful_verification_or_a_vanished_row(bool vanish)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate first = (await WriteEncryptedAsync(blobs, "write-failure")) with { CatalogRowId = -17 };
        BlobEncryptionCandidate neighbor = await WriteEncryptedAsync(blobs, "write-failure-neighbor");
        MutableMetadataInventory metadata = new([first, neighbor], []);
        metadata.BeforeUpdate = candidate =>
        {
            if (candidate.CatalogRowId == -17)
            {
                if (vanish) metadata.Candidates = [neighbor];
                throw new InvalidDataException("PRIVATE metadata write failure");
            }
        };
        FakeLongRunningOperationStore operations = new(TimeProvider.System);

        BlobEncryptionOperationResult result = await InventoryService(metadata, blobs, keys, operations).MigrateAsync(1, long.MaxValue);

        Assert.Equal(1, result.ProcessedFiles);
        Assert.Equal(0, result.RemainingFiles);
        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(BlobEncryptionVerificationIssue.IoError, Assert.Single(result.Issues).Key);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, Assert.Single(operations.Operations).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_inventory_or_rotation_failure_settles_the_owned_attempt(bool failRotation)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        MutableMetadataInventory metadata = new([], []);
        IOException failure = new("PRIVATE acquisition failure");
        if (failRotation) keys.BeforeRotate = () => throw failure;
        else metadata.BeforeList = _ => throw failure;
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        BlobEncryptionLifecycleService service = InventoryService(metadata, blobs, keys, operations);

        IOException observed = await Assert.ThrowsAsync<IOException>(() => failRotation
            ? service.RotateKeyAsync(1, long.MaxValue)
            : service.MigrateAsync(1, long.MaxValue));

        Assert.Same(failure, observed);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, Assert.Single(operations.Operations).State);
        Assert.Empty(keys.Retired);
    }

    [Fact]
    public async Task Refused_final_transition_cannot_return_success_or_overwrite_the_new_owner()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        MutableMetadataInventory metadata = new([], []);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        metadata.BeforeList = calls =>
        {
            if (calls == 2)
            {
                LongRunningOperation current = Assert.Single(operations.Operations);
                operations.Add(current with { LeaseOwner = "new-owner", Revision = current.Revision + 1 });
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InventoryService(metadata, blobs, keys, operations).MigrateAsync(1, long.MaxValue));

        LongRunningOperation remaining = Assert.Single(operations.Operations);
        Assert.Equal("new-owner", remaining.LeaseOwner);
        Assert.Equal(LongRunningOperationState.Running, remaining.State);
    }

    [Theory]
    [InlineData(LongRunningOperationKinds.BlobEncryptionMigration)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionKeyRotation)]
    public async Task Recovery_preserves_an_initial_invalid_row_even_if_it_disappears_before_verification(string kind)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        MutableMetadataInventory metadata = new([], [InvalidRow(-17, null)]);
        metadata.BeforeList = calls => { if (calls == 2) metadata.InvalidRows = []; };

        LongRunningOperation operation = Operation(kind);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        operations.Add(operation);
        LongRunningOperationRecoveryResult result = await InventoryService(metadata, blobs, keys, operations)
            .RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);
        Assert.Empty(keys.Retired);
    }

    [Fact]
    public async Task Rotation_does_not_retire_keys_after_another_owner_adopts_the_operation()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        MutableMetadataInventory metadata = new([], []);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        metadata.BeforeList = calls =>
        {
            if (calls == 3)
            {
                LongRunningOperation current = Assert.Single(operations.Operations);
                operations.Add(current with { LeaseOwner = "new-owner", Revision = current.Revision + 1 });
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InventoryService(metadata, blobs, keys, operations).RotateKeyAsync(1, long.MaxValue));

        Assert.Empty(keys.Retired);
        Assert.Equal("new-owner", Assert.Single(operations.Operations).LeaseOwner);
    }

    [Fact]
    public async Task Migration_rechecks_operation_ownership_after_waiting_before_touching_a_blob()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("ownership-before-blob");
        MutableMetadataInventory metadata = new([legacy], []);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        int reads = 0;
        operations.GetOverride = current => ++reads == 1 ? current : current! with { LeaseOwner = "new-owner" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InventoryService(metadata, blobs, keys, operations).MigrateAsync(1, long.MaxValue));

        Assert.False(blobs.HasEnvelope(legacy.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Throttling_preserves_cancellation_for_large_physical_io_budgets(bool initiallyCancelled)
    {
        BlobEncryptionLifecycleService.OperationIoThrottle throttle = new(1);
        using CancellationTokenSource cancellation = new();
        if (initiallyCancelled) cancellation.Cancel();
        else cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.WaitAsync(long.MaxValue, cancellation.Token));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("empty-key")]
    [InlineData("null-key")]
    [InlineData("version")]
    public async Task Verify_refuses_metadata_that_disagrees_with_the_authenticated_envelope(string mismatch)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate encrypted = await WriteEncryptedAsync(blobs, "envelope-metadata");
        BlobEncryptionCandidate incorrect = mismatch switch
        {
            "key" => encrypted with { EncryptionKeyId = "different-key" },
            "empty-key" => encrypted with { EncryptionKeyId = string.Empty },
            "null-key" => encrypted with { EncryptionKeyId = null },
            _ => encrypted with { EncryptionVersion = encrypted.EncryptionVersion + 1 },
        };
        MutableMetadataInventory metadata = new([incorrect], []);
        BlobEncryptionFileProcessor processor = new(metadata, blobs);

        BlobEncryptionVerificationResult result = await processor.VerifyAsync(incorrect);

        Assert.False(result.IsValid);
        Assert.Equal("MetadataEnvelopeMismatch", result.Issue.ToString());
        await processor.MigrateAsync(incorrect);
        Assert.True((await processor.VerifyAsync(Assert.Single(metadata.Candidates))).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_settlement_error_preserves_the_original_failure_and_public_cancellation(bool cancel)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        MutableMetadataInventory metadata = new([], []);
        Exception original = cancel ? new OperationCanceledException("PRIVATE cancellation") : new IOException("PRIVATE inventory");
        InvalidOperationException settlement = new("PRIVATE ledger");
        bool primaryFailed = false;
        metadata.BeforeList = _ => { primaryFailed = true; throw original; };
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        operations.GetOverride = current => primaryFailed ? throw settlement : current;

        Exception? observed = await Record.ExceptionAsync(() =>
            InventoryService(metadata, blobs, keys, operations).MigrateAsync(1, long.MaxValue));

        AggregateException combined = cancel
            ? Assert.IsType<AggregateException>(Assert.IsAssignableFrom<OperationCanceledException>(observed).InnerException)
            : Assert.IsType<AggregateException>(observed);
        Assert.Equal(new[] { original, settlement }, combined.InnerExceptions);
        Assert.Equal(LongRunningOperationState.Running, Assert.Single(operations.Operations).State);
    }

    [Theory]
    [InlineData(LongRunningOperationKinds.BlobEncryptionMigration)]
    [InlineData(LongRunningOperationKinds.BlobEncryptionKeyRotation)]
    public async Task Recovery_refuses_blob_mutation_after_another_owner_adopts_its_lease(string kind)
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate legacy = await WriteLegacyAsync("adopted-recovery");
        MutableMetadataInventory metadata = new([legacy], []);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        LongRunningOperation operation = Operation(kind);
        operations.Add(operation with { LeaseOwner = "new-owner" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InventoryService(metadata, blobs, keys, operations).RecoverAsync(operation, CancellationToken.None));

        Assert.False(blobs.HasEnvelope(legacy.Path));
    }

    [Fact]
    public async Task Attempt_and_final_failures_for_different_records_are_both_retained()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate first = (await WriteEncryptedAsync(blobs, "attempt-failure")) with { CatalogRowId = -17 };
        BlobEncryptionCandidate second = (await WriteEncryptedAsync(blobs, "final-failure")) with { CatalogRowId = 12 };
        MutableMetadataInventory metadata = new([first, second], []);
        metadata.BeforeUpdate = candidate => { if (candidate.CatalogRowId == -17) throw new IOException("PRIVATE write"); };
        metadata.BeforeList = calls =>
        {
            if (calls == 2)
                metadata.Candidates = metadata.Candidates.Select(candidate => candidate.CatalogRowId == 12
                    ? candidate with { ExpectedPlaintextSha256 = new string('0', 64) } : candidate).ToArray();
        };

        BlobEncryptionOperationResult result = await InventoryService(metadata, blobs, keys, new(TimeProvider.System)).MigrateAsync(1, long.MaxValue);

        Assert.Equal(2, result.FailedFiles);
        Assert.Equal(1, result.RemainingFiles);
        Assert.Equal(1, result.Issues[BlobEncryptionVerificationIssue.IoError]);
        Assert.Equal(1, result.Issues[BlobEncryptionVerificationIssue.PlaintextHashMismatch]);
        Assert.DoesNotContain(BlobEncryptionVerificationIssue.None, result.Issues.Keys);
    }

    [Fact]
    public async Task Refused_completion_compare_exchange_cannot_return_a_clean_result()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        FakeLongRunningOperationStore operations = new(TimeProvider.System) { TryTransitionOverride = _ => false };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InventoryService(new MutableMetadataInventory([], []), blobs, keys, operations).MigrateAsync(1, long.MaxValue));

        Assert.Equal(LongRunningOperationState.Running, Assert.Single(operations.Operations).State);
    }

    [Fact]
    public async Task Retirement_failure_preserves_prior_committed_retirements_and_never_completes()
    {
        InMemoryKeyRing keys = new() { AdditionalKeys = ["old-one", "old-two"] };
        IOException failure = new("PRIVATE key persistence failure");
        keys.BeforeRetire = key => { if (key == "old-two") throw failure; };
        EncryptedBlobStore blobs = new(keys);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);

        IOException observed = await Assert.ThrowsAsync<IOException>(() =>
            InventoryService(new MutableMetadataInventory([], []), blobs, keys, operations).RotateKeyAsync(1, long.MaxValue));

        Assert.Same(failure, observed);
        Assert.Equal(new[] { "old-one" }, keys.Retired);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, Assert.Single(operations.Operations).State);
    }

    [Fact]
    public async Task Retirement_cancellation_preserves_prior_committed_retirements_and_stops_the_next()
    {
        using CancellationTokenSource caller = new();
        InMemoryKeyRing keys = new() { AdditionalKeys = ["old-one", "old-two"] };
        keys.AfterRetire = _ => caller.Cancel();
        EncryptedBlobStore blobs = new(keys);
        FakeLongRunningOperationStore operations = new(TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InventoryService(new MutableMetadataInventory([], []), blobs, keys, operations).RotateKeyAsync(1, long.MaxValue, caller.Token));

        Assert.Equal(new[] { "old-one" }, keys.Retired);
        Assert.Equal(LongRunningOperationState.Abandoned, Assert.Single(operations.Operations).State);
    }

    [Fact]
    public async Task Recovery_distinguishes_new_invalid_retirement_inventory_from_cancellation()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        MutableMetadataInventory metadata = new([], []);
        metadata.BeforeList = calls => { if (calls == 3) metadata.InvalidRows = [InvalidRow(-17, null)]; };
        FakeLongRunningOperationStore operations = new(TimeProvider.System);
        LongRunningOperation operation = Operation(LongRunningOperationKinds.BlobEncryptionKeyRotation);
        operations.Add(operation);

        LongRunningOperationRecoveryResult result = await InventoryService(metadata, blobs, keys, operations)
            .RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);
        Assert.Equal(LongRunningOperationErrorCodes.RecoveryFailed, result.ErrorCode);
        Assert.Empty(keys.Retired);
    }

    [Fact]
    public async Task Remaining_byte_overflow_is_unknown_and_rowids_from_different_tables_do_not_alias()
    {
        InMemoryKeyRing keys = new();
        EncryptedBlobStore blobs = new(keys);
        BlobEncryptionCandidate upload = Candidate(Path.Combine(_root, "missing-upload"), long.MaxValue, null, 0, null)
            with
        { CatalogRowId = -17 };
        BlobEncryptionCandidate attachment = Candidate(Path.Combine(_root, "missing-attachment"), 1, null, 0, null)
            with
        { Kind = BlobEncryptionRecordKind.SessionAttachment, CatalogRowId = -17 };
        MutableMetadataInventory metadata = new([upload, attachment], []);

        BlobEncryptionOperationResult result = await InventoryService(metadata, blobs, keys, new(TimeProvider.System)).VerifyAsync(1, long.MaxValue);

        Assert.Equal(2, result.FailedFiles);
        Assert.Equal(2, result.RemainingFiles);
        AssertUnknownBytes(result.RemainingBytes);
        Assert.Equal(2, result.Issues[BlobEncryptionVerificationIssue.MissingFile]);
    }

    [Fact]
    public async Task Renewal_cancellation_after_heartbeat_keeps_the_public_cancellation_disposition()
    {
        InMemoryKeyRing keys = new();

        EncryptedBlobStore blobs = new(keys);

        FakeLongRunningOperationStore operations = new(TimeProvider.System);

        int reads = 0;

        operations.GetOverride = current =>
        {
            if (++reads == 2)
            {
                LongRunningOperation cancelling = current! with
                {
                    State = LongRunningOperationState.Cancelling,
                    Revision = current.Revision + 1,
                };

                operations.Add(cancelling);

                return cancelling;
            }

            return current;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InventoryService(new MutableMetadataInventory([], []), blobs, keys, operations).MigrateAsync(1, long.MaxValue));

        Assert.Equal(LongRunningOperationState.Abandoned, Assert.Single(operations.Operations).State);

        Assert.Empty(keys.Retired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_refuses_a_missing_or_expired_owned_lease_before_mutating_a_blob(bool expired)
    {
        InMemoryKeyRing keys = new();

        EncryptedBlobStore blobs = new(keys);

        BlobEncryptionCandidate candidate = await WriteLegacyAsync("invalid-recovery-lease");

        MutableMetadataInventory metadata = new([candidate], []);

        FakeLongRunningOperationStore operations = new(TimeProvider.System);

        LongRunningOperation operation = Operation(LongRunningOperationKinds.BlobEncryptionMigration) with
        {
            LeaseExpiresAt = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
        };

        operations.Add(operation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InventoryService(metadata, blobs, keys, operations).RecoverAsync(operation, CancellationToken.None));

        Assert.False(blobs.HasEnvelope(candidate.Path));

        Assert.Empty(keys.Retired);
    }

    private static void AssertUnknownBytes(long? bytes) => Assert.Null(bytes);

    private static BlobEncryptionInvalidRow InvalidRow(long rowId, long? knownBytes) =>
        new(BlobEncryptionRecordKind.UploadedFile, rowId, BlobEncryptionMetadataIssue.InvalidIdentity, knownBytes);

    private static BlobEncryptionLifecycleService InventoryService(
        IBlobEncryptionMetadataStore metadata,
        IEncryptedBlobStore blobs,
        IFileEncryptionKeyRing keys,
        FakeLongRunningOperationStore operations) =>
        new(metadata, new BlobEncryptionFileProcessor(metadata, blobs), blobs, keys,
            new LongRunningOperationCoordinator(operations, TimeProvider.System), operations, TimeProvider.System);

    private sealed class MutableMetadataInventory(
        BlobEncryptionCandidate[] candidates,
        BlobEncryptionInvalidRow[] invalidRows) : IBlobEncryptionMetadataStore
    {
        private int _listCalls;

        public BlobEncryptionCandidate[] Candidates { get; set; } = candidates;

        public BlobEncryptionInvalidRow[] InvalidRows { get; set; } = invalidRows;

        public Action<int>? BeforeList { get; set; }

        public Action<BlobEncryptionCandidate>? BeforeUpdate { get; set; }

        public Task<BlobEncryptionInventory> ListAsync(CancellationToken cancellationToken = default)
        {
            BeforeList?.Invoke(++_listCalls);
            return Task.FromResult(new BlobEncryptionInventory(Candidates.ToArray(), InvalidRows.ToArray()));
        }

        public Task UpdateEncryptionMetadataAsync(BlobEncryptionCandidate candidate,
            EncryptedBlobDescriptor descriptor, string plaintextSha256, CancellationToken cancellationToken = default)
        {
            BeforeUpdate?.Invoke(candidate);
            int index = Array.IndexOf(Candidates, candidate);
            Candidates[index] = candidate with
            {
                EncryptionVersion = descriptor.Version,
                EncryptionKeyId = descriptor.KeyId,
            };
            return Task.CompletedTask;
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Storage;

public sealed class BlobEncryptionLifecycleService(
    IBlobEncryptionMetadataStore metadataStore,
    BlobEncryptionFileProcessor processor,
    IEncryptedBlobStore blobStore,
    IFileEncryptionKeyRing keyRing,
    ILongRunningOperationCoordinator operationCoordinator,
    ILongRunningOperationStore operationStore,
    TimeProvider timeProvider) : IBlobEncryptionLifecycleService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The inventory: catalog metadata plus each file's presence and envelope magic.
    /// </summary>
    /// <remarks>
    /// No blob body is opened, decrypted, or hashed here. Content verification (authentication, length,
    /// SHA-256, key availability) is the explicit, bounded and throttled <see cref="VerifyAsync"/>, so
    /// status stays cheap on a large store and <see cref="BlobEncryptionStatus.InvalidFiles"/> counts invalid catalog rows and missing files. A present file whose
    /// envelope header cannot be read (access denied, locked) is not distinguished here: the header peek
    /// reports it as carrying no envelope, so it is counted as legacy plaintext, and as needing
    /// reconciliation when its metadata says encrypted. <see cref="VerifyAsync"/> is what reports it,
    /// as <see cref="BlobEncryptionVerificationIssue.IoError"/>.
    /// </remarks>
    public async Task<BlobEncryptionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        BlobEncryptionInventory inventory = await metadataStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<BlobEncryptionCandidate> candidates = inventory.Candidates;
        int encrypted = 0;
        ByteTotal encryptedBytes = new();
        int legacy = 0;
        ByteTotal legacyBytes = new();
        int reconciliation = 0;
        int invalid = inventory.InvalidRows.Count;
        Dictionary<string, int> byKey = new(StringComparer.Ordinal);

        foreach (BlobEncryptionCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate.Path))
            {
                invalid++;
                continue;
            }

            if (blobStore.HasEnvelope(candidate.Path))
            {
                encrypted++;
                encryptedBytes.Add(candidate.ExpectedPlaintextLength);
                string key = candidate.EncryptionKeyId ?? "<unknown>";
                byKey[key] = byKey.GetValueOrDefault(key) + 1;
                if (candidate.EncryptionVersion == 0)
                {
                    reconciliation++;
                }
            }
            else
            {
                legacy++;
                legacyBytes.Add(candidate.ExpectedPlaintextLength);
                if (candidate.EncryptionVersion > 0)
                {
                    reconciliation++;
                }
            }
        }

        return new BlobEncryptionStatus(
            candidates.Count + inventory.InvalidRows.Count,
            SumBytes(candidates.Select(static candidate => (long?)candidate.ExpectedPlaintextLength)
                .Concat(inventory.InvalidRows.Select(static row => row.KnownPlaintextLength))),
            encrypted,
            encryptedBytes.Value,
            legacy,
            legacyBytes.Value,
            reconciliation,
            invalid,
            byKey);
    }

    public async Task<BlobEncryptionOperationResult> MigrateAsync(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken = default)
    {
        string owner = CreateOwner("migration");
        LongRunningOperationLeaseResult lease = await operationCoordinator.StartAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.BlobEncryptionMigration,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "Blob encryption migration is starting.",
                    timeProvider.GetUtcNow()),
                owner,
                LeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (!lease.Acquired)
        {
            throw new InvalidOperationException("Could not acquire the blob migration lease.");
        }

        return await RunDurableAsync(
                lease.Operation,
                owner,
                rotate: false,
                maxConcurrency,
                maxBytesPerSecond,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BlobEncryptionOperationResult> VerifyAsync(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken = default) =>
        RunVerificationAsync(
            Guid.Empty,
            maxConcurrency,
            maxBytesPerSecond,
            cancellationToken);

    public async Task<BlobEncryptionOperationResult> RotateKeyAsync(
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken = default)
    {
        string owner = CreateOwner("rotation");
        LongRunningOperationLeaseResult lease = await operationCoordinator.StartAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.BlobEncryptionKeyRotation,
                    LongRunningOperationRecoveryPolicy.RestartIdempotently,
                    "File-encryption key rotation is starting.",
                    timeProvider.GetUtcNow()),
                owner,
                LeaseDuration,
                cancellationToken)
            .ConfigureAwait(false);
        if (!lease.Acquired)
        {
            throw new InvalidOperationException("Could not acquire the key-rotation lease.");
        }

        return await RunDurableAsync(
                lease.Operation,
                owner,
                rotate: true,
                maxConcurrency,
                maxBytesPerSecond,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<RecordLocator, BlobEncryptionVerificationIssue> failures = new();
        try
        {
            if (await IsRecoveryCancellationRequestedAsync(operation, cancellationToken).ConfigureAwait(false))
            {
                return LongRunningOperationRecoveryResult.Abandoned(LongRunningOperationRecoveryOutcomes.Cancelled);
            }

            string recoveryOwner = operation.LeaseOwner ?? throw LostOperationOwnership();
            await RenewOwnedLeaseAsync(operation.Id, recoveryOwner, cancellationToken).ConfigureAwait(false);

            string? targetKeyId = null;
            if (operation.Kind == LongRunningOperationKinds.BlobEncryptionKeyRotation)
            {
                targetKeyId = (await keyRing.GetForWriteAsync(cancellationToken).ConfigureAwait(false)).KeyId;
            }

            BlobEncryptionInventory inventory = await metadataStore
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (BlobEncryptionInvalidRow row in inventory.InvalidRows)
                failures[RecordLocator.For(row)] = BlobEncryptionVerificationIssue.InvalidMetadata;
            await RenewOwnedLeaseAsync(operation.Id, recoveryOwner, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<BlobEncryptionCandidate> candidates = inventory.Candidates;
            foreach (BlobEncryptionCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await IsRecoveryCancellationRequestedAsync(operation, cancellationToken).ConfigureAwait(false))
                {
                    return CancelledRecoveryResult(failures.Count);
                }

                await RenewOwnedLeaseAsync(operation.Id, recoveryOwner, cancellationToken).ConfigureAwait(false);

                try
                {
                    if (targetKeyId is null)
                    {
                        _ = await processor.MigrateAsync(candidate, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _ = await processor.ReencryptAsync(candidate, targetKeyId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                // The same filter RunDurableAsync uses. A blob deleted out-of-band or access-denied, a
                // length/hash mismatch, or an unavailable key is an expected data condition, not a reason
                // to abandon every candidate queued behind it. Recovery for these kinds runs once —
                // FindExpiredAsync re-selects only the DataRetention kinds — so an early unwind would
                // leave the remaining files legacy plaintext (or on the retired key) permanently.
                catch (Exception ex) when (BlobEncryptionFileProcessor.IsPerBlobFailure(ex))
                {
                    failures[RecordLocator.For(candidate)] = BlobEncryptionVerificationIssue.IoError;
                }
            }

            if (await IsRecoveryCancellationRequestedAsync(operation, cancellationToken).ConfigureAwait(false))
            {
                return CancelledRecoveryResult(failures.Count);
            }

            // Both branches verify their own work before claiming success. Migration recovery used to
            // return Completed() unconditionally, so a pass that never touched a file was
            // indistinguishable from a clean one.
            BlobEncryptionOperationResult verification = await RunVerificationAsync(
                    operation.Id,
                    maxConcurrency: 2,
                    maxBytesPerSecond: 64L * 1024 * 1024,
                    cancellationToken,
                    attemptFailures: failures,
                    renewLease: ct => RenewOwnedLeaseAsync(operation.Id, recoveryOwner, ct))
                .ConfigureAwait(false);
            if (!failures.IsEmpty || verification.FailedFiles != 0 || verification.RemainingFiles != 0)
            {
                return LongRunningOperationRecoveryResult.RequiresAttention(
                    LongRunningOperationErrorCodes.RecoveryFailed);
            }

            if (await IsRecoveryCancellationRequestedAsync(operation, cancellationToken).ConfigureAwait(false))
            {
                return CancelledRecoveryResult(failures.Count);
            }

            if (targetKeyId is not null)
            {
                KeyRetirementResult retirement = await RetireUnreferencedKeysAsync(
                    targetKeyId, cancellationToken, operation, failures).ConfigureAwait(false);
                if (retirement.InvalidRows.Count != 0)
                    return LongRunningOperationRecoveryResult.RequiresAttention(LongRunningOperationErrorCodes.RecoveryFailed);
                if (retirement.Cancelled) return CancelledRecoveryResult(failures.Count);
            }

            await RenewOwnedLeaseAsync(operation.Id, recoveryOwner, cancellationToken).ConfigureAwait(false);
            return LongRunningOperationRecoveryResult.Completed();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (await IsRecoveryCancellationRequestedAsync(operation, cancellationToken).ConfigureAwait(false))
                return CancelledRecoveryResult(failures.Count);
            throw;
        }
    }

    private async Task<bool> IsRecoveryCancellationRequestedAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        operation.State == LongRunningOperationState.Cancelling
        || (await operationStore.GetAsync(operation.Id, cancellationToken).ConfigureAwait(false))?.State
            == LongRunningOperationState.Cancelling;

    private static LongRunningOperationRecoveryResult CancelledRecoveryResult(int failed) =>
        failed == 0
            ? LongRunningOperationRecoveryResult.Abandoned(LongRunningOperationRecoveryOutcomes.Cancelled)
            : LongRunningOperationRecoveryResult.RequiresAttention(LongRunningOperationErrorCodes.RecoveryFailed);

    private async Task<KeyRetirementResult> RetireUnreferencedKeysAsync(
        string activeKeyId,
        CancellationToken cancellationToken,
        LongRunningOperation operation,
        ConcurrentDictionary<RecordLocator, BlobEncryptionVerificationIssue> failures)
    {
        string owner = operation.LeaseOwner ?? throw LostOperationOwnership();

        await RenewOwnedLeaseAsync(operation.Id, owner, cancellationToken).ConfigureAwait(false);
        BlobEncryptionInventory inventory = await metadataStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (BlobEncryptionInvalidRow row in inventory.InvalidRows)
            failures[RecordLocator.For(row)] = BlobEncryptionVerificationIssue.InvalidMetadata;

        await RenewOwnedLeaseAsync(operation.Id, owner, cancellationToken).ConfigureAwait(false);
        if (inventory.InvalidRows.Count != 0)
            return new KeyRetirementResult(false, inventory.InvalidRows);
        IReadOnlyList<BlobEncryptionCandidate> current = inventory.Candidates;
        HashSet<string> referenced = current
            .Select(static candidate => candidate.EncryptionKeyId)
            .Where(static keyId => !string.IsNullOrWhiteSpace(keyId))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> active = await keyRing
            .GetActiveKeyIdsAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (string keyId in active)
        {
            if (!string.Equals(keyId, activeKeyId, StringComparison.Ordinal)
                && !referenced.Contains(keyId))
            {
                if (await IsRecoveryCancellationRequestedAsync(operation, cancellationToken).ConfigureAwait(false))
                {
                    return new KeyRetirementResult(true, []);
                }

                await RenewOwnedLeaseAsync(operation.Id, owner, cancellationToken).ConfigureAwait(false);

                await keyRing.RetireAsync(keyId, cancellationToken).ConfigureAwait(false);
            }
        }

        return new KeyRetirementResult(false, []);
    }

    private async Task<BlobEncryptionOperationResult> RunDurableAsync(
        LongRunningOperation operation,
        string owner,
        bool rotate,
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<RecordLocator, BlobEncryptionVerificationIssue> failures = new();
        int processed = 0;
        ByteTotal processedBytes = new();
        using SemaphoreSlim checkpointGate = new(1, 1);
        OperationIoThrottle throttle = new(maxBytesPerSecond);

        try
        {
            await RenewOwnedLeaseAsync(operation.Id, owner, cancellationToken).ConfigureAwait(false);
            string? rotateToKeyId = rotate
                ? (await keyRing.RotateAsync(cancellationToken).ConfigureAwait(false)).KeyId
                : null;
            BlobEncryptionInventory inventory = await metadataStore.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (BlobEncryptionInvalidRow row in inventory.InvalidRows)
                failures[RecordLocator.For(row)] = BlobEncryptionVerificationIssue.InvalidMetadata;
            await RenewOwnedLeaseAsync(operation.Id, owner, cancellationToken).ConfigureAwait(false);

            await Parallel.ForEachAsync(
                inventory.Candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 8),
                    CancellationToken = cancellationToken,
                },
                async (candidate, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    LongRunningOperation? live = await operationStore.GetAsync(operation.Id, ct).ConfigureAwait(false);
                    if (live?.State == LongRunningOperationState.Cancelling)
                        throw new OperationCanceledException("Blob encryption operation cancellation was requested.");
                    RequireOwnedRunning(live, owner);

                    try
                    {
                        await throttle.WaitAsync(PhysicalLength(candidate), ct,
                            token => RenewOwnedLeaseAsync(operation.Id, owner, token)).ConfigureAwait(false);
                        LongRunningOperation? beforeMutation = await operationStore.GetAsync(operation.Id, ct).ConfigureAwait(false);
                        if (beforeMutation?.State == LongRunningOperationState.Cancelling)
                            throw new OperationCanceledException("Blob encryption operation cancellation was requested.");
                        RequireOwnedRunning(beforeMutation, owner);
                        BlobEncryptionFileResult result = rotateToKeyId is null
                            ? await processor.MigrateAsync(candidate, ct).ConfigureAwait(false)
                            : await processor.ReencryptAsync(candidate, rotateToKeyId, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref processed);
                        processedBytes.Add(result.PlaintextLength);
                    }
                    catch (Exception ex) when (BlobEncryptionFileProcessor.IsPerBlobFailure(ex))
                    {
                        BlobEncryptionVerificationResult verification = await VerifyCandidateAsync(
                            candidate, CancellationToken.None).ConfigureAwait(false);
                        failures[RecordLocator.For(candidate)] = verification.IsValid
                            ? BlobEncryptionVerificationIssue.IoError
                            : verification.Issue;
                    }

                    await checkpointGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (!await operationCoordinator.HeartbeatAsync(operation.Id, owner, LeaseDuration,
                            CancellationToken.None).ConfigureAwait(false))
                            throw LostOperationOwnership();
                        LongRunningOperation? current = await operationStore.GetAsync(operation.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                        RequireOwnedRunning(current, owner, allowCancelling: true);
                        if (!await operationCoordinator.CheckpointAsync(operation.Id, owner, current!.CheckpointVersion,
                            checkpointVersion: 1, BuildCheckpoint(Volatile.Read(ref processed), processedBytes.Value ?? -1, failures.Count),
                            checkpointReference: null,
                            $"Processed {Volatile.Read(ref processed)} of {inventory.Candidates.Count + inventory.InvalidRows.Count} files; "
                            + $"{FormatBytes(processedBytes.Value)} bytes complete; {failures.Count} files excluded.",
                            CancellationToken.None).ConfigureAwait(false))
                            throw LostOperationOwnership();
                    }
                    finally { checkpointGate.Release(); }
                }).ConfigureAwait(false);

            BlobEncryptionOperationResult verification = await RunVerificationAsync(
                operation.Id, maxConcurrency, maxBytesPerSecond, cancellationToken, failures,
                ct => RenewOwnedLeaseAsync(operation.Id, owner, ct)).ConfigureAwait(false);
            if (rotateToKeyId is not null && verification.FailedFiles == 0 && verification.RemainingFiles == 0)
            {
                KeyRetirementResult retirement = await RetireUnreferencedKeysAsync(
                    rotateToKeyId, cancellationToken, operation, failures).ConfigureAwait(false);
                if (retirement.Cancelled) throw new OperationCanceledException("Blob encryption operation cancellation was requested.");
                if (retirement.InvalidRows.Count != 0)
                {
                    foreach (BlobEncryptionInvalidRow row in retirement.InvalidRows)
                        failures[RecordLocator.For(row)] = BlobEncryptionVerificationIssue.InvalidMetadata;
                    verification = verification with
                    {
                        RemainingFiles = retirement.InvalidRows.Count,
                        RemainingBytes = SumBytes(retirement.InvalidRows.Select(static row => row.KnownPlaintextLength)),
                        FailedFiles = failures.Count,
                        Issues = CountIssues(failures),
                    };
                }
            }

            await RenewOwnedLeaseAsync(operation.Id, owner, cancellationToken).ConfigureAwait(false);
            LongRunningOperation? current = await operationStore.GetAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);
            if (current?.State == LongRunningOperationState.Cancelling)
                throw new OperationCanceledException("Blob encryption operation cancellation was requested.");
            RequireOwnedRunning(current, owner);
            bool settled = verification.FailedFiles == 0 && verification.RemainingFiles == 0
                ? await operationCoordinator.CompleteAsync(operation.Id, owner, current!.Revision, CancellationToken.None).ConfigureAwait(false)
                : await operationStore.TryTransitionAsync(operation.Id, current!.Revision, owner,
                    LongRunningOperationState.ReconciliationRequired, timeProvider.GetUtcNow(),
                    LongRunningOperationErrorCodes.BlobEncryptionIncomplete, CancellationToken.None).ConfigureAwait(false);
            if (!settled) throw LostOperationOwnership();
            return verification with { ProcessedFiles = processed, ProcessedBytes = processedBytes.Value };
        }
        catch (OperationCanceledException exception)
        {
            await SettlePreservingFailureAsync(exception, operation.Id, owner, failures.IsEmpty
                ? LongRunningOperationState.Abandoned : LongRunningOperationState.ReconciliationRequired, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await SettlePreservingFailureAsync(exception, operation.Id, owner,
                LongRunningOperationState.ReconciliationRequired, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task SettlePreservingFailureAsync(Exception original, Guid operationId, string owner,
        LongRunningOperationState state, CancellationToken cancellationToken)
    {
        bool settled;
        try
        {
            settled = await SettleFailureAsync(operationId, owner, state).ConfigureAwait(false);
        }
        catch (Exception settlement)
        {
            AggregateException combined = new("Blob encryption and durable settlement both failed.", original, settlement);
            if (original is OperationCanceledException)
                throw new OperationCanceledException("Blob encryption was cancelled and durable settlement failed.", combined, cancellationToken);
            throw combined;
        }

        if (!settled)
        {
            if (original is OperationCanceledException)
                throw new OperationCanceledException("Blob encryption was cancelled; its durable operation could not be settled.", original, cancellationToken);
            throw new InvalidOperationException("Blob encryption failed; its durable operation could not be settled.", original);
        }
    }

    private async Task<bool> SettleFailureAsync(Guid operationId, string owner, LongRunningOperationState state)
    {
        LongRunningOperation? current = await operationStore.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        if (current is null || current.LeaseOwner != owner
            || current.State is not (LongRunningOperationState.Running or LongRunningOperationState.Cancelling)) return false;
        return await operationStore.TryTransitionAsync(operationId, current.Revision, owner, state, timeProvider.GetUtcNow(),
            state == LongRunningOperationState.Abandoned
                ? LongRunningOperationRecoveryOutcomes.Cancelled : LongRunningOperationErrorCodes.BlobEncryptionIncomplete,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RenewOwnedLeaseAsync(Guid operationId, string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LongRunningOperation? current = await operationStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);

        RequireOwnedRunning(current, owner);

        if (!await operationCoordinator.HeartbeatAsync(operationId, owner, LeaseDuration, cancellationToken).ConfigureAwait(false))
            throw LostOperationOwnership();

        RequireOwnedRunning(await operationStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false), owner);
    }

    private void RequireOwnedRunning(LongRunningOperation? operation, string owner, bool allowCancelling = false)
    {
        if (operation is null || operation.LeaseOwner != owner
            || operation.LeaseExpiresAt is not { } expiresAt || expiresAt <= timeProvider.GetUtcNow())
            throw LostOperationOwnership();

        if (operation.State == LongRunningOperationState.Cancelling && !allowCancelling)
            throw new OperationCanceledException("Blob encryption operation cancellation was requested.");

        if (operation.State != LongRunningOperationState.Running
            && !(allowCancelling && operation.State == LongRunningOperationState.Cancelling))
            throw LostOperationOwnership();
    }

    private static InvalidOperationException LostOperationOwnership() =>
        new("Blob encryption could not retain or settle its durable operation lease.");

    private async Task<BlobEncryptionOperationResult> RunVerificationAsync(
        Guid operationId,
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken,
        ConcurrentDictionary<RecordLocator, BlobEncryptionVerificationIssue>? attemptFailures = null,
        Func<CancellationToken, Task>? renewLease = null)
    {
        if (renewLease is not null) await renewLease(cancellationToken).ConfigureAwait(false);
        BlobEncryptionInventory inventory = await metadataStore.ListAsync(cancellationToken).ConfigureAwait(false);
        ConcurrentDictionary<RecordLocator, BlobEncryptionVerificationIssue> failures = attemptFailures ?? new();
        ByteTotal remainingBytes = new();
        foreach (BlobEncryptionInvalidRow row in inventory.InvalidRows)
        {
            failures[RecordLocator.For(row)] = BlobEncryptionVerificationIssue.InvalidMetadata;
            remainingBytes.Add(row.KnownPlaintextLength);
        }

        if (renewLease is not null) await renewLease(cancellationToken).ConfigureAwait(false);
        int valid = 0;
        ByteTotal validBytes = new();
        OperationIoThrottle throttle = new(maxBytesPerSecond);
        await Parallel.ForEachAsync(inventory.Candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 8),
                CancellationToken = cancellationToken,
            },
            async (candidate, ct) =>
            {
                BlobEncryptionVerificationResult result = await VerifyCandidateAsync(candidate, ct, throttle, renewLease).ConfigureAwait(false);
                if (result.IsValid && result.Descriptor is not null)
                {
                    Interlocked.Increment(ref valid);
                    validBytes.Add(result.PlaintextLength);
                }
                else
                {
                    failures[RecordLocator.For(candidate)] = result.Issue;
                    remainingBytes.Add(candidate.ExpectedPlaintextLength);
                }
            }).ConfigureAwait(false);

        if (renewLease is not null) await renewLease(cancellationToken).ConfigureAwait(false);
        return new BlobEncryptionOperationResult(operationId, valid, validBytes.Value,
            inventory.Candidates.Count + inventory.InvalidRows.Count - valid, remainingBytes.Value,
            failures.Count, CountIssues(failures));
    }

    private static IReadOnlyDictionary<BlobEncryptionVerificationIssue, int> CountIssues(
        ConcurrentDictionary<RecordLocator, BlobEncryptionVerificationIssue> failures) =>
        failures.Values.GroupBy(static issue => issue).ToDictionary(static group => group.Key, static group => group.Count());

    private static long PhysicalLength(BlobEncryptionCandidate candidate) =>
        File.Exists(candidate.Path) ? new FileInfo(candidate.Path).Length : 0;

    private static long? SumBytes(IEnumerable<long?> values)
    {
        ByteTotal total = new();
        foreach (long? value in values) total.Add(value);
        return total.Value;
    }

    private static string FormatBytes(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

    private sealed record KeyRetirementResult(bool Cancelled, IReadOnlyList<BlobEncryptionInvalidRow> InvalidRows);

    private readonly record struct RecordLocator(BlobEncryptionRecordKind Kind, long? RowId, string? FallbackId)
    {
        public static RecordLocator For(BlobEncryptionCandidate candidate) =>
            new(candidate.Kind, candidate.CatalogRowId, candidate.CatalogRowId.HasValue ? null : candidate.RecordId);

        public static RecordLocator For(BlobEncryptionInvalidRow row) => new(row.Kind, row.CatalogRowId, null);
    }

    private sealed class ByteTotal
    {
        private readonly Lock _gate = new();
        private long? _value = 0;

        public long? Value { get { lock (_gate) return _value; } }

        public void Add(long? value)
        {
            lock (_gate)
            {
                _value = _value is not { } current || value is not { } addition || addition < 0 || long.MaxValue - current < addition
                    ? null : current + addition;
            }
        }
    }

    /// <summary>
    /// Verifies one candidate and never lets that one blob end the pass: a per-blob failure the
    /// processor did not already classify is recorded as <see cref="BlobEncryptionVerificationIssue.IoError"/>.
    /// </summary>
    private async Task<BlobEncryptionVerificationResult> VerifyCandidateAsync(
        BlobEncryptionCandidate candidate,
        CancellationToken cancellationToken,
        OperationIoThrottle? throttle = null,
        Func<CancellationToken, Task>? renewLease = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throttle is not null)
                await throttle.WaitAsync(PhysicalLength(candidate), cancellationToken, renewLease).ConfigureAwait(false);
            return await processor.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (BlobEncryptionFileProcessor.IsPerBlobFailure(exception))
        {
            return new BlobEncryptionVerificationResult(BlobEncryptionVerificationIssue.IoError);
        }
    }

    private static byte[] BuildCheckpoint(int processedFiles, long processedBytes, int failedFiles)
    {
        byte[] payload = new byte[16];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), processedFiles);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 8), processedBytes);
        BitConverter.TryWriteBytes(payload.AsSpan(12, 4), failedFiles);
        return payload;
    }

    private static string CreateOwner(string suffix) =>
        $"{Environment.MachineName}:{Environment.ProcessId}:{suffix}:{Guid.NewGuid():N}";

    internal sealed class OperationIoThrottle(long bytesPerSecond)
    {
        private readonly object _sync = new();
        private readonly long _rate = Math.Clamp(bytesPerSecond, 1, 1024L * 1024 * 1024 * 1024);
        private long _nextTimestamp = Stopwatch.GetTimestamp();

        public async Task WaitAsync(long bytes, CancellationToken cancellationToken,
            Func<CancellationToken, Task>? renewLease = null)
        {
            // Reserve at most one second of actual file I/O per step. This bounds timestamp
            // arithmetic and delay sizes even for a large sparse file at a low configured rate.
            long remaining = Math.Max(0, bytes);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (renewLease is not null) await renewLease(cancellationToken).ConfigureAwait(false);
                long chunk = Math.Min(remaining, _rate);
                TimeSpan delay;
                lock (_sync)
                {
                    long now = Stopwatch.GetTimestamp();
                    long start = Math.Max(now, _nextTimestamp);
                    long duration = (long)(chunk / (double)_rate * Stopwatch.Frequency);
                    _nextTimestamp = checked(start + duration);
                    delay = TimeSpan.FromSeconds((start - now) / (double)Stopwatch.Frequency);
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (renewLease is not null) await renewLease(cancellationToken).ConfigureAwait(false);
                remaining -= chunk;
                if (remaining == 0) return;
            }
        }
    }
}

internal sealed class BlobEncryptionMigrationRecoveryHandler(
    BlobEncryptionLifecycleService lifecycleService) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.BlobEncryptionMigration;

    public int SupportedCheckpointVersion => 1;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        await lifecycleService.RecoverAsync(operation, cancellationToken).ConfigureAwait(false);
}

internal sealed class BlobEncryptionKeyRotationRecoveryHandler(
    BlobEncryptionLifecycleService lifecycleService) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.BlobEncryptionKeyRotation;

    public int SupportedCheckpointVersion => 1;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        await lifecycleService.RecoverAsync(operation, cancellationToken).ConfigureAwait(false);
}

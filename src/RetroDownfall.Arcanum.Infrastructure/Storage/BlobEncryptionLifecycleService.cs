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

    public async Task<BlobEncryptionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BlobEncryptionCandidate> candidates = await metadataStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        int encrypted = 0;
        long encryptedBytes = 0;
        int legacy = 0;
        long legacyBytes = 0;
        int reconciliation = 0;
        int invalid = 0;
        Dictionary<string, int> byKey = new(StringComparer.Ordinal);

        foreach (BlobEncryptionCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlobEncryptionVerificationResult result = await processor
                .VerifyAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            bool hasEnvelope = blobStore.HasEnvelope(candidate.Path);
            if (hasEnvelope)
            {
                encrypted++;
                encryptedBytes += candidate.ExpectedPlaintextLength;
                string key = result.Descriptor?.KeyId
                    ?? candidate.EncryptionKeyId
                    ?? "<unknown>";
                byKey[key] = byKey.GetValueOrDefault(key) + 1;
            }
            else if (File.Exists(candidate.Path))
            {
                legacy++;
                legacyBytes += candidate.ExpectedPlaintextLength;
            }

            if (result.Issue is BlobEncryptionVerificationIssue.MetadataEncryptedFilePlaintext
                or BlobEncryptionVerificationIssue.MetadataPlaintextFileEncrypted)
            {
                reconciliation++;
            }
            else if (result.Issue != BlobEncryptionVerificationIssue.LegacyPlaintext)
            {
                invalid++;
            }
        }

        return new BlobEncryptionStatus(
            candidates.Count,
            candidates.Sum(static candidate => candidate.ExpectedPlaintextLength),
            encrypted,
            encryptedBytes,
            legacy,
            legacyBytes,
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
                rotateToKeyId: null,
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

        FileEncryptionKeyMaterial next = await keyRing.RotateAsync(cancellationToken)
            .ConfigureAwait(false);
        BlobEncryptionOperationResult result = await RunDurableAsync(
                lease.Operation,
                owner,
                next.KeyId,
                maxConcurrency,
                maxBytesPerSecond,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.FailedFiles == 0 && result.RemainingFiles == 0)
        {
            await RetireUnreferencedKeysAsync(next.KeyId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    internal async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        string? targetKeyId = null;
        if (operation.Kind == LongRunningOperationKinds.BlobEncryptionKeyRotation)
        {
            targetKeyId = (await keyRing.GetForWriteAsync(cancellationToken).ConfigureAwait(false)).KeyId;
        }

        IReadOnlyList<BlobEncryptionCandidate> candidates = await metadataStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (BlobEncryptionCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        if (targetKeyId is not null)
        {
            BlobEncryptionOperationResult verification = await RunVerificationAsync(
                    operation.Id,
                    maxConcurrency: 2,
                    maxBytesPerSecond: 64L * 1024 * 1024,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verification.FailedFiles != 0 || verification.RemainingFiles != 0)
            {
                return LongRunningOperationRecoveryResult.RequiresAttention(
                    LongRunningOperationErrorCodes.RecoveryFailed);
            }

            await RetireUnreferencedKeysAsync(targetKeyId, cancellationToken).ConfigureAwait(false);
        }

        return LongRunningOperationRecoveryResult.Completed();
    }

    private async Task RetireUnreferencedKeysAsync(
        string activeKeyId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BlobEncryptionCandidate> current = await metadataStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
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
                await keyRing.RetireAsync(keyId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<BlobEncryptionOperationResult> RunDurableAsync(
        LongRunningOperation operation,
        string owner,
        string? rotateToKeyId,
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BlobEncryptionCandidate> candidates = await metadataStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        ConcurrentDictionary<BlobEncryptionVerificationIssue, int> issues = new();
        int processed = 0;
        long processedBytes = 0;
        int failed = 0;
        SemaphoreSlim checkpointGate = new(1, 1);
        OperationIoThrottle throttle = new(maxBytesPerSecond);

        try
        {
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 8),
                    CancellationToken = cancellationToken,
                },
                async (candidate, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    LongRunningOperation? live = await operationStore
                        .GetAsync(operation.Id, ct)
                        .ConfigureAwait(false);
                    if (live?.State == LongRunningOperationState.Cancelling)
                    {
                        throw new OperationCanceledException(
                            "Blob encryption operation cancellation was requested.");
                    }

                    await throttle.WaitAsync(candidate.ExpectedPlaintextLength, ct)
                        .ConfigureAwait(false);
                    try
                    {
                        if (rotateToKeyId is null)
                        {
                            _ = await processor.MigrateAsync(candidate, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            _ = await processor.ReencryptAsync(candidate, rotateToKeyId, ct)
                                .ConfigureAwait(false);
                        }

                        Interlocked.Increment(ref processed);
                        Interlocked.Add(ref processedBytes, candidate.ExpectedPlaintextLength);
                    }
                    catch (Exception ex) when (ex is IOException
                                               or InvalidDataException
                                               or System.Security.Cryptography.CryptographicException)
                    {
                        Interlocked.Increment(ref failed);
                        BlobEncryptionVerificationResult verification = await processor
                            .VerifyAsync(candidate, CancellationToken.None)
                            .ConfigureAwait(false);
                        issues.AddOrUpdate(verification.Issue, 1, static (_, count) => count + 1);
                    }

                    await checkpointGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        _ = await operationCoordinator.HeartbeatAsync(
                                operation.Id,
                                owner,
                                LeaseDuration,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        LongRunningOperation? current = await operationStore
                            .GetAsync(operation.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (current is not null)
                        {
                            byte[] checkpoint = BuildCheckpoint(
                                Volatile.Read(ref processed),
                                Interlocked.Read(ref processedBytes),
                                Volatile.Read(ref failed));
                            _ = await operationCoordinator.CheckpointAsync(
                                    operation.Id,
                                    owner,
                                    current.CheckpointVersion,
                                    checkpointVersion: 1,
                                    checkpoint,
                                    checkpointReference: null,
                                    $"Processed {Volatile.Read(ref processed)} of {candidates.Count} files; "
                                    + $"{Interlocked.Read(ref processedBytes)} bytes complete; "
                                    + $"{Volatile.Read(ref failed)} files excluded.",
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        checkpointGate.Release();
                    }
                }).ConfigureAwait(false);

            BlobEncryptionOperationResult verification = await RunVerificationAsync(
                    operation.Id,
                    maxConcurrency,
                    maxBytesPerSecond,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach ((BlobEncryptionVerificationIssue issue, int count) in verification.Issues)
            {
                issues.AddOrUpdate(issue, count, (_, existing) => existing + count);
            }

            LongRunningOperation? completed = await operationStore
                .GetAsync(operation.Id, CancellationToken.None)
                .ConfigureAwait(false);
            if (completed is not null)
            {
                _ = await operationCoordinator.CompleteAsync(
                        operation.Id,
                        owner,
                        completed.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return verification with
            {
                ProcessedFiles = processed,
                ProcessedBytes = processedBytes,
                FailedFiles = failed + verification.FailedFiles,
                Issues = issues,
            };
        }
        catch (OperationCanceledException)
        {
            LongRunningOperation? cancelled = await operationStore
                .GetAsync(operation.Id, CancellationToken.None)
                .ConfigureAwait(false);
            if (cancelled is not null)
            {
                _ = await operationStore.TryTransitionAsync(
                        operation.Id,
                        cancelled.Revision,
                        owner,
                        LongRunningOperationState.Abandoned,
                        timeProvider.GetUtcNow(),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            checkpointGate.Dispose();
        }
    }

    private async Task<BlobEncryptionOperationResult> RunVerificationAsync(
        Guid operationId,
        int maxConcurrency,
        long maxBytesPerSecond,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BlobEncryptionCandidate> candidates = await metadataStore
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);
        ConcurrentDictionary<BlobEncryptionVerificationIssue, int> issues = new();
        int valid = 0;
        long validBytes = 0;
        OperationIoThrottle throttle = new(maxBytesPerSecond);
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxConcurrency, 1, 8),
                CancellationToken = cancellationToken,
            },
            async (candidate, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await throttle.WaitAsync(candidate.ExpectedPlaintextLength, ct).ConfigureAwait(false);
                BlobEncryptionVerificationResult result = await processor
                    .VerifyAsync(candidate, CancellationToken.None)
                    .ConfigureAwait(false);
                if (result.IsValid && result.Descriptor is not null)
                {
                    Interlocked.Increment(ref valid);
                    Interlocked.Add(ref validBytes, candidate.ExpectedPlaintextLength);
                }
                else
                {
                    issues.AddOrUpdate(result.Issue, 1, static (_, count) => count + 1);
                }
            }).ConfigureAwait(false);

        int failed = issues.Values.Sum();
        return new BlobEncryptionOperationResult(
            operationId,
            valid,
            validBytes,
            candidates.Count - valid,
            candidates.Sum(static candidate => candidate.ExpectedPlaintextLength) - validBytes,
            failed,
            issues);
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

    private sealed class OperationIoThrottle(long bytesPerSecond)
    {
        private readonly object _sync = new();
        private readonly long _rate = Math.Clamp(bytesPerSecond, 1, 1024L * 1024 * 1024 * 1024);
        private long _nextTimestamp = Stopwatch.GetTimestamp();

        public async Task WaitAsync(long bytes, CancellationToken cancellationToken)
        {
            TimeSpan delay;
            lock (_sync)
            {
                long now = Stopwatch.GetTimestamp();
                long start = Math.Max(now, _nextTimestamp);
                double seconds = Math.Max(0, bytes) / (double)_rate;
                long duration = checked((long)(seconds * Stopwatch.Frequency));
                _nextTimestamp = checked(start + duration);
                delay = TimeSpan.FromSeconds((start - now) / (double)Stopwatch.Frequency);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal sealed class BlobEncryptionMigrationRecoveryHandler(
    BlobEncryptionLifecycleService lifecycleService) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.BlobEncryptionMigration;

    public int SupportedCheckpointVersion => 1;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        lifecycleService.RecoverAsync(operation, cancellationToken);
}

internal sealed class BlobEncryptionKeyRotationRecoveryHandler(
    BlobEncryptionLifecycleService lifecycleService) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.BlobEncryptionKeyRotation;

    public int SupportedCheckpointVersion => 1;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken) =>
        lifecycleService.RecoverAsync(operation, cancellationToken);
}

using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class FileEncryptionKeyProviderTests
{
    [Fact]
    public async Task GetForWriteAsync_generates_a_dedicated_256_bit_secret_once()
    {
        RecordingSecretStore secrets = new(SecretStoreReadResult.Missing());
        FileEncryptionKeyProvider provider = new(secrets);

        FileEncryptionKeyMaterial first = await provider.GetForWriteAsync();
        FileEncryptionKeyMaterial second = await provider.GetForWriteAsync();

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.Equal(1, secrets.SaveCount);
        Assert.NotNull(secrets.SavedSecret);
        Assert.Equal(32, Convert.FromBase64String(secrets.SavedSecret!).Length);
    }

    [Fact]
    public async Task GetForReadAsync_missing_or_wrong_key_fails_closed_with_recovery_guidance()
    {
        FileEncryptionKeyProvider missing = new(
            new RecordingSecretStore(SecretStoreReadResult.Missing()));

        EncryptedBlobKeyException missingError =
            await Assert.ThrowsAsync<EncryptedBlobKeyException>(
                () => missing.GetForReadAsync("0123456789abcdef").AsTask());
        Assert.Contains("restore", missingError.Message, StringComparison.OrdinalIgnoreCase);

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        FileEncryptionKeyProvider wrong = new(
            new RecordingSecretStore(SecretStoreReadResult.Ok(secret)));
        await Assert.ThrowsAsync<EncryptedBlobKeyException>(
            () => wrong.GetForReadAsync("0123456789abcdef").AsTask());
    }

    [Fact]
    public async Task Ordinary_bootstrap_after_a_peek_still_runs_the_migration_capable_read()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RecordingSecretStore secrets = new(SecretStoreReadResult.Ok(secret));
        using FileEncryptionKeyProvider provider = new(secrets);
        using FileEncryptionKeyMaterial expected = FileEncryptionKeyMaterial.Create(
            Convert.FromBase64String(secret));

        _ = await provider.PeekForReadAsync(expected.KeyId);

        Assert.Equal(0, secrets.ReadCount);
        Assert.Equal(1, secrets.PeekCount);

        _ = await provider.GetForWriteAsync();

        Assert.Equal(1, secrets.ReadCount);
    }

    [Fact]
    public async Task Corrupt_protected_secret_never_generates_a_replacement()
    {
        RecordingSecretStore secrets = new(
            SecretStoreReadResult.Corrupted("protected secret is corrupt; restore backup"));
        FileEncryptionKeyProvider provider = new(secrets);

        EncryptedBlobKeyException error =
            await Assert.ThrowsAsync<EncryptedBlobKeyException>(
                () => provider.GetForWriteAsync().AsTask());

        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, secrets.SaveCount);
    }

    [Fact]
    public async Task Missing_secret_with_existing_ciphertext_never_generates_a_replacement()
    {
        RecordingSecretStore secrets = new(SecretStoreReadResult.Missing());
        FileEncryptionKeyProvider provider = new(secrets, encryptedBlobsExist: static () => true);

        EncryptedBlobKeyException error =
            await Assert.ThrowsAsync<EncryptedBlobKeyException>(
                () => provider.GetForWriteAsync().AsTask());

        Assert.Contains("restore", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, secrets.SaveCount);
    }

    [Fact]
    public async Task Rotate_retains_prior_key_for_reads_until_explicit_retirement()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RecordingSecretStore secrets = new(SecretStoreReadResult.Ok(secret));
        FileEncryptionKeyProvider provider = new(secrets);
        FileEncryptionKeyMaterial prior = await provider.GetForWriteAsync();

        FileEncryptionKeyMaterial current = await provider.RotateAsync();

        Assert.NotEqual(prior.KeyId, current.KeyId);
        Assert.Equal(current.KeyId, (await provider.GetForWriteAsync()).KeyId);
        Assert.Equal(prior.KeyId, (await provider.GetForReadAsync(prior.KeyId)).KeyId);
        Assert.Contains(prior.KeyId, await provider.GetActiveKeyIdsAsync());
        Assert.Contains(current.KeyId, await provider.GetActiveKeyIdsAsync());

        FileEncryptionKeyProvider restored = new(secrets);
        Assert.Equal(current.KeyId, (await restored.GetForWriteAsync()).KeyId);
        Assert.Equal(prior.KeyId, (await restored.GetForReadAsync(prior.KeyId)).KeyId);

        await restored.RetireAsync(prior.KeyId);

        await Assert.ThrowsAsync<EncryptedBlobKeyException>(
            () => restored.GetForReadAsync(prior.KeyId).AsTask());
        Assert.Equal([current.KeyId], await restored.GetActiveKeyIdsAsync());
    }

    // The persisted key ring must be LF-delimited on every platform: AppendLine emits CRLF on
    // Windows. This provider, BackupSecretSnapshotReader and BackupSecretRewrapper all accept CRLF,
    // which is what rescues rings an older Windows build already wrote — tolerance on the reading
    // side, one canonical form on the writing side. This pins the writing half, so the tolerance
    // never has to cover a ring this build produced.
    [Fact]
    public async Task Persisted_key_ring_is_line_feed_delimited_on_every_platform()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RecordingSecretStore secrets = new(SecretStoreReadResult.Ok(secret));
        FileEncryptionKeyProvider provider = new(secrets);
        _ = await provider.GetForWriteAsync();

        _ = await provider.RotateAsync();

        Assert.NotNull(secrets.SavedSecret);
        Assert.DoesNotContain('\r', secrets.SavedSecret!);
        Assert.StartsWith("ARCANUM-KEYRING-1\n", secrets.SavedSecret!, StringComparison.Ordinal);
    }

    // A ring already written with CRLF by an older Windows build must still load, otherwise the
    // install stays bricked after the writer is fixed.
    [Fact]
    public async Task Key_ring_written_with_carriage_returns_still_loads()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RecordingSecretStore source = new(SecretStoreReadResult.Ok(secret));
        FileEncryptionKeyProvider writer = new(source);
        FileEncryptionKeyMaterial prior = await writer.GetForWriteAsync();
        FileEncryptionKeyMaterial current = await writer.RotateAsync();
        string crlfRing = source.SavedSecret!.Replace("\n", "\r\n", StringComparison.Ordinal);

        FileEncryptionKeyProvider restored = new(
            new RecordingSecretStore(SecretStoreReadResult.Ok(crlfRing)));

        Assert.Equal(current.KeyId, (await restored.GetForWriteAsync()).KeyId);
        Assert.Equal(prior.KeyId, (await restored.GetForReadAsync(prior.KeyId)).KeyId);
    }

    // A reader leaves the ring's gate holding the shared material and only then reads MasterKey.Span to
    // derive its purpose key. Retirement runs inside the live host (the rotation-recovery handler,
    // driven by startup reconciliation or POST /operations/reconcile), so it can land between those two
    // steps. Zeroizing the buffer in place would leave that reader deriving from 32 zero bytes and
    // reporting a tag mismatch — a corruption signal — for a blob that reads fine with the live key.
    [Fact]
    public async Task Retirement_does_not_zeroize_material_a_reader_already_holds()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        RecordingSecretStore secrets = new(SecretStoreReadResult.Ok(secret));
        FileEncryptionKeyProvider provider = new(secrets);
        FileEncryptionKeyMaterial prior = await provider.GetForWriteAsync();
        _ = await provider.RotateAsync();

        FileEncryptionKeyMaterial held = await provider.GetForReadAsync(prior.KeyId);
        byte[] observed = held.MasterKey.ToArray();
        await provider.RetireAsync(prior.KeyId);

        Assert.Equal(observed, held.MasterKey.ToArray());
        Assert.NotEqual(-1, held.MasterKey.Span.IndexOfAnyExcept((byte)0));
    }

    [Fact]
    public async Task Active_write_key_cannot_be_retired()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        FileEncryptionKeyProvider provider = new(
            new RecordingSecretStore(SecretStoreReadResult.Ok(secret)));
        FileEncryptionKeyMaterial current = await provider.GetForWriteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RetireAsync(current.KeyId));
    }

    /// <summary>
    /// TryFindKey used to allocate two ASCII byte[] per candidate key on every read, so an
    /// unknown key id — the shape every miss takes, order-independent of dictionary enumeration —
    /// walked and allocated across the whole ring. A dictionary lookup does not; this asserts the
    /// allocation a five-key ring's read pays does not scale past a one-key ring's.
    /// </summary>
    [Fact]
    public async Task GetForReadAsync_lookup_allocation_does_not_scale_with_ring_size()
    {

        const int Repetitions = 100;

        const string UnknownKeyId = "does-not-exist-in-the-ring";

        FileEncryptionKeyProvider oneKeyRing = new(
            new RecordingSecretStore(
                SecretStoreReadResult.Ok(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));

        _ = await oneKeyRing.GetForWriteAsync();

        FileEncryptionKeyProvider fiveKeyRing = new(
            new RecordingSecretStore(
                SecretStoreReadResult.Ok(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));

        _ = await fiveKeyRing.GetForWriteAsync();

        for (int i = 0; i < 4; i++)
        {

            _ = await fiveKeyRing.RotateAsync();

        }

        // Warm up: JIT the lookup and the exception path on both providers before measuring
        // either, so first-call JIT cost never lands inside the measured window.
        await MissRepeatedlyAsync(oneKeyRing, UnknownKeyId, repetitions: 5);

        await MissRepeatedlyAsync(fiveKeyRing, UnknownKeyId, repetitions: 5);

        long oneKeyBytes = await MeasureMissAllocationAsync(oneKeyRing, UnknownKeyId, Repetitions);

        long fiveKeyBytes = await MeasureMissAllocationAsync(fiveKeyRing, UnknownKeyId, Repetitions);

        long deltaBytes = fiveKeyBytes - oneKeyBytes;

        // The old per-candidate array-allocating scan cost roughly 8 extra arrays (2 per each of
        // the 4 additional candidates) per repetition here — tens of thousands of bytes over 100
        // repetitions. The threshold sits far below that and comfortably above GC/JIT jitter.
        Assert.True(
            deltaBytes < Repetitions * 200,
            $"oneKeyRing={oneKeyBytes}; fiveKeyRing={fiveKeyBytes}; delta={deltaBytes}; "
                + $"repetitions={Repetitions}");

    }

    private static async Task MissRepeatedlyAsync(
        FileEncryptionKeyProvider provider,
        string unknownKeyId,
        int repetitions)
    {

        for (int i = 0; i < repetitions; i++)
        {

            try
            {

                _ = await provider.GetForReadAsync(unknownKeyId);

            }
            catch (EncryptedBlobKeyException)
            {

            }

        }

    }

    private static async Task<long> MeasureMissAllocationAsync(
        FileEncryptionKeyProvider provider,
        string unknownKeyId,
        int repetitions)
    {

        int managedThreadId = System.Environment.CurrentManagedThreadId;

        long before = GC.GetAllocatedBytesForCurrentThread();

        await MissRepeatedlyAsync(provider, unknownKeyId, repetitions);

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        if (System.Environment.CurrentManagedThreadId != managedThreadId)
        {

            throw new InvalidOperationException("Allocation measurement changed managed threads.");

        }

        return allocatedBytes;

    }

    private sealed class RecordingSecretStore(SecretStoreReadResult readResult) : ISecretStore
    {
        public int SaveCount { get; private set; }

        public int ReadCount { get; private set; }

        public int PeekCount { get; private set; }

        public string? SavedSecret { get; private set; }

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync()
        {
            ReadCount++;

            if (SavedSecret is not null)
            {
                return Task.FromResult(SecretStoreReadResult.Ok(SavedSecret));
            }

            return Task.FromResult(readResult);
        }

        public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync()
        {
            PeekCount++;

            if (SavedSecret is not null)
            {
                return Task.FromResult(SecretStoreReadResult.Ok(SavedSecret));
            }

            return Task.FromResult(readResult);
        }

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret)
        {
            SaveCount++;
            SavedSecret = encryptionSecret;
            return Task.CompletedTask;
        }
    }
}

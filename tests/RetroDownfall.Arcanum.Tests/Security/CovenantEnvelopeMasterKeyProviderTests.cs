using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Master-material handling: taken once, zeroized, boot-salted, and never reused across a rollback.
/// </summary>
public sealed class CovenantEnvelopeMasterKeyProviderTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Dataset = Guid.Parse("0D1E2F30-4152-4637-8899-AABBCCDDEEFF");

    private static readonly Guid NextDataset = Guid.Parse("12345678-90AB-4CDE-8F01-234567890ABC");

    [Fact]
    public void Initialize_takes_the_material_once_and_zeroizes_the_callers_buffer()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        byte[] material = Encoding.UTF8.GetBytes("master-key-material");

        Result first = RuntimeInitialize(keys, material, Transition());

        Assert.True(first.IsSuccess);
        Assert.All(material, value => Assert.Equal(0, value));

        Result second = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        Assert.False(second.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, second.Error.Code);

    }

    [Fact]
    public void Initialize_refuses_empty_material()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        Result result = RuntimeInitialize(keys, [], Transition());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, result.Error.Code);
        Assert.Null(keys.Current);

    }

    [Fact]
    public void Two_processes_holding_the_same_material_and_epoch_derive_different_envelope_keys()
    {

        // This is the database-snapshot rollback case. A restored covenant_state brings back the old
        // epoch and the old counters; only the fresh boot salt stops the second process from reusing a
        // (key, nonce) pair the first one already spent.
        using CovenantEnvelopeMasterKeyProvider first = new();

        using CovenantEnvelopeMasterKeyProvider second = new();

        _ = RuntimeInitialize(first, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        _ = RuntimeInitialize(second, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        Span<byte> firstKey = stackalloc byte[32];

        Span<byte> secondKey = stackalloc byte[32];

        try
        {

            Assert.Equal(
                CovenantEnvelopeKeyCopyStatus.Success,
                first.TryCopyPurposeKey(
                    CovenantEnvelopePurpose.Cursor,
                    firstKey,
                    out _));

            Assert.Equal(
                CovenantEnvelopeKeyCopyStatus.Success,
                second.TryCopyPurposeKey(
                    CovenantEnvelopePurpose.Cursor,
                    secondKey,
                    out _));

            Assert.False(firstKey.SequenceEqual(secondKey));

        }
        finally
        {

            CryptographicOperations.ZeroMemory(firstKey);

            CryptographicOperations.ZeroMemory(secondKey);

        }

    }

    [Fact]
    public void Two_processes_holding_the_same_material_derive_the_same_diagnostic_key()
    {

        // The mirror of the previous test, and the reason the boot salt is deliberately excluded here:
        // a correlation label that changed every restart would correlate nothing.
        using CovenantEnvelopeMasterKeyProvider first = new();

        using CovenantEnvelopeMasterKeyProvider second = new();

        _ = RuntimeInitialize(first, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        _ = RuntimeInitialize(second, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        Span<byte> firstKey = stackalloc byte[32];

        Span<byte> secondKey = stackalloc byte[32];

        Assert.True(first.TryCopyDiagnosticKey(firstKey, out uint firstVersion));

        Assert.True(second.TryCopyDiagnosticKey(secondKey, out uint secondVersion));

        Assert.True(firstKey.SequenceEqual(secondKey));
        Assert.Equal(firstVersion, secondVersion);

    }

    [Fact]
    public void Every_purpose_derives_a_distinct_key()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        List<byte[]> derived = [];

        try
        {

            foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
            {

                byte[] key = new byte[32];

                Assert.Equal(
                    CovenantEnvelopeKeyCopyStatus.Success,
                    keys.TryCopyPurposeKey(purpose, key, out _));

                derived.Add(key);

            }

            Assert.All(derived, key => Assert.Equal(32, key.Length));

            Assert.Equal(
                derived.Count,
                derived.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count());

        }
        finally
        {

            foreach (byte[] key in derived)
            {
                CryptographicOperations.ZeroMemory(key);
            }

        }

    }

    [Fact]
    public void An_advanced_epoch_or_version_re_keys_every_family_and_invalidates_old_tokens()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        FakeTimeProvider time = FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

        CovenantEnvelopeCodec codec = new(keys, time);

        string cursor = codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(30)).Value;

        string recovery = codec.Encode(
            CovenantEnvelopePurpose.FamilyReinitialize,
            [1],
            TimeSpan.FromMinutes(30)).Value;

        Assert.True(codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);

        Result rekeyed = RuntimePublish(
            keys,
            Transition(canonicalEnvelopeEpoch: 4, recoveryEnvelopeEpoch: 3));

        Assert.True(rekeyed.IsSuccess);

        Assert.False(codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);
        Assert.False(codec.Decode(CovenantEnvelopePurpose.FamilyReinitialize, recovery).IsSuccess);

        // The counter restarts with the new generation, which is safe precisely because the key changed.
        Assert.True(codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(1)).IsSuccess);

    }

    [Fact]
    public void Rekey_with_unchanged_recovery_epoch_invalidates_all_six_purposes()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        CovenantEnvelopeCodec codec = new(
            keys,
            FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        Dictionary<CovenantEnvelopePurpose, string> old = Enum
            .GetValues<CovenantEnvelopePurpose>()
            .ToDictionary(
                static purpose => purpose,
                purpose => codec.Encode(purpose, [(byte)purpose], TimeSpan.FromMinutes(5)).Value);

        Result rekeyed = RuntimePublish(
            keys,
            Transition(
                canonicalEnvelopeEpoch: 4,
                recoveryEnvelopeEpoch: 2,
                dataset: NextDataset));

        Assert.True(rekeyed.IsSuccess);

        foreach ((CovenantEnvelopePurpose purpose, string token) in old)
        {

            Assert.False(codec.Decode(purpose, token).IsSuccess);

            Assert.True(codec.Encode(purpose, [(byte)purpose], TimeSpan.FromMinutes(5)).IsSuccess);

        }

    }

    [Fact]
    public void Preparing_a_generation_does_not_publish_it_and_abandonment_preserves_current_tokens()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        CovenantEnvelopeCodec codec = new(
            keys,
            FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        CovenantEnvelopeKeyGeneration current = keys.Current!;

        string token = codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(5)).Value;

        CovenantCommittedAuthorityTransition transition = Transition(
            canonicalEnvelopeEpoch: 4,
            dataset: NextDataset);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);
        Assert.Same(current, keys.Current);

        prepared.Value.Dispose();

        Assert.Same(current, keys.Current);
        Assert.True(codec.Decode(CovenantEnvelopePurpose.Cursor, token).IsSuccess);

    }

    [Fact]
    public void Publishing_a_prepared_generation_swaps_once_and_transfers_ownership_once()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        CovenantEnvelopeCodec codec = new(
            keys,
            FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        CovenantEnvelopeKeyGeneration current = keys.Current!;

        string old = codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(5)).Value;

        CovenantCommittedAuthorityTransition transition = Transition(
            canonicalEnvelopeEpoch: 4,
            dataset: NextDataset);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        RuntimePublishPrepared(keys, prepared.Value, transition);

        CovenantEnvelopeKeyGeneration published = keys.Current!;

        Assert.NotSame(current, published);
        Assert.False(codec.Decode(CovenantEnvelopePurpose.Cursor, old).IsSuccess);
        Assert.True(codec.Encode(CovenantEnvelopePurpose.Cursor, [2], TimeSpan.FromMinutes(5)).IsSuccess);

        Assert.Throws<InvalidOperationException>(
            () => RuntimePublishPrepared(keys, prepared.Value, transition));
        Assert.Same(published, keys.Current);

    }

    [Fact]
    public void A_prepared_generation_can_only_publish_through_its_owner()
    {

        using CovenantEnvelopeMasterKeyProvider owner = new();

        using CovenantEnvelopeMasterKeyProvider other = new();

        _ = RuntimeInitialize(owner, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        _ = RuntimeInitialize(other, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        CovenantCommittedAuthorityTransition transition = Transition(
            canonicalEnvelopeEpoch: 4,
            dataset: NextDataset);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = owner.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        CovenantEnvelopeKeyGeneration otherCurrent = other.Current!;

        Assert.Throws<InvalidOperationException>(
            () => RuntimePublishPrepared(other, prepared.Value, transition));
        Assert.Same(otherCurrent, other.Current);

        RuntimePublishPrepared(owner, prepared.Value, transition);

        Assert.Equal(NextDataset, owner.Current!.Snapshot.DatasetGeneration);

    }

    [Fact]
    public void Retiring_live_keys_preserves_the_root_for_a_later_recovery_publication()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        CovenantEnvelopeCodec codec = new(
            keys,
            FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        string old = codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(5)).Value;

        RuntimeRetire(keys);

        Assert.Null(keys.Current);
        Assert.False(codec.Decode(CovenantEnvelopePurpose.Cursor, old).IsSuccess);
        Assert.False(codec.Encode(CovenantEnvelopePurpose.Cursor, [2], TimeSpan.FromMinutes(5)).IsSuccess);

        CovenantCommittedAuthorityTransition transition = Transition(
            canonicalEnvelopeEpoch: 4,
            dataset: NextDataset);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        RuntimePublishPrepared(keys, prepared.Value, transition);

        Assert.True(codec.Encode(CovenantEnvelopePurpose.Cursor, [3], TimeSpan.FromMinutes(5)).IsSuccess);

    }

    [Fact]
    public void Bootstrap_without_a_dataset_derives_only_recovery_families_and_diagnostics()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        Result initialized = RuntimeInitialize(
            keys,
            Encoding.UTF8.GetBytes("master-key-material"),
            Bootstrap(dataset: null));

        Assert.True(initialized.IsSuccess);

        CovenantEnvelopeCodec codec = new(
            keys,
            FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
        {

            Result<string> encoded = codec.Encode(purpose, [(byte)purpose], TimeSpan.FromMinutes(5));

            Assert.Equal(!CovenantEnvelopeLimits.IsDatasetKeyed(purpose), encoded.IsSuccess);

        }

        Span<byte> diagnosticKey = stackalloc byte[32];

        Assert.True(keys.TryCopyDiagnosticKey(diagnosticKey, out uint keyVersion));
        Assert.Equal(7u, keyVersion);

    }

    [Fact]
    public void Bootstrap_with_a_healthy_dataset_derives_all_six_families()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        Result initialized = RuntimeInitialize(
            keys,
            Encoding.UTF8.GetBytes("master-key-material"),
            Bootstrap(Dataset));

        Assert.True(initialized.IsSuccess);
        Assert.Equal(Dataset, keys.Current!.Snapshot.DatasetGeneration);

        CovenantEnvelopeCodec codec = new(
            keys,
            FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        foreach (CovenantEnvelopePurpose purpose in Enum.GetValues<CovenantEnvelopePurpose>())
        {
            Assert.True(codec.Encode(purpose, [(byte)purpose], TimeSpan.FromMinutes(5)).IsSuccess);
        }

    }

    [Fact]
    public void Committed_rekey_refuses_a_missing_dataset_without_changing_current()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        CovenantEnvelopeKeyGeneration current = keys.Current!;

        _ = Assert.Throws<ArgumentException>(() => Transition(dataset: Guid.Empty));

        Assert.Same(current, keys.Current);

    }

    [Fact]
    public void Partial_derivation_zeroizes_every_allocated_key_and_sensitive_temporary()
    {

        RecordingDerivationCheckpoint checkpoint = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(checkpoint);

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        CovenantEnvelopeCodec codec = new(
            keys,
            FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)));

        string current = codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(5)).Value;

        checkpoint.ThrowAfterPurposeKey(3);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(
            Transition(canonicalEnvelopeEpoch: 4, dataset: NextDataset));

        Assert.False(prepared.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, prepared.Error.Code);
        Assert.DoesNotContain("master-key-material", prepared.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Installation.ToString(), prepared.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(NextDataset.ToString(), prepared.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(codec.Decode(CovenantEnvelopePurpose.Cursor, current).IsSuccess);

        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.DiagnosticKey, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.PurposeKey, expectedCount: 3);
        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.GenerationSalt, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.DiagnosticBinding, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.DiagnosticInfo, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.PurposeBinding, expectedCount: 3);
        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.PurposeInfo, expectedCount: 3);

    }

    [Fact]
    public void Abandoning_a_prepared_generation_zeroizes_every_owned_key()
    {

        RecordingDerivationCheckpoint checkpoint = new();

        using CovenantEnvelopeMasterKeyProvider keys = new(checkpoint);

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        checkpoint.ObserveWithoutThrowing();

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareRekey(
            Transition(canonicalEnvelopeEpoch: 4, dataset: NextDataset));

        Assert.True(prepared.IsSuccess);

        prepared.Value.Dispose();

        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.PurposeKey, expectedCount: 6);
        checkpoint.AssertZeroized(CovenantEnvelopeSensitiveBufferKind.DiagnosticKey, expectedCount: 1);

    }

    [Fact]
    public async Task Diagnostic_key_copy_is_synchronized_with_retirement()
    {

        using BlockingKeyAccessCheckpoint checkpoint = new(
            CovenantEnvelopeKeyAccessStep.DiagnosticKeyCopied);

        using CovenantEnvelopeMasterKeyProvider keys = new(
            CovenantEnvelopeDerivationCheckpoint.None,
            checkpoint);

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        Task<DiagnosticCopy> copying = Task.Run(
            () =>
            {

                byte[] destination = new byte[32];

                bool copied = keys.TryCopyDiagnosticKey(destination, out uint keyVersion);

                return new DiagnosticCopy(copied, keyVersion, destination);

            });

        checkpoint.WaitUntilReached();

        Task retirement = Task.Run(() => RuntimeRetire(keys));

        try
        {

            Task completed = await Task.WhenAny(
                retirement,
                Task.Delay(TimeSpan.FromMilliseconds(100)));

            Assert.NotSame(retirement, completed);

        }
        finally
        {
            checkpoint.Release();
        }

        DiagnosticCopy copy = await copying;

        await retirement;

        Assert.True(copy.Copied);
        Assert.Equal(7u, copy.KeyVersion);
        Assert.Contains(copy.Key, static value => value != 0);
        Assert.Null(keys.Current);

        CryptographicOperations.ZeroMemory(copy.Key);

    }

    [Fact]
    public void Rekey_before_initialization_fails_closed()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        Result result = RuntimePublish(keys, Transition());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, result.Error.Code);

    }

    [Fact]
    public void Disposal_clears_every_key_and_leaves_no_generation()
    {

        CovenantEnvelopeMasterKeyProvider keys = new();

        _ = RuntimeInitialize(keys, Encoding.UTF8.GetBytes("master-key-material"), Transition());

        keys.Dispose();

        Assert.Null(keys.Current);

        Span<byte> key = stackalloc byte[32];

        Assert.False(keys.TryCopyDiagnosticKey(key, out _));

    }

    private static Result RuntimeInitialize(
        CovenantEnvelopeMasterKeyProvider keys,
        Span<byte> material,
        CovenantCommittedAuthorityTransition transition) =>
        CovenantEnvelopeRuntimeTestHarness.Initialize(keys, material, transition);

    private static Result RuntimeInitialize(
        CovenantEnvelopeMasterKeyProvider keys,
        Span<byte> material,
        CovenantEnvelopeBootstrapKeyInput input) =>
        CovenantEnvelopeRuntimeTestHarness.Initialize(keys, material, input);

    private static Result RuntimePublish(
        CovenantEnvelopeMasterKeyProvider keys,
        CovenantCommittedAuthorityTransition transition) =>
        CovenantEnvelopeRuntimeTestHarness.Publish(keys, transition);

    private static void RuntimePublishPrepared(
        CovenantEnvelopeMasterKeyProvider keys,
        CovenantPreparedEnvelopeKeyGeneration prepared,
        CovenantCommittedAuthorityTransition transition) =>
        CovenantEnvelopeRuntimeTestHarness.PublishOwned(keys, prepared, transition);

    private static void RuntimeRetire(CovenantEnvelopeMasterKeyProvider keys) =>
        CovenantEnvelopeRuntimeTestHarness.Retire(keys);

    private static CovenantCommittedAuthorityTransition Transition(
        long canonicalEnvelopeEpoch = 3,
        long recoveryEnvelopeEpoch = 2,
        uint masterKeyVersion = 7,
        Guid? dataset = null) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            authorityEpoch: 11,
            masterKeyVersion: masterKeyVersion,
            canonicalEnvelopeEpoch: canonicalEnvelopeEpoch,
            recoveryEnvelopeEpoch: recoveryEnvelopeEpoch,
            CovenantHostToolsState.Clean,
            transitionId: null,
            Capability(dataset ?? Dataset));

    private static CovenantEnvelopeBootstrapKeyInput Bootstrap(Guid? dataset) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            masterKeyVersion: 7,
            canonicalEnvelopeEpoch: 3,
            recoveryEnvelopeEpoch: 2,
            datasetGeneration: dataset);

    private static CovenantCommittedCapabilityTransition Capability(Guid dataset) =>
        new(
            ExpectedGeneration: 1,
            Generation: 2,
            FeatureEnabled: true,
            CovenantCapabilityState.Healthy,
            CanonicalSchemaVersion: 1,
            CanonicalInstalledFingerprint: "sha256-canonical",
            CovenantCapabilityState.Healthy,
            AcceleratorSchemaVersion: 1,
            AcceleratorInstalledFingerprint: "sha256-accelerator",
            dataset,
            CanonicalSequence: 0,
            CoreCampaignDeletionSequence: 0,
            CanonicalAppliedCampaignDeletionSequence: 0,
            CanonicalAppliedSessionDeletionSequence: 0,
            AppliedDatasetGeneration: null,
            AppliedSequence: null,
            AppliedCampaignDeletionSequence: null,
            AcceleratorEpoch: 1,
            CovenantFtsSynchronizationState.Dirty,
            RebuildRequired: true,
            CleanupAppliedCampaignSequence: 0,
            CleanupAppliedSessionSequence: 0,
            CleanupFullSweepRequired: false,
            CanonicalDiagnosticCode: null,
            AcceleratorDiagnosticCode: null);

    private sealed class RecordingDerivationCheckpoint : ICovenantEnvelopeDerivationCheckpoint
    {

        private readonly List<(CovenantEnvelopeSensitiveBufferKind Kind, bool IsZero)> _zeroizations = [];

        private int _throwAfterPurposeKey;

        public void ObserveWithoutThrowing()
        {

            _zeroizations.Clear();

            _throwAfterPurposeKey = 0;

        }

        public void ThrowAfterPurposeKey(int count)
        {

            _zeroizations.Clear();

            _throwAfterPurposeKey = count;

        }

        public void Reached(CovenantEnvelopeDerivationStep step, int purposeKeysDerived)
        {

            if (step is CovenantEnvelopeDerivationStep.PurposeKeyDerived
                && purposeKeysDerived == _throwAfterPurposeKey)
            {
                throw new InvalidOperationException("Injected derivation checkpoint failure.");
            }

        }

        public void Zeroized(CovenantEnvelopeSensitiveBufferKind kind, bool isZero) =>
            _zeroizations.Add((kind, isZero));

        public void AssertZeroized(CovenantEnvelopeSensitiveBufferKind kind, int expectedCount)
        {

            (CovenantEnvelopeSensitiveBufferKind Kind, bool IsZero)[] matching =
                [.. _zeroizations.Where(item => item.Kind == kind)];

            Assert.Equal(expectedCount, matching.Length);
            Assert.All(matching, static item => Assert.True(item.IsZero));

        }

    }

    private sealed class BlockingKeyAccessCheckpoint(
        CovenantEnvelopeKeyAccessStep blockedStep) : ICovenantEnvelopeKeyAccessCheckpoint, IDisposable
    {

        private readonly ManualResetEventSlim _reached = new();

        private readonly ManualResetEventSlim _release = new();

        public void Reached(CovenantEnvelopeKeyAccessStep step)
        {

            if (step != blockedStep)
            {
                return;
            }

            _reached.Set();

            _release.Wait();

        }

        public void WaitUntilReached() => Assert.True(_reached.Wait(TimeSpan.FromSeconds(5)));

        public void Release() => _release.Set();

        public void Dispose()
        {

            _reached.Dispose();

            _release.Dispose();

        }

    }

    private sealed record DiagnosticCopy(bool Copied, uint KeyVersion, byte[] Key);


    /// <summary>A fixed clock, so envelope timestamps and expiry are exact rather than approximate.</summary>
    private static FakeTimeProvider FakeClock(DateTimeOffset now)
    {

        FakeTimeProvider provider = new();

        provider.SetUtcNow(now);

        return provider;

    }

}

using System.Text;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Master-material handling: taken once, zeroized, boot-salted, and never reused across a rollback.
/// </summary>
public sealed class CovenantEnvelopeMasterKeyProviderTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Dataset = Guid.Parse("0D1E2F30-4152-4637-8899-AABBCCDDEEFF");

    [Fact]
    public void Initialize_takes_the_material_once_and_zeroizes_the_callers_buffer()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        byte[] material = Encoding.UTF8.GetBytes("master-key-material");

        Result first = keys.Initialize(material, Transition());

        Assert.True(first.IsSuccess);
        Assert.All(material, value => Assert.Equal(0, value));

        Result second = keys.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

        Assert.False(second.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, second.Error.Code);

    }

    [Fact]
    public void Initialize_refuses_empty_material()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        Result result = keys.Initialize([], Transition());

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

        _ = first.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

        _ = second.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

        Assert.False(
            first.Current!.PurposeKey(CovenantEnvelopePurpose.Cursor)
                .SequenceEqual(second.Current!.PurposeKey(CovenantEnvelopePurpose.Cursor)));

    }

    [Fact]
    public void Two_processes_holding_the_same_material_derive_the_same_diagnostic_key()
    {

        // The mirror of the previous test, and the reason the boot salt is deliberately excluded here:
        // a correlation label that changed every restart would correlate nothing.
        using CovenantEnvelopeMasterKeyProvider first = new();

        using CovenantEnvelopeMasterKeyProvider second = new();

        _ = first.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

        _ = second.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

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

        _ = keys.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

        List<byte[]> derived = [.. Enum.GetValues<CovenantEnvelopePurpose>()
            .Select(purpose => keys.Current!.PurposeKey(purpose).ToArray())];

        Assert.All(derived, key => Assert.Equal(32, key.Length));

        Assert.Equal(derived.Count, derived.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count());

    }

    [Fact]
    public void An_advanced_epoch_or_version_re_keys_every_family_and_invalidates_old_tokens()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        _ = keys.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

        FakeTimeProvider time = FakeClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

        CovenantEnvelopeCodec codec = new(keys, time);

        string cursor = codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(30)).Value;

        string recovery = codec.Encode(
            CovenantEnvelopePurpose.FamilyReinitialize,
            [1],
            TimeSpan.FromMinutes(30)).Value;

        Assert.True(codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);

        Result rekeyed = keys.Rekey(Transition(canonicalEnvelopeEpoch: 4, recoveryEnvelopeEpoch: 3));

        Assert.True(rekeyed.IsSuccess);

        Assert.False(codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);
        Assert.False(codec.Decode(CovenantEnvelopePurpose.FamilyReinitialize, recovery).IsSuccess);

        // The counter restarts with the new generation, which is safe precisely because the key changed.
        Assert.True(codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(1)).IsSuccess);

    }

    [Fact]
    public void Rekey_before_initialization_fails_closed()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        Result result = keys.Rekey(Transition());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, result.Error.Code);

    }

    [Fact]
    public void Disposal_clears_every_key_and_leaves_no_generation()
    {

        CovenantEnvelopeMasterKeyProvider keys = new();

        _ = keys.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

        keys.Dispose();

        Assert.Null(keys.Current);

        Span<byte> key = stackalloc byte[32];

        Assert.False(keys.TryCopyDiagnosticKey(key, out _));

    }

    private static CovenantCommittedAuthorityTransition Transition(
        long canonicalEnvelopeEpoch = 3,
        long recoveryEnvelopeEpoch = 2,
        uint masterKeyVersion = 7) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            authorityEpoch: 11,
            masterKeyVersion: masterKeyVersion,
            canonicalEnvelopeEpoch: canonicalEnvelopeEpoch,
            recoveryEnvelopeEpoch: recoveryEnvelopeEpoch,
            capabilityGeneration: 1,
            datasetGeneration: Dataset,
            covenantEnabled: true);


    /// <summary>A fixed clock, so envelope timestamps and expiry are exact rather than approximate.</summary>
    private static FakeTimeProvider FakeClock(DateTimeOffset now)
    {

        FakeTimeProvider provider = new();

        provider.SetUtcNow(now);

        return provider;

    }

}

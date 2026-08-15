using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantDisclosureStateAlgebraTests
{
    [Fact]
    public void Receipt_digest_maps_to_four_literal_disclosure_bloom_positions()
    {
        byte[] bloom = CovenantDisclosureStateAlgebra.CreateEvidenceBloom(Digest(1));

        Assert.Equal("0000000000000000000001000000000000000020000000100000100000000000", Convert.ToHexString(bloom));
    }

    [Fact]
    public void Local_increment_is_checked_exactly_once_and_preserves_lower_bound()
    {
        CovenantDisclosureState exact = State(CovenantDisclosureCountKind.Exact, true, 4, 100, [0x01]);
        CovenantDisclosureState lower = State(CovenantDisclosureCountKind.LowerBound, true, 7, 200, [0x02]);

        CovenantDisclosureState incrementedExact = CovenantDisclosureStateAlgebra.IncrementLocal(exact, 3, 150, Bloom(0x04));
        CovenantDisclosureState incrementedLower = CovenantDisclosureStateAlgebra.IncrementLocal(lower, 2, 175, Bloom(0x08));

        Assert.Equal(CovenantDisclosureCountKind.Exact, incrementedExact.CountKind);
        Assert.Equal(7UL, incrementedExact.Count);
        Assert.True(incrementedExact.EverOccurred);
        Assert.Equal(150, incrementedExact.MaximumTimestamp);
        Assert.Equal((byte)0x05, incrementedExact.EvidenceBloom[0]);

        Assert.Equal(CovenantDisclosureCountKind.LowerBound, incrementedLower.CountKind);
        Assert.Equal(9UL, incrementedLower.Count);
        Assert.Equal(200, incrementedLower.MaximumTimestamp);
        Assert.Equal((byte)0x0A, incrementedLower.EvidenceBloom[0]);

        Assert.NotEqual(incrementedExact, CovenantDisclosureStateAlgebra.IncrementLocal(incrementedExact, 3, 150, Bloom(0x04)));
        Assert.Throws<OverflowException>(() => CovenantDisclosureStateAlgebra.IncrementLocal(State(CovenantDisclosureCountKind.Exact, true, ulong.MaxValue, 1, [0x01]), 1, 1, Bloom(0x02)));
    }

    [Fact]
    public void Restore_join_uses_overlap_safe_maximum_and_monotonic_evidence()
    {
        CovenantDisclosureState left = State(CovenantDisclosureCountKind.Exact, true, 4, 100, [0x01, 0x10]);
        CovenantDisclosureState right = State(CovenantDisclosureCountKind.Exact, true, 7, 200, [0x02, 0x20]);

        CovenantDisclosureState joined = CovenantDisclosureStateAlgebra.JoinRestore(left, right);

        Assert.Equal(CovenantDisclosureCountKind.LowerBound, joined.CountKind);
        Assert.True(joined.EverOccurred);
        Assert.Equal(7UL, joined.Count);
        Assert.Equal(200, joined.MaximumTimestamp);
        Assert.Equal((byte)0x03, joined.EvidenceBloom[0]);
        Assert.Equal((byte)0x30, joined.EvidenceBloom[1]);
    }

    [Fact]
    public void Restore_join_compares_counts_as_unsigned_above_long_max_value()
    {
        ulong aboveSignedMaximum = (ulong)long.MaxValue + 1;
        CovenantDisclosureState left = State(CovenantDisclosureCountKind.Exact, true, aboveSignedMaximum, 100, [0x01]);
        CovenantDisclosureState right = State(CovenantDisclosureCountKind.Exact, true, ulong.MaxValue - 1, 200, [0x02]);

        CovenantDisclosureState joined = CovenantDisclosureStateAlgebra.JoinRestore(left, right);

        Assert.Equal(ulong.MaxValue - 1, joined.Count);
        Assert.Equal(CovenantDisclosureCountKind.LowerBound, joined.CountKind);
    }

    [Fact]
    public void Restore_join_is_associative_commutative_and_idempotent()
    {
        CovenantDisclosureState a = State(CovenantDisclosureCountKind.Exact, true, 2, 100, [0x01]);
        CovenantDisclosureState b = State(CovenantDisclosureCountKind.Exact, true, 5, 200, [0x02]);
        CovenantDisclosureState c = State(CovenantDisclosureCountKind.LowerBound, true, 3, 150, [0x04]);

        Assert.Equal(
            CovenantDisclosureStateAlgebra.JoinRestore(CovenantDisclosureStateAlgebra.JoinRestore(a, b), c),
            CovenantDisclosureStateAlgebra.JoinRestore(a, CovenantDisclosureStateAlgebra.JoinRestore(b, c)));
        Assert.Equal(CovenantDisclosureStateAlgebra.JoinRestore(a, b), CovenantDisclosureStateAlgebra.JoinRestore(b, a));
        Assert.Equal(a, CovenantDisclosureStateAlgebra.JoinRestore(a, a));
    }

    [Fact]
    public void Disclosure_state_digest_vector_is_exact()
    {
        CovenantDisclosureState state = new(
            CovenantEgressDestination.Network,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantDisclosureCountKind.Exact,
            true,
            7,
            1700000001,
            Enumerable.Range(0, CovenantLimits.DisclosureEvidenceBloomBytes).Select(static value => (byte)value).ToArray());

        Assert.Equal("F69C65DE3A27794B3B04A1143174D96E41CCF610DDF437D9F05666CFEA0C4E2A", state.Digest.ToString());
    }

    [Fact]
    public void Exact_false_zero_zero_zero_bloom_is_the_only_empty_state()
    {
        CovenantDisclosureState empty = CovenantDisclosureState.Empty(
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable);

        Assert.Equal(CovenantDisclosureCountKind.Exact, empty.CountKind);
        Assert.False(empty.EverOccurred);
        Assert.Equal(0UL, empty.Count);
        Assert.Equal(0, empty.MaximumTimestamp);
        Assert.All(empty.EvidenceBloom, static value => Assert.Equal((byte)0, value));

        Assert.Throws<ArgumentException>(() => State(CovenantDisclosureCountKind.LowerBound, false, 0, 0, []));
        Assert.Throws<ArgumentException>(() => State(CovenantDisclosureCountKind.Exact, false, 1, 0, [0x01]));
        Assert.Throws<ArgumentException>(() => State(CovenantDisclosureCountKind.Exact, true, 0, 1, [0x01]));
        Assert.Throws<ArgumentException>(() => State(CovenantDisclosureCountKind.Exact, true, 1, 0, [0x01]));
        Assert.Throws<ArgumentException>(() => State(CovenantDisclosureCountKind.Exact, true, 1, 1, []));
    }

    [Fact]
    public void Empty_state_literal_is_exact()
    {
        CovenantDisclosureState empty = CovenantDisclosureState.Empty(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable);

        Assert.Equal("124888B9A1C15CA1A396EFFF2292CE17BED8C8D1C14774C4342D819C819ACC7A", empty.Digest.ToString());
    }

    [Fact]
    public void Nonempty_states_and_local_increments_require_positive_timestamps()
    {
        byte[] evidence = Bloom(0x01);
        CovenantDisclosureState empty = CovenantDisclosureState.Empty(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable);
        CovenantDisclosureState existing = State(CovenantDisclosureCountKind.Exact, true, 1, 10, [0x01]);

        Assert.Throws<ArgumentException>(() => State(CovenantDisclosureCountKind.Exact, true, 1, -1, [0x01]));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ExternalDisclosureState(new ExternalDisclosureStateDigestInput(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 1, -1, [.. evidence])));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDisclosureStateAlgebra.IncrementLocal(empty, 1, 0, evidence));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDisclosureStateAlgebra.IncrementLocal(empty, 1, -1, evidence));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDisclosureStateAlgebra.IncrementLocal(existing, 1, -1, evidence));
    }

    [Fact]
    public void Local_increment_moves_the_empty_state_to_one_exact_occurrence()
    {
        CovenantDisclosureState empty = CovenantDisclosureState.Empty(
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable);

        CovenantDisclosureState incremented = CovenantDisclosureStateAlgebra.IncrementLocal(empty, 1, 42, Bloom(0x04));

        Assert.Equal(CovenantDisclosureCountKind.Exact, incremented.CountKind);
        Assert.True(incremented.EverOccurred);
        Assert.Equal(1UL, incremented.Count);
        Assert.Equal(42, incremented.MaximumTimestamp);
        Assert.Equal((byte)0x04, incremented.EvidenceBloom[0]);
    }

    [Fact]
    public void Restore_join_rejects_different_state_buckets()
    {
        CovenantDisclosureState provider = State(CovenantDisclosureCountKind.Exact, true, 1, 1, [0x01]);
        CovenantDisclosureState network = new(
            CovenantEgressDestination.Network,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantDisclosureCountKind.Exact,
            true,
            1,
            1,
            Bloom(0x01));

        Assert.Throws<ArgumentException>(() => CovenantDisclosureStateAlgebra.JoinRestore(provider, network));
    }

    [Fact]
    public void Empty_joins_and_last_bloom_byte_are_monotonic()
    {
        CovenantDisclosureState empty = CovenantDisclosureState.Empty(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable);
        byte[] lastByteBloom = Bloom();

        lastByteBloom[^1] = 0x80;

        CovenantDisclosureState one = CovenantDisclosureStateAlgebra.IncrementLocal(empty, 1, 1, lastByteBloom);
        CovenantDisclosureState joined = CovenantDisclosureStateAlgebra.JoinRestore(empty, one);

        Assert.Equal(empty, CovenantDisclosureStateAlgebra.JoinRestore(empty, empty));
        Assert.Equal(CovenantDisclosureCountKind.LowerBound, joined.CountKind);
        Assert.Equal(1UL, joined.Count);
        Assert.Equal(1, joined.MaximumTimestamp);
        Assert.Equal((byte)0x80, joined.EvidenceBloom[^1]);
    }

    private static CovenantDisclosureState State(
        CovenantDisclosureCountKind countKind,
        bool everOccurred,
        ulong count,
        long timestamp,
        byte[] setBits) =>
        new(
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable,
            countKind,
            everOccurred,
            count,
            timestamp,
            Bloom(setBits));

    private static byte[] Bloom(params byte[] prefix)
    {
        byte[] bloom = new byte[CovenantLimits.DisclosureEvidenceBloomBytes];

        prefix.CopyTo(bloom, 0);

        return bloom;
    }

    private static CovenantDigest Digest(byte value) =>
        new(Enumerable.Repeat(value, CovenantLimits.DigestBytes).ToArray());
}

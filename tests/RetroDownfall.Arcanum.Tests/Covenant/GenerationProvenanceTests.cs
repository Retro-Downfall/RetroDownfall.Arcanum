using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class GenerationProvenanceTests
{
    private static readonly Guid RawFirst = Guid.Parse("00010000-0000-0000-0000-000000000000");

    private static readonly Guid RawSecond = Guid.Parse("01000000-0000-0000-0000-000000000000");

    public static TheoryData<int> ExactCounts =>
        new()
        {
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8
        };

    [Theory]
    [MemberData(nameof(ExactCounts))]
    public void Zero_through_eight_distinct_generations_remain_exact(int count)
    {
        Guid[] ids = Enumerable.Range(1, count).Select(Generation).Reverse().ToArray();

        GenerationProvenance provenance = GenerationProvenance.Create(ids);

        Assert.Equal(GenerationProvenanceMode.Exact, provenance.Mode);
        Assert.Equal(Enumerable.Range(1, count).Select(Generation), provenance.ExactGenerationIds);
        Assert.Empty(provenance.BloomBits);
    }

    [Fact]
    public void Exact_factory_deduplicates_and_uses_raw_network_guid_order()
    {
        Guid[] source = [RawSecond, RawFirst, RawSecond];

        GenerationProvenance provenance = GenerationProvenance.Create(source);

        source[0] = Guid.Empty;

        Assert.Equal<Guid>([RawFirst, RawSecond], provenance.ExactGenerationIds);
        Assert.False(provenance.IsEmpty);
    }

    [Fact]
    public void Ninth_distinct_generation_permanently_transitions_to_literal_bloom()
    {
        GenerationProvenance eight = GenerationProvenance.Create(Enumerable.Range(1, 8).Select(Generation));

        GenerationProvenance duplicateAtEight = eight.Add(Generation(8));
        GenerationProvenance ninth = duplicateAtEight.Add(Generation(9));
        GenerationProvenance afterDuplicate = ninth.Add(Generation(1));

        Assert.Equal(GenerationProvenanceMode.Exact, duplicateAtEight.Mode);
        Assert.Equal(GenerationProvenanceMode.BloomOverflow, ninth.Mode);
        Assert.Equal("20000A1001860000000801000001000000044400002100491601522100005001", Convert.ToHexString(ninth.BloomBits.AsSpan()));
        Assert.Equal(ninth, afterDuplicate);
        Assert.False(ninth.ContainsExact(Generation(1)));
    }

    [Fact]
    public void Persisted_nonzero_bloom_vectors_match_each_generation_hash_contribution()
    {
        GenerationProvenance one = Bloom("0000001000000000000000000000000000000000002100000400000000000000");
        GenerationProvenance duplicatePositions = Bloom("0000000000008000000000000000000000000000000000000008000008000000");
        GenerationProvenance lsbPositions = Bloom("0100000200002000000000000000000000000000000000000000000002000000");

        Assert.Equal("0000001000000000000000000000000000000000002100000400000000000000", Convert.ToHexString(one.BloomBits.AsSpan()));
        Assert.Equal("0000000000008000000000000000000000000000000000000008000008000000", Convert.ToHexString(duplicatePositions.BloomBits.AsSpan()));
        Assert.Equal("0100000200002000000000000000000000000000000000000000000002000000", Convert.ToHexString(lsbPositions.BloomBits.AsSpan()));
        Assert.Same(one, one.Add(Generation(1)));
        Assert.Same(duplicatePositions, duplicatePositions.Add(Generation(63)));
        Assert.Same(lsbPositions, lsbPositions.Add(Generation(101)));
    }

    [Fact]
    public void Public_api_cannot_force_exact_provenance_into_bloom_before_the_ninth_distinct_id()
    {
        MethodInfo? forcedTransition = typeof(GenerationProvenance).GetMethod(
            "ToBloom",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.Null(forcedTransition);
    }

    [Fact]
    public void Direct_nine_item_factory_transitions_without_retaining_an_exact_overflow()
    {
        GenerationProvenance provenance = GenerationProvenance.Create(Enumerable.Range(1, 9).Select(Generation));

        Assert.Equal(GenerationProvenanceMode.BloomOverflow, provenance.Mode);
        Assert.Empty(provenance.ExactGenerationIds);
        Assert.Equal(CovenantLimits.GenerationBloomBytes, provenance.BloomBits.Length);
    }

    [Fact]
    public void Bloom_factory_is_permutation_and_duplicate_invariant()
    {
        Guid[] ascending = Enumerable.Range(1, 9).Select(Generation).ToArray();
        Guid[] reversed = ascending.Reverse().ToArray();
        Guid[] duplicateInterleaved = ascending.SelectMany(static value => new[] { value, value }).Reverse().ToArray();

        GenerationProvenance first = GenerationProvenance.Create(ascending);
        GenerationProvenance second = GenerationProvenance.Create(reversed);
        GenerationProvenance third = GenerationProvenance.Create(duplicateInterleaved);
        CovenantDigest firstDigest = CovenantDigests.Sensitivity(first.ToDigestInput(ContentSensitivity.CovenantDerived));

        Assert.Equal("20000A1001860000000801000001000000044400002100491601522100005001", Convert.ToHexString(first.BloomBits.AsSpan()));
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(firstDigest, CovenantDigests.Sensitivity(second.ToDigestInput(ContentSensitivity.CovenantDerived)));
        Assert.Equal(firstDigest, CovenantDigests.Sensitivity(third.ToDigestInput(ContentSensitivity.CovenantDerived)));
    }

    [Fact]
    public void Exact_and_bloom_merges_follow_literal_transition_and_or_rules()
    {
        GenerationProvenance left = GenerationProvenance.Create(Enumerable.Range(1, 5).Select(Generation));
        GenerationProvenance overlap = GenerationProvenance.Create(Enumerable.Range(4, 5).Select(Generation));
        GenerationProvenance exactEight = left.Merge(overlap);
        GenerationProvenance ninth = GenerationProvenance.Create([Generation(9)]);
        GenerationProvenance overflow = exactEight.Merge(ninth);
        GenerationProvenance otherBloom = Bloom("0000000000008000000000000000000000000000000000000008000008000000");
        GenerationProvenance bloomUnion = overflow.Merge(otherBloom);
        GenerationProvenance exactSubset = GenerationProvenance.Create([Generation(1), Generation(2)]);
        GenerationProvenance emptyExact = GenerationProvenance.Create([]);
        GenerationProvenance g1ThroughEightBloom = Bloom("0000001000000000000000000000000000000000002100000400000000000000");

        for (int generation = 2; generation <= 8; generation++)
        {
            g1ThroughEightBloom = g1ThroughEightBloom.Add(Generation(generation));
        }

        Assert.Equal(GenerationProvenanceMode.Exact, exactEight.Mode);
        Assert.Equal(Enumerable.Range(1, 8).Select(Generation), exactEight.ExactGenerationIds);
        Assert.Equal("2000021001860000000801000001000000044400002100091601522100005001", Convert.ToHexString(g1ThroughEightBloom.BloomBits.AsSpan()));
        Assert.Equal("20000A1001860000000801000001000000044400002100491601522100005001", Convert.ToHexString(overflow.BloomBits.AsSpan()));
        Assert.Equal("20000A1001868000000801000001000000044400002100491609522108005001", Convert.ToHexString(bloomUnion.BloomBits.AsSpan()));
        Assert.Equal(overflow, exactSubset.Merge(overflow));
        Assert.Equal(overflow, overflow.Merge(exactSubset));
        Assert.Equal(overflow, emptyExact.Merge(overflow));
        Assert.Equal(GenerationProvenanceMode.BloomOverflow, exactSubset.Merge(overflow).Mode);
    }

    [Fact]
    public void Sensitivity_uses_monotonic_maximum_and_rejects_unknown_codes()
    {
        Assert.Equal(ContentSensitivity.None, ContentSensitivityAlgebra.Maximum(ContentSensitivity.None, ContentSensitivity.None));
        Assert.Equal(ContentSensitivity.CovenantDerived, ContentSensitivityAlgebra.Maximum(ContentSensitivity.None, ContentSensitivity.CovenantDerived));
        Assert.Equal(ContentSensitivity.CovenantDerived, ContentSensitivityAlgebra.Maximum(ContentSensitivity.CovenantDerived, ContentSensitivity.None));
        Assert.Equal(ContentSensitivity.CovenantDerived, ContentSensitivityAlgebra.Maximum(ContentSensitivity.CovenantDerived, ContentSensitivity.CovenantDerived));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContentSensitivityAlgebra.Maximum((ContentSensitivity)2, ContentSensitivity.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContentSensitivityAlgebra.Maximum(ContentSensitivity.None, (ContentSensitivity)2));
    }

    [Theory]
    [InlineData(0, ContentSensitivity.None, "75847DCBCBADA78EF12DDC7A559D3303336CA7B7603CE1C2544467FA8C98B6E3")]
    [InlineData(1, ContentSensitivity.CovenantDerived, "E931CF505F684C0E8182A34A0D319E43A445D48F32DF1BE735063E19692440A9")]
    [InlineData(2, ContentSensitivity.CovenantDerived, "94C220D3859BDD7A97CBDE355F232417C2E0EF886C17ED1582A215B37A5FD9A7")]
    [InlineData(3, ContentSensitivity.CovenantDerived, "14DD9047D8CC2A5E9FA139093B29F0C815C386F6FE23B24675667DD23D57BCA5")]
    [InlineData(4, ContentSensitivity.CovenantDerived, "9D7FCEF0BEC40856E3CC472198B5E87AFD703E4E97E36269DD200775C1B62146")]
    [InlineData(5, ContentSensitivity.CovenantDerived, "462FEB0CF3400720454AAFB70F75E3A67AEBFAB71066233935EBE33C23B2108C")]
    [InlineData(6, ContentSensitivity.CovenantDerived, "F98AE99C1F0F610F95C944A94879FF75E0E6E7CC9AA558B2A797B901CE528E37")]
    [InlineData(7, ContentSensitivity.CovenantDerived, "75DC69B14D43D8A6A273B6C5BD4BE9F36A5C8E3E72B463EAD5988F079F941EF7")]
    [InlineData(8, ContentSensitivity.CovenantDerived, "EBE37EA7AEBBFF9FCCCB45AD5A0D9662AD3725DC08646F8F7DEDCBC7F13D2EA0")]
    public void Sensitivity_exact_digest_literals_bind_only_level_and_exact_leaves(int count, ContentSensitivity level, string expected)
    {
        GenerationProvenance provenance = GenerationProvenance.Create(Enumerable.Range(1, count).Select(Generation).Reverse());

        CovenantDigest digest = CovenantDigests.Sensitivity(provenance.ToDigestInput(level));

        Assert.Equal(expected, digest.ToString());
    }

    [Fact]
    public void Sensitivity_bloom_and_raw_guid_order_literals_are_exact()
    {
        GenerationProvenance bloom = GenerationProvenance.Create(Enumerable.Range(1, 9).Select(Generation));
        GenerationProvenance rawOrder = GenerationProvenance.Create([RawSecond, RawFirst]);

        Assert.Equal("4AD060A834D41357712E04A27A98A148141EA95D08FEBDCD1412FEB8A565274F", CovenantDigests.Sensitivity(bloom.ToDigestInput(ContentSensitivity.CovenantDerived)).ToString());
        Assert.Equal("2A8F170FD9C3B49CBC65385842495A673C4BA50C3CAB2057C81014F2A0E00602", CovenantDigests.Sensitivity(rawOrder.ToDigestInput(ContentSensitivity.CovenantDerived)).ToString());
    }

    [Fact]
    public void Invalid_and_inconsistent_provenance_shapes_fail_closed()
    {
        ImmutableArray<Guid> emptyIds = [];
        ImmutableArray<byte> emptyBloom = [];
        byte[] zeroBloom = new byte[CovenantLimits.GenerationBloomBytes];
        byte[] oneBitBloom = new byte[CovenantLimits.GenerationBloomBytes];

        oneBitBloom[0] = 1;

        ImmutableArray<byte> validBloom = oneBitBloom.ToImmutableArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationProvenance((GenerationProvenanceMode)0, emptyIds, emptyBloom));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GenerationProvenance((GenerationProvenanceMode)byte.MaxValue, emptyIds, emptyBloom));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.Exact, default, emptyBloom));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.Exact, emptyIds, default));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.Exact, [Guid.Empty], emptyBloom));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.Exact, [Generation(2), Generation(1)], emptyBloom));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.Exact, [Generation(1), Generation(1)], emptyBloom));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.Exact, [.. Enumerable.Range(1, 9).Select(Generation)], emptyBloom));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.BloomOverflow, default, validBloom));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.BloomOverflow, emptyIds, default));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.BloomOverflow, emptyIds, zeroBloom.ToImmutableArray()));
        Assert.Throws<ArgumentException>(() => new GenerationProvenance(GenerationProvenanceMode.BloomOverflow, [Generation(1)], new byte[CovenantLimits.GenerationBloomBytes].ToImmutableArray()));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.CreateBloom(new byte[CovenantLimits.GenerationBloomBytes - 1]));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.CreateBloom(zeroBloom));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.CreateExact(Enumerable.Range(1, 9).Select(Generation)));
        Assert.Throws<ArgumentNullException>(() => GenerationProvenance.Create(null!));
        Assert.Throws<ArgumentNullException>(() => GenerationProvenance.CreateExact(null!));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.Create([Guid.Empty]));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.Create([]).Add(Guid.Empty));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.Create([]).ContainsExact(Guid.Empty));
        Assert.Throws<ArgumentNullException>(() => GenerationProvenance.Create([]).Merge(null!));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.Create([]).ToDigestInput(ContentSensitivity.CovenantDerived));
        Assert.Throws<ArgumentException>(() => GenerationProvenance.Create([Generation(1)]).ToDigestInput(ContentSensitivity.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => GenerationProvenance.Create([]).ToDigestInput((ContentSensitivity)2));
    }

    [Fact]
    public void Factory_inputs_are_copied_and_content_equality_is_explicit()
    {
        byte[] bits = new byte[CovenantLimits.GenerationBloomBytes];

        bits[3] = 0x80;

        GenerationProvenance first = GenerationProvenance.CreateBloom(bits);
        GenerationProvenance second = GenerationProvenance.CreateBloom(bits);

        bits[3] = 0;

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal((byte)0x80, first.BloomBits[3]);
    }

    [Fact]
    public void Json_constructor_deep_copies_caller_owned_exact_and_bloom_arrays()
    {
        Guid originalGeneration = Generation(1);
        Guid[] exactBacking = [originalGeneration];
        byte[] bloomBacking = new byte[CovenantLimits.GenerationBloomBytes];

        bloomBacking[3] = 0x80;

        GenerationProvenance exact = new(
            GenerationProvenanceMode.Exact,
            ImmutableCollectionsMarshal.AsImmutableArray(exactBacking),
            []);
        GenerationProvenance bloom = new(
            GenerationProvenanceMode.BloomOverflow,
            [],
            ImmutableCollectionsMarshal.AsImmutableArray(bloomBacking));
        ArtifactSensitivityLabel label = new(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            SensitiveArtifactKind.AssistantEntry,
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            sessionId: null,
            campaignId: null,
            turnId: null,
            artifactRevision: 0,
            new CovenantDigest(Enumerable.Repeat((byte)1, CovenantLimits.DigestBytes).ToArray()),
            ContentSensitivity.CovenantDerived,
            exact,
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            DateTimeOffset.UnixEpoch);
        CovenantDigest exactDigest = CovenantDigests.Sensitivity(exact.ToDigestInput(ContentSensitivity.CovenantDerived));
        CovenantDigest bloomDigest = CovenantDigests.Sensitivity(bloom.ToDigestInput(ContentSensitivity.CovenantDerived));
        CovenantDigest labelDigest = label.LabelDigest;

        exactBacking[0] = Generation(2);
        bloomBacking[3] = 0;
        bloomBacking[4] = 0x40;

        Assert.Equal(originalGeneration, Assert.Single(exact.ExactGenerationIds));
        Assert.Equal((byte)0x80, bloom.BloomBits[3]);
        Assert.Equal((byte)0, bloom.BloomBits[4]);
        Assert.Equal(exactDigest, CovenantDigests.Sensitivity(exact.ToDigestInput(ContentSensitivity.CovenantDerived)));
        Assert.Equal(bloomDigest, CovenantDigests.Sensitivity(bloom.ToDigestInput(ContentSensitivity.CovenantDerived)));
        Assert.Equal(
            labelDigest,
            CovenantDigests.ArtifactLabel(label.ToDigestInput() with
            {
                SensitivityDigest = CovenantDigests.Sensitivity(label.Provenance.ToDigestInput(label.Sensitivity))
            }));
    }

    [Fact]
    public void Fixed_seed_merge_properties_are_associative_commutative_idempotent_and_permutation_stable()
    {
        Random random = new(0x5EED_0079);

        for (int index = 0; index < 512; index++)
        {
            GenerationProvenance a = RandomProvenance(random);
            GenerationProvenance b = RandomProvenance(random);
            GenerationProvenance c = RandomProvenance(random);

            Assert.Equal(a.Merge(b), b.Merge(a));
            Assert.Equal(a, a.Merge(a));
            Assert.Equal(a.Merge(b).Merge(c), a.Merge(b.Merge(c)));

            if (a.Mode == GenerationProvenanceMode.Exact)
            {
                GenerationProvenance permuted = GenerationProvenance.Create(a.ExactGenerationIds.Reverse());

                Assert.Equal(a, permuted);
                Assert.Equal(
                    CovenantDigests.Sensitivity(a.ToDigestInput(a.IsEmpty ? ContentSensitivity.None : ContentSensitivity.CovenantDerived)),
                    CovenantDigests.Sensitivity(permuted.ToDigestInput(permuted.IsEmpty ? ContentSensitivity.None : ContentSensitivity.CovenantDerived)));
            }
        }
    }

    private static GenerationProvenance RandomProvenance(Random random)
    {
        int count = random.Next(0, 21);
        Guid[] values = new Guid[count];

        for (int index = 0; index < values.Length; index++)
        {
            values[index] = Generation(random.Next(1, 17));
        }

        return GenerationProvenance.Create(values);
    }

    private static Guid Generation(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:x12}");

    private static GenerationProvenance Bloom(string hexadecimalBits) =>
        GenerationProvenance.CreateBloom(Convert.FromHexString(hexadecimalBits));
}

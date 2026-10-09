using System.Reflection;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// The keyed erasure digests, pinned by hand-assembled preimages.
/// </summary>
/// <remarks>
/// Every expected value is computed twice: once by an independent HMAC over bytes written out by hand
/// below, never through the grammar, and once as a literal. The reference must equal the literal and
/// the grammar must equal the reference, so a grammar change and a matching test edit cannot drift
/// together unnoticed.
/// </remarks>
public sealed class MemoryErasureDigestGrammarTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(static value => (byte)value)];

    private const string Campaign = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";

    private const string CampaignBytes = "3F2504E04F8911D39A0C0305E82C3301";

    private const string Mutation = "6F9619FF-8B86-D011-B42D-00C04FC964FF";

    private const string MutationBytes = "6F9619FF8B86D011B42D00C04FC964FF";

    private const string Memory = "7C9E6679-7425-40DE-944B-E07FC1F90AE7";

    private const string LexiconEntry = "AAAAAAAA-1111-4111-8111-111111111111";

    private const string LexiconEntryBytes = "AAAAAAAA111141118111111111111111";

    private const string CovenantEntry = "BBBBBBBB-2222-4222-8222-222222222222";

    private const string CovenantEntryBytes = "BBBBBBBB222242228222222222222222";

    private const string Claim = "11111111-2222-4333-8444-555555555555";

    private const string ClaimBytes = "11111111222243338444555555555555";

    private const string AnnalVersion = "22222222-3333-4444-8555-666666666666";

    private const string AnnalVersionBytes = "22222222333344448555666666666666";

    private const string LabelId = "33333333-4444-4555-8666-777777777777";

    private const string LabelIdBytes = "33333333444445558666777777777777";

    private const string ConfirmedVersion = "44444444-5555-4666-8777-888888888888";

    private const string ConfirmedVersionBytes = "44444444555546668777888888888888";

    private const string DatasetGeneration = "55555555-6666-4777-8888-999999999999";

    private const string DatasetGenerationBytes = "55555555666647778888999999999999";

    private const int DigestBytes = 32;

    private const string CafeNfc = "caf\u00E9";

    private const string CafeNfd = "cafe\u0301";

    private const string CafeNfcBytes = "636166C3A9";

    private const string CafeNfdBytes = "63616665CC81";

    [Fact]
    public void Key_id_is_the_first_sixteen_bytes_of_the_labelled_hmac()
    {
        byte[] reference = Reference(Label("Arcanum.MemoryErasure.KeyId.v1"))[..16];

        AssertVector("D5046E85FE5A45250A18B7BE9B743D3D", reference, MemoryErasureDigestGrammar.KeyId(Key));

        Assert.Equal(16, MemoryErasureDigestGrammar.KeyIdBytes);

        Assert.Equal(32, MemoryErasureDigestGrammar.KeyBytes);

        Assert.Equal(DigestBytes, MemoryErasureDigestGrammar.DigestBytes);
    }

    [Fact]
    public void Saga_fingerprint_binds_exact_content_bytes_and_campaign_guid_bytes()
    {
        AssertVector(
            "C1E1157273EAAD6380189566B10E1D310C363C8ED7339E827AF49B0FBEBCF9FC",
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("020201"), Hex(CampaignBytes), Hex("00000005"), Hex(CafeNfcBytes)),
            MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Guid.Parse(Campaign), CafeNfc)));

        AssertVector(
            "2DF703C75F5C1A30CC578D527363C663767D0CB9833820FC7D367C2B036FF310",
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("020201"), Hex(CampaignBytes), Hex("00000006"), Hex(CafeNfdBytes)),
            MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Guid.Parse(Campaign), CafeNfd)));
    }

    [Fact]
    public void Saga_scope_byte_distinguishes_every_scope_kind()
    {
        byte[] global = MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, CafeNfc));

        byte[] unclassified = MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Unclassified, null, CafeNfc));

        byte[] legacy = MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.LegacyUnresolved, null, CafeNfc));

        byte[] campaign = MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Guid.Parse(Campaign), CafeNfc));

        AssertVector(
            "0264C8974C6663D3EC3957A012A137EEC48864FB39F5BC07C309B233653216CA",
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("020100"), Hex("00000005"), Hex(CafeNfcBytes)),
            global);

        AssertVector(
            "AB0C9532CB4F9A54E8253379C0EC82DA6DA3857283410F0F64F66D20B55FD717",
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("020000"), Hex("00000005"), Hex(CafeNfcBytes)),
            unclassified);

        Assert.Equal(
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("020300"), Hex("00000005"), Hex(CafeNfcBytes)),
            legacy);

        Assert.Equal(4, new[] { global, unclassified, legacy, campaign }.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Lexicon_fingerprint_trims_and_uppercases_the_name()
    {
        byte[] reference = Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("030100"), Hex("00000005"), Hex("414C494345"));

        AssertVector(
            "995141892E6F98DC4B8CDF90DEC4EB595FCFADD0263C7639BA20FFE364D54D48",
            reference,
            MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForLexicon(null, " alice ")));

        Assert.Equal(reference, MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForLexicon(null, "ALICE")));

        AssertVector(
            "88534824EA5CCF3A253FF227B811ADCCE5D60D73C9379F2E8E7C615CF636ED26",
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("030201"), Hex(CampaignBytes), Hex("00000005"), Hex("414C494345")),
            MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForLexicon(Guid.Parse(Campaign), "alice")));
    }

    [Fact]
    public void Covenant_fingerprint_binds_the_normalized_key_verbatim()
    {
        AssertVector(
            "0CF3673C21260CAC6A7E94A9C9E7AF72CA2CCA3330C95F183A71A263EF48AA46",
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("010201"), Hex(CampaignBytes), Hex("0000000C"), Hex("706572736F6E612E746F6E65")),
            MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, Guid.Parse(Campaign), "persona.tone")));

        byte[] global = MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForCovenant(CovenantScope.Global, null, "persona.tone"));

        AssertVector(
            "27B804DB028B681F2221519B3EE9EDCDE29EDD4004A13CF9A1C54411E52A3C25",
            Reference(Label("Arcanum.MemoryErasure.Fingerprint.v1"), Hex("010100"), Hex("0000000C"), Hex("706572736F6E612E746F6E65")),
            global);

        // The normalized key is the identity as given. The grammar folds nothing, so a key the
        // Covenant grammar would never produce is a different identity rather than an alias.
        Assert.NotEqual(global, MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForCovenant(CovenantScope.Global, null, "PERSONA.TONE")));
    }

    [Fact]
    public void Request_digests_bind_non_content_fields_only()
    {
        AssertVector(
            "9AAAB777F97F4FA9C49802C42C78E8ED5A963A80E5FE9E7545F2A2C0DC62CE13",
            Reference(Label("Arcanum.MemoryErasure.Request.v1"), Hex("02"), Hex(MutationBytes), Hex("00000024"), Ascii(Memory), Hex("00")),
            MemoryErasureDigestGrammar.SagaRequest(Key, Guid.Parse(Mutation), Memory, null));

        AssertVector(
            "42B74D9E3DF5EAE19D6C9FE5E1721BA4463D514438523561E36AD173FF5CBDB1",
            Reference(Label("Arcanum.MemoryErasure.Request.v1"), Hex("02"), Hex(MutationBytes), Hex("00000024"), Ascii(Memory), Hex("01"), Hex(ClaimBytes)),
            MemoryErasureDigestGrammar.SagaRequest(Key, Guid.Parse(Mutation), Memory, Guid.Parse(Claim)));

        byte[] lexiconReference = Reference(
            Label("Arcanum.MemoryErasure.Request.v1"),
            Hex("03"),
            Hex(MutationBytes),
            Hex("0201"),
            Hex(CampaignBytes),
            Hex(LexiconEntryBytes),
            Hex("0000000000000003"),
            Hex("01"),
            Hex(AnnalVersionBytes),
            Hex("01"),
            Hex(LabelIdBytes),
            Hex("0000000000000002"));

        byte[] lexicon = MemoryErasureDigestGrammar.LexiconRequest(Key, Guid.Parse(Mutation), CampaignLexiconTarget());

        // Everything a Lexicon target carries that describes content moves while the durable digest
        // stays put, so a stored receipt can never be used to test a guess about what was erased.
        // Asserted before the pinned vector, so a grammar that starts binding content fails here on
        // the invariance itself rather than only on a changed literal.
        LexiconCurationTarget contentMoved = CampaignLexiconTarget() with
        {
            NormalizedName = "SOMEONE ELSE",
            SnapshotDigest = new string('d', 64),
            AnnalHead = CampaignLexiconTarget().AnnalHead with { ContentHash = new string('e', 64) },
            SensitivityLabel = CampaignLexiconTarget().SensitivityLabel with { ArtifactContentDigest = new string('f', 64) },
        };

        Assert.Equal(lexicon, MemoryErasureDigestGrammar.LexiconRequest(Key, Guid.Parse(Mutation), contentMoved));

        AssertVector("5A12E136E2D2823CE8875B3383AC6652CDD18D1E0477D956A740ECEE02693BEE", lexiconReference, lexicon);

        AssertVector(
            "41BE807E5C5D9FA1801EAE1A08AE09C502A5E6F0BB1156910B3C55C8C3497853",
            Reference(Label("Arcanum.MemoryErasure.Request.v1"), Hex("03"), Hex(MutationBytes), Hex("0100"), Hex(LexiconEntryBytes), Hex("0000000000000001"), Hex("00"), Hex("00")),
            MemoryErasureDigestGrammar.LexiconRequest(Key, Guid.Parse(Mutation), GlobalLexiconTargetWithoutHeadOrLabel()));

        byte[] covenant = MemoryErasureDigestGrammar.CovenantRequest(Key, Guid.Parse(Mutation), CampaignCovenantPrepare("persona.tone"));

        // The Covenant key is content too, and the same invariance holds for it.
        Assert.Equal(covenant, MemoryErasureDigestGrammar.CovenantRequest(Key, Guid.Parse(Mutation), CampaignCovenantPrepare("some.other.key")));

        AssertVector(
            "689486726CE84404F6F3E18F4891456EAE2B93F3221335C1BB5DB845D92AC86E",
            Reference(
                Label("Arcanum.MemoryErasure.Request.v1"),
                Hex("01"),
                Hex(MutationBytes),
                Hex("0201"),
                Hex(CampaignBytes),
                Hex(CovenantEntryBytes),
                Hex("01"),
                Hex(ConfirmedVersionBytes),
                Hex("0000000000000004"),
                Hex("00")),
            covenant);
    }

    [Fact]
    public void Subject_and_content_binding_digests_canonicalize_the_row_id()
    {
        byte[] sagaSubject = Reference(Label("Arcanum.MemoryErasure.Subject.v1"), Hex("02"), Hex("00000024"), Ascii(Memory));

        foreach (string spelling in new[] { Memory, Memory.ToLowerInvariant(), Guid.Parse(Memory).ToString("N") })
        {
            AssertVector(
                "A1748872FE5B50C5CB5449686C607887E2FD1E3A693D95D94240AB635E0BB1FA",
                sagaSubject,
                MemoryErasureDigestGrammar.Subject(Key, MemoryReviewStore.Saga, spelling));
        }

        AssertVector(
            "0E9235CF9C2A86B4326972E124EBC813BB6542C9830D6651D52D41C30E8B2444",
            Reference(Label("Arcanum.MemoryErasure.Subject.v1"), Hex("03"), Hex("00000024"), Ascii(LexiconEntry)),
            MemoryErasureDigestGrammar.Subject(Key, MemoryReviewStore.Lexicon, Guid.Parse(LexiconEntry).ToString()));

        AssertVector(
            "422101860C7015FE6E11C414B2794CC570EA61CA98012E99139FEC7AB6E83CB4",
            Reference(Label("Arcanum.MemoryErasure.ContentBinding.v1"), Hex("02"), Hex("00000024"), Ascii(Memory), Hex("00000005"), Hex(CafeNfcBytes)),
            MemoryErasureDigestGrammar.SagaContentBinding(Key, Memory.ToLowerInvariant(), CafeNfc));

        Assert.Equal(Memory, MemoryErasureDigestGrammar.CanonicalRowId(Guid.Parse(Memory).ToString("N")));
    }

    [Fact]
    public void Effect_digest_sorts_rows_and_aligns_versions()
    {
        const string Second = "0a1b2c3d-0000-4000-8000-000000000002";

        MemoryErasureEffectFacts saga = SagaEffect();

        byte[] sagaDigest = MemoryErasureDigestGrammar.Effect(Key, saga);

        AssertVector(
            "7CB701C67C4CDDC768EF4D38A8B25028723829C91D71B4F97045C3820388B41A",
            Reference(
                Label("Arcanum.MemoryErasure.Effect.v1"),
                Hex("02"),
                Hex("00000002"),
                Hex("00000024"),
                Ascii(Second.ToUpperInvariant()),
                Hex("00000024"),
                Ascii(Memory),
                Hex("00000002"),
                Hex("00"),
                Hex("01"),
                Hex(ClaimBytes),
                Hex("00000002"),
                Hex("01"),
                Hex("0000000000000002"),
                Hex("02"),
                Hex("0000000000000002"),
                Hex("00000000"),
                Hex("00000001"),
                Hex("01"),
                Hex("0103010203"),
                Hex("000000DF")),
            sagaDigest);

        // The caller's row order is not part of the effect, and each version travels with its row.
        Assert.Equal(sagaDigest, MemoryErasureDigestGrammar.Effect(Key, saga with
        {
            RowIds = [Second, Memory.ToLowerInvariant()],
            Versions = [null, Guid.Parse(Claim)],
        }));

        AssertVector(
            "31ABEBE1DE834B96327C64E2641CD67C90668925FF834B6238F8EB0C7BFF7FB4",
            Reference(
                Label("Arcanum.MemoryErasure.Effect.v1"),
                Hex("01"),
                Hex("00000001"),
                Hex("00000024"),
                Ascii(CovenantEntry),
                Hex("00000001"),
                Hex("01"),
                Hex(ConfirmedVersionBytes),
                Hex("00000002"),
                Hex("11"),
                Hex("0000000000000003"),
                Hex("10"),
                Hex("0000000000000001"),
                Hex("00000000"),
                Hex("00000000"),
                Hex("05"),
                Hex(DatasetGenerationBytes),
                Hex("0000000000000007"),
                Hex("0000000000000002"),
                Hex("0102040303"),
                Hex("000000DF")),
            MemoryErasureDigestGrammar.Effect(Key, CovenantEffect()));
    }

    [Fact]
    public void Campaign_identities_in_any_spelling_yield_one_fingerprint()
    {
        foreach (string spelling in new[] { Campaign, Campaign.ToLowerInvariant(), Guid.Parse(Campaign).ToString("N") })
        {
            Guid campaign = Guid.Parse(spelling);

            Assert.Equal(
                "C1E1157273EAAD6380189566B10E1D310C363C8ED7339E827AF49B0FBEBCF9FC",
                Convert.ToHexString(MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, campaign, CafeNfc))));

            Assert.Equal(
                "88534824EA5CCF3A253FF227B811ADCCE5D60D73C9379F2E8E7C615CF636ED26",
                Convert.ToHexString(MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForLexicon(campaign, "alice"))));

            Assert.Equal(
                "0CF3673C21260CAC6A7E94A9C9E7AF72CA2CCA3330C95F183A71A263EF48AA46",
                Convert.ToHexString(MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, campaign, "persona.tone"))));
        }
    }

    [Fact]
    public void Saga_identity_is_exact_bytes()
    {
        Assert.NotEqual(SagaGlobal(CafeNfc), SagaGlobal(CafeNfc + " "));

        Assert.NotEqual(SagaGlobal("a\r\nb"), SagaGlobal("a\nb"));

        Assert.NotEqual(SagaGlobal(CafeNfc), SagaGlobal(CafeNfd));
    }

    /// <summary>
    /// An identity's text names its store, scope and Campaign and never its value, which is the erased
    /// content, name or key.
    /// </summary>
    /// <remarks>
    /// A positional record struct prints every member, so an identity that reached a log line, an
    /// exception message or a debugger view would copy exactly what an erasure removes.
    /// </remarks>
    [Fact]
    public void An_identity_prints_its_store_scope_and_campaign_but_never_its_value()
    {
        Guid campaign = Guid.Parse(Campaign);

        const string content = "the exact erased memory content";

        MemoryErasureIdentity[] identities =
        [
            MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, campaign, content),
            MemoryErasureIdentity.ForLexicon(campaign, "Warden Of The Mill"),
            MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, campaign, "persona.private.tone"),
        ];

        foreach (MemoryErasureIdentity identity in identities)
        {
            string printed = identity.ToString();

            Assert.DoesNotContain(identity.Value, printed, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(content, $"{identity}", StringComparison.Ordinal);

            Assert.Contains($"Store = {identity.Store}", printed, StringComparison.Ordinal);

            Assert.Contains("Scope = Campaign", printed, StringComparison.Ordinal);

            Assert.Contains($"CampaignId = {campaign}", printed, StringComparison.Ordinal);
        }

        Assert.StartsWith("MemoryErasureIdentity { ", identities[0].ToString(), StringComparison.Ordinal);

        Assert.EndsWith(" }", identities[0].ToString(), StringComparison.Ordinal);

        // The value is still the identity: leaving it out of the text must not leave it out of equality.
        Assert.NotEqual(
            MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "one"),
            MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "two"));

        Assert.Equal(
            MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "one"),
            MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "one"));

        // A default identity bypasses the factories, and printing one must not throw.
        Assert.StartsWith("MemoryErasureIdentity { ", default(MemoryErasureIdentity).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_shapes_throw_rather_than_digest()
    {
        Guid campaign = Guid.Parse(Campaign);

        Guid mutation = Guid.Parse(Mutation);

        // The baselines every refusal below departs from by exactly one field: each store's whole
        // flag set is accepted, so a refusal is about the field it changes and nothing else.
        Assert.Equal(DigestBytes, MemoryErasureDigestGrammar.Effect(Key, SagaEffect()).Length);

        Assert.Equal(DigestBytes, MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Store = MemoryReviewStore.Lexicon,
            Flags = MemoryErasureEffectFlags.Pinned | MemoryErasureEffectFlags.GlobalEntryResurfaces,
        }).Length);

        Assert.Equal(DigestBytes, MemoryErasureDigestGrammar.Effect(Key, CovenantEffect() with
        {
            Flags = MemoryErasureEffectFlags.Pinned
                | MemoryErasureEffectFlags.ReclaimsKey
                | MemoryErasureEffectFlags.RetainsCampaignMask
                | MemoryErasureEffectFlags.GlobalConfirmedResurfaces,
        }).Length);

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, null, CafeNfc));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Guid.Empty, CafeNfc));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, campaign, CafeNfc));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, null, "persona.tone"));

        foreach (MemoryReviewStore store in new[] { MemoryReviewStore.Lexicon, MemoryReviewStore.Covenant })
        {
            foreach (MemoryErasureScopeKind scope in new[] { MemoryErasureScopeKind.Unclassified, MemoryErasureScopeKind.LegacyUnresolved })
            {
                Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Fingerprint(Key, new MemoryErasureIdentity(store, scope, null, "ALICE")));
            }
        }

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, string.Empty));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureIdentity.ForLexicon(null, "   "));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Fingerprint(Key, new MemoryErasureIdentity(MemoryReviewStore.Saga, MemoryErasureScopeKind.Global, null, string.Empty)));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Fingerprint(Key, default));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "a\uD800b")));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.SagaContentBinding(Key, Memory, "a\uD800b"));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.CanonicalRowId("not-a-guid"));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Subject(Key, MemoryReviewStore.Saga, "not-a-guid"));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.SagaRequest(Key, mutation, "not-a-guid", null));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.CovenantRequest(Key, mutation, CampaignCovenantPrepare("persona.tone") with
        {
            Scope = CovenantScope.Global,
            CampaignId = null,
            Proposed = new CovenantEraseHeadExpectation(Guid.Parse(ConfirmedVersion), 1),
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.CovenantRequest(Key, mutation, CampaignCovenantPrepare("persona.tone") with
        {
            Confirmed = new CovenantEraseHeadExpectation(Guid.Parse(ConfirmedVersion), -1),
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.CovenantRequest(Key, mutation, CampaignCovenantPrepare("persona.tone") with
        {
            CampaignId = null,
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with { Versions = [null] }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            RowIds = [Memory, Memory.ToLowerInvariant()],
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Evidence = [MemoryExternalEvidence.Known, MemoryExternalEvidence.Known, MemoryExternalEvidence.Known, MemoryExternalEvidence.Known],
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Evidence = [.. Enumerable.Repeat(MemoryExternalEvidence.Known, 6)],
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Evidence = [MemoryExternalEvidence.Known, MemoryExternalEvidence.Known, MemoryExternalEvidence.Known, MemoryExternalEvidence.Known, 0],
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Targets = [new MemoryErasureTableCount("saga_memoriez", 2)],
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Targets = [new MemoryErasureTableCount("saga_memories", -1)],
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Targets = [new MemoryErasureTableCount("saga_memories", 2), new MemoryErasureTableCount("saga_memories", 2)],
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with { Labels = -1 }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with { RetirementSuppressions = -1 }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with { RetainedCopiesMask = 256 }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, CovenantEffect() with { Covenant = null }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, CovenantEffect() with
        {
            Covenant = new CovenantErasureEffectFacts(Guid.Parse(DatasetGeneration), -1, 2),
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Covenant = new CovenantErasureEffectFacts(Guid.Parse(DatasetGeneration), 7, 2),
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Store = MemoryReviewStore.Lexicon,
            Covenant = new CovenantErasureEffectFacts(Guid.Parse(DatasetGeneration), 7, 2),
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with { Flags = MemoryErasureEffectFlags.GlobalEntryResurfaces }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, SagaEffect() with
        {
            Store = MemoryReviewStore.Lexicon,
            Flags = MemoryErasureEffectFlags.ReclaimsKey,
        }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, CovenantEffect() with { Flags = MemoryErasureEffectFlags.GlobalEntryResurfaces }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Effect(Key, CovenantEffect() with { Flags = (MemoryErasureEffectFlags)32 }));

        byte[] shortKey = new byte[31];

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.KeyId(shortKey));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.Fingerprint(shortKey, MemoryErasureIdentity.ForLexicon(null, "alice")));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureKey.FromBytes(shortKey));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.LexiconRequest(Key, mutation, CampaignLexiconTarget() with { CurationGeneration = 0 }));

        Assert.ThrowsAny<ArgumentException>(() => MemoryErasureDigestGrammar.LexiconRequest(Key, mutation, CampaignLexiconTarget() with
        {
            AnnalHead = CampaignLexiconTarget().AnnalHead with { VersionId = "not-a-guid" },
        }));
    }

    [Fact]
    public void Key_copies_its_input_exposes_the_key_id_and_zeroes_on_dispose()
    {
        byte[] buffer = [.. Key];

        MemoryErasureKey key = MemoryErasureKey.FromBytes(buffer);

        buffer.AsSpan().Fill(0xFF);

        Assert.Equal("D5046E85FE5A45250A18B7BE9B743D3D", Convert.ToHexString(key.KeyId));

        MemoryErasureIdentity identity = MemoryErasureIdentity.ForLexicon(Guid.Parse(Campaign), "alice");

        Guid mutation = Guid.Parse(Mutation);

        Assert.Equal(MemoryErasureDigestGrammar.Fingerprint(Key, identity), key.Fingerprint(identity));

        Assert.Equal(MemoryErasureDigestGrammar.SagaRequest(Key, mutation, Memory, Guid.Parse(Claim)), key.SagaRequest(mutation, Memory, Guid.Parse(Claim)));

        Assert.Equal(MemoryErasureDigestGrammar.LexiconRequest(Key, mutation, CampaignLexiconTarget()), key.LexiconRequest(mutation, CampaignLexiconTarget()));

        Assert.Equal(
            MemoryErasureDigestGrammar.CovenantRequest(Key, mutation, CampaignCovenantPrepare("persona.tone")),
            key.CovenantRequest(mutation, CampaignCovenantPrepare("persona.tone")));

        Assert.Equal(MemoryErasureDigestGrammar.Subject(Key, MemoryReviewStore.Saga, Memory), key.Subject(MemoryReviewStore.Saga, Memory));

        Assert.Equal(MemoryErasureDigestGrammar.Effect(Key, SagaEffect()), key.Effect(SagaEffect()));

        Assert.Equal(MemoryErasureDigestGrammar.SagaContentBinding(Key, Memory, CafeNfc), key.SagaContentBinding(Memory, CafeNfc));

        byte[] keyId = key.KeyId.ToArray();

        Assert.True(key.HasKeyId(keyId));

        keyId[^1] ^= 0x01;

        Assert.False(key.HasKeyId(keyId));

        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.Fingerprint(identity));

        Assert.Equal("D5046E85FE5A45250A18B7BE9B743D3D", Convert.ToHexString(key.KeyId));
    }

    /// <summary>
    /// Disposal overwrites the key's own private copy, which no public member exposes, so the buffer is
    /// read through reflection: a Dispose that only flagged the key as disposed would still refuse every
    /// digest and leave the bytes in memory.
    /// </summary>
    [Fact]
    public void Dispose_zeroes_the_private_key_buffer_and_never_touches_the_callers_copy()
    {
        byte[] caller = [.. Key];

        MemoryErasureKey key = MemoryErasureKey.FromBytes(caller);

        FieldInfo field = typeof(MemoryErasureKey).GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MemoryErasureKey no longer holds its bytes in a field named _key.");

        byte[] held = Assert.IsType<byte[]>(field.GetValue(key));

        Assert.NotSame(caller, held);

        Assert.Equal(Key, held);

        key.Dispose();

        Assert.Equal(MemoryErasureDigestGrammar.KeyBytes, held.Length);

        Assert.All(held, static value => Assert.Equal(0, value));

        Assert.Equal(Key, caller);

        // A second disposal is harmless, and the key id, which does not reveal the key, stays readable.
        key.Dispose();

        Assert.All(held, static value => Assert.Equal(0, value));

        Assert.Equal("D5046E85FE5A45250A18B7BE9B743D3D", Convert.ToHexString(key.KeyId));
    }

    [Fact]
    public void Table_codes_are_append_only()
    {
        string[] expected =
        [
            "saga_memories",
            "saga_memory_embeddings",
            "saga_memory_embeddings_vec",
            "saga_memory_attachment_provenance",
            "saga_retirement_suppressions",
            "lexicon_entries",
            "lexicon_fact_attachment_provenance",
            "lexicon_annal_fact_provenance",
            "annal_claims",
            "annal_versions",
            "annal_heads",
            "annal_dependencies",
            "annal_review_events",
            "annal_review_decision_receipts",
            "artifact_sensitivity",
            "covenant_entries",
            "covenant_versions",
            "covenant_heads",
            "covenant_version_attachment_provenance",
            "covenant_mutation_receipts",
            "covenant_search_outbox",
            "covenant_search_documents",
            "covenant_curation_heads",
            "covenant_curation_versions",
            "covenant_curation_receipts",
            "covenant_key_epochs",
            "covenant_review_events",
            "covenant_review_decision_receipts",
            "long_rest_receipts",
            "long_rest_receipt_inputs",
            "long_rest_suppressions",
        ];

        Assert.Equal(expected, MemoryErasureTableCodes.Tables);

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(index + 1, MemoryErasureTableCodes.For(expected[index]));
        }

        Assert.Equal(16, MemoryErasureTableCodes.For("covenant_entries"));

        Assert.Equal(28, MemoryErasureTableCodes.For("covenant_review_decision_receipts"));

        Assert.Equal(29, MemoryErasureTableCodes.For("long_rest_receipts"));

        Assert.Equal(30, MemoryErasureTableCodes.For("long_rest_receipt_inputs"));

        Assert.Equal(31, MemoryErasureTableCodes.For("long_rest_suppressions"));
    }

    private static byte[] SagaGlobal(string content) =>
        MemoryErasureDigestGrammar.Fingerprint(Key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, content));

    private static MemoryErasureEffectFacts SagaEffect() =>
        new(
            MemoryReviewStore.Saga,
            [Memory.ToLowerInvariant(), "0a1b2c3d-0000-4000-8000-000000000002"],
            [Guid.Parse(Claim), null],
            [new MemoryErasureTableCount("saga_memories", 2), new MemoryErasureTableCount("saga_memory_embeddings", 2)],
            Labels: 0,
            RetirementSuppressions: 1,
            MemoryErasureEffectFlags.Pinned,
            Covenant: null,
            [
                MemoryExternalEvidence.Known,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.Known,
                MemoryExternalEvidence.ReceiptWindow,
                MemoryExternalEvidence.NotRecorded,
            ],
            RetainedCopiesMask: 223);

    private static MemoryErasureEffectFacts CovenantEffect() =>
        new(
            MemoryReviewStore.Covenant,
            [CovenantEntry],
            [Guid.Parse(ConfirmedVersion)],
            [new MemoryErasureTableCount("covenant_versions", 3), new MemoryErasureTableCount("covenant_entries", 1)],
            Labels: 0,
            RetirementSuppressions: 0,
            MemoryErasureEffectFlags.ReclaimsKey | MemoryErasureEffectFlags.Pinned,
            new CovenantErasureEffectFacts(Guid.Parse(DatasetGeneration), 7, 2),
            [
                MemoryExternalEvidence.Known,
                MemoryExternalEvidence.ReceiptWindow,
                MemoryExternalEvidence.NotApplicable,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.NotRecorded,
            ],
            RetainedCopiesMask: 223);

    private static CovenantErasePrepareRequest CampaignCovenantPrepare(string key) =>
        new(
            CovenantScope.Campaign,
            Guid.Parse(Campaign),
            key,
            Guid.Parse(CovenantEntry),
            new CovenantEraseHeadExpectation(Guid.Parse(ConfirmedVersion), 4),
            Proposed: null,
            Guid.Parse(Mutation));

    private static LexiconCurationTarget CampaignLexiconTarget() =>
        new(
            new LexiconCurationScope(LexiconScopeKind.Campaign, Guid.Parse(Campaign)),
            "ALICE",
            Guid.Parse(LexiconEntry),
            CurationGeneration: 3,
            new string('a', 64),
            new LexiconEntryLifecycle(null, null),
            new LexiconCurationAnnalHead(
                true,
                "claim-alice",
                AnnalVersion.ToLowerInvariant(),
                1,
                AnnalOperation.Assert,
                AnnalContentHashFormat.LexiconStructuredSnapshot,
                new string('b', 64)),
            new LexiconCurationSensitivityLabel(
                true,
                Guid.Parse(LabelId),
                2UL,
                new string('c', 64),
                GenerationProvenance.Create([Guid.Parse(DatasetGeneration)])));

    private static LexiconCurationTarget GlobalLexiconTargetWithoutHeadOrLabel() =>
        new(
            new LexiconCurationScope(LexiconScopeKind.Global, null),
            "ALICE",
            Guid.Parse(LexiconEntry),
            CurationGeneration: 1,
            new string('a', 64),
            new LexiconEntryLifecycle(null, null),
            new LexiconCurationAnnalHead(false, null, null, null, null, null, null),
            new LexiconCurationSensitivityLabel(false, null, null, null, null));

    private static void AssertVector(string pinned, byte[] reference, byte[] actual)
    {
        Assert.Equal(pinned, Convert.ToHexString(reference));

        Assert.Equal(reference, actual);
    }

    private static byte[] Reference(params byte[][] parts)
    {
        byte[] preimage = [.. parts.SelectMany(static part => part)];

        return HMACSHA256.HashData(Key, preimage);
    }

    private static byte[] Label(string ascii) =>
        [.. Encoding.ASCII.GetBytes(ascii), 0x00];

    private static byte[] Hex(string hex) =>
        Convert.FromHexString(hex);

    private static byte[] Ascii(string text) =>
        Encoding.ASCII.GetBytes(text);
}

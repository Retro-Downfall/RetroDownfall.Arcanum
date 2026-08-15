using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class ArtifactSensitivityLabelTests
{
    private static readonly Guid ArtifactId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static readonly Guid SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly Guid TurnId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Plan_and_admission_evidence_with_revision_zero_match_the_literal_label()
    {
        ArtifactSensitivityLabel label = CreateLabel(
            producingPlanDigest: Digest(0x22),
            producingAdmissionDigest: Digest(0x33),
            producingMaintenanceReceiptDigest: null);

        Assert.Equal(0UL, label.ArtifactRevision);
        Assert.Equal("94C220D3859BDD7A97CBDE355F232417C2E0EF886C17ED1582A215B37A5FD9A7", label.SensitivityDigest.ToString());
        Assert.Equal("C62020814F899729AB084C07FE4B6729179695816176BD33320BE2C7179A5C16", label.LabelDigest.ToString());
        Assert.Equal(Digest(0x22), label.ProducingPlanDigest);
        Assert.Equal(Digest(0x33), label.ProducingAdmissionDigest);
        Assert.Null(label.ProducingMaintenanceReceiptDigest);
        Assert.Equal(label.LabelDigest, CovenantDigests.ArtifactLabel(label.ToDigestInput()));
    }

    [Fact]
    public void Maintenance_only_and_all_absent_reserved_for_later_persistence_validation_match_literals()
    {
        ArtifactSensitivityLabel maintenance = CreateLabel(
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: Digest(0x44));
        ArtifactSensitivityLabel allAbsent = CreateLabel(
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null);

        Assert.Equal("B8077FE4FE1D55B72A80D0D469FE8EF6153CD828DC1DE1B7A9B226E87411609C", maintenance.LabelDigest.ToString());
        Assert.Equal("E7168951E61E95D4F57CDEDB791F1C3EF0030AEAA4EBE0B5388536DF08610E25", allAbsent.LabelDigest.ToString());
    }

    [Fact]
    public void Label_identity_and_timestamp_are_excluded_from_the_artifact_label_digest()
    {
        ArtifactSensitivityLabel first = CreateLabel(
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null);
        ArtifactSensitivityLabel second = new(
            Guid.Parse("ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb"),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            Digest(0x11),
            ContentSensitivity.CovenantDerived,
            ExactProvenance(),
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            CreatedAt.AddDays(1));

        Assert.NotEqual(first.LabelId, second.LabelId);
        Assert.NotEqual(first.CreatedAt, second.CreatedAt);
        Assert.Equal(first.LabelDigest, second.LabelDigest);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Mutable_factory_inputs_cannot_change_stored_sensitivity_or_label_digests()
    {
        Guid[] generationIds = [Generation(2), Generation(1)];
        byte[] contentBytes = Enumerable.Repeat((byte)0x11, CovenantLimits.DigestBytes).ToArray();
        GenerationProvenance provenance = GenerationProvenance.Create(generationIds);
        ArtifactSensitivityLabel label = new(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            new CovenantDigest(contentBytes),
            ContentSensitivity.CovenantDerived,
            provenance,
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            CreatedAt);
        CovenantDigest sensitivityDigest = label.SensitivityDigest;
        CovenantDigest labelDigest = label.LabelDigest;

        generationIds[0] = Guid.Empty;
        contentBytes[0] = 0xFF;

        Assert.Equal(sensitivityDigest, label.SensitivityDigest);
        Assert.Equal(labelDigest, label.LabelDigest);
        Assert.Equal((byte)0x11, label.ArtifactContentDigest.Bytes[0]);
        Assert.Equal(Generation(1), label.Provenance.ExactGenerationIds[0]);
    }

    [Fact]
    public void Artifact_owner_content_sensitivity_and_evidence_fields_are_all_digest_bound()
    {
        ArtifactSensitivityLabel baseline = CreateLabel(null, null, null);
        CovenantDigest baselineDigest = baseline.LabelDigest;

        Assert.NotEqual(baselineDigest, CreateLabel(null, null, null, artifactKind: SensitiveArtifactKind.Summary).LabelDigest);
        Assert.NotEqual(baselineDigest, CreateLabel(null, null, null, artifactId: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeef")).LabelDigest);
        Assert.NotEqual(baselineDigest, CreateLabel(null, null, null, includeSession: false).LabelDigest);
        ArtifactSensitivityLabel firstCampaign = CreateLabel(null, null, null, campaignId: Guid.Parse("12345678-1111-2222-3333-444444444444"));
        ArtifactSensitivityLabel secondCampaign = CreateLabel(null, null, null, campaignId: Guid.Parse("12345678-1111-2222-3333-555555555555"));

        Assert.NotEqual(baselineDigest, firstCampaign.LabelDigest);
        Assert.NotEqual(firstCampaign.LabelDigest, secondCampaign.LabelDigest);
        Assert.NotEqual(baselineDigest, CreateLabel(null, null, null, includeTurn: false).LabelDigest);
        Assert.NotEqual(baselineDigest, CreateLabel(null, null, null, artifactRevision: 1).LabelDigest);
        Assert.NotEqual(baselineDigest, CreateLabel(null, null, null, artifactContentDigest: Digest(0x12)).LabelDigest);
        Assert.NotEqual(baselineDigest, CreateLabel(Digest(0x22), Digest(0x33), null).LabelDigest);
        Assert.NotEqual(baselineDigest, CreateLabel(null, null, Digest(0x44)).LabelDigest);

        ArtifactSensitivityLabel none = CreateLabel(
            null,
            null,
            null,
            sensitivity: ContentSensitivity.None,
            provenance: GenerationProvenance.Create([]));

        Assert.NotEqual(baselineDigest, none.LabelDigest);
    }

    [Fact]
    public void Production_evidence_arms_are_closed_and_mutually_exclusive()
    {
        Assert.Throws<ArgumentException>(() => CreateLabel(Digest(0x22), null, null));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, Digest(0x33), null));
        Assert.Throws<ArgumentException>(() => CreateLabel(Digest(0x22), Digest(0x33), Digest(0x44)));
    }

    [Fact]
    public void Invalid_defaults_enums_identifiers_digests_and_sensitivity_shapes_fail_closed()
    {
        CovenantDigest? invalidOptionalDigest = default(CovenantDigest);

        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, labelId: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateLabel(null, null, null, artifactKind: (SensitiveArtifactKind)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateLabel(null, null, null, artifactKind: (SensitiveArtifactKind)byte.MaxValue));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, artifactId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, sessionId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, campaignId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, turnId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ArtifactSensitivityLabel(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            artifactContentDigest: default,
            ContentSensitivity.CovenantDerived,
            ExactProvenance(),
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateLabel(null, null, null, sensitivity: (ContentSensitivity)2));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, sensitivity: ContentSensitivity.CovenantDerived, provenance: GenerationProvenance.Create([])));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, sensitivity: ContentSensitivity.None, provenance: ExactProvenance()));
        Assert.Throws<ArgumentException>(() => CreateLabel(invalidOptionalDigest, Digest(0x33), null));
        Assert.Throws<ArgumentException>(() => CreateLabel(Digest(0x22), invalidOptionalDigest, null));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, invalidOptionalDigest));
        Assert.Throws<ArgumentException>(() => CreateLabel(null, null, null, createdAt: new DateTimeOffset()));
        Assert.Throws<ArgumentNullException>(() => new ArtifactSensitivityLabel(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            Digest(0x11),
            ContentSensitivity.CovenantDerived,
            provenance: null!,
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            CreatedAt));
    }

    [Fact]
    public void Legacy_nested_and_final_digests_are_verified_independently()
    {
        GenerationProvenance provenance = ExactProvenance();
        ArtifactSensitivityLabel canonical = CreateLabel(null, null, null);
        CovenantDigest wrongSensitivity = Digest(0x55);
        CovenantDigest forgedWrongNestedLabel = CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            CampaignId: null,
            TurnId,
            ArtifactRevision: 0,
            ArtifactContentDigest: Digest(0x11),
            SensitivityDigest: wrongSensitivity,
            ProducingPlanDigest: null,
            ProducingAdmissionDigest: null,
            ProducingMaintenanceReceiptDigest: null));

        Assert.Throws<ArgumentException>(() => new ArtifactSensitivityLabel(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            Digest(0x11),
            ContentSensitivity.CovenantDerived,
            provenance,
            sensitivityDigest: canonical.SensitivityDigest,
            producingEvidenceDigest: null,
            labelDigest: Digest(0x66),
            CreatedAt));

        Assert.Throws<ArgumentException>(() => new ArtifactSensitivityLabel(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            Digest(0x11),
            ContentSensitivity.CovenantDerived,
            provenance,
            sensitivityDigest: wrongSensitivity,
            producingEvidenceDigest: null,
            labelDigest: forgedWrongNestedLabel,
            CreatedAt));

        ArtifactSensitivityLabel verified = new(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            Digest(0x11),
            ContentSensitivity.CovenantDerived,
            provenance,
            sensitivityDigest: canonical.SensitivityDigest,
            producingEvidenceDigest: null,
            labelDigest: canonical.LabelDigest,
            CreatedAt);

        Assert.Equal(canonical.SensitivityDigest, verified.SensitivityDigest);
        Assert.Equal(canonical.LabelDigest, verified.LabelDigest);
    }

    [Fact]
    public void Legacy_ambiguous_present_producing_evidence_is_refused()
    {
        ArtifactSensitivityLabel maintenance = CreateLabel(null, null, Digest(0x44));

        Assert.Throws<ArgumentException>(() => new ArtifactSensitivityLabel(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            Digest(0x11),
            ContentSensitivity.CovenantDerived,
            ExactProvenance(),
            sensitivityDigest: maintenance.SensitivityDigest,
            producingEvidenceDigest: Digest(0x44),
            labelDigest: maintenance.LabelDigest,
            CreatedAt));
    }

    [Fact]
    public void Persisted_pair_maintenance_and_absent_arms_require_matching_nested_and_final_digests()
    {
        CovenantDigest expectedSensitivity = DigestHex("94C220D3859BDD7A97CBDE355F232417C2E0EF886C17ED1582A215B37A5FD9A7");
        CovenantDigest wrongSensitivity = Digest(0x55);
        (CovenantDigest? Plan, CovenantDigest? Admission, CovenantDigest? Maintenance, string ExpectedLabel)[] cases =
        [
            (Digest(0x22), Digest(0x33), null, "C62020814F899729AB084C07FE4B6729179695816176BD33320BE2C7179A5C16"),
            (null, null, Digest(0x44), "B8077FE4FE1D55B72A80D0D469FE8EF6153CD828DC1DE1B7A9B226E87411609C"),
            (null, null, null, "E7168951E61E95D4F57CDEDB791F1C3EF0030AEAA4EBE0B5388536DF08610E25")
        ];

        foreach ((CovenantDigest? plan, CovenantDigest? admission, CovenantDigest? maintenance, string expectedLabelHex) in cases)
        {
            CovenantDigest expectedLabel = DigestHex(expectedLabelHex);
            ArtifactSensitivityLabel verified = Rehydrate(
                plan,
                admission,
                maintenance,
                expectedSensitivity,
                expectedLabel);
            CovenantDigest forgedWrongNestedLabel = CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(
                SensitiveArtifactKind.AssistantEntry,
                ArtifactId,
                SessionId,
                CampaignId: null,
                TurnId,
                ArtifactRevision: 0,
                ArtifactContentDigest: Digest(0x11),
                SensitivityDigest: wrongSensitivity,
                ProducingPlanDigest: plan,
                ProducingAdmissionDigest: admission,
                ProducingMaintenanceReceiptDigest: maintenance));

            Assert.Equal(expectedSensitivity, verified.SensitivityDigest);
            Assert.Equal(expectedLabel, verified.LabelDigest);
            Assert.Throws<ArgumentException>(() => Rehydrate(plan, admission, maintenance, expectedSensitivity, Digest(0x66)));
            Assert.Throws<ArgumentException>(() => Rehydrate(plan, admission, maintenance, wrongSensitivity, forgedWrongNestedLabel));
        }
    }

    [Fact]
    public void Persisted_rehydration_rejects_invalid_digest_and_evidence_shapes()
    {
        CovenantDigest expectedSensitivity = DigestHex("94C220D3859BDD7A97CBDE355F232417C2E0EF886C17ED1582A215B37A5FD9A7");
        CovenantDigest expectedAbsentLabel = DigestHex("E7168951E61E95D4F57CDEDB791F1C3EF0030AEAA4EBE0B5388536DF08610E25");

        Assert.Throws<ArgumentException>(() => Rehydrate(null, null, null, default, expectedAbsentLabel));
        Assert.Throws<ArgumentException>(() => Rehydrate(null, null, null, expectedSensitivity, default));
        Assert.Throws<ArgumentException>(() => Rehydrate(Digest(0x22), null, null, expectedSensitivity, Digest(0x66)));
        Assert.Throws<ArgumentException>(() => Rehydrate(null, Digest(0x33), null, expectedSensitivity, Digest(0x66)));
        Assert.Throws<ArgumentException>(() => Rehydrate(Digest(0x22), Digest(0x33), Digest(0x44), expectedSensitivity, Digest(0x66)));
    }

    private static ArtifactSensitivityLabel CreateLabel(
        CovenantDigest? producingPlanDigest,
        CovenantDigest? producingAdmissionDigest,
        CovenantDigest? producingMaintenanceReceiptDigest,
        Guid? labelId = null,
        SensitiveArtifactKind artifactKind = SensitiveArtifactKind.AssistantEntry,
        Guid? artifactId = null,
        Guid? sessionId = default,
        Guid? campaignId = null,
        Guid? turnId = default,
        bool includeSession = true,
        bool includeTurn = true,
        ulong artifactRevision = 0,
        CovenantDigest? artifactContentDigest = null,
        ContentSensitivity sensitivity = ContentSensitivity.CovenantDerived,
        GenerationProvenance? provenance = null,
        DateTimeOffset? createdAt = null) =>
        new(
            labelId ?? Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            artifactKind,
            artifactId ?? ArtifactId,
            includeSession ? sessionId ?? SessionId : null,
            campaignId,
            includeTurn ? turnId ?? TurnId : null,
            artifactRevision,
            artifactContentDigest ?? Digest(0x11),
            sensitivity,
            provenance ?? ExactProvenance(),
            producingPlanDigest,
            producingAdmissionDigest,
            producingMaintenanceReceiptDigest,
            createdAt ?? CreatedAt);

    private static GenerationProvenance ExactProvenance() =>
        GenerationProvenance.Create([Generation(2), Generation(1)]);

    private static ArtifactSensitivityLabel Rehydrate(
        CovenantDigest? producingPlanDigest,
        CovenantDigest? producingAdmissionDigest,
        CovenantDigest? producingMaintenanceReceiptDigest,
        CovenantDigest sensitivityDigest,
        CovenantDigest labelDigest) =>
        new(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            SensitiveArtifactKind.AssistantEntry,
            ArtifactId,
            SessionId,
            campaignId: null,
            TurnId,
            artifactRevision: 0,
            Digest(0x11),
            ContentSensitivity.CovenantDerived,
            ExactProvenance(),
            sensitivityDigest,
            producingPlanDigest,
            producingAdmissionDigest,
            producingMaintenanceReceiptDigest,
            labelDigest,
            CreatedAt);

    private static Guid Generation(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:x12}");

    private static CovenantDigest Digest(byte value) =>
        new(Enumerable.Repeat(value, CovenantLimits.DigestBytes).ToArray());

    private static CovenantDigest DigestHex(string hexadecimalBytes) =>
        new(Convert.FromHexString(hexadecimalBytes));
}

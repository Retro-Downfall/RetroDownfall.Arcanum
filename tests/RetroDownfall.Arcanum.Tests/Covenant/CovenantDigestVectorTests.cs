using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantDigestVectorTests
{
    private static readonly Guid G1 = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private static readonly Guid G2 = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");

    private static readonly Guid G3 = Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f");

    private static readonly Guid G4 = Guid.Parse("30415263-7485-96a7-b8c9-daebfc0d1e2f");

    private static readonly Guid G5 = Guid.Parse("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");

    [Fact]
    public void Section_vectors_cover_every_placement_and_empty_form()
    {
        SectionItemDigestInput globalZ = new(new CovenantKey("z.key"), G2, G3, 2, D(1));
        SectionItemDigestInput globalA = new(new CovenantKey("a.key"), G1, G2, 1, D(2));

        AssertDigest(
            "66826E632897C00071F6307A2344E02795ACEF895324C61C14F603D2BDE46940",
            CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [globalZ, globalA], [.. "global\n"u8])));
        AssertDigest(
            "F2228AE87408BCE6E41AB2683DA3429F74222679E71A0D1A1FCFEA12FCB89D20",
            CovenantDigests.Section(new SectionDigestInput(
                CovenantPlacement.CampaignConfirmed,
                [new SectionItemDigestInput(new CovenantKey("campaign.key"), G3, G4, 3, D(3))],
                [.. "campaign\n"u8])));
        AssertDigest(
            "A5225539B9694DDBC16972C4AEA9CC1D5F8A911FED7092912B92FCB737CCA426",
            CovenantDigests.Section(new SectionDigestInput(
                CovenantPlacement.CampaignProposed,
                [new SectionItemDigestInput(new CovenantKey("proposed.key"), G4, G5, 4, D(4))],
                [.. "proposed\n"u8])));
        AssertDigest(
            "96F4B26A9D980CE073E9EFFEE2DFEAD4ED0C819194029390474C43C30BE35CDE",
            CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignProposed, [], [])));
    }

    [Fact]
    public void Mutation_protocol_vectors_are_exact()
    {
        CovenantDigest request = CovenantDigests.MutationRequest(CreateMutationRequest());
        CovenantDigest preflight = CovenantDigests.PreflightBody(new PreflightBodyDigestInput(
            request,
            8,
            G5,
            9,
            10,
            11,
            12,
            D(7),
            D(8),
            D(9),
            1700000000,
            1700000300));
        CovenantDigest authorization = CovenantDigests.Authorization(new AuthorizationDigestInput(
            request,
            G5,
            8,
            10,
            11,
            12,
            preflight,
            D(10),
            CovenantAuthorizationMode.WardInteractive));

        AssertDigest("C11161C8989E00B9AB50791CF5B053249A5860144A4937E5E1A1B55A44EB0666", request);
        AssertDigest("0490723DC6B89CB04670479F8BF4DC02C7594A74E81AAB288B82AC52B9F60BAD", preflight);
        AssertDigest("E038746CFDB35EE391359FBF6664B909F144322BB87ED2F11C92D8460135D025", authorization);
        AssertDigest(
            "46928AD92AD94825F380F2F79D92521824023D9DE54E8DC2E36AC6F2163EADE7",
            CovenantDigests.Mutation(new MutationDigestInput(request, authorization)));
    }

    [Fact]
    public void Snapshot_plan_and_admission_vectors_are_exact()
    {
        CovenantDigest snapshot = CovenantDigests.Snapshot(CreateSnapshot());
        CovenantDigest plan = CovenantDigests.Plan(CreatePlan(snapshot));
        CovenantDigest admission = CovenantDigests.Admission(CreateAdmission(plan));

        AssertDigest("95622F1A4999CC3C674FD2C83BD01E2F1FCCDD5EAE6FA4B07EE2A89DB6435F3B", snapshot);
        AssertDigest("64007A7EBA7FB1EA3CACF9E20912E14F8D5CEB8C16B6104D5DD2BEA8165E96A3", plan);
        AssertDigest("A960D423408E3EC3A84A99FA1F59789275C5B2B9E2D22DB035518B52C21EC062", admission);
    }

    [Fact]
    public void Materialization_and_sensitivity_vectors_are_exact()
    {
        AssertDigest(
            "32D0013559988A9F6E215056E7A869AC39432EA89BDB54214995078D85441906",
            CovenantDigests.Materialization(CreateMaterialization()));
        AssertDigest(
            "831363D5AF8477EA864480EBD14AFFEFFDCB85B733AE46834A190A3556080FC2",
            CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G2, G1], default)));
        AssertDigest(
            "C8FC1E6573DF59D41ABF9A55DC5ADE04821C5300A1D1A3D2A84283A3F5541F1C",
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                ContentSensitivity.CovenantDerived,
                GenerationProvenanceMode.BloomOverflow,
                default,
                [.. Enumerable.Range(0, CovenantLimits.GenerationBloomBytes).Select(static value => (byte)value)])));
    }

    [Fact]
    public void Sensitivity_bloom_requires_at_least_one_generation_contribution()
    {
        Assert.Throws<ArgumentException>(() => CovenantDigests.Sensitivity(new SensitivityDigestInput(
            ContentSensitivity.CovenantDerived,
            GenerationProvenanceMode.BloomOverflow,
            [],
            new byte[CovenantLimits.GenerationBloomBytes].ToImmutableArray())));
    }

    [Fact]
    public void Artifact_and_session_vectors_pin_optional_blocks_and_signed_generations()
    {
        CovenantDigest sensitivity = Sensitivity();
        CovenantDigest plan = Plan();
        CovenantDigest request = SessionRequest();

        AssertDigest(
            "3737B33BB8F0FCEE1CCA466970C6C6E3B1DF3A92EA08A05A3E831251B5F7EC57",
            CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(
                SensitiveArtifactKind.AssistantEntry,
                G1,
                G2,
                G3,
                G4,
                5,
                D(1),
                sensitivity,
                plan,
                D(2),
                null)));
        AssertDigest("35312267CE6DE1F0F216C4DBB25C27FF0757D23F47D742F14FE99DB3CA767EE1", request);
        AssertDigest(
            "780764C2F508CAA94BF176851A1734EE1027207C952756F70A124D6B21FABDD5",
            CovenantDigests.SessionTurnExecution(CreateSessionExecution(request, [G4, G2, G3])));
    }

    [Fact]
    public void Provider_options_and_all_provider_part_unions_have_exact_vectors()
    {
        AssertDigest(
            "05CDCA7064FCA739706B713490903A2CF426749EB57C36A1E80BD556C0447B79",
            CovenantDigests.ProviderOptions(CreateProviderOptions()));
        AssertDigest(
            "8D36857D7B3C640231C704ACC108B6E6F6D51A923820BF35F58BFDD17F277C9D",
            CovenantDigests.ProviderCall(CreateProviderCall()));
    }

    [Fact]
    public void Ward_and_effect_vectors_cover_every_effect_domain()
    {
        CovenantDigest sensitivity = Sensitivity();
        CovenantDigest admission = Admission();
        CovenantDigest call = ProviderCall();
        CovenantDigest ward = CovenantDigests.WardEvidence(new WardEvidenceDigestInput(
            D(1),
            D(2),
            CovenantToolRiskIdentity.CovenantSensitiveEgress,
            sensitivity,
            CovenantEgressDestination.Network,
            D(4),
            17,
            CovenantWardDecision.Approved));
        CovenantDigest providerEffect = CovenantDigests.ProviderDispatchEffect(new ProviderDispatchEffectDigestInput(G1, 1, admission, call, D(4)));

        AssertDigest("BFE84F8507F27A091F7594238F0127BDD3D474E05AE2C68CA628DC78449322B9", ward);
        AssertDigest("EE83C5F98F9B23371E4BB7EDF39034385B3FAB0705A524CC1BD4E5FBE13C6317", providerEffect);
        AssertDigest(
            "A67C5E86AF7A6DF6261DF3EA3C361F5442082AA7609D7095AD07EB8AB1A0BF86",
            CovenantDigests.MaintenanceDispatchEffect(new MaintenanceDispatchEffectDigestInput(G2, CovenantMaintenanceStep.Saga, 2, call, D(5))));
        AssertDigest(
            "4093CA88B9264BBF90511BB995331D8979C568ED99DDD023028894422E290AC4",
            CovenantDigests.ToolEgressEffect(new ToolEgressEffectDigestInput(G1, 3, admission, D(2), "call-1", D(3), D(4), CovenantEgressDestination.Network, D(5))));
        AssertDigest(
            "799FBB3C0DF9F030F73B7BCFDA98D0AAC6B104E127A3F451577C0C2858E260C7",
            CovenantDigests.ManagedFileEffect(new ManagedFileEffectDigestInput(G1, 4, "call-2", D(6), D(7))));
        AssertDigest(
            "0F991F9C65E7E8259AADA96720FD6AF310948BFC60E85B8E5DBDAFDEBB594999",
            CovenantDigests.BackupDisclosureEffect(new BackupDisclosureEffectDigestInput(G1, 5, G2, D(8), BackupDisclosurePhase.EncryptedArchiveWrite)));
        AssertDigest(
            "0D51D2319E134129E27A26CAECA3589F30A149242CCAD4666AF6DC70E96DAAAB",
            CovenantDigests.ExternalDisclosure(new ExternalDisclosureDigestInput(
                G1,
                CovenantDisclosureSubjectKind.Turn,
                G2,
                providerEffect,
                11,
                CovenantEgressDestination.Provider,
                CovenantDisclosureRevocability.Nonrevocable,
                D(2),
                sensitivity,
                ward,
                admission,
                null,
                1700000000)));
    }

    [Fact]
    public void State_apply_receipt_aggregate_and_cursor_vectors_are_exact()
    {
        CovenantDigest finalReceipt = CovenantDigests.FinalReceipt(CreateFinalReceipt());

        AssertDigest(
            "F69C65DE3A27794B3B04A1143174D96E41CCF610DDF437D9F05666CFEA0C4E2A",
            CovenantDigests.ExternalDisclosureState(new ExternalDisclosureStateDigestInput(
                CovenantEgressDestination.Network,
                CovenantDisclosureRevocability.Nonrevocable,
                CovenantDisclosureCountKind.Exact,
                true,
                7,
                1700000001,
                [.. Enumerable.Range(0, CovenantLimits.DisclosureEvidenceBloomBytes).Select(static value => (byte)value)])));
        AssertDigest(
            "32122B1D38DC46DFBD8E4CAEAEFC5552F2FCF929285AB2CC244D36685F878D2F",
            CovenantDigests.CampaignPathApplyRequest(new CampaignPathApplyRequestDigestInput(G1, G2, CampaignPathIdentityOperation.RepairMoved, D(1))));
        AssertDigest(
            "A69B93ED8F934FEADEC07A3A273DD0333BA4746C575899DADD0E32B92EEA2BCD",
            CovenantDigests.SessionBindingApplyRequest(new SessionBindingApplyRequestDigestInput(G1, G2, SessionCampaignBindingKind.Campaign, G3, D(2), D(3))));
        AssertDigest(
            "02A6A048569BBDD4D641288F496062D32B011B53C827D10EAB24301C59F9AD22",
            CovenantDigests.FamilyReinitializeApplyRequest(new FamilyReinitializeApplyRequestDigestInput(G1, D(1), D(2), D(3))));
        AssertDigest("2BEDD9C9BE05EFB2F14D668559A63BF96B65C92843DC336AF762395BF34C9E54", finalReceipt);
        AssertDigest(
            "96BC9B64B84BCDFD86552DC9B019F51BA1654CECD1B045FE98E4954AED6E49B8",
            CovenantDigests.TurnAggregate(new TurnAggregateDigestInput(finalReceipt, 12, D(2), G1, D(3), Sensitivity(), 13, D(5), 14, 15, 3, CovenantFinalOutcome.Completed)));
        AssertDigest(
            "7CB570A957038AF8C657D9213E1E833D49F0E31B0270D1042633DCD0B2E0C3FF",
            CovenantDigests.CursorFilter(new CursorFilterDigestInput(CovenantCursorEndpoint.FtsQuery, CovenantCursorScopeSelection.AllScopes, G1, G2, CovenantLane.Confirmed, CovenantLifecycle.Any, D(6), 50, CovenantCursorSort.FtsRank)));
    }

    [Fact]
    public void Minimal_and_absent_optional_shapes_have_independent_literal_vectors()
    {
        CovenantDigest emptyGlobal = CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [], []));
        CovenantDigest emptyCampaign = CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignConfirmed, [], []));
        CovenantDigest emptyProposed = CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignProposed, [], []));
        MutationRequestDigestInput mutationRequest = new(CovenantMutationKind.OperatorSet, G1, CovenantScope.Global, null, new CovenantKey("a"), CovenantLane.Confirmed, CovenantOperation.Set, 0, false, CovenantOrigin.Operator, null, null, 0, null, null, []);
        CovenantDigest request = CovenantDigests.MutationRequest(mutationRequest);
        SessionTurnRequestDigestInput turnRequest = new(SessionTurnSurface.Intelligence, CovenantProviderDispatchMode.Buffered, G1, null, CovenantContextPolicy.Default, null, D(1), null, null);
        CovenantDigest turnRequestDigest = CovenantDigests.SessionTurnRequest(turnRequest);
        ProviderOptionsDigestInput options = MinimalProviderOptions();
        CovenantDigest optionsDigest = CovenantDigests.ProviderOptions(options);

        AssertDigest("0B34E24C9783A11367626BA3DF96565B8825D8CBEC84ACEA55E4DB86EAB44456", emptyGlobal);
        AssertDigest("FECF20E8F3E930EF14884DC2E92C52A791DF2C61900D5009E45B7B70835A0065", emptyCampaign);
        AssertDigest("96F4B26A9D980CE073E9EFFEE2DFEAD4ED0C819194029390474C43C30BE35CDE", emptyProposed);
        AssertDigest("A1A25676634D894652316B20A1B86ED734191EE976EB4054EBF0F7C8BA374D79", request);
        AssertDigest("819E01A1072D0A86CDCF784EC96E7B82859AA206067BCAEC3F2E651D8BC62A72", CovenantDigests.PreflightBody(new PreflightBodyDigestInput(request, 0, G2, 0, 0, 0, null, null, D(1), D(2), -1, 0)));
        AssertDigest("DCBAAA783EEFE32D83EC0A663C6184148EC78B6834B098DAF0C7F4D53B7A0EBB", CovenantDigests.Authorization(new AuthorizationDigestInput(request, G2, null, null, null, null, null, null, CovenantAuthorizationMode.None)));
        AssertDigest("FCB2B04534037F7F5C7851065AD9F781D8157E9042476C3FFB348A3AF66B5084", CovenantDigests.Snapshot(new SnapshotDigestInput(G3, null, 0, [new SnapshotCandidateDigestInput(1, G1, G2, CovenantScope.Global, null, CovenantLane.Confirmed, CovenantOperation.Set, CovenantOrigin.Operator, 0, null, 0, 0, D(1), D(2), 0, D(13), 0)])));
        AssertDigest("415B650CEA66CBE222C006DEB8C2405EF60936B231D057FE9087C74632957BB2", CovenantDigests.Plan(new PlanDigestInput(D(1), 0, 0, [], emptyGlobal, emptyCampaign, emptyProposed)));
        AssertDigest("B9DFBAFDEB9623312C7797CE54786D899912F49456FA3D95A28C29D6A910E1B1", CovenantDigests.Materialization(new MaterializationDigestInput(false, [])));
        AssertDigest("B3F8D89762D01D2922572C3171580BA4D78E30F0A32BDCB7540F06611EA731A1", CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, null, null, null, 0, D(1), D(2), null, null, null)));
        AssertDigest("AECABBCD0BE6AD5D7C9EC0C17CADB954F79BC2E3A46C07ADB4029D9BC49B3EF3", CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, null, null, null, 0, D(1), D(2), null, null, D(3))));
        AssertDigest("A19091F258B6A9FE307937E48C171BFBB03750B45F02C887816C54B87C39F5BD", turnRequestDigest);
        AssertDigest("C6D2465160C884C533EB592DC4C0338038E44006AE25B5DD94F1B147C7BF1BEA", CovenantDigests.SessionTurnExecution(new SessionTurnExecutionDigestInput(turnRequestDigest, G2, SessionCampaignBindingKind.GlobalOnly, null, null, 1, null, 1, "p", "m", D(1), [], CovenantToolPolicyCode.AllTools, false, false, InvocationAttendance.Attended)));
        AssertDigest("DF7E45D2C26A4F1F3554C3A221E9BF9911971B6D9DFDE6A6F24C9276D49F708B", CovenantDigests.SessionTurnExecution(new SessionTurnExecutionDigestInput(turnRequestDigest, G2, SessionCampaignBindingKind.Campaign, new SessionTurnExecutionCampaignContextDigestInput(G3, 1), null, 1, null, 1, "p", "m", D(1), [], CovenantToolPolicyCode.AllTools, false, false, InvocationAttendance.Attended)));
        AssertDigest("9AB75EEA77391D3A9CF058DB6C6FB5E87BEBCB79A6C44C3F3DB2666E4D43D72A", optionsDigest);
        AssertDigest("E86DC406B4F790041F00AF16E86869D404DE2BC388BD35CCBE6A490424D630B2", CovenantDigests.ProviderCall(new ProviderCallDigestInput("p", "m", CovenantProviderDispatchMode.Buffered, "t", 1, 0, D(1), optionsDigest, [], [], D(2), [], [], null)));
        AssertDigest("B52931D8D12223456BA67F8CC0C1CF0B8C77FD3B4667C83E0F14B35BF6F074CB", CovenantDigests.Admission(new AdmissionDigestInput(D(1), 1, G1, 1, null, D(2), D(3), D(4), 0, [], emptyGlobal, emptyCampaign, emptyProposed)));
        AssertDigest("12FFD6A827274435DCCA44F79058F21E6E83FE1DA1B72E33B06835B2413C91CA", CovenantDigests.ExternalDisclosure(new ExternalDisclosureDigestInput(G1, CovenantDisclosureSubjectKind.Turn, G2, D(1), 1, CovenantEgressDestination.Provider, CovenantDisclosureRevocability.LocallyRevocable, D(2), D(3), null, null, null, -1)));
        AssertDigest("C1B273FAF506AA962540A947615192E5CD30C9BE28DA6F87E82B1FD4E9F35F7F", CovenantDigests.SessionBindingApplyRequest(new SessionBindingApplyRequestDigestInput(G1, G2, SessionCampaignBindingKind.GlobalOnly, null, D(1), D(2))));
        AssertDigest("2D10C6247193E04711B5533D0E8312CEDC6D043532B5F44571918F6999E2866F", CovenantDigests.CursorFilter(new CursorFilterDigestInput(CovenantCursorEndpoint.List, CovenantCursorScopeSelection.Global, null, null, null, CovenantLifecycle.Any, null, 1, CovenantCursorSort.CanonicalHeads)));
    }

    [Fact]
    public void Provider_option_union_and_tri_state_branches_have_literal_vectors()
    {
        AssertDigest("9AB75EEA77391D3A9CF058DB6C6FB5E87BEBCB79A6C44C3F3DB2666E4D43D72A", CovenantDigests.ProviderOptions(MinimalProviderOptions()));
        AssertDigest("6173F934E71AFFE6C232628399E327A47E1EC9CBC789069EF036E52A060BF993", CovenantDigests.ProviderOptions(MinimalProviderOptions() with { ToolChoice = ProviderToolChoice.None }));
        AssertDigest("A12D6E7B7BAA3AC66FBB839796C818A852F68DCA4118BF4DCDB5D4C09CA0265E", CovenantDigests.ProviderOptions(MinimalProviderOptions() with { ToolChoice = ProviderToolChoice.Required }));
        AssertDigest("BA72BB90C2EADB7B6884C532BFF1B45627B9B41E2913CC5F214481BEB7B24B13", CovenantDigests.ProviderOptions(MinimalProviderOptions() with { ParallelToolCalls = CovenantTriStateBoolean.False }));
        AssertDigest("57E733736C1CB3E0283034E8B468B38F1FAFDC679048AA6B735C6DFD4289F4F3", CovenantDigests.ProviderOptions(MinimalProviderOptions() with { ParallelToolCalls = CovenantTriStateBoolean.True }));
        AssertDigest("DD6E6661B2953F16D40BD592BA913443683114E98A6F07236E194340EC6420EF", CovenantDigests.ProviderOptions(MinimalProviderOptions() with { ResponseFormat = ProviderResponseFormat.JsonObject }));
        AssertDigest("3890A4B108C3C16F1A8A0984F284F1F69DE769873E9CAD6C216B6D80E2922A49", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.Absent)));
        AssertDigest("D7C19DAA9E03C2A0F4DC8190E5B9D396F426286FE2D34DDD627FE25226D100E7", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.False)));
        AssertDigest("DDA9AE4235B73A29E66073E815E275BCE170BB7FBF30E6FC0FEDF6F1E017D1C0", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.True)));
        AssertDigest("2DE124CC2383205724EC04CCCE6F4EB704F0AB250DC7027FF8895D26E75D4FDB", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.Absent) with { JsonSchemaDescription = "d" }));
        AssertDigest("37CBC1FB5E3E331C14E798C0510A063188BFE46BCFDCCA7D121EA5F8DCC00561", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.False) with { JsonSchemaDescription = "d" }));
        AssertDigest("185C3537F26FE66213D0473047B4750FD86394600395C00E3ACCCDC26A971FB6", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.True) with { JsonSchemaDescription = "d" }));
        AssertDigest("113A01A01AE38E84D7AF878EFDFAB13CC952C7FD6B23DCFB431213A5D523119B", CovenantDigests.ProviderOptions(MinimalProviderOptions() with { ToolChoice = ProviderToolChoice.Named, NamedTool = "t" }));
    }

    [Fact]
    public void Provider_option_closed_unions_reject_each_forbidden_singleton_field()
    {
        ProviderOptionsDigestInput minimal = MinimalProviderOptions();

        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.Text, JsonSchemaName = "s" });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.Text, CanonicalJsonSchemaDigest = D(3) });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.Text, JsonSchemaDescription = "d" });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.Text, JsonSchemaStrict = CovenantTriStateBoolean.False });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.Text, JsonSchemaStrict = CovenantTriStateBoolean.True });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaName = "s" });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, CanonicalJsonSchemaDigest = D(3) });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaDescription = "d" });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaStrict = CovenantTriStateBoolean.False });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaStrict = CovenantTriStateBoolean.True });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.JsonSchema, JsonSchemaName = "s" });
        AssertRejected(minimal with { ResponseFormat = ProviderResponseFormat.JsonSchema, CanonicalJsonSchemaDigest = D(3) });

        static void AssertRejected(ProviderOptionsDigestInput input)
        {
            Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(input));
            Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(input));
        }
    }

    [Fact]
    public void Provider_tool_choice_closed_union_accepts_only_its_valid_shapes()
    {
        ProviderOptionsDigestInput minimal = MinimalProviderOptions();

        Assert.Equal(ProviderToolChoice.Auto, FrozenProviderOptions.Create(minimal).ToolChoice);
        Assert.Equal(ProviderToolChoice.None, FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.None }).ToolChoice);
        Assert.Equal(ProviderToolChoice.Required, FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.Required }).ToolChoice);
        Assert.Equal("t", FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.Named, NamedTool = "t" }).NamedTool);
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.Named }));
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.Named, NamedTool = "" }));
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.Auto, NamedTool = "t" }));
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.None, NamedTool = "t" }));
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(minimal with { ToolChoice = ProviderToolChoice.Required, NamedTool = "t" }));
    }

    [Fact]
    public void Frozen_provider_response_format_union_accepts_each_valid_shape()
    {
        ProviderOptionsDigestInput minimal = MinimalProviderOptions();
        byte[] schema = "{\"a\":1}"u8.ToArray();
        CovenantDigest schemaDigest = H("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");

        Assert.Equal(ProviderResponseFormat.Text, FrozenProviderOptions.Create(minimal).ResponseFormat);
        Assert.Equal(ProviderResponseFormat.JsonObject, FrozenProviderOptions.Create(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject }).ResponseFormat);

        foreach (CovenantTriStateBoolean strict in new[]
        {
            CovenantTriStateBoolean.Absent,
            CovenantTriStateBoolean.False,
            CovenantTriStateBoolean.True
        })
        {
            FrozenProviderOptions frozen = FrozenProviderOptions.Create(
                minimal with
                {
                    ResponseFormat = ProviderResponseFormat.JsonSchema,
                    JsonSchemaName = "s",
                    JsonSchemaDescription = "d",
                    CanonicalJsonSchemaDigest = schemaDigest,
                    JsonSchemaStrict = strict
                },
                schema);

            Assert.Equal(ProviderResponseFormat.JsonSchema, frozen.ResponseFormat);
            Assert.Equal(strict, frozen.JsonSchemaStrict);
            Assert.True(frozen.HasCanonicalJsonSchema);
        }
    }

    [Fact]
    public void Only_explicitly_unordered_inputs_are_canonically_sorted()
    {
        SectionItemDigestInput a = new(new CovenantKey("a.key"), G1, G2, 1, D(2));
        SectionItemDigestInput z = new(new CovenantKey("z.key"), G2, G3, 2, D(1));
        SectionDigestInput sectionForward = new(CovenantPlacement.GlobalConfirmed, [a, z], [.. "global\n"u8]);
        SectionDigestInput sectionReverse = new(CovenantPlacement.GlobalConfirmed, [z, a], [.. "global\n"u8]);
        SessionTurnExecutionDigestInput attachmentsForward = CreateSessionExecution(SessionRequest(), [G2, G3, G4]);
        SessionTurnExecutionDigestInput attachmentsReverse = CreateSessionExecution(SessionRequest(), [G4, G2, G3]);
        ProviderOptionsDigestInput biasesForward = CreateProviderOptions([new FrozenLogitBias(-7, 0.5), new FrozenLogitBias(8, -0.25)]);
        ProviderOptionsDigestInput biasesReverse = CreateProviderOptions([new FrozenLogitBias(8, -0.25), new FrozenLogitBias(-7, 0.5)]);

        Assert.Equal(CovenantDigests.Section(sectionForward), CovenantDigests.Section(sectionReverse));
        Assert.Equal(CovenantDigests.Materialization(CreateMaterialization(false)), CovenantDigests.Materialization(CreateMaterialization(true)));
        Assert.Equal(CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1, G2], default)), CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G2, G1], default)));
        Assert.Equal(CovenantDigests.SessionTurnExecution(attachmentsForward), CovenantDigests.SessionTurnExecution(attachmentsReverse));
        Assert.Equal(CovenantDigests.ProviderOptions(biasesForward), CovenantDigests.ProviderOptions(biasesReverse));
    }

    [Fact]
    public void Raw_guid_and_signed_numeric_comparators_have_discriminating_literals()
    {
        Guid lower = Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff");
        Guid upper = Guid.Parse("80000000-0000-0000-0000-000000000000");
        SensitivityDigestInput provenance = new(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [upper, lower], default);
        ProviderOptionsDigestInput logitBias = MinimalProviderOptions() with
        {
            LogitBias = [new FrozenLogitBias(0, -0.25), new FrozenLogitBias(-1, 0.5)]
        };

        AssertDigest("BF5F0FD2594DC50A16D89354F52F6856F76CA5CACEB3AE44B288E93CD7C73586", CovenantDigests.Sensitivity(provenance));
        AssertDigest("AB994D837F04D8091583B382BCA67E9B20FC99815E810B8B111D2ECDB6560267", CovenantDigests.ProviderOptions(logitBias));
        Assert.Equal(
            CovenantDigests.Sensitivity(provenance),
            CovenantDigests.Sensitivity(provenance with { ExactGenerationIds = [lower, upper] }));
        Assert.Equal(
            CovenantDigests.ProviderOptions(logitBias),
            CovenantDigests.ProviderOptions(logitBias with { LogitBias = [new FrozenLogitBias(-1, 0.5), new FrozenLogitBias(0, -0.25)] }));
    }

    [Fact]
    public void Every_materialization_source_and_occurrence_comparator_tier_is_literal()
    {
        Guid attachmentLower = Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff");
        Guid attachmentUpper = Guid.Parse("80000000-0000-0000-0000-000000000000");
        Guid groupVersion = Guid.Parse("10000000-0000-0000-0000-000000000000");
        Guid groupKey = Guid.Parse("20000000-0000-0000-0000-000000000000");
        Guid groupRange = Guid.Parse("30000000-0000-0000-0000-000000000000");
        Guid groupStart = Guid.Parse("40000000-0000-0000-0000-000000000000");
        Guid groupEnd = Guid.Parse("50000000-0000-0000-0000-000000000000");
        ImmutableArray<MaterializationOccurrenceDigestInput> occurrences =
        [
            new(CovenantMaterializationContainer.MessagePart, 4, 0, CovenantMaterializationOccurrence.Utf16TextRange, 1, 1),
            new(CovenantMaterializationContainer.MessagePart, 4, 0, CovenantMaterializationOccurrence.Utf16TextRange, 0, 1),
            new(CovenantMaterializationContainer.MessagePart, 3, 0, CovenantMaterializationOccurrence.Utf16TextRange, 0, 1),
            new(CovenantMaterializationContainer.MessagePart, 3, 0, CovenantMaterializationOccurrence.WholeBinaryPart, null, 1),
            new(CovenantMaterializationContainer.MessagePart, 2, 1, CovenantMaterializationOccurrence.Utf16TextRange, 0, 1),
            new(CovenantMaterializationContainer.MessagePart, 2, 0, CovenantMaterializationOccurrence.Utf16TextRange, 10, 1),
            new(CovenantMaterializationContainer.MessagePart, 1, 0, CovenantMaterializationOccurrence.Utf16TextRange, 0, 1),
            new(CovenantMaterializationContainer.MessagePart, 0, 10, CovenantMaterializationOccurrence.Utf16TextRange, 10, 1),
            new(CovenantMaterializationContainer.MessagePart, 0, 0, CovenantMaterializationOccurrence.Utf16TextRange, 0, 1),
            new(CovenantMaterializationContainer.SystemPrompt, null, null, CovenantMaterializationOccurrence.Utf16TextRange, 100, 1)
        ];
        ImmutableArray<MaterializationSourceDigestInput> sources =
        [
            new(attachmentUpper, attachmentLower, "a", D(2), CovenantMaterializationSourceRange.Utf16Range, 1, 1, []),
            new(attachmentLower, attachmentUpper, "z", D(1), CovenantMaterializationSourceRange.ByteRange, 2, 2, []),
            new(groupEnd, G1, "end", D(12), CovenantMaterializationSourceRange.ByteRange, 1, 2, occurrences),
            new(groupEnd, G1, "end", D(11), CovenantMaterializationSourceRange.ByteRange, 1, 1, []),
            new(groupStart, G1, "start", D(10), CovenantMaterializationSourceRange.ByteRange, 2, 2, []),
            new(groupStart, G1, "start", D(9), CovenantMaterializationSourceRange.ByteRange, 1, 100, []),
            new(groupRange, G1, "range", D(8), CovenantMaterializationSourceRange.ByteRange, 1, 1, []),
            new(groupRange, G1, "range", D(7), CovenantMaterializationSourceRange.Utf16Range, 100, 100, []),
            new(groupKey, G1, "\U00010000", D(6), CovenantMaterializationSourceRange.Utf16Range, 1, 1, []),
            new(groupKey, G1, "\uE000", D(5), CovenantMaterializationSourceRange.ByteRange, 2, 2, []),
            new(groupVersion, attachmentUpper, "a", D(4), CovenantMaterializationSourceRange.Utf16Range, 1, 1, []),
            new(groupVersion, attachmentLower, "z", D(3), CovenantMaterializationSourceRange.ByteRange, 2, 2, [])
        ];
        CovenantDigest expected = CovenantDigests.Materialization(new MaterializationDigestInput(false, sources));
        ImmutableArray<MaterializationSourceDigestInput> reversed =
        [..
            sources
                .Reverse()
                .Select(source => source.Occurrences.IsEmpty
                    ? source
                    : source with { Occurrences = [.. source.Occurrences.Reverse()] })
        ];

        AssertDigest("D2407E8D6B5A4EAFC0C47FDA48E4E2FF587BB27B9278C13B435F0276596BF19E", expected);
        Assert.Equal(expected, CovenantDigests.Materialization(new MaterializationDigestInput(false, reversed)));
    }

    [Fact]
    public void Guid_sorting_uses_unsigned_raw_rfc_4122_bytes()
    {
        Guid rawFirst = Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff");
        Guid rawSecond = Guid.Parse("80000000-0000-0000-0000-000000000000");
        SectionDigestInput section = new(
            CovenantPlacement.GlobalConfirmed,
            [
                new SectionItemDigestInput(new CovenantKey("same.key"), rawSecond, G3, 2, D(2)),
                new SectionItemDigestInput(new CovenantKey("same.key"), rawFirst, G2, 1, D(1))
            ],
            [.. "tie\n"u8]);
        SessionTurnExecutionDigestInput execution = CreateSessionExecution(SessionRequest(), [rawSecond, rawFirst]);

        AssertDigest("E8F3F0642AD8211AEE9313185F53011F484A59F366A38A594106A887AFED61A9", CovenantDigests.Section(section));
        AssertDigest("63749C2D23CED235C4A3DC7D167F5DC92C2729579718EFD29FF2FD0A993AF652", CovenantDigests.SessionTurnExecution(execution));
    }

    [Fact]
    public void Provider_visible_and_producer_owned_collections_preserve_supplied_order()
    {
        ProviderOptionsDigestInput stopForward = CreateProviderOptions(stop: ["alpha", "beta"]);
        ProviderOptionsDigestInput stopReverse = CreateProviderOptions(stop: ["beta", "alpha"]);
        ProviderCallDigestInput messageForward = CreateProviderCall(messages: [Message("one"), Message("two")]);
        ProviderCallDigestInput messageReverse = CreateProviderCall(messages: [Message("two"), Message("one")]);
        SnapshotCandidateDigestInput first = CreateSnapshot().Candidates[0];
        SnapshotCandidateDigestInput second = first with { SearchDocumentId = 13, EntryId = G2 };
        SnapshotDigestInput snapshotForward = CreateSnapshot([first, second]);
        SnapshotDigestInput snapshotReverse = CreateSnapshot([second, first]);

        Assert.NotEqual(CovenantDigests.ProviderOptions(stopForward), CovenantDigests.ProviderOptions(stopReverse));
        Assert.NotEqual(CovenantDigests.ProviderCall(messageForward), CovenantDigests.ProviderCall(messageReverse));
        Assert.NotEqual(CovenantDigests.Snapshot(snapshotForward), CovenantDigests.Snapshot(snapshotReverse));
    }

    [Fact]
    public void Every_supplied_order_collection_changes_identity_when_reversed()
    {
        PlanDecisionDigestInput firstDecision = new(G1, G2, CovenantPlanDecision.EligibleConfirmed, null, CovenantPlacement.GlobalConfirmed, D(1), 1);
        PlanDecisionDigestInput secondDecision = new(G2, G3, CovenantPlanDecision.EligibleProposed, null, CovenantPlacement.CampaignProposed, D(2), 2);
        AdmissionCandidateDigestInput firstCandidate = new(G1, G2, CovenantAdmissionDecision.Admitted, 1);
        AdmissionCandidateDigestInput secondCandidate = new(G2, G3, CovenantAdmissionDecision.Pressured, 2);
        ProviderMessageDigestInput partsForward = new(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.Text("one"), ProviderContentPartDigestInput.Text("two")]);
        ProviderMessageDigestInput partsReverse = new(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.Text("two"), ProviderContentPartDigestInput.Text("one")]);
        ProviderCallDigestInput call = CreateProviderCall();

        Assert.NotEqual(CovenantDigests.MutationRequest(CreateMutationRequest()), CovenantDigests.MutationRequest(CreateMutationRequest() with { ProvenanceDigests = [D(6), D(5)] }));
        Assert.NotEqual(CovenantDigests.Plan(CreatePlan(Snapshot()) with { Decisions = [firstDecision, secondDecision] }), CovenantDigests.Plan(CreatePlan(Snapshot()) with { Decisions = [secondDecision, firstDecision] }));
        Assert.NotEqual(CovenantDigests.Admission(CreateAdmission(Plan()) with { EligibleCandidates = [firstCandidate, secondCandidate] }), CovenantDigests.Admission(CreateAdmission(Plan()) with { EligibleCandidates = [secondCandidate, firstCandidate] }));
        Assert.NotEqual(CovenantDigests.ProviderCall(call with { Messages = [partsForward] }), CovenantDigests.ProviderCall(call with { Messages = [partsReverse] }));
        Assert.NotEqual(CovenantDigests.ProviderCall(call), CovenantDigests.ProviderCall(call with { ToolDefinitions = [call.ToolDefinitions[1], call.ToolDefinitions[0]] }));
    }

    [Fact]
    public void Snapshot_provenance_count_and_aggregate_digest_are_independent()
    {
        SnapshotCandidateDigestInput candidate = new(
            1,
            G2,
            G3,
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            0,
            null,
            0,
            0,
            D(3),
            D(4),
            2,
            D(1),
            0);
        SnapshotDigestInput snapshot = new(G1, null, 0, [candidate]);

        AssertDigest("91DD26C7E5D570DD09CEDC1533BFEA2A0A21D83A7F51B862846CBC2168B703CE", CovenantDigests.Snapshot(snapshot));
        AssertDigest("C76D01C3C914B2A154361FC58123E43B8E3227F00464A0263FCF18486608EDB9", CovenantDigests.Snapshot(snapshot with { Candidates = [candidate with { ProvenanceCount = 1 }] }));
        AssertDigest("D0056C2C6B9E22FB069B980A53F1DD7BD51D4A1AB6BA55CD059BC1CE4D47C4A4", CovenantDigests.Snapshot(snapshot with { Candidates = [candidate with { ProvenanceDigest = D(2) }] }));
        AssertDigest("4AF3FEA640D4D173671DEB535BCF9335C0FE8D9B734EB43598F88609334E3F1F", CovenantDigests.Snapshot(snapshot with { Candidates = [candidate with { ProvenanceCount = 0 }] }));
        AssertDigest("08BCFBE01BD4314D15EA7973AB73E00DE17542675FFAE8FA7BAB5606DC9CCFCB", CovenantDigests.Snapshot(snapshot with { Candidates = [candidate with { ProvenanceCount = CovenantLimits.MaxVersionSources }] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.Snapshot(snapshot with { Candidates = [candidate with { ProvenanceCount = CovenantLimits.MaxVersionSources + 1 }] }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Snapshot(snapshot with { Candidates = [candidate with { ProvenanceDigest = default }] }));
    }

    [Fact]
    public void Materialization_comparator_tiers_are_independently_permutation_invariant()
    {
        MaterializationOccurrenceDigestInput occurrenceA = new(CovenantMaterializationContainer.MessagePart, 1, 2, CovenantMaterializationOccurrence.Utf16TextRange, 3, 4);
        MaterializationOccurrenceDigestInput occurrenceB = new(CovenantMaterializationContainer.MessagePart, 1, 2, CovenantMaterializationOccurrence.WholeBinaryPart, null, 4);
        MaterializationSourceDigestInput sourceA = new(G1, G2, "a", D(1), CovenantMaterializationSourceRange.Utf16Range, 1, 2, [occurrenceB, occurrenceA]);
        MaterializationSourceDigestInput sourceB = new(G1, G3, "a", D(2), CovenantMaterializationSourceRange.Utf16Range, 1, 2, []);
        MaterializationSourceDigestInput sourceC = new(G2, G1, "b", D(3), CovenantMaterializationSourceRange.WholeSource, null, null, []);

        CovenantDigest forward = CovenantDigests.Materialization(new MaterializationDigestInput(false, [sourceA, sourceB, sourceC]));
        CovenantDigest reverse = CovenantDigests.Materialization(new MaterializationDigestInput(false, [sourceC, sourceB, sourceA with { Occurrences = [occurrenceA, occurrenceB] }]));

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void Provider_option_unions_and_finite_numbers_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ToolChoice = ProviderToolChoice.Named, NamedTool = null }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ToolChoice = ProviderToolChoice.Named, NamedTool = "" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ToolChoice = ProviderToolChoice.Auto, NamedTool = "tool.one" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ToolChoice = ProviderToolChoice.None, NamedTool = "tool.one" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ToolChoice = ProviderToolChoice.Required, NamedTool = "tool.one" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ResponseFormat = ProviderResponseFormat.Text, JsonSchemaName = "schema.one" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaStrict = CovenantTriStateBoolean.True }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ResponseFormat = ProviderResponseFormat.JsonSchema, CanonicalJsonSchemaDigest = null }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { ResponseFormat = ProviderResponseFormat.JsonSchema, JsonSchemaName = null }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions() with { Temperature = double.NaN }));
    }

    [Fact]
    public void Route_campaign_path_and_artifact_evidence_unions_fail_closed()
    {
        SessionTurnExecutionDigestInput campaignExecution = CreateSessionExecution(SessionRequest(), []);

        Assert.Throws<ArgumentException>(() => CovenantDigests.SessionTurnRequest(CreateSessionRequest(SessionTurnSurface.Intelligence, SessionTurnRouteValue.ForPrompt(G3))));
        Assert.Throws<ArgumentException>(() => CovenantDigests.SessionTurnRequest(CreateSessionRequest(SessionTurnSurface.SpellExecute, SessionTurnRouteValue.ForPrompt(G3))));
        Assert.Throws<ArgumentException>(() => CovenantDigests.SessionTurnExecution(CreateSessionExecution(SessionRequest(), []) with { BindingKind = SessionCampaignBindingKind.GlobalOnly }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.SessionTurnExecution(CreateSessionExecution(SessionRequest(), []) with { Path = null, CampaignContext = null, BindingKind = SessionCampaignBindingKind.Campaign }));
        Assert.True(CovenantDigests.SessionTurnExecution(campaignExecution with { Path = null }).IsValid);
        Assert.Throws<ArgumentException>(() => CovenantDigests.SessionTurnExecution(campaignExecution with { BindingKind = SessionCampaignBindingKind.GlobalOnly, CampaignContext = null }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, null, null, null, 1, D(1), Sensitivity(), Plan(), null, null)));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, null, null, null, 1, D(1), Sensitivity(), null, D(1), null)));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, null, null, null, 1, D(1), Sensitivity(), Plan(), D(1), D(2))));
    }

    [Fact]
    public void Positive_signed_generations_and_required_text_identities_fail_closed()
    {
        SessionTurnExecutionDigestInput execution = CreateSessionExecution(SessionRequest(), []);
        ProviderCallDigestInput call = CreateProviderCall();

        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(execution with { CampaignContext = execution.CampaignContext! with { AvailabilityGeneration = 0 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(execution with { CampaignContext = execution.CampaignContext! with { AvailabilityGeneration = -1 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(execution with { Path = execution.Path! with { PathRevision = 0 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(execution with { Path = execution.Path! with { PathRevision = -1 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(execution with { ProviderConfigurationGeneration = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(execution with { ProviderConfigurationGeneration = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(execution with { PreRequestHistoryWatermark = -1 }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.SessionTurnExecution(execution with { ProviderIdentity = "" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.SessionTurnExecution(execution with { ModelIdentity = "" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderCall(call with { TokenizerProfile = "" }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderCall(call with { Messages = [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.ToolCall("", "tool", "{}"u8)])] }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderCall(call with { Messages = [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.ToolCall("call", "", "{}"u8)])] }));
    }

    [Fact]
    public void Provider_call_span_validation_counts_utf16_without_materializing_the_prompt()
    {
        byte[] prompt = Enumerable.Repeat((byte)'a', 1_000_000).ToArray();
        ProviderCallDigestInput input = CreateProviderCall() with
        {
            SystemPromptBytes = ImmutableArray.Create(prompt),
            PromptSpans = []
        };

        _ = CovenantDigests.ProviderCall(input with { SystemPromptBytes = [] });

        long before = GC.GetAllocatedBytesForCurrentThread();

        _ = CovenantDigests.ProviderCall(input);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < prompt.Length / 4, $"Provider-call validation allocated {allocated} bytes for a {prompt.Length}-byte prompt.");
    }

    [Fact]
    public void Text_reasoning_absent_and_present_empty_protected_data_are_distinct_literals()
    {
        ProviderContentPartDigestInput absent = ProviderContentPartDigestInput.TextReasoning("r");
        ProviderContentPartDigestInput presentEmpty = ProviderContentPartDigestInput.TextReasoning("r", ReadOnlySpan<byte>.Empty);

        Assert.True(absent.ProtectedData.IsDefault);
        Assert.False(presentEmpty.ProtectedData.IsDefault);
        Assert.Empty(presentEmpty.ProtectedData);
        AssertDigest("4E0023B3885CE0CDC86B28B802AD473332B1EAE6A46831598AFA6CA9D776BCBA", CovenantDigests.ProviderCall(CreateReasoningCall(absent)));
        AssertDigest("C9E134CB540BB2C8A9ADC48A219DF4F15B6AFBFF12A7C0A030BB849318C7DB51", CovenantDigests.ProviderCall(CreateReasoningCall(presentEmpty)));

        ProviderContentPartEnvelope frozenAbsent = ProviderContentPartEnvelope.TextReasoning("r");
        ProviderContentPartEnvelope frozenPresentEmpty = ProviderContentPartEnvelope.TextReasoning("r", ReadOnlySpan<byte>.Empty);

        Assert.True(frozenAbsent.ProtectedData.IsDefault);
        Assert.False(frozenPresentEmpty.ProtectedData.IsDefault);
        Assert.True(frozenAbsent.ToDigestInput().ProtectedData.IsDefault);
        Assert.False(frozenPresentEmpty.ToDigestInput().ProtectedData.IsDefault);

        static ProviderCallDigestInput CreateReasoningCall(ProviderContentPartDigestInput part) =>
            new(
                "p",
                "m",
                CovenantProviderDispatchMode.Buffered,
                "t",
                1,
                0,
                D(1),
                D(2),
                [],
                [],
                D(3),
                [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [part])],
                [],
                null);
    }

    [Fact]
    public void Tool_call_arguments_and_json_parts_require_exact_canonical_json()
    {
        byte[][] rejected =
        [
            [],
            [(byte)'"', 0xc3, 0x28, (byte)'"'],
            "{\"a\":1,\"a\":2}"u8.ToArray(),
            "{ \"a\" : 1 }"u8.ToArray(),
            "{\"b\":1,\"a\":2}"u8.ToArray(),
            "{\"a\":1.0}"u8.ToArray(),
            "{\"a\":\"\\u0061\"}"u8.ToArray()
        ];

        foreach (byte[] value in rejected)
        {
            Assert.Throws<ArgumentException>(() => ProviderContentPartDigestInput.ToolCall("call", "tool", value));
            Assert.Throws<ArgumentException>(() => ProviderContentPartDigestInput.Json(value));
            Assert.Throws<ArgumentException>(() => ProviderContentPartEnvelope.ToolCall("call", "tool", value));
            Assert.Throws<ArgumentException>(() => ProviderContentPartEnvelope.Json(value));
        }

        byte[] canonical = "{\"a\":\"a\",\"b\":1}"u8.ToArray();
        ProviderContentPartDigestInput toolCall = ProviderContentPartDigestInput.ToolCall("call", "tool", canonical);
        ProviderContentPartDigestInput json = ProviderContentPartDigestInput.Json(canonical);
        ProviderContentPartEnvelope frozenToolCall = ProviderContentPartEnvelope.ToolCall("call", "tool", canonical);
        ProviderContentPartEnvelope frozenJson = ProviderContentPartEnvelope.Json(canonical);

        Assert.True(toolCall.Bytes.AsSpan().SequenceEqual(canonical));
        Assert.True(json.Bytes.AsSpan().SequenceEqual(canonical));
        Assert.True(frozenToolCall.Bytes.AsSpan().SequenceEqual(canonical));
        Assert.True(frozenJson.Bytes.AsSpan().SequenceEqual(canonical));
    }

    [Fact]
    public void Frozen_content_parts_reuse_one_owned_payload_copy_through_hashing()
    {
        byte[] payload = new byte[64 * 1024];
        byte[] protectedData = [1, 2, 3];

        _ = ProviderContentPartEnvelope.Binary("application/octet-stream", null, null, [1]).ToDigestInput();

        long beforeConstruction = GC.GetAllocatedBytesForCurrentThread();

        ProviderContentPartEnvelope binary = ProviderContentPartEnvelope.Binary("application/octet-stream", null, null, payload);

        long constructionAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeConstruction;

        payload[0] = 0xff;

        Assert.Equal((byte)0, binary.Bytes[0]);
        Assert.True(constructionAllocation < payload.Length + 16_384, $"Frozen content construction allocated {constructionAllocation} bytes for a {payload.Length}-byte payload.");

        long beforeProjection = GC.GetAllocatedBytesForCurrentThread();

        ProviderContentPartDigestInput projected = binary.ToDigestInput();

        long projectionAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeProjection;

        Assert.Same(ImmutableCollectionsMarshal.AsArray(binary.Bytes), ImmutableCollectionsMarshal.AsArray(projected.Bytes));
        Assert.True(projectionAllocation < 4_096, $"Frozen content projection allocated {projectionAllocation} bytes.");

        ProviderContentPartEnvelope reasoning = ProviderContentPartEnvelope.TextReasoning("reason", protectedData);

        protectedData[0] = 0xff;

        Assert.Equal((byte)1, reasoning.ProtectedData[0]);
        Assert.Same(ImmutableCollectionsMarshal.AsArray(reasoning.ProtectedData), ImmutableCollectionsMarshal.AsArray(reasoning.ToDigestInput().ProtectedData));

        GenerationProvenance provenance = GenerationProvenance.CreateExact([G1]);
        ProviderCallSensitivity sensitivity = new(
            ContentSensitivity.CovenantDerived,
            provenance,
            CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, provenance.Mode, provenance.ExactGenerationIds, provenance.BloomBits)));
        FrozenProviderOptions options = FrozenProviderOptions.Create(MinimalProviderOptions());
        ProviderCallMaterializationSnapshot materialization = new(false, []);

        long beforeHashing = GC.GetAllocatedBytesForCurrentThread();

        ProviderCallEnvelope call = new(
            "provider",
            "model",
            CovenantProviderDispatchMode.Buffered,
            "tok",
            4096,
            0,
            sensitivity,
            options,
            [],
            [],
            materialization,
            [new ProviderMessageEnvelope(CovenantProviderRole.User, null, null, [binary])],
            [],
            null);

        long hashingAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeHashing;
        ProviderContentPartDigestInput hashedPart = call.ToDigestInput().Messages[0].ContentParts[0];

        Assert.Same(ImmutableCollectionsMarshal.AsArray(binary.Bytes), ImmutableCollectionsMarshal.AsArray(hashedPart.Bytes));
        Assert.True(hashingAllocation < payload.Length / 2, $"Provider-call hashing allocated {hashingAllocation} bytes for a {payload.Length}-byte payload.");
    }

    [Fact]
    public void Default_and_invalid_scalar_values_fail_before_encoding()
    {
        Assert.Throws<ArgumentException>(() => CovenantDigests.MutationRequest(CreateMutationRequest() with { NormalizedKey = default }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Mutation(new MutationDigestInput(default, D(1))));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [new SectionItemDigestInput(default, G1, G2, 1, D(1))], [1])));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.SessionTurnExecution(CreateSessionExecution(SessionRequest(), []) with { PreRequestHistoryWatermark = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantDigests.WardEvidence(new WardEvidenceDigestInput(D(1), D(2), (CovenantToolRiskIdentity)0, Sensitivity(), CovenantEgressDestination.Network, D(3), 1, CovenantWardDecision.Approved)));
    }

    [Fact]
    public void Default_task_one_scalar_access_fails_closed_and_explicit_zero_counts_remain_valid()
    {
        CovenantKey key = default;
        CovenantGenerationId generation = default;
        CovenantRowCount rows = default;
        CovenantByteCount bytes = default;

        Assert.Throws<InvalidOperationException>(() => key.Value);
        Assert.Throws<InvalidOperationException>(() => generation.Value);
        Assert.Throws<InvalidOperationException>(() => rows.Value);
        Assert.Throws<InvalidOperationException>(() => rows.Maximum);
        Assert.Throws<InvalidOperationException>(() => bytes.Value);
        Assert.Throws<InvalidOperationException>(() => bytes.Maximum);

        CovenantRowCount zeroRows = new(0, 1);
        CovenantByteCount zeroBytes = new(0, 1);

        Assert.Equal(0U, zeroRows.Value);
        Assert.Equal(1U, zeroRows.Maximum);
        Assert.Equal(0U, zeroBytes.Value);
        Assert.Equal(1U, zeroBytes.Maximum);
    }

    [Fact]
    public void Duplicate_canonical_keys_and_coordinates_fail_closed()
    {
        SectionItemDigestInput item = new(new CovenantKey("same.key"), G1, G2, 1, D(1));
        MaterializationDigestInput materialization = CreateMaterialization();
        MaterializationSourceDigestInput source = materialization.Sources[0];

        Assert.Throws<ArgumentException>(() => CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [item, item], [1])));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1, G1], default)));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Materialization(materialization with { Sources = [source, source] }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Materialization(new MaterializationDigestInput(false, [source with { Occurrences = [source.Occurrences[0], source.Occurrences[0]] }])));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderOptions(CreateProviderOptions([new FrozenLogitBias(1, 0.1), new FrozenLogitBias(1, 0.2)])));
    }

    [Fact]
    public void Hard_bounded_digest_inputs_reject_over_capacity_vectors()
    {
        SectionItemDigestInput item = new(new CovenantKey("key"), G1, G2, 1, D(1));
        SnapshotCandidateDigestInput candidate = CreateSnapshot().Candidates[0];
        MaterializationSourceDigestInput source = CreateMaterialization().Sources[0];

        Assert.Throws<ArgumentException>(() => CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, Enumerable.Repeat(item, CovenantLimits.MaxGlobalConfirmedEntries + 1).ToImmutableArray(), [1])));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignProposed, [], new byte[CovenantLimits.MaxCampaignProposedRenderedBytes + 1].ToImmutableArray())));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Snapshot(new SnapshotDigestInput(G1, null, 0, Enumerable.Repeat(candidate, CovenantLimits.MaxActiveSnapshotRows + 1).ToImmutableArray())));
        Assert.Throws<ArgumentException>(() => CovenantDigests.Materialization(new MaterializationDigestInput(false, Enumerable.Repeat(source, CovenantLimits.MaxAttachmentSourcesPerAgentMutation + 1).ToImmutableArray())));
    }

    [Fact]
    public void Prompt_spans_are_in_order_nonoverlapping_and_within_system_text()
    {
        ProviderCallDigestInput call = CreateProviderCall();
        ProviderCallDigestInput literalCall = new(
            "p",
            "m",
            CovenantProviderDispatchMode.Buffered,
            "t",
            1,
            0,
            D(1),
            D(2),
            [.. "abcd"u8],
            [
                new ProviderPromptSpanDigestInput(CovenantPromptAttribution.DataHeader, 0, 1, D(4)),
                new ProviderPromptSpanDigestInput(CovenantPromptAttribution.Instructions, 2, 2, D(5))
            ],
            D(3),
            [],
            [],
            null);

        AssertDigest("847ADC03AC66B944C61522B5876CC325E486491A658DD90798C65B2C2783921C", CovenantDigests.ProviderCall(literalCall));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderCall(literalCall with { PromptSpans = [literalCall.PromptSpans[1], literalCall.PromptSpans[0]] }));

        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderCall(call with
        {
            PromptSpans =
            [
                new ProviderPromptSpanDigestInput(CovenantPromptAttribution.Preamble, 3, 2, D(1)),
                new ProviderPromptSpanDigestInput(CovenantPromptAttribution.ContextBody, 2, 2, D(2))
            ]
        }));
        Assert.Throws<ArgumentException>(() => CovenantDigests.ProviderCall(call with
        {
            PromptSpans = [new ProviderPromptSpanDigestInput(CovenantPromptAttribution.Preamble, 5, 2, D(1))]
        }));
    }

    [Fact]
    public void Immutable_provider_materialization_receipt_and_disclosure_models_copy_mutable_input()
    {
        byte[] systemPrompt = "system"u8.ToArray();
        byte[] binary = [1, 2, 3];
        ProviderCallMaterializationSnapshot materialization = new(false, CreateMaterialization().Sources);
        FrozenProviderOptions options = FrozenProviderOptions.Create(MinimalProviderOptions());
        ProviderCallSensitivity sensitivity = new(
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([G1]),
            CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1], default)));
        ProviderCallEnvelope envelope = new(
            "provider",
            "model",
            CovenantProviderDispatchMode.Buffered,
            "tok",
            4096,
            0,
            sensitivity,
            options,
            systemPrompt,
            [],
            materialization,
            [new ProviderMessageEnvelope(CovenantProviderRole.User, null, null, [ProviderContentPartEnvelope.Binary("application/octet-stream", null, null, binary)])],
            [],
            null);
        CovenantFinalReceipt receipt = new(CreateFinalReceipt());
        CovenantDisclosureDraft draft = new(G1, CovenantDisclosureSubjectKind.Turn, G2, D(1), CovenantEgressDestination.Network, CovenantDisclosureRevocability.Nonrevocable, D(2), Sensitivity(), null, null, null, 1);
        CovenantDisclosureReceipt disclosure = new(draft, 1);

        systemPrompt[0] = 0;
        binary[0] = 0;
        byte[] disclosedBloom = disclosure.EvidenceBloom;

        disclosedBloom[0] ^= 0xff;

        Assert.Equal((byte)'s', envelope.SystemPromptBytes[0]);
        Assert.Equal((byte)1, envelope.Messages[0].ContentParts[0].Bytes[0]);
        Assert.Equal(CovenantDigests.FinalReceipt(CreateFinalReceipt()), receipt.Digest);
        Assert.True(disclosure.Digest.IsValid);
        Assert.NotEqual(disclosedBloom[0], disclosure.EvidenceBloom[0]);
    }

    [Fact]
    public void Frozen_provider_projection_retains_verified_provider_visible_values_without_aliasing()
    {
        byte[] optionsSchema = "{\"x\":1}"u8.ToArray();
        byte[] toolInputSchema = "{\"a\":1}"u8.ToArray();
        byte[] toolOutputSchema = "{\"b\":2}"u8.ToArray();
        byte[] structuredSchema = "{\"type\":\"object\"}"u8.ToArray();
        CovenantDigest optionsSchemaDigest = H("5041BF1F713DF204784353E82F6A4A535931CB64F1F4B4A5AEAFFCB720918B22");
        CovenantDigest descriptionDigest = H("C9046F7A37AD0EA7CEE73355984FA5428982F8B37C8F7BCEC91F7AC71A7CD104");
        CovenantDigest inputSchemaDigest = H("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");
        CovenantDigest outputSchemaDigest = H("0AB1A6D394CD30195F0642B67AE1180C375FFADF5DD7F39C390668B5FDB6DA93");
        CovenantDigest structuredSchemaDigest = H("A2C799262A3CE3C19EF5CDD983BF3D12B43AB3C426227091B909DCB7054738C0");
        FrozenProviderOptions options = FrozenProviderOptions.Create(
            MinimalSchemaOptions(CovenantTriStateBoolean.True) with
            {
                JsonSchemaDescription = "schema description",
                CanonicalJsonSchemaDigest = optionsSchemaDigest
            },
            optionsSchema);
        ProviderToolDefinitionEnvelope tool = new(
            "tool.one",
            "description",
            descriptionDigest,
            toolInputSchema,
            inputSchemaDigest,
            toolOutputSchema,
            outputSchemaDigest,
            CovenantToolRiskIdentity.Ordinary);
        ProviderCallSensitivity sensitivity = new(
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([G1]),
            CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1], default)));
        ProviderCallEnvelope envelope = new(
            "provider",
            "model",
            CovenantProviderDispatchMode.Buffered,
            "tok",
            4096,
            0,
            sensitivity,
            options,
            [],
            [],
            new ProviderCallMaterializationSnapshot(false, []),
            [],
            [tool],
            structuredSchemaDigest,
            structuredSchema);

        optionsSchema[0] = (byte)'!';
        toolInputSchema[0] = (byte)'!';
        toolOutputSchema[0] = (byte)'!';
        structuredSchema[0] = (byte)'!';

        Assert.True(options.HasCanonicalJsonSchema);
        Assert.Equal("{\"x\":1}", Encoding.UTF8.GetString(options.CanonicalJsonSchemaBytes.AsSpan()));
        Assert.Equal("description", tool.Description);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(tool.CanonicalInputSchemaBytes.AsSpan()));
        Assert.True(tool.HasOutputSchema);
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(tool.CanonicalOutputSchemaBytes.AsSpan()));
        Assert.True(envelope.HasStructuredOutputSchema);
        Assert.Equal("{\"type\":\"object\"}", Encoding.UTF8.GetString(envelope.CanonicalStructuredOutputSchemaBytes.AsSpan()));
        Assert.Equal(optionsSchemaDigest, options.ToDigestInput().CanonicalJsonSchemaDigest);
        Assert.Equal("schema description", options.ToDigestInput().JsonSchemaDescription);
        Assert.Equal(options.Digest, CovenantDigests.ProviderOptions(options.ToDigestInput()));
        Assert.Equal(descriptionDigest, tool.ToDigestInput().DescriptionDigest);
        Assert.Equal(inputSchemaDigest, tool.ToDigestInput().InputSchemaDigest);
        Assert.Equal(outputSchemaDigest, tool.ToDigestInput().OutputSchemaDigest);
        Assert.Equal(structuredSchemaDigest, envelope.ToDigestInput().StructuredOutputSchemaDigest);
        Assert.Equal(CovenantDigests.ProviderCall(envelope.ToDigestInput()), envelope.Digest);

        ProviderToolDefinitionEnvelope absentOutput = new(
            "tool.two",
            "description",
            descriptionDigest,
            "{\"a\":1}"u8,
            inputSchemaDigest,
            [],
            null,
            CovenantToolRiskIdentity.Ordinary);
        ProviderCallEnvelope absentStructured = new(
            "provider",
            "model",
            CovenantProviderDispatchMode.Buffered,
            "tok",
            4096,
            0,
            sensitivity,
            FrozenProviderOptions.Create(MinimalProviderOptions()),
            [],
            [],
            new ProviderCallMaterializationSnapshot(false, []),
            [],
            [absentOutput],
            null);

        Assert.False(absentOutput.HasOutputSchema);
        Assert.True(absentOutput.CanonicalOutputSchemaBytes.IsDefault);
        Assert.False(absentStructured.HasStructuredOutputSchema);
        Assert.True(absentStructured.CanonicalStructuredOutputSchemaBytes.IsDefault);
    }

    [Fact]
    public void Frozen_provider_projection_rejects_missing_noncanonical_and_mismatched_visible_values()
    {
        byte[] canonical = "{\"a\":1}"u8.ToArray();
        byte[] noncanonical = "{ \"a\" : 1 }"u8.ToArray();
        CovenantDigest canonicalDigest = H("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");
        CovenantDigest descriptionDigest = H("C9046F7A37AD0EA7CEE73355984FA5428982F8B37C8F7BCEC91F7AC71A7CD104");
        ProviderOptionsDigestInput schemaOptions = MinimalSchemaOptions(CovenantTriStateBoolean.Absent) with { CanonicalJsonSchemaDigest = canonicalDigest };

        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(schemaOptions));
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(MinimalProviderOptions(), canonical));
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(schemaOptions, noncanonical));
        Assert.Throws<ArgumentException>(() => FrozenProviderOptions.Create(schemaOptions with { CanonicalJsonSchemaDigest = D(1) }, canonical));
        Assert.Throws<ArgumentException>(() => new ProviderToolDefinitionEnvelope("tool", "description", D(1), canonical, canonicalDigest, [], null, CovenantToolRiskIdentity.Ordinary));
        Assert.Throws<ArgumentException>(() => new ProviderToolDefinitionEnvelope("tool", "description", descriptionDigest, canonical, D(1), [], null, CovenantToolRiskIdentity.Ordinary));
        Assert.Throws<ArgumentException>(() => new ProviderToolDefinitionEnvelope("tool", "description", descriptionDigest, noncanonical, canonicalDigest, [], null, CovenantToolRiskIdentity.Ordinary));
        Assert.Throws<ArgumentException>(() => new ProviderToolDefinitionEnvelope("tool", "description", descriptionDigest, canonical, canonicalDigest, canonical, null, CovenantToolRiskIdentity.Ordinary));
        Assert.Throws<ArgumentException>(() => new ProviderToolDefinitionEnvelope("tool", "description", descriptionDigest, canonical, canonicalDigest, [], canonicalDigest, CovenantToolRiskIdentity.Ordinary));
        Assert.Throws<ArgumentException>(() => new ProviderToolDefinitionEnvelope("tool", "description", descriptionDigest, canonical, canonicalDigest, canonical, D(1), CovenantToolRiskIdentity.Ordinary));
        Assert.Throws<ArgumentException>(() => new ProviderToolDefinitionEnvelope("tool", "\ud800", D(1), canonical, canonicalDigest, [], null, CovenantToolRiskIdentity.Ordinary));
        Assert.Throws<ArgumentException>(() => CreateEnvelope(null, canonical));
        Assert.Throws<ArgumentException>(() => CreateEnvelope(canonicalDigest, []));
        Assert.Throws<ArgumentException>(() => CreateEnvelope(D(1), canonical));
        Assert.Throws<ArgumentException>(() => CreateEnvelope(canonicalDigest, noncanonical));

        static ProviderCallEnvelope CreateEnvelope(CovenantDigest? structuredDigest, byte[] structuredBytes)
        {
            ProviderCallSensitivity sensitivity = new(
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([G1]),
                CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1], default)));

            return new ProviderCallEnvelope(
                "provider",
                "model",
                CovenantProviderDispatchMode.Buffered,
                "tok",
                4096,
                0,
                sensitivity,
                FrozenProviderOptions.Create(MinimalProviderOptions()),
                [],
                [],
                new ProviderCallMaterializationSnapshot(false, []),
                [],
                [],
                structuredDigest,
                structuredBytes);
        }
    }

    [Fact]
    public void Frozen_provider_models_use_reference_semantics_and_digest_identity()
    {
        FrozenProviderOptions firstOptions = FrozenProviderOptions.Create(MinimalProviderOptions());
        FrozenProviderOptions secondOptions = FrozenProviderOptions.Create(MinimalProviderOptions());
        ProviderContentPartEnvelope firstPart = ProviderContentPartEnvelope.Text("same");
        ProviderContentPartEnvelope secondPart = ProviderContentPartEnvelope.Text("same");
        ProviderMessageEnvelope firstMessage = new(CovenantProviderRole.User, null, null, []);
        ProviderMessageEnvelope secondMessage = new(CovenantProviderRole.User, null, null, []);
        ProviderCallMaterializationSnapshot firstMaterialization = new(false, []);
        ProviderCallMaterializationSnapshot secondMaterialization = new(false, []);
        CovenantDigest descriptionDigest = H("C9046F7A37AD0EA7CEE73355984FA5428982F8B37C8F7BCEC91F7AC71A7CD104");
        CovenantDigest schemaDigest = H("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");
        ProviderToolDefinitionEnvelope firstTool = new("tool", "description", descriptionDigest, "{\"a\":1}"u8, schemaDigest, [], null, CovenantToolRiskIdentity.Ordinary);
        ProviderToolDefinitionEnvelope secondTool = new("tool", "description", descriptionDigest, "{\"a\":1}"u8, schemaDigest, [], null, CovenantToolRiskIdentity.Ordinary);
        GenerationProvenance provenance = GenerationProvenance.CreateExact([G1]);
        CovenantDigest sensitivityDigest = CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1], default));
        ProviderCallSensitivity sensitivity = new(
            ContentSensitivity.CovenantDerived,
            provenance,
            sensitivityDigest);
        ProviderCallSensitivity secondSensitivity = new(
            ContentSensitivity.CovenantDerived,
            provenance,
            sensitivityDigest);
        ProviderCallEnvelope firstCall = CreateCall(firstOptions, firstMaterialization, sensitivity);
        ProviderCallEnvelope secondCall = CreateCall(firstOptions, firstMaterialization, sensitivity);

        Assert.NotEqual(firstOptions, secondOptions);
        Assert.NotEqual(firstPart, secondPart);
        Assert.NotEqual(firstMessage, secondMessage);
        Assert.NotEqual(firstMaterialization, secondMaterialization);
        Assert.NotEqual(firstTool, secondTool);
        Assert.NotEqual(sensitivity, secondSensitivity);
        Assert.NotEqual(firstCall, secondCall);
        Assert.Equal(sensitivity.Digest, secondSensitivity.Digest);
        Assert.Equal(firstOptions.Digest, secondOptions.Digest);
        Assert.Equal(firstMaterialization.Digest, secondMaterialization.Digest);
        Assert.Equal(firstCall.Digest, secondCall.Digest);

        static ProviderCallEnvelope CreateCall(
            FrozenProviderOptions options,
            ProviderCallMaterializationSnapshot materialization,
            ProviderCallSensitivity sensitivity) =>
            new(
                "provider",
                "model",
                CovenantProviderDispatchMode.Buffered,
                "tok",
                4096,
                0,
                sensitivity,
                options,
                [],
                [],
                materialization,
                [],
                [],
                null);
    }

    [Fact]
    public void Frozen_provider_and_disclosure_model_boundaries_reject_invalid_defaults()
    {
        Assert.Throws<ArgumentException>(() => ProviderContentPartEnvelope.ToolCall("", "tool", "{}"u8));
        Assert.Throws<ArgumentException>(() => ProviderContentPartEnvelope.ToolCall("call", "", "{}"u8));
        Assert.Throws<ArgumentException>(() => new ProviderMessageEnvelope(CovenantProviderRole.User, "", null, []));
        Assert.Throws<ArgumentException>(() => new CovenantDisclosureDraft(Guid.Empty, CovenantDisclosureSubjectKind.Turn, G2, D(1), CovenantEgressDestination.Network, CovenantDisclosureRevocability.Nonrevocable, D(2), Sensitivity(), null, null, null, 1));
        Assert.Throws<ArgumentException>(() => new CovenantDisclosureDraft(G1, CovenantDisclosureSubjectKind.Turn, G2, default, CovenantEgressDestination.Network, CovenantDisclosureRevocability.Nonrevocable, D(2), Sensitivity(), null, null, null, 1));
    }

    [Fact]
    public void A_full_snapshot_reaches_the_canonical_writer_without_copying_its_digests()
    {
        SnapshotCandidateDigestInput template = CreateSnapshot().Candidates[0];
        SnapshotDigestInput snapshot = CreateSnapshot(
            [.. Enumerable.Repeat(template, CovenantLimits.MaxActiveSnapshotRows)]);

        _ = CovenantDigests.Snapshot(snapshot);

        long before = GC.GetAllocatedBytesForCurrentThread();

        _ = CovenantDigests.Snapshot(snapshot);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Three digest fields per row reach the writer, and a byte[32] costs 56 bytes on 64-bit, so
        // a copying read is ~168 bytes of Gen0 garbage per row on the generation-bound turn path.
        Assert.True(
            allocated < CovenantLimits.MaxActiveSnapshotRows * 100,
            $"Snapshot hashing allocated {allocated} bytes across {CovenantLimits.MaxActiveSnapshotRows} rows.");
    }

    [Fact]
    public void Pairing_two_digests_reads_both_operands_in_place()
    {
        CovenantDigest first = D(1);
        CovenantDigest second = D(2);

        _ = CovenantDigestPair.Combine(first, second);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 1_000; index++)
        {
            _ = CovenantDigestPair.Combine(first, second);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The hash result and the digest it becomes are the only arrays a pairing needs; reading the
        // two operands through the copying accessor doubles that for no caller's benefit.
        Assert.True(allocated < 1_000 * 168, $"Pairing 1,000 digests allocated {allocated} bytes.");
    }

    private static MutationRequestDigestInput CreateMutationRequest() =>
        new(CovenantMutationKind.AgentPropose, G1, CovenantScope.Campaign, G2, new CovenantKey("response.style"), CovenantLane.Proposed, CovenantOperation.Set, 7, true, CovenantOrigin.AgentProposed, D(1), D(2), 1, D(3), D(4), [D(5), D(6)]);

    private static SnapshotDigestInput CreateSnapshot(ImmutableArray<SnapshotCandidateDigestInput>? candidates = null) =>
        new(
            G5,
            G3,
            14,
            candidates ??
            [
                new SnapshotCandidateDigestInput(12, G1, G2, CovenantScope.Campaign, G3, CovenantLane.Confirmed, CovenantOperation.Set, CovenantOrigin.AgentApproved, 13, G4, 1, 1, D(11), D(12), 2, D(13), 99)
            ]);

    private static PlanDigestInput CreatePlan(CovenantDigest snapshot) =>
        new(snapshot, 1, 2, [new PlanDecisionDigestInput(G1, G2, CovenantPlanDecision.EligibleConfirmed, null, CovenantPlacement.CampaignConfirmed, D(15), 101)], SectionGlobal(), SectionCampaign(), SectionProposed());

    private static MaterializationDigestInput CreateMaterialization(bool reverse = false)
    {
        MaterializationOccurrenceDigestInput first = new(CovenantMaterializationContainer.SystemPrompt, null, null, CovenantMaterializationOccurrence.Utf16TextRange, 5, 6);
        MaterializationOccurrenceDigestInput second = new(CovenantMaterializationContainer.MessagePart, 3, 4, CovenantMaterializationOccurrence.WholeBinaryPart, null, 9);
        MaterializationSourceDigestInput one = new(G1, G2, "attachment.one", D(19), CovenantMaterializationSourceRange.Utf16Range, 7, 8, reverse ? [second, first] : [first, second]);
        MaterializationSourceDigestInput two = new(G3, G4, "attachment.two", D(20), CovenantMaterializationSourceRange.WholeSource, null, null, []);

        return new MaterializationDigestInput(false, reverse ? [two, one] : [one, two]);
    }

    private static SessionTurnRequestDigestInput CreateSessionRequest(SessionTurnSurface surface = SessionTurnSurface.PromptExecute, SessionTurnRouteValue? route = null) =>
        new(surface, CovenantProviderDispatchMode.Buffered, G1, G2, CovenantContextPolicy.Default, route ?? SessionTurnRouteValue.ForPrompt(G3), D(5), G4, D(6));

    private static SessionTurnExecutionDigestInput CreateSessionExecution(CovenantDigest request, ImmutableArray<Guid> attachments) =>
        new(request, G2, SessionCampaignBindingKind.Campaign, new SessionTurnExecutionCampaignContextDigestInput(G3, 21), new SessionTurnExecutionPathDigestInput(22, D(7)), 23, G4, 24, "provider", "model", D(8), attachments, CovenantToolPolicyCode.AllTools, false, true, InvocationAttendance.Attended);

    private static ProviderOptionsDigestInput CreateProviderOptions(ImmutableArray<FrozenLogitBias>? logitBias = null, ImmutableArray<string>? stop = null) =>
        new(1000, 0.25, 0.75, -0.5, 0.125, -9, "user-1", stop ?? ["stop"], ProviderToolChoice.Named, "tool.one", CovenantTriStateBoolean.True, ProviderResponseFormat.JsonSchema, "schema.one", "schema desc", D(20), CovenantTriStateBoolean.True, CovenantReasoningEffort.High, 2048, CovenantReasoningOutput.Summary, CovenantReasoningWireDialect.OpenRouter, logitBias ?? [new FrozenLogitBias(-7, 0.5)]);

    private static ProviderOptionsDigestInput MinimalProviderOptions() =>
        new(null, null, null, null, null, null, null, [], ProviderToolChoice.Auto, null, CovenantTriStateBoolean.Absent, ProviderResponseFormat.Text, null, null, null, CovenantTriStateBoolean.Absent, null, null, null, CovenantReasoningWireDialect.Standard, default);

    private static ProviderOptionsDigestInput MinimalSchemaOptions(CovenantTriStateBoolean strict) =>
        MinimalProviderOptions() with
        {
            ResponseFormat = ProviderResponseFormat.JsonSchema,
            JsonSchemaName = "s",
            CanonicalJsonSchemaDigest = D(3),
            JsonSchemaStrict = strict
        };

    private static ProviderCallDigestInput CreateProviderCall(ImmutableArray<ProviderMessageDigestInput>? messages = null) =>
        new(
            "provider",
            "model",
            CovenantProviderDispatchMode.Streaming,
            "tok-v1",
            8192,
            3,
            Sensitivity(),
            CovenantDigests.ProviderOptions(CreateProviderOptions()),
            [.. "system"u8],
            [new ProviderPromptSpanDigestInput(CovenantPromptAttribution.CovenantConfirmed, 0, 6, D(21))],
            Materialization(),
            messages ?? [CompleteMessage()],
            [new ProviderToolDefinitionDigestInput("tool.one", D(22), D(23), D(24), CovenantToolRiskIdentity.CovenantSensitiveEgress), new ProviderToolDefinitionDigestInput("tool.two", D(25), D(26), null, CovenantToolRiskIdentity.Ordinary)],
            D(28));

    private static ProviderMessageDigestInput CompleteMessage() =>
        new(
            CovenantProviderRole.User,
            "m1",
            "alice",
            [
                ProviderContentPartDigestInput.Text("hello"),
                ProviderContentPartDigestInput.Binary("image/png", "img", CovenantImageDetail.High, [1, 2]),
                ProviderContentPartDigestInput.ToolCall("call-1", "tool.one", "{\"a\":1}"u8),
                ProviderContentPartDigestInput.ToolResult("call-1", "result"u8),
                ProviderContentPartDigestInput.Json("{\"j\":true}"u8),
                ProviderContentPartDigestInput.Uri("https://example.test/x", "image/jpeg", CovenantImageDetail.Low),
                ProviderContentPartDigestInput.TextReasoning("reason", "protected"u8)
            ]);

    private static ProviderMessageDigestInput Message(string value) =>
        new(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.Text(value)]);

    private static AdmissionDigestInput CreateAdmission(CovenantDigest plan) =>
        new(plan, 15, G3, 2, D(30), ProviderCall(), Materialization(), Sensitivity(), 4096, [new AdmissionCandidateDigestInput(G1, G2, CovenantAdmissionDecision.Admitted, 42)], SectionGlobal(), SectionCampaign(), SectionProposed());

    private static FinalReceiptDigestInput CreateFinalReceipt() =>
        new(Snapshot(), Plan(), 4, D(3), G1, 5, D(4), D(5), Sensitivity(), 7, D(8), 9, 10, 2, CovenantFinalOutcome.Completed);

    private static CovenantDigest Snapshot() => CovenantDigests.Snapshot(CreateSnapshot());

    private static CovenantDigest Plan() => CovenantDigests.Plan(CreatePlan(Snapshot()));

    private static CovenantDigest Materialization() => CovenantDigests.Materialization(CreateMaterialization());

    private static CovenantDigest Sensitivity() => CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1, G2], default));

    private static CovenantDigest SessionRequest() => CovenantDigests.SessionTurnRequest(CreateSessionRequest());

    private static CovenantDigest ProviderCall() => CovenantDigests.ProviderCall(CreateProviderCall());

    private static CovenantDigest Admission() => CovenantDigests.Admission(CreateAdmission(Plan()));

    private static CovenantDigest SectionGlobal() => CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [new SectionItemDigestInput(new CovenantKey("z.key"), G2, G3, 2, D(1)), new SectionItemDigestInput(new CovenantKey("a.key"), G1, G2, 1, D(2))], [.. "global\n"u8]));

    private static CovenantDigest SectionCampaign() => CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignConfirmed, [new SectionItemDigestInput(new CovenantKey("campaign.key"), G3, G4, 3, D(3))], [.. "campaign\n"u8]));

    private static CovenantDigest SectionProposed() => CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignProposed, [new SectionItemDigestInput(new CovenantKey("proposed.key"), G4, G5, 4, D(4))], [.. "proposed\n"u8]));

    private static void AssertDigest(string expected, CovenantDigest actual) =>
        Assert.Equal(expected, actual.ToString());

    private static CovenantDigest D(byte value) =>
        new(Enumerable.Repeat(value, CovenantLimits.DigestBytes).ToArray());

    private static CovenantDigest H(string hexadecimal) =>
        new(Convert.FromHexString(hexadecimal));
}

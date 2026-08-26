using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Covenant;

public readonly record struct CovenantDigestCorpusCategoryCounts(
    int DomainVectors,
    int SectionCases,
    int ProviderCases,
    int OptionalCases,
    int OrderingCases,
    int ChainCases,
    int WriterCases,
    int DisclosureCases,
    int RefusalCases)
{
    public int Total => checked(
        DomainVectors
        + SectionCases
        + ProviderCases
        + OptionalCases
        + OrderingCases
        + ChainCases
        + WriterCases
        + DisclosureCases
        + RefusalCases);
}

public readonly record struct CovenantDigestCorpusResult(
    bool Succeeded,
    string? FirstFailureCaseId,
    int TotalCaseCount,
    CovenantDigestCorpusCategoryCounts CategoryCounts,
    CovenantDigest CaseManifestDigest,
    CovenantDigest ResultAggregateDigest,
    CovenantDigest Aggregate);

public static class CovenantDigestCorpus
{
    private static readonly Guid G1 = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private static readonly Guid G2 = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");

    private static readonly Guid G3 = Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f");

    private static readonly Guid G4 = Guid.Parse("30415263-7485-96a7-b8c9-daebfc0d1e2f");

    private static readonly Guid G5 = Guid.Parse("40516273-8495-a6b7-c8d9-eafb0c1d2e3f");

    public static CovenantDigestCorpusResult Run()
    {
        using CorpusRecorder recorder = new();

        RunDomainVectors(recorder);
        RunSectionCases(recorder);
        RunProviderCases(recorder);
        RunOptionalCases(recorder);
        RunOrderingCases(recorder);
        RunChainCases(recorder);
        RunWriterCases(recorder);
        RunDisclosureCases(recorder);
        RunRefusalCases(recorder);

        return recorder.Complete();
    }

    private static void RunDomainVectors(CorpusRecorder recorder)
    {
        CovenantCompiledContent compiled = new CovenantCompiler().Compile("response.style", "  concise\r\nand\tclear  ");
        CovenantDigest section = SectionGlobal();
        CovenantDigest request = CovenantDigests.MutationRequest(CreateMutationRequest());
        CovenantDigest preflight = CovenantDigests.PreflightBody(new PreflightBodyDigestInput(request, 8, G5, 9, 10, 11, 12, D(7), D(8), D(9), 1700000000, 1700000300));
        CovenantDigest authorization = CovenantDigests.Authorization(new AuthorizationDigestInput(request, G5, 8, 10, 11, 12, preflight, D(10), CovenantAuthorizationMode.WardInteractive));
        CovenantDigest mutation = CovenantDigests.Mutation(new MutationDigestInput(request, authorization));
        CovenantDigest snapshot = CovenantDigests.Snapshot(CreateSnapshot());
        CovenantDigest campaignSection = SectionCampaign();
        CovenantDigest proposedSection = SectionProposed();
        CovenantDigest plan = CovenantDigests.Plan(CreatePlan(snapshot, section, campaignSection, proposedSection));
        CovenantDigest materialization = CovenantDigests.Materialization(CreateMaterialization());
        CovenantDigest sensitivity = CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G2, G1], default));
        CovenantDigest bloomSensitivity = CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.BloomOverflow, default, [.. Enumerable.Range(0, CovenantLimits.GenerationBloomBytes).Select(static value => (byte)value)]));
        CovenantDigest artifact = CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, G2, G3, G4, 5, D(1), sensitivity, plan, D(2), null));
        CovenantDigest turnRequest = CovenantDigests.SessionTurnRequest(new SessionTurnRequestDigestInput(SessionTurnSurface.PromptExecute, CovenantProviderDispatchMode.Buffered, G1, G2, CovenantContextPolicy.Default, SessionTurnRouteValue.ForPrompt(G3), D(5), G4, D(6)));
        CovenantDigest options = CovenantDigests.ProviderOptions(CreateProviderOptions());
        CovenantDigest turnExecution = CovenantDigests.SessionTurnExecution(new SessionTurnExecutionDigestInput(turnRequest, G2, SessionCampaignBindingKind.Campaign, new SessionTurnExecutionCampaignContextDigestInput(G3, 21), new SessionTurnExecutionPathDigestInput(22, D(7)), 23, G4, 24, "provider", "model", D(8), [G4, G2, G3], CovenantToolPolicyCode.AllTools, false, true, InvocationAttendance.Attended));
        CovenantDigest providerCall = CovenantDigests.ProviderCall(CreateProviderCall(sensitivity, options, materialization));
        CovenantDigest admission = CovenantDigests.Admission(new AdmissionDigestInput(plan, 15, G3, 2, D(30), providerCall, materialization, sensitivity, 4096, [new AdmissionCandidateDigestInput(G1, G2, CovenantAdmissionDecision.Admitted, 42)], section, campaignSection, proposedSection));
        CovenantDigest ward = CovenantDigests.WardEvidence(new WardEvidenceDigestInput(D(1), D(2), CovenantToolRiskIdentity.CovenantSensitiveEgress, sensitivity, CovenantEgressDestination.Network, D(4), 17, CovenantWardDecision.Approved));
        CovenantDigest providerEffect = CovenantDigests.ProviderDispatchEffect(new ProviderDispatchEffectDigestInput(G1, 1, admission, providerCall, D(4)));
        CovenantDigest maintenanceEffect = CovenantDigests.MaintenanceDispatchEffect(new MaintenanceDispatchEffectDigestInput(G2, CovenantMaintenanceStep.Saga, 2, providerCall, D(5)));
        CovenantDigest toolEffect = CovenantDigests.ToolEgressEffect(new ToolEgressEffectDigestInput(G1, 3, admission, D(2), "call-1", D(3), D(4), CovenantEgressDestination.Network, D(5)));
        CovenantDigest managedEffect = CovenantDigests.ManagedFileEffect(new ManagedFileEffectDigestInput(G1, 4, "call-2", D(6), D(7)));
        CovenantDigest backupEffect = CovenantDigests.BackupDisclosureEffect(new BackupDisclosureEffectDigestInput(G1, 5, G2, D(8), BackupDisclosurePhase.EncryptedArchiveWrite));
        CovenantDigest disclosure = CovenantDigests.ExternalDisclosure(new ExternalDisclosureDigestInput(G1, CovenantDisclosureSubjectKind.Turn, G2, providerEffect, 11, CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, D(2), sensitivity, ward, admission, null, 1700000000));
        CovenantDigest state = CovenantDigests.ExternalDisclosureState(new ExternalDisclosureStateDigestInput(CovenantEgressDestination.Network, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 7, 1700000001, [.. Enumerable.Range(0, CovenantLimits.DisclosureEvidenceBloomBytes).Select(static value => (byte)value)]));
        CovenantDigest pathApply = CovenantDigests.CampaignPathApplyRequest(new CampaignPathApplyRequestDigestInput(G1, G2, CampaignPathIdentityOperation.RepairMoved, D(1)));
        CovenantDigest bindingApply = CovenantDigests.SessionBindingApplyRequest(new SessionBindingApplyRequestDigestInput(G1, G2, SessionCampaignBindingKind.Campaign, G3, D(2), D(3)));
        CovenantDigest familyApply = CovenantDigests.FamilyReinitializeApplyRequest(new FamilyReinitializeApplyRequestDigestInput(G1, D(1), D(2), D(3)));
        CovenantDigest receipt = CovenantDigests.FinalReceipt(new FinalReceiptDigestInput(snapshot, plan, 4, D(3), G1, 5, D(4), D(5), sensitivity, 7, D(8), 9, 10, 2, CovenantFinalOutcome.Completed));
        CovenantDigest turnAggregate = CovenantDigests.TurnAggregate(new TurnAggregateDigestInput(receipt, 12, D(2), G1, D(3), sensitivity, 13, D(5), 14, 15, 3, CovenantFinalOutcome.Completed));
        CovenantDigest cursor = CovenantDigests.CursorFilter(new CursorFilterDigestInput(CovenantCursorEndpoint.FtsQuery, CovenantCursorScopeSelection.AllScopes, G1, G2, CovenantLane.Confirmed, CovenantLifecycle.Any, D(6), 50, CovenantCursorSort.FtsRank));

        recorder.RecordDigest(CorpusCategory.Domain, "domain.authored", compiled.AuthoredHash, "B5C835F676515711F21CA61CF53A9FDEABD16057BA2938B9E96930B0A984BB26");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.fragment", compiled.FragmentHash, "E645D901DB511E428E00E1EC2E2F90F218B522FDBB1E3AEECF49BDCC43ED47BC");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.section", section, "66826E632897C00071F6307A2344E02795ACEF895324C61C14F603D2BDE46940");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.request", request, "C11161C8989E00B9AB50791CF5B053249A5860144A4937E5E1A1B55A44EB0666");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.preflight-body", preflight, "F35209D3DCCF5F5B96E324D1ED734CB6D67666EFCB605D1A1B1044FE639DD653");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.authorization", authorization, "BDCCAD751EDD947E7D1CDE2B79EC1217B5906B627308E264DAD35D92485C7794");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.mutation", mutation, "5359D52F695C18826A3856F7A4877182599A04F8EC0665EDC2F9254F1A6B5DAF");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.snapshot", snapshot, "95622F1A4999CC3C674FD2C83BD01E2F1FCCDD5EAE6FA4B07EE2A89DB6435F3B");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.plan", plan, "64007A7EBA7FB1EA3CACF9E20912E14F8D5CEB8C16B6104D5DD2BEA8165E96A3");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.materialization", materialization, "32D0013559988A9F6E215056E7A869AC39432EA89BDB54214995078D85441906");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.sensitivity", sensitivity, "831363D5AF8477EA864480EBD14AFFEFFDCB85B733AE46834A190A3556080FC2");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.sensitivity-bloom-overflow", bloomSensitivity, "C8FC1E6573DF59D41ABF9A55DC5ADE04821C5300A1D1A3D2A84283A3F5541F1C");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.artifact-label", artifact, "3737B33BB8F0FCEE1CCA466970C6C6E3B1DF3A92EA08A05A3E831251B5F7EC57");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.session-turn-request", turnRequest, "35312267CE6DE1F0F216C4DBB25C27FF0757D23F47D742F14FE99DB3CA767EE1");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.session-turn-execution", turnExecution, "780764C2F508CAA94BF176851A1734EE1027207C952756F70A124D6B21FABDD5");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.provider-options", options, "05CDCA7064FCA739706B713490903A2CF426749EB57C36A1E80BD556C0447B79");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.provider-call", providerCall, "8D36857D7B3C640231C704ACC108B6E6F6D51A923820BF35F58BFDD17F277C9D");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.admission", admission, "A960D423408E3EC3A84A99FA1F59789275C5B2B9E2D22DB035518B52C21EC062");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.attempt-chain", CovenantEvidenceChains.SeedAttemptChain().Head, "011426617416BDF818E7A0893C9F3883C9D1AB5435846393FB37E61E439BF510");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.branch-chain", CovenantEvidenceChains.SeedBranchChain(G1, null, null).Head, "76FF628A532349AD9A7FF2A63A56A165CDBBD56800A0338AB723181AF4C0E710");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.ward-evidence", ward, "BFE84F8507F27A091F7594238F0127BDD3D474E05AE2C68CA628DC78449322B9");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.provider-effect", providerEffect, "EE83C5F98F9B23371E4BB7EDF39034385B3FAB0705A524CC1BD4E5FBE13C6317");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.maintenance-effect", maintenanceEffect, "A67C5E86AF7A6DF6261DF3EA3C361F5442082AA7609D7095AD07EB8AB1A0BF86");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.tool-egress-effect", toolEffect, "4093CA88B9264BBF90511BB995331D8979C568ED99DDD023028894422E290AC4");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.managed-file-effect", managedEffect, "799FBB3C0DF9F030F73B7BCFDA98D0AAC6B104E127A3F451577C0C2858E260C7");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.backup-effect", backupEffect, "0F991F9C65E7E8259AADA96720FD6AF310948BFC60E85B8E5DBDAFDEBB594999");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.external-disclosure", disclosure, "0D51D2319E134129E27A26CAECA3589F30A149242CCAD4666AF6DC70E96DAAAB");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.disclosure-chain", CovenantEvidenceChains.SeedDisclosureChain().Head, "338E809242611A14B2E00821758B33139EC8E59AFD77E2D6A5378818E1ED0D54");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.external-state", state, "F69C65DE3A27794B3B04A1143174D96E41CCF610DDF437D9F05666CFEA0C4E2A");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.path-apply", pathApply, "32122B1D38DC46DFBD8E4CAEAEFC5552F2FCF929285AB2CC244D36685F878D2F");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.binding-apply", bindingApply, "A69B93ED8F934FEADEC07A3A273DD0333BA4746C575899DADD0E32B92EEA2BCD");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.family-apply", familyApply, "02A6A048569BBDD4D641288F496062D32B011B53C827D10EAB24301C59F9AD22");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.receipt", receipt, "2BEDD9C9BE05EFB2F14D668559A63BF96B65C92843DC336AF762395BF34C9E54");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.turn-aggregate", turnAggregate, "96BC9B64B84BCDFD86552DC9B019F51BA1654CECD1B045FE98E4954AED6E49B8");
        recorder.RecordDigest(CorpusCategory.Domain, "domain.cursor", cursor, "7CB570A957038AF8C657D9213E1E833D49F0E31B0270D1042633DCD0B2E0C3FF");
    }

    private static void RunSectionCases(CorpusRecorder recorder)
    {
        recorder.RecordDigest(CorpusCategory.Section, "section.global-confirmed", SectionGlobal(), "66826E632897C00071F6307A2344E02795ACEF895324C61C14F603D2BDE46940");
        recorder.RecordDigest(CorpusCategory.Section, "section.campaign-confirmed", SectionCampaign(), "F2228AE87408BCE6E41AB2683DA3429F74222679E71A0D1A1FCFEA12FCB89D20");
        recorder.RecordDigest(CorpusCategory.Section, "section.campaign-proposed", SectionProposed(), "A5225539B9694DDBC16972C4AEA9CC1D5F8A911FED7092912B92FCB737CCA426");
        recorder.RecordDigest(CorpusCategory.Section, "section.empty.global-confirmed", CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [], [])), "0B34E24C9783A11367626BA3DF96565B8825D8CBEC84ACEA55E4DB86EAB44456");
        recorder.RecordDigest(CorpusCategory.Section, "section.empty.campaign-confirmed", CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignConfirmed, [], [])), "FECF20E8F3E930EF14884DC2E92C52A791DF2C61900D5009E45B7B70835A0065");
        recorder.RecordDigest(CorpusCategory.Section, "section.empty", CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignProposed, [], [])), "96F4B26A9D980CE073E9EFFEE2DFEAD4ED0C819194029390474C43C30BE35CDE");
    }

    private static void RunProviderCases(CorpusRecorder recorder)
    {
        ProviderOptionsDigestInput minimal = MinimalProviderOptions();

        recorder.RecordDigest(CorpusCategory.Provider, "provider.tool-choice.auto", CovenantDigests.ProviderOptions(minimal), "9AB75EEA77391D3A9CF058DB6C6FB5E87BEBCB79A6C44C3F3DB2666E4D43D72A");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.tool-choice.none", CovenantDigests.ProviderOptions(minimal with { ToolChoice = ProviderToolChoice.None }), "6173F934E71AFFE6C232628399E327A47E1EC9CBC789069EF036E52A060BF993");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.tool-choice.required", CovenantDigests.ProviderOptions(minimal with { ToolChoice = ProviderToolChoice.Required }), "A12D6E7B7BAA3AC66FBB839796C818A852F68DCA4118BF4DCDB5D4C09CA0265E");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.tool-choice.named", CovenantDigests.ProviderOptions(minimal with { ToolChoice = ProviderToolChoice.Named, NamedTool = "t" }), "113A01A01AE38E84D7AF878EFDFAB13CC952C7FD6B23DCFB431213A5D523119B");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.parallel.absent", CovenantDigests.ProviderOptions(minimal), "9AB75EEA77391D3A9CF058DB6C6FB5E87BEBCB79A6C44C3F3DB2666E4D43D72A");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.parallel.false", CovenantDigests.ProviderOptions(minimal with { ParallelToolCalls = CovenantTriStateBoolean.False }), "BA72BB90C2EADB7B6884C532BFF1B45627B9B41E2913CC5F214481BEB7B24B13");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.parallel.true", CovenantDigests.ProviderOptions(minimal with { ParallelToolCalls = CovenantTriStateBoolean.True }), "57E733736C1CB3E0283034E8B468B38F1FAFDC679048AA6B735C6DFD4289F4F3");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.text", CovenantDigests.ProviderOptions(minimal), "9AB75EEA77391D3A9CF058DB6C6FB5E87BEBCB79A6C44C3F3DB2666E4D43D72A");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.json-object", CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject }), "DD6E6661B2953F16D40BD592BA913443683114E98A6F07236E194340EC6420EF");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.schema.strict-absent", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.Absent)), "3890A4B108C3C16F1A8A0984F284F1F69DE769873E9CAD6C216B6D80E2922A49");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.schema.strict-false", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.False)), "D7C19DAA9E03C2A0F4DC8190E5B9D396F426286FE2D34DDD627FE25226D100E7");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.schema.strict-true", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.True)), "DDA9AE4235B73A29E66073E815E275BCE170BB7FBF30E6FC0FEDF6F1E017D1C0");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.schema.description", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.Absent) with { JsonSchemaDescription = "d" }), "2DE124CC2383205724EC04CCCE6F4EB704F0AB250DC7027FF8895D26E75D4FDB");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.schema.description-strict-false", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.False) with { JsonSchemaDescription = "d" }), "37CBC1FB5E3E331C14E798C0510A063188BFE46BCFDCCA7D121EA5F8DCC00561");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.response.schema.description-strict-true", CovenantDigests.ProviderOptions(MinimalSchemaOptions(CovenantTriStateBoolean.True) with { JsonSchemaDescription = "d" }), "185C3537F26FE66213D0473047B4750FD86394600395C00E3ACCCDC26A971FB6");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.text", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.Text("x"))), "35154D3D8D9D59BCFE3D36DA0D8794A9DC51D0296A6ED0C759362DB0A17871DF");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.binary", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.Binary("application/octet-stream", null, null, [1, 2]))), "9087BE7B81D75FA98B252C111636D084D649CAE6FDA9508B0473F2BC25520F67");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.tool-call", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.ToolCall("c", "t", "{}"u8))), "030DED69A999F415AA141D6CA529612048598AFE300CDDF4AC89CF4ED6F8428C");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.tool-call.canonical-json", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.ToolCall("c", "t", "{\"a\":\"é\",\"b\":1}"u8))), "15C3CA0507D3C8500F7279329981004C7DE18C00F56B0597AC88249615CF8000");
        recorder.RecordCheck(CorpusCategory.Provider, "provider.envelope.tool-call.canonical-json", FrozenCanonicalToolCallChecks());
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.tool-result", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.ToolResult("c", "{}"u8))), "FFF25D77F330673A1014B6862BED28B6CD37853426B11BDF80D5BBA10237AD19");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.json", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.Json("{}"u8))), "310779BBF1F10AF7A0D56943F02DE75855C9AD2FAC97DFA9EAC1D4BB849CEF03");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.json.canonical-json", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.Json("{\"a\":\"é\",\"b\":1}"u8))), "347BBA8ECFF0FC4319D5351DE2D30AD2CB8760FC2E9251B44ED50A2DA9701CEE");
        recorder.RecordCheck(CorpusCategory.Provider, "provider.envelope.json.canonical-json", FrozenCanonicalJsonChecks());
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.uri", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.Uri("https://e", null, null))), "715F78C2A1B0822950AC6774ABFE8C5E85F2243B988D7F91DF7866EA3B4A0080");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.reasoning-absent", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.TextReasoning("r"))), "4E0023B3885CE0CDC86B28B802AD473332B1EAE6A46831598AFA6CA9D776BCBA");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.part.reasoning-empty", CovenantDigests.ProviderCall(CreateSinglePartCall(ProviderContentPartDigestInput.TextReasoning("r", ReadOnlySpan<byte>.Empty))), "C9E134CB540BB2C8A9ADC48A219DF4F15B6AFBFF12A7C0A030BB849318C7DB51");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.tool-output.absent", CovenantDigests.ProviderCall(CreateToolShapeCall(null, null)), "E7A1343F013074DE4485BEAD44523A405951783796CB458D38CD3F95D490E415");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.tool-output.present", CovenantDigests.ProviderCall(CreateToolShapeCall(D(6), null)), "518DA8E5DEDEE57A327A5AE5FF72CD0EE868D2F3F460A4725374E52BE276B181");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.structured-output.absent", CovenantDigests.ProviderCall(CreateToolShapeCall(null, null)), "E7A1343F013074DE4485BEAD44523A405951783796CB458D38CD3F95D490E415");
        recorder.RecordDigest(CorpusCategory.Provider, "provider.structured-output.present", CovenantDigests.ProviderCall(CreateToolShapeCall(null, D(7))), "02AEAC2172EB55BF52E7CE5F36EED89CD7A99D4598D47B5EDAD472FDD5AA7302");
        recorder.RecordCheck(CorpusCategory.Provider, "provider.projection.options", FrozenOptionsProjectionChecks());
        recorder.RecordCheck(CorpusCategory.Provider, "provider.projection.tool", FrozenToolProjectionChecks());
        recorder.RecordCheck(CorpusCategory.Provider, "provider.projection.structured", FrozenStructuredProjectionChecks());
        recorder.RecordCheck(CorpusCategory.Provider, "provider.projection.reasoning-presence", FrozenReasoningPresenceChecks());
    }

    private static void RunOptionalCases(CorpusRecorder recorder)
    {
        CovenantDigest emptyGlobal = CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [], []));
        CovenantDigest emptyCampaign = CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignConfirmed, [], []));
        CovenantDigest emptyProposed = CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignProposed, [], []));
        MutationRequestDigestInput mutation = new(CovenantMutationKind.OperatorSet, G1, CovenantScope.Global, null, new CovenantKey("a"), CovenantLane.Confirmed, CovenantOperation.Set, 0, false, CovenantOrigin.Operator, null, null, 0, null, null, []);
        CovenantDigest request = CovenantDigests.MutationRequest(mutation);
        SessionTurnRequestDigestInput turn = new(SessionTurnSurface.Intelligence, CovenantProviderDispatchMode.Buffered, G1, null, CovenantContextPolicy.Default, null, D(1), null, null);
        CovenantDigest turnDigest = CovenantDigests.SessionTurnRequest(turn);
        CovenantDigest options = CovenantDigests.ProviderOptions(MinimalProviderOptions());

        recorder.RecordDigest(CorpusCategory.Optional, "optional.request", request, "A1A25676634D894652316B20A1B86ED734191EE976EB4054EBF0F7C8BA374D79");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.preflight", CovenantDigests.PreflightBody(new PreflightBodyDigestInput(request, 0, G2, 0, 0, 0, null, null, D(1), D(2), -1, 0)), "B003FC48591C91B92414B82AAEEBFC28C95D9AB60BA86F6ACC62086A4A108669");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.authorization", CovenantDigests.Authorization(new AuthorizationDigestInput(request, G2, null, null, null, null, null, null, CovenantAuthorizationMode.None)), "DCBAAA783EEFE32D83EC0A663C6184148EC78B6834B098DAF0C7F4D53B7A0EBB");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.snapshot", CovenantDigests.Snapshot(new SnapshotDigestInput(G3, null, 0, [new SnapshotCandidateDigestInput(1, G1, G2, CovenantScope.Global, null, CovenantLane.Confirmed, CovenantOperation.Set, CovenantOrigin.Operator, 0, null, 0, 0, D(1), D(2), 0, D(13), 0)])), "FCB2B04534037F7F5C7851065AD9F781D8157E9042476C3FFB348A3AF66B5084");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.plan", CovenantDigests.Plan(new PlanDigestInput(D(1), 0, 0, [], emptyGlobal, emptyCampaign, emptyProposed)), "415B650CEA66CBE222C006DEB8C2405EF60936B231D057FE9087C74632957BB2");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.materialization", CovenantDigests.Materialization(new MaterializationDigestInput(false, [])), "B9DFBAFDEB9623312C7797CE54786D899912F49456FA3D95A28C29D6A910E1B1");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.artifact.no-evidence", CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, null, null, null, 0, D(1), D(2), null, null, null)), "B3F8D89762D01D2922572C3171580BA4D78E30F0A32BDCB7540F06611EA731A1");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.artifact.maintenance", CovenantDigests.ArtifactLabel(new ArtifactLabelDigestInput(SensitiveArtifactKind.AssistantEntry, G1, null, null, null, 0, D(1), D(2), null, null, D(3))), "AECABBCD0BE6AD5D7C9EC0C17CADB954F79BC2E3A46C07ADB4029D9BC49B3EF3");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.turn-request", turnDigest, "A19091F258B6A9FE307937E48C171BFBB03750B45F02C887816C54B87C39F5BD");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.turn-execution", CovenantDigests.SessionTurnExecution(new SessionTurnExecutionDigestInput(turnDigest, G2, SessionCampaignBindingKind.GlobalOnly, null, null, 1, null, 1, "p", "m", D(1), [], CovenantToolPolicyCode.AllTools, false, false, InvocationAttendance.Attended)), "C6D2465160C884C533EB592DC4C0338038E44006AE25B5DD94F1B147C7BF1BEA");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.turn-execution.campaign-no-path", CovenantDigests.SessionTurnExecution(new SessionTurnExecutionDigestInput(turnDigest, G2, SessionCampaignBindingKind.Campaign, new SessionTurnExecutionCampaignContextDigestInput(G3, 1), null, 1, null, 1, "p", "m", D(1), [], CovenantToolPolicyCode.AllTools, false, false, InvocationAttendance.Attended)), "DF7E45D2C26A4F1F3554C3A221E9BF9911971B6D9DFDE6A6F24C9276D49F708B");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.provider-options", options, "9AB75EEA77391D3A9CF058DB6C6FB5E87BEBCB79A6C44C3F3DB2666E4D43D72A");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.provider-call", CovenantDigests.ProviderCall(new ProviderCallDigestInput("p", "m", CovenantProviderDispatchMode.Buffered, "t", 1, 0, D(1), options, [], [], D(2), [], [], null)), "E86DC406B4F790041F00AF16E86869D404DE2BC388BD35CCBE6A490424D630B2");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.admission", CovenantDigests.Admission(new AdmissionDigestInput(D(1), 1, G1, 1, null, D(2), D(3), D(4), 0, [], emptyGlobal, emptyCampaign, emptyProposed)), "B52931D8D12223456BA67F8CC0C1CF0B8C77FD3B4667C83E0F14B35BF6F074CB");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.external-disclosure", CovenantDigests.ExternalDisclosure(new ExternalDisclosureDigestInput(G1, CovenantDisclosureSubjectKind.Turn, G2, D(1), 1, CovenantEgressDestination.Provider, CovenantDisclosureRevocability.LocallyRevocable, D(2), D(3), null, null, null, -1)), "12FFD6A827274435DCCA44F79058F21E6E83FE1DA1B72E33B06835B2413C91CA");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.binding", CovenantDigests.SessionBindingApplyRequest(new SessionBindingApplyRequestDigestInput(G1, G2, SessionCampaignBindingKind.GlobalOnly, null, D(1), D(2))), "C1B273FAF506AA962540A947615192E5CD30C9BE28DA6F87E82B1FD4E9F35F7F");
        recorder.RecordDigest(CorpusCategory.Optional, "optional.cursor", CovenantDigests.CursorFilter(new CursorFilterDigestInput(CovenantCursorEndpoint.List, CovenantCursorScopeSelection.Global, null, null, null, CovenantLifecycle.Any, null, 1, CovenantCursorSort.CanonicalHeads)), "2D10C6247193E04711B5533D0E8312CEDC6D043532B5F44571918F6999E2866F");
    }

    private static void RunOrderingCases(CorpusRecorder recorder)
    {
        SectionDigestInput section = new(CovenantPlacement.GlobalConfirmed, [new SectionItemDigestInput(new CovenantKey("z.key"), G2, G3, 2, D(1)), new SectionItemDigestInput(new CovenantKey("a.key"), G1, G2, 1, D(2))], [.. "global\n"u8]);
        Guid lower = Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff");
        Guid upper = Guid.Parse("80000000-0000-0000-0000-000000000000");
        SectionDigestInput rawGuidSection = new(CovenantPlacement.GlobalConfirmed, [new SectionItemDigestInput(new CovenantKey("same.key"), upper, G3, 2, D(2)), new SectionItemDigestInput(new CovenantKey("same.key"), lower, G2, 1, D(1))], [.. "tie\n"u8]);
        SensitivityDigestInput provenance = new(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [upper, lower], default);
        ProviderOptionsDigestInput logit = MinimalProviderOptions() with { LogitBias = [new FrozenLogitBias(0, -0.25), new FrozenLogitBias(-1, 0.5)] };
        SessionTurnExecutionDigestInput attachments = CreateExecution([G2, G3, G4]);
        CovenantDigest attachmentRequest = CovenantDigests.SessionTurnRequest(new SessionTurnRequestDigestInput(SessionTurnSurface.PromptExecute, CovenantProviderDispatchMode.Buffered, G1, G2, CovenantContextPolicy.Default, SessionTurnRouteValue.ForPrompt(G3), D(5), G4, D(6)));
        SessionTurnExecutionDigestInput rawGuidAttachments = CreateExecution([upper, lower]) with { SessionTurnRequestDigest = attachmentRequest };
        SnapshotCandidateDigestInput firstCandidate = CreateSnapshot().Candidates[0];
        SnapshotCandidateDigestInput secondCandidate = firstCandidate with { SearchDocumentId = 13, EntryId = G2 };
        ProviderCallDigestInput call = CreateProviderCall(D(1), D(2), D(3));
        PlanDecisionDigestInput firstDecision = new(G1, G2, CovenantPlanDecision.EligibleConfirmed, null, CovenantPlacement.GlobalConfirmed, D(1), 1);
        PlanDecisionDigestInput secondDecision = new(G2, G3, CovenantPlanDecision.EligibleProposed, null, CovenantPlacement.CampaignProposed, D(2), 2);
        AdmissionCandidateDigestInput firstAdmission = new(G1, G2, CovenantAdmissionDecision.Admitted, 1);
        AdmissionCandidateDigestInput secondAdmission = new(G2, G3, CovenantAdmissionDecision.Pressured, 2);
        SnapshotCandidateDigestInput provenanceCandidate = new(1, G2, G3, CovenantScope.Global, null, CovenantLane.Confirmed, CovenantOperation.Set, CovenantOrigin.Operator, 0, null, 0, 0, D(3), D(4), 2, D(1), 0);
        ProviderCallDigestInput promptCall = new("p", "m", CovenantProviderDispatchMode.Buffered, "t", 1, 0, D(1), D(2), [.. "abcd"u8], [new ProviderPromptSpanDigestInput(CovenantPromptAttribution.DataHeader, 0, 1, D(4)), new ProviderPromptSpanDigestInput(CovenantPromptAttribution.Instructions, 2, 2, D(5))], D(3), [], [], null);
        MaterializationDigestInput materialization = CreateComparatorMaterialization();
        MaterializationDigestInput reversedMaterialization = materialization with
        {
            Sources =
            [
                .. materialization.Sources
                    .Reverse()
                    .Select(source => source.Occurrences.IsEmpty
                        ? source
                        : source with { Occurrences = [.. source.Occurrences.Reverse()] })
            ]
        };

        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.section", CovenantDigests.Section(section) == CovenantDigests.Section(section with { Items = [.. section.Items.Reverse()] }));
        recorder.RecordDigest(CorpusCategory.Ordering, "ordering.section.raw-guid", CovenantDigests.Section(rawGuidSection), "E8F3F0642AD8211AEE9313185F53011F484A59F366A38A594106A887AFED61A9");
        recorder.RecordDigest(CorpusCategory.Ordering, "ordering.generation-guid", CovenantDigests.Sensitivity(provenance), "BF5F0FD2594DC50A16D89354F52F6856F76CA5CACEB3AE44B288E93CD7C73586");
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.attachments-guid", CovenantDigests.SessionTurnExecution(attachments) == CovenantDigests.SessionTurnExecution(attachments with { ResolvedAttachmentVersionIds = [G4, G2, G3] }));
        recorder.RecordDigest(CorpusCategory.Ordering, "ordering.attachments.raw-guid", CovenantDigests.SessionTurnExecution(rawGuidAttachments), "63749C2D23CED235C4A3DC7D167F5DC92C2729579718EFD29FF2FD0A993AF652");
        recorder.RecordDigest(CorpusCategory.Ordering, "ordering.materialization-tiers", CovenantDigests.Materialization(materialization), "D2407E8D6B5A4EAFC0C47FDA48E4E2FF587BB27B9278C13B435F0276596BF19E");
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.materialization-permutation", CovenantDigests.Materialization(materialization) == CovenantDigests.Materialization(reversedMaterialization));
        recorder.RecordDigest(CorpusCategory.Ordering, "ordering.logit-bias-signed", CovenantDigests.ProviderOptions(logit), "AB994D837F04D8091583B382BCA67E9B20FC99815E810B8B111D2ECDB6560267");
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.stops-supplied", CovenantDigests.ProviderOptions(MinimalProviderOptions() with { Stop = ["a", "b"] }) != CovenantDigests.ProviderOptions(MinimalProviderOptions() with { Stop = ["b", "a"] }));
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.snapshot-candidates-supplied", CovenantDigests.Snapshot(new SnapshotDigestInput(G1, null, 0, [firstCandidate, secondCandidate])) != CovenantDigests.Snapshot(new SnapshotDigestInput(G1, null, 0, [secondCandidate, firstCandidate])));
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.mutation-provenance-supplied", CovenantDigests.MutationRequest(CreateMutationRequest()) != CovenantDigests.MutationRequest(CreateMutationRequest() with { ProvenanceDigests = [D(6), D(5)] }));
        recorder.RecordDigest(CorpusCategory.Ordering, "ordering.snapshot-provenance-count", CovenantDigests.Snapshot(new SnapshotDigestInput(G1, null, 0, [provenanceCandidate with { ProvenanceCount = 1 }])), "C76D01C3C914B2A154361FC58123E43B8E3227F00464A0263FCF18486608EDB9");
        recorder.RecordDigest(CorpusCategory.Ordering, "ordering.snapshot-provenance-aggregate", CovenantDigests.Snapshot(new SnapshotDigestInput(G1, null, 0, [provenanceCandidate with { ProvenanceDigest = D(2) }])), "D0056C2C6B9E22FB069B980A53F1DD7BD51D4A1AB6BA55CD059BC1CE4D47C4A4");
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.messages-supplied", CovenantDigests.ProviderCall(call with { Messages = [Message("a"), Message("b")] }) != CovenantDigests.ProviderCall(call with { Messages = [Message("b"), Message("a")] }));
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.parts-supplied", CovenantDigests.ProviderCall(call with { Messages = [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.Text("a"), ProviderContentPartDigestInput.Text("b")])] }) != CovenantDigests.ProviderCall(call with { Messages = [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.Text("b"), ProviderContentPartDigestInput.Text("a")])] }));
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.tools-supplied", CovenantDigests.ProviderCall(call) != CovenantDigests.ProviderCall(call with { ToolDefinitions = [call.ToolDefinitions[1], call.ToolDefinitions[0]] }));
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.plan-decisions-supplied", CovenantDigests.Plan(new PlanDigestInput(D(1), 0, 0, [firstDecision, secondDecision], D(2), D(3), D(4))) != CovenantDigests.Plan(new PlanDigestInput(D(1), 0, 0, [secondDecision, firstDecision], D(2), D(3), D(4))));
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.admission-decisions-supplied", CovenantDigests.Admission(new AdmissionDigestInput(D(1), 1, G1, 1, null, D(2), D(3), D(4), 0, [firstAdmission, secondAdmission], D(5), D(6), D(7))) != CovenantDigests.Admission(new AdmissionDigestInput(D(1), 1, G1, 1, null, D(2), D(3), D(4), 0, [secondAdmission, firstAdmission], D(5), D(6), D(7))));
        recorder.RecordCheck(CorpusCategory.Ordering, "ordering.prompt-spans", CovenantDigests.ProviderCall(promptCall) == FromHex("847ADC03AC66B944C61522B5876CC325E486491A658DD90798C65B2C2783921C") && Rejects<ArgumentException>(() => CovenantDigests.ProviderCall(promptCall with { PromptSpans = [promptCall.PromptSpans[1], promptCall.PromptSpans[0]] })));
    }

    private static void RunChainCases(CorpusRecorder recorder)
    {
        CovenantAttemptChain attempt = CovenantEvidenceChains.SeedAttemptChain();
        CovenantBranchChain root = CovenantEvidenceChains.SeedBranchChain(G1, null, null);
        CovenantBranchChain branch = CovenantEvidenceChains.SeedBranchChain(G2, D(3), D(4));
        CovenantDisclosureChain disclosure = CovenantEvidenceChains.SeedDisclosureChain();

        recorder.RecordDigest(CorpusCategory.Chain, "chain.attempt.seed", attempt.Head, "011426617416BDF818E7A0893C9F3883C9D1AB5435846393FB37E61E439BF510");
        attempt = CovenantEvidenceChains.AppendAttempt(attempt, D(1));
        recorder.RecordDigest(CorpusCategory.Chain, "chain.attempt.a1", attempt.Head, "95880907B1CBA63332B8327DFAD8C5153F0B824FE689F6B161DA868248BE3E01");
        attempt = CovenantEvidenceChains.AppendAttempt(attempt, D(2));
        recorder.RecordDigest(CorpusCategory.Chain, "chain.attempt.a2", attempt.Head, "C88B5606F23D9ACA73831247A476B9EAA25B948A232C7555A82D289B4AE8CADC");
        recorder.RecordDigest(CorpusCategory.Chain, "chain.branch.root", root.Head, "76FF628A532349AD9A7FF2A63A56A165CDBBD56800A0338AB723181AF4C0E710");
        recorder.RecordDigest(CorpusCategory.Chain, "chain.branch.fork", branch.Head, "F8BC4AA8B072FA15EC43FE8EDEA75EE9C6E69279C63EEFF2A98C7ED622B7126A");
        branch = CovenantEvidenceChains.AppendBranch(branch, D(5));
        recorder.RecordDigest(CorpusCategory.Chain, "chain.branch.b1", branch.Head, "00AD30FC9650B23C2D1FA87B6B133E5BA553B12643E8002802E7F8D523D512E9");
        branch = CovenantEvidenceChains.AppendBranch(branch, D(6));
        recorder.RecordDigest(CorpusCategory.Chain, "chain.branch.b2", branch.Head, "8AB93043E21DBC3E3A4AEE39F056B418FBAF4A41181F940D456C8AE2E86C43DA");
        recorder.RecordDigest(CorpusCategory.Chain, "chain.disclosure.seed", disclosure.Head, "338E809242611A14B2E00821758B33139EC8E59AFD77E2D6A5378818E1ED0D54");
        disclosure = CovenantEvidenceChains.AppendDisclosure(disclosure, D(6));
        recorder.RecordDigest(CorpusCategory.Chain, "chain.disclosure.d1", disclosure.Head, "4EB606B6BBCA7DF58B8E08740FB67CA7D7CB80CCEFF5D68B7B90218FEFD301BB");
        disclosure = CovenantEvidenceChains.AppendDisclosure(disclosure, D(7));
        recorder.RecordDigest(CorpusCategory.Chain, "chain.disclosure.d2", disclosure.Head, "EE48B3586B2AF7D65E587C5A2A548955510ACD440AC6D631993BB31AFC035568");
    }

    private static void RunWriterCases(CorpusRecorder recorder)
    {
        recorder.RecordCheck(CorpusCategory.Writer, "writer.parity.all-primitives", WriterParity());
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.fixed32", WriterFaults(static writer => writer.WriteFixed32(new byte[31])));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.utf8-null", WriterFaults(static writer => writer.WriteUtf8(null!)));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.utf8-malformed", WriterFaults(static writer => writer.WriteUtf8("\ud800")));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.nonfinite", WriterFaults(static writer => writer.WriteBinary64(double.NaN)));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.count-overflow", WriterFaults(static writer => writer.WriteCount((ulong)uint.MaxValue + 1)));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.optional-callback-null", WriterFaults(static writer => writer.WriteOptional<int>(1, null!)));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.optional-reference-callback-null", WriterFaults(static writer => writer.WriteOptionalReference<string>("x", null!)));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.list-null", WriterFaults(static writer => writer.WriteList<int>(null!, static (valueWriter, value) => valueWriter.WriteInt32(value))));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.list-callback-null", WriterFaults(static writer => writer.WriteList([1], null!)));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.callback-throws", WriterFaults(static writer => writer.WriteOptional<int>(1, static (valueWriter, value) => { valueWriter.WriteInt32(value); throw new InvalidOperationException(); })));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.optional-reference-callback-throws", WriterFaults(static writer => writer.WriteOptionalReference("x", static (valueWriter, value) => { valueWriter.WriteUtf8(value); throw new InvalidOperationException(); })));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.list-callback-throws", WriterFaults(static writer => writer.WriteList([1], static (valueWriter, value) => { valueWriter.WriteInt32(value); throw new InvalidOperationException(); })));
        recorder.RecordCheck(CorpusCategory.Writer, "writer.fault.finalize", FaultedFinalizeRejects());
        recorder.RecordCheck(CorpusCategory.Writer, "writer.disposed", DisposedWriterRejects());
        recorder.RecordCheck(CorpusCategory.Writer, "writer.finalized", FinalizedWriterRejects());
    }

    private static void RunDisclosureCases(CorpusRecorder recorder)
    {
        CovenantDisclosureState empty = CovenantDisclosureState.Empty(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable);
        CovenantDisclosureState nonempty = new(CovenantEgressDestination.Network, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 7, 1700000001, Enumerable.Range(0, CovenantLimits.DisclosureEvidenceBloomBytes).Select(static value => (byte)value).ToArray());
        byte[] last = Bloom();

        last[^1] = 0x80;

        CovenantDisclosureState lastByte = new(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 1, 1, last);
        CovenantDisclosureState exact = State(CovenantDisclosureCountKind.Exact, 4, 100, [0x01]);
        CovenantDisclosureState lower = State(CovenantDisclosureCountKind.LowerBound, 7, 200, [0x02]);
        CovenantDisclosureState a = State(CovenantDisclosureCountKind.Exact, 2, 100, [0x01]);
        CovenantDisclosureState b = State(CovenantDisclosureCountKind.Exact, 5, 200, [0x02]);
        CovenantDisclosureState c = State(CovenantDisclosureCountKind.LowerBound, 3, 150, [0x04]);
        CovenantDisclosureState incrementedExact = CovenantDisclosureStateAlgebra.IncrementLocal(exact, 3, 150, Bloom(0x04));
        CovenantDisclosureState incrementedLower = CovenantDisclosureStateAlgebra.IncrementLocal(lower, 2, 175, Bloom(0x08));
        CovenantDisclosureState joined = CovenantDisclosureStateAlgebra.JoinRestore(exact, lower);
        CovenantDisclosureState byte31 = CovenantDisclosureStateAlgebra.JoinRestore(empty, lastByte);

        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.bloom.literal", Convert.ToHexString(CovenantDisclosureStateAlgebra.CreateEvidenceBloom(D(1))) == "0000000000000000000001000000000000000020000000100000100000000000");
        recorder.RecordDigest(CorpusCategory.Disclosure, "disclosure.state.empty", empty.Digest, "124888B9A1C15CA1A396EFFF2292CE17BED8C8D1C14774C4342D819C819ACC7A");
        recorder.RecordDigest(CorpusCategory.Disclosure, "disclosure.state.nonempty", nonempty.Digest, "F69C65DE3A27794B3B04A1143174D96E41CCF610DDF437D9F05666CFEA0C4E2A");
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.increment.exact", incrementedExact.Count == 7);
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.increment.exact-full-state", incrementedExact.Equals(State(CovenantDisclosureCountKind.Exact, 7, 150, [0x05])));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.increment.lower-bound", incrementedLower.Equals(State(CovenantDisclosureCountKind.LowerBound, 9, 200, [0x0a])));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.increment.non-idempotent", !CovenantDisclosureStateAlgebra.IncrementLocal(incrementedLower, 2, 175, Bloom(0x08)).Equals(incrementedLower));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.join.maximum", joined.Count == 7);
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.join.full-state", joined.Equals(State(CovenantDisclosureCountKind.LowerBound, 7, 200, [0x03])));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.join.associative", CovenantDisclosureStateAlgebra.JoinRestore(CovenantDisclosureStateAlgebra.JoinRestore(a, b), c).Equals(CovenantDisclosureStateAlgebra.JoinRestore(a, CovenantDisclosureStateAlgebra.JoinRestore(b, c))));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.join.commutative", CovenantDisclosureStateAlgebra.JoinRestore(a, b).Equals(CovenantDisclosureStateAlgebra.JoinRestore(b, a)));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.join.idempotent", CovenantDisclosureStateAlgebra.JoinRestore(a, a).Equals(a));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.join.empty", CovenantDisclosureStateAlgebra.JoinRestore(empty, empty).Equals(empty));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.bloom.byte31", byte31.EvidenceBloom[^1] == 0x80);
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.bloom.byte31-full-state", byte31.Equals(State(CovenantDisclosureCountKind.LowerBound, 1, 1, last)));
        recorder.RecordCheck(CorpusCategory.Disclosure, "disclosure.join.unsigned-count", CovenantDisclosureStateAlgebra.JoinRestore(State(CovenantDisclosureCountKind.Exact, (ulong)long.MaxValue + 1, 1, [0x01]), State(CovenantDisclosureCountKind.Exact, ulong.MaxValue - 1, 2, [0x02])).Count == ulong.MaxValue - 1);
    }

    private static void RunRefusalCases(CorpusRecorder recorder)
    {
        ProviderOptionsDigestInput minimal = MinimalProviderOptions();
        SessionTurnExecutionDigestInput execution = CreateExecution([]);
        ProviderCallDigestInput call = CreateProviderCall(D(1), D(2), D(3));

        recorder.RecordRefusal<InvalidOperationException>(CorpusCategory.Refusal, "refusal.default.key", static () => _ = default(CovenantKey).Value);
        recorder.RecordRefusal<InvalidOperationException>(CorpusCategory.Refusal, "refusal.default.generation", static () => _ = default(CovenantGenerationId).Value);
        recorder.RecordRefusal<InvalidOperationException>(CorpusCategory.Refusal, "refusal.default.row-count", static () => _ = default(CovenantRowCount).Value);
        recorder.RecordRefusal<InvalidOperationException>(CorpusCategory.Refusal, "refusal.default.byte-count", static () => _ = default(CovenantByteCount).Value);
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.default.digest", static () => CovenantDigests.Mutation(new MutationDigestInput(default, D(1))));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.invalid.enum", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = (ProviderResponseFormat)0 }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.guid.required-empty", static () => CovenantDigests.MutationRequest(CreateMutationRequest() with { MutationId = Guid.Empty }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.guid.present-optional-empty", static () => CovenantDigests.MutationRequest(CreateMutationRequest() with { CampaignId = Guid.Empty }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.tool-choice.named-null", () => CovenantDigests.ProviderOptions(minimal with { ToolChoice = ProviderToolChoice.Named }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.tool-choice.named-empty", () => CovenantDigests.ProviderOptions(minimal with { ToolChoice = ProviderToolChoice.Named, NamedTool = "" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.tool-choice.auto-name", () => CovenantDigests.ProviderOptions(minimal with { NamedTool = "t" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.tool-choice.none-name", () => CovenantDigests.ProviderOptions(minimal with { ToolChoice = ProviderToolChoice.None, NamedTool = "t" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.tool-choice.required-name", () => CovenantDigests.ProviderOptions(minimal with { ToolChoice = ProviderToolChoice.Required, NamedTool = "t" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.text.schema-name", () => CovenantDigests.ProviderOptions(minimal with { JsonSchemaName = "s" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.text.schema-digest", () => CovenantDigests.ProviderOptions(minimal with { CanonicalJsonSchemaDigest = D(3) }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.text.schema-description", () => CovenantDigests.ProviderOptions(minimal with { JsonSchemaDescription = "d" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.text.schema-strict-false", () => CovenantDigests.ProviderOptions(minimal with { JsonSchemaStrict = CovenantTriStateBoolean.False }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.text.schema-strict-true", () => CovenantDigests.ProviderOptions(minimal with { JsonSchemaStrict = CovenantTriStateBoolean.True }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-object.schema-name", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaName = "s" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-object.schema-digest", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, CanonicalJsonSchemaDigest = D(3) }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-object.schema-description", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaDescription = "d" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-object.schema-strict-false", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaStrict = CovenantTriStateBoolean.False }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-object.schema-strict-true", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonObject, JsonSchemaStrict = CovenantTriStateBoolean.True }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-schema.missing-name", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonSchema, CanonicalJsonSchemaDigest = D(3) }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-schema.missing-digest", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonSchema, JsonSchemaName = "s" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-schema.missing-required-pair", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonSchema }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.json-schema.name-empty", () => CovenantDigests.ProviderOptions(minimal with { ResponseFormat = ProviderResponseFormat.JsonSchema, JsonSchemaName = "", CanonicalJsonSchemaDigest = D(3) }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.branch.parent-admission-only", static () => CovenantEvidenceChains.SeedBranchChain(G1, D(1), null));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.branch.parent-branch-only", static () => CovenantEvidenceChains.SeedBranchChain(G1, null, D(1)));
        recorder.RecordRefusal<OverflowException>(CorpusCategory.Refusal, "refusal.attempt.overflow", static () => CovenantEvidenceChains.AppendAttempt(new CovenantAttemptChain(ulong.MaxValue, D(1)), D(2)));
        recorder.RecordRefusal<OverflowException>(CorpusCategory.Refusal, "refusal.branch.overflow", static () => CovenantEvidenceChains.AppendBranch(new CovenantBranchChain(G1, ulong.MaxValue, D(1)), D(2)));
        recorder.RecordRefusal<OverflowException>(CorpusCategory.Refusal, "refusal.disclosure.overflow", static () => CovenantEvidenceChains.AppendDisclosure(new CovenantDisclosureChain(ulong.MaxValue, D(1)), D(2)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.attempt.default-head", static () => CovenantEvidenceChains.AppendAttempt(new CovenantAttemptChain(0, default), D(1)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.branch.default-head", static () => CovenantEvidenceChains.AppendBranch(new CovenantBranchChain(G1, 0, default), D(1)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.default-head", static () => CovenantEvidenceChains.AppendDisclosure(new CovenantDisclosureChain(0, default), D(1)));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.history-zero", () => CovenantDigests.SessionTurnExecution(execution with { PreRequestHistoryWatermark = 0 }));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.history-negative", () => CovenantDigests.SessionTurnExecution(execution with { PreRequestHistoryWatermark = -1 }));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.provider-generation-zero", () => CovenantDigests.SessionTurnExecution(execution with { ProviderConfigurationGeneration = 0 }));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.provider-generation-negative", () => CovenantDigests.SessionTurnExecution(execution with { ProviderConfigurationGeneration = -1 }));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.campaign-generation-zero", () => CovenantDigests.SessionTurnExecution(execution with { CampaignContext = execution.CampaignContext! with { AvailabilityGeneration = 0 } }));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.campaign-generation-negative", () => CovenantDigests.SessionTurnExecution(execution with { CampaignContext = execution.CampaignContext! with { AvailabilityGeneration = -1 } }));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.path-revision-zero", () => CovenantDigests.SessionTurnExecution(execution with { Path = execution.Path! with { PathRevision = 0 } }));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.execution.path-revision-negative", () => CovenantDigests.SessionTurnExecution(execution with { Path = execution.Path! with { PathRevision = -1 } }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.execution.global-path", () => CovenantDigests.SessionTurnExecution(execution with { BindingKind = SessionCampaignBindingKind.GlobalOnly, CampaignContext = null }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.execution.provider-empty", () => CovenantDigests.SessionTurnExecution(execution with { ProviderIdentity = "" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.execution.model-empty", () => CovenantDigests.SessionTurnExecution(execution with { ModelIdentity = "" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.provider-call.tokenizer-empty", () => CovenantDigests.ProviderCall(call with { TokenizerProfile = "" }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.tool-call.id-empty", () => CovenantDigests.ProviderCall(call with { Messages = [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.ToolCall("", "t", "{}"u8)])] }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.tool-call.name-empty", () => CovenantDigests.ProviderCall(call with { Messages = [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.ToolCall("c", "", "{}"u8)])] }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.tool-call.empty", static () => ProviderContentPartDigestInput.ToolCall("c", "t", ReadOnlySpan<byte>.Empty));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.json.empty", static () => ProviderContentPartDigestInput.Json(ReadOnlySpan<byte>.Empty));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.tool-call.empty", static () => ProviderContentPartEnvelope.ToolCall("c", "t", ReadOnlySpan<byte>.Empty));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.json.empty", static () => ProviderContentPartEnvelope.Json(ReadOnlySpan<byte>.Empty));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.tool-call.malformed-utf8", static () => ProviderContentPartDigestInput.ToolCall("c", "t", new byte[] { 0xff }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.json.malformed-utf8", static () => ProviderContentPartDigestInput.Json(new byte[] { 0xff }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.tool-call.malformed-utf8", static () => ProviderContentPartEnvelope.ToolCall("c", "t", new byte[] { 0xff }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.json.malformed-utf8", static () => ProviderContentPartEnvelope.Json(new byte[] { 0xff }));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.tool-call.duplicate-key", static () => ProviderContentPartDigestInput.ToolCall("c", "t", "{\"a\":1,\"a\":2}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.json.duplicate-key", static () => ProviderContentPartDigestInput.Json("{\"a\":1,\"a\":2}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.tool-call.duplicate-key", static () => ProviderContentPartEnvelope.ToolCall("c", "t", "{\"a\":1,\"a\":2}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.json.duplicate-key", static () => ProviderContentPartEnvelope.Json("{\"a\":1,\"a\":2}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.tool-call.whitespace", static () => ProviderContentPartDigestInput.ToolCall("c", "t", "{\"a\":\"é\", \"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.json.whitespace", static () => ProviderContentPartDigestInput.Json("{\"a\":\"é\", \"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.tool-call.whitespace", static () => ProviderContentPartEnvelope.ToolCall("c", "t", "{\"a\":\"é\", \"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.json.whitespace", static () => ProviderContentPartEnvelope.Json("{\"a\":\"é\", \"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.tool-call.property-order", static () => ProviderContentPartDigestInput.ToolCall("c", "t", "{\"b\":1,\"a\":\"é\"}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.json.property-order", static () => ProviderContentPartDigestInput.Json("{\"b\":1,\"a\":\"é\"}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.tool-call.property-order", static () => ProviderContentPartEnvelope.ToolCall("c", "t", "{\"b\":1,\"a\":\"é\"}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.json.property-order", static () => ProviderContentPartEnvelope.Json("{\"b\":1,\"a\":\"é\"}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.tool-call.number", static () => ProviderContentPartDigestInput.ToolCall("c", "t", "{\"a\":\"é\",\"b\":1.0}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.json.number", static () => ProviderContentPartDigestInput.Json("{\"a\":\"é\",\"b\":1.0}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.tool-call.number", static () => ProviderContentPartEnvelope.ToolCall("c", "t", "{\"a\":\"é\",\"b\":1.0}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.json.number", static () => ProviderContentPartEnvelope.Json("{\"a\":\"é\",\"b\":1.0}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.tool-call.escape", static () => ProviderContentPartDigestInput.ToolCall("c", "t", "{\"a\":\"\\u00e9\",\"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.digest-input.json.escape", static () => ProviderContentPartDigestInput.Json("{\"a\":\"\\u00e9\",\"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.tool-call.escape", static () => ProviderContentPartEnvelope.ToolCall("c", "t", "{\"a\":\"\\u00e9\",\"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.canonical-json.envelope.json.escape", static () => ProviderContentPartEnvelope.Json("{\"a\":\"\\u00e9\",\"b\":1}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.empty-kind", static () => _ = new CovenantDisclosureState(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.LowerBound, false, 0, 0, Bloom()));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.empty-count", static () => _ = new CovenantDisclosureState(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, false, 1, 0, Bloom(0x01)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.nonempty-count", static () => _ = new CovenantDisclosureState(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 0, 1, Bloom(0x01)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.nonempty-timestamp", static () => _ = new CovenantDisclosureState(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 1, 0, Bloom(0x01)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.state-timestamp-negative", static () => _ = new CovenantDisclosureState(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 1, -1, Bloom(0x01)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.digest-timestamp-negative", static () => CovenantDigests.ExternalDisclosureState(new ExternalDisclosureStateDigestInput(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 1, -1, [.. Bloom(0x01)])));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.disclosure.increment-timestamp-zero", static () => CovenantDisclosureStateAlgebra.IncrementLocal(State(CovenantDisclosureCountKind.Exact, 1, 1, [0x01]), 1, 0, Bloom(0x02)));
        recorder.RecordRefusal<ArgumentOutOfRangeException>(CorpusCategory.Refusal, "refusal.disclosure.increment-timestamp-negative", static () => CovenantDisclosureStateAlgebra.IncrementLocal(State(CovenantDisclosureCountKind.Exact, 1, 1, [0x01]), 1, -1, Bloom(0x02)));
        recorder.RecordRefusal<OverflowException>(CorpusCategory.Refusal, "refusal.disclosure.increment-overflow", static () => CovenantDisclosureStateAlgebra.IncrementLocal(State(CovenantDisclosureCountKind.Exact, ulong.MaxValue, 1, [0x01]), 1, 1, Bloom(0x02)));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.nonempty-bloom", static () => _ = new CovenantDisclosureState(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 1, 1, Bloom()));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.join-destination", static () => CovenantDisclosureStateAlgebra.JoinRestore(State(CovenantDisclosureCountKind.Exact, 1, 1, [0x01]), new CovenantDisclosureState(CovenantEgressDestination.Network, CovenantDisclosureRevocability.Nonrevocable, CovenantDisclosureCountKind.Exact, true, 1, 1, Bloom(0x01))));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.disclosure.join-revocability", static () => CovenantDisclosureStateAlgebra.JoinRestore(State(CovenantDisclosureCountKind.Exact, 1, 1, [0x01]), new CovenantDisclosureState(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.LocallyRevocable, CovenantDisclosureCountKind.Exact, true, 1, 1, Bloom(0x01))));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.projection.options-mismatch", static () => CreateMismatchedOptionsProjection());
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.projection.description-mismatch", static () => CreateMismatchedDescriptionProjection());
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.projection.tool-schema-mismatch", static () => CreateMismatchedToolSchemaProjection());
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.projection.tool-output-schema-mismatch", static () => CreateMismatchedToolOutputSchemaProjection());
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.projection.structured-mismatch", static () => CreateProjectionCall(D(1), "{\"type\":\"object\"}"u8));
        recorder.RecordRefusal<ArgumentException>(CorpusCategory.Refusal, "refusal.projection.noncanonical", static () => CreateNoncanonicalOptionsProjection());
    }

    private static MutationRequestDigestInput CreateMutationRequest() =>
        new(CovenantMutationKind.AgentPropose, G1, CovenantScope.Campaign, G2, new CovenantKey("response.style"), CovenantLane.Proposed, CovenantOperation.Set, 7, true, CovenantOrigin.AgentProposed, D(1), D(2), 1, D(3), D(4), [D(5), D(6)]);

    private static SnapshotDigestInput CreateSnapshot() =>
        new(G5, G3, 14, [new SnapshotCandidateDigestInput(12, G1, G2, CovenantScope.Campaign, G3, CovenantLane.Confirmed, CovenantOperation.Set, CovenantOrigin.AgentApproved, 13, G4, 1, 1, D(11), D(12), 2, D(13), 99)]);

    private static PlanDigestInput CreatePlan(CovenantDigest snapshot, CovenantDigest global, CovenantDigest campaign, CovenantDigest proposed) =>
        new(snapshot, 1, 2, [new PlanDecisionDigestInput(G1, G2, CovenantPlanDecision.EligibleConfirmed, null, CovenantPlacement.CampaignConfirmed, D(15), 101)], global, campaign, proposed);

    private static MaterializationDigestInput CreateMaterialization() =>
        new(false, [new MaterializationSourceDigestInput(G1, G2, "attachment.one", D(19), CovenantMaterializationSourceRange.Utf16Range, 7, 8, [new MaterializationOccurrenceDigestInput(CovenantMaterializationContainer.SystemPrompt, null, null, CovenantMaterializationOccurrence.Utf16TextRange, 5, 6), new MaterializationOccurrenceDigestInput(CovenantMaterializationContainer.MessagePart, 3, 4, CovenantMaterializationOccurrence.WholeBinaryPart, null, 9)]), new MaterializationSourceDigestInput(G3, G4, "attachment.two", D(20), CovenantMaterializationSourceRange.WholeSource, null, null, [])]);

    private static MaterializationDigestInput CreateComparatorMaterialization()
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

        return new MaterializationDigestInput(
            false,
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
            ]);
    }

    private static ProviderOptionsDigestInput CreateProviderOptions() =>
        new(1000, 0.25, 0.75, -0.5, 0.125, -9, "user-1", ["stop"], ProviderToolChoice.Named, "tool.one", CovenantTriStateBoolean.True, ProviderResponseFormat.JsonSchema, "schema.one", "schema desc", D(20), CovenantTriStateBoolean.True, CovenantReasoningEffort.High, 2048, CovenantReasoningOutput.Summary, CovenantReasoningWireDialect.OpenRouter, [new FrozenLogitBias(-7, 0.5)]);

    private static ProviderOptionsDigestInput MinimalProviderOptions() =>
        new(null, null, null, null, null, null, null, [], ProviderToolChoice.Auto, null, CovenantTriStateBoolean.Absent, ProviderResponseFormat.Text, null, null, null, CovenantTriStateBoolean.Absent, null, null, null, CovenantReasoningWireDialect.Standard, default);

    private static ProviderOptionsDigestInput MinimalSchemaOptions(CovenantTriStateBoolean strict) =>
        MinimalProviderOptions() with { ResponseFormat = ProviderResponseFormat.JsonSchema, JsonSchemaName = "s", CanonicalJsonSchemaDigest = D(3), JsonSchemaStrict = strict };

    private static ProviderCallDigestInput CreateProviderCall(CovenantDigest sensitivity, CovenantDigest options, CovenantDigest materialization) =>
        new("provider", "model", CovenantProviderDispatchMode.Streaming, "tok-v1", 8192, 3, sensitivity, options, [.. "system"u8], [new ProviderPromptSpanDigestInput(CovenantPromptAttribution.CovenantConfirmed, 0, 6, D(21))], materialization, [new ProviderMessageDigestInput(CovenantProviderRole.User, "m1", "alice", [ProviderContentPartDigestInput.Text("hello"), ProviderContentPartDigestInput.Binary("image/png", "img", CovenantImageDetail.High, [1, 2]), ProviderContentPartDigestInput.ToolCall("call-1", "tool.one", "{\"a\":1}"u8), ProviderContentPartDigestInput.ToolResult("call-1", "result"u8), ProviderContentPartDigestInput.Json("{\"j\":true}"u8), ProviderContentPartDigestInput.Uri("https://example.test/x", "image/jpeg", CovenantImageDetail.Low), ProviderContentPartDigestInput.TextReasoning("reason", "protected"u8)])], [new ProviderToolDefinitionDigestInput("tool.one", D(22), D(23), D(24), CovenantToolRiskIdentity.CovenantSensitiveEgress), new ProviderToolDefinitionDigestInput("tool.two", D(25), D(26), null, CovenantToolRiskIdentity.Ordinary)], D(28));

    private static ProviderCallDigestInput CreateSinglePartCall(ProviderContentPartDigestInput part) =>
        new("p", "m", CovenantProviderDispatchMode.Buffered, "t", 1, 0, D(1), D(2), [], [], D(3), [new ProviderMessageDigestInput(CovenantProviderRole.User, null, null, [part])], [], null);

    private static ProviderCallDigestInput CreateToolShapeCall(CovenantDigest? output, CovenantDigest? structured) =>
        new("p", "m", CovenantProviderDispatchMode.Buffered, "t", 1, 0, D(1), D(2), [], [], D(3), [], [new ProviderToolDefinitionDigestInput("t", D(4), D(5), output, CovenantToolRiskIdentity.Ordinary)], structured);

    private static ProviderMessageDigestInput Message(string value) =>
        new(CovenantProviderRole.User, null, null, [ProviderContentPartDigestInput.Text(value)]);

    private static SessionTurnExecutionDigestInput CreateExecution(ImmutableArray<Guid> attachments) =>
        new(D(1), G2, SessionCampaignBindingKind.Campaign, new SessionTurnExecutionCampaignContextDigestInput(G3, 21), new SessionTurnExecutionPathDigestInput(22, D(7)), 23, G4, 24, "provider", "model", D(8), attachments, CovenantToolPolicyCode.AllTools, false, true, InvocationAttendance.Attended);

    private static CovenantDigest SectionGlobal() =>
        CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.GlobalConfirmed, [new SectionItemDigestInput(new CovenantKey("z.key"), G2, G3, 2, D(1)), new SectionItemDigestInput(new CovenantKey("a.key"), G1, G2, 1, D(2))], [.. "global\n"u8]));

    private static CovenantDigest SectionCampaign() =>
        CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignConfirmed, [new SectionItemDigestInput(new CovenantKey("campaign.key"), G3, G4, 3, D(3))], [.. "campaign\n"u8]));

    private static CovenantDigest SectionProposed() =>
        CovenantDigests.Section(new SectionDigestInput(CovenantPlacement.CampaignProposed, [new SectionItemDigestInput(new CovenantKey("proposed.key"), G4, G5, 4, D(4))], [.. "proposed\n"u8]));

    private static bool FrozenOptionsProjectionChecks()
    {
        byte[] schema = "{\"a\":1}"u8.ToArray();
        CovenantDigest schemaDigest = FromHex("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");
        FrozenProviderOptions text = FrozenProviderOptions.Create(MinimalProviderOptions());
        FrozenProviderOptions jsonObject = FrozenProviderOptions.Create(MinimalProviderOptions() with { ResponseFormat = ProviderResponseFormat.JsonObject });
        bool valid = text.ResponseFormat == ProviderResponseFormat.Text
            && jsonObject.ResponseFormat == ProviderResponseFormat.JsonObject;
        FrozenProviderOptions? last = null;

        foreach (CovenantTriStateBoolean strict in new[] { CovenantTriStateBoolean.Absent, CovenantTriStateBoolean.False, CovenantTriStateBoolean.True })
        {
            FrozenProviderOptions frozen = FrozenProviderOptions.Create(
                MinimalProviderOptions() with
                {
                    ResponseFormat = ProviderResponseFormat.JsonSchema,
                    JsonSchemaName = "s",
                    JsonSchemaDescription = "d",
                    CanonicalJsonSchemaDigest = schemaDigest,
                    JsonSchemaStrict = strict
                },
                schema);

            valid &= frozen.ResponseFormat == ProviderResponseFormat.JsonSchema
                && frozen.JsonSchemaStrict == strict
                && frozen.JsonSchemaDescription == "d"
                && frozen.HasCanonicalJsonSchema
                && frozen.Digest == CovenantDigests.ProviderOptions(frozen.ToDigestInput());
            last = frozen;
        }

        schema[0] = (byte)'!';

        return valid
            && last is not null
            && last.CanonicalJsonSchemaBytes.AsSpan().SequenceEqual("{\"a\":1}"u8);
    }

    private static bool FrozenToolProjectionChecks()
    {
        byte[] inputSchema = "{\"a\":1}"u8.ToArray();
        byte[] outputSchema = "{\"b\":2}"u8.ToArray();
        CovenantDigest descriptionDigest = FromHex("C9046F7A37AD0EA7CEE73355984FA5428982F8B37C8F7BCEC91F7AC71A7CD104");
        CovenantDigest inputDigest = FromHex("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");
        CovenantDigest outputDigest = FromHex("0AB1A6D394CD30195F0642B67AE1180C375FFADF5DD7F39C390668B5FDB6DA93");
        ProviderToolDefinitionEnvelope present = new("tool", "description", descriptionDigest, inputSchema, inputDigest, outputSchema, outputDigest, CovenantToolRiskIdentity.Ordinary);
        ProviderToolDefinitionEnvelope absent = new("tool", "description", descriptionDigest, "{\"a\":1}"u8, inputDigest, [], null, CovenantToolRiskIdentity.Ordinary);

        inputSchema[0] = (byte)'!';
        outputSchema[0] = (byte)'!';

        ProviderToolDefinitionDigestInput projected = present.ToDigestInput();

        return present.Description == "description"
            && present.CanonicalInputSchemaBytes.AsSpan().SequenceEqual("{\"a\":1}"u8)
            && present.CanonicalOutputSchemaBytes.AsSpan().SequenceEqual("{\"b\":2}"u8)
            && present.HasOutputSchema
            && projected.DescriptionDigest == descriptionDigest
            && projected.InputSchemaDigest == inputDigest
            && projected.OutputSchemaDigest == outputDigest
            && !absent.HasOutputSchema
            && absent.CanonicalOutputSchemaBytes.IsDefault;
    }

    private static bool FrozenStructuredProjectionChecks()
    {
        byte[] schema = "{\"type\":\"object\"}"u8.ToArray();
        CovenantDigest schemaDigest = FromHex("A2C799262A3CE3C19EF5CDD983BF3D12B43AB3C426227091B909DCB7054738C0");
        ProviderCallEnvelope present = CreateProjectionCall(schemaDigest, schema);
        ProviderCallEnvelope absent = CreateProjectionCall(null, default);

        schema[0] = (byte)'!';

        return present.HasStructuredOutputSchema
            && present.CanonicalStructuredOutputSchemaBytes.AsSpan().SequenceEqual("{\"type\":\"object\"}"u8)
            && present.ToDigestInput().StructuredOutputSchemaDigest == schemaDigest
            && present.Digest == CovenantDigests.ProviderCall(present.ToDigestInput())
            && !absent.HasStructuredOutputSchema
            && absent.CanonicalStructuredOutputSchemaBytes.IsDefault;
    }

    private static bool FrozenReasoningPresenceChecks()
    {
        ProviderContentPartEnvelope absent = ProviderContentPartEnvelope.TextReasoning("r");
        ProviderContentPartEnvelope present = ProviderContentPartEnvelope.TextReasoning("r", ReadOnlySpan<byte>.Empty);
        ProviderMessageEnvelope message = new(CovenantProviderRole.Assistant, null, null, [absent, present]);

        return !absent.HasProtectedData
            && absent.ToDigestInput().ProtectedData.IsDefault
            && present.HasProtectedData
            && !present.ToDigestInput().ProtectedData.IsDefault
            && present.ProtectedData.IsEmpty
            && message.ContentParts.Length == 2;
    }

    private static bool FrozenCanonicalToolCallChecks()
    {
        byte[] callerBytes = "{\"a\":\"é\",\"b\":1}"u8.ToArray();
        ProviderContentPartEnvelope envelope = ProviderContentPartEnvelope.ToolCall("c", "t", callerBytes);

        callerBytes[0] = (byte)'!';

        ProviderContentPartDigestInput projected = envelope.ToDigestInput();

        return projected.Bytes.AsSpan().SequenceEqual("{\"a\":\"é\",\"b\":1}"u8)
            && CovenantDigests.ProviderCall(CreateSinglePartCall(projected)) == FromHex("15C3CA0507D3C8500F7279329981004C7DE18C00F56B0597AC88249615CF8000");
    }

    private static bool FrozenCanonicalJsonChecks()
    {
        byte[] callerBytes = "{\"a\":\"é\",\"b\":1}"u8.ToArray();
        ProviderContentPartEnvelope envelope = ProviderContentPartEnvelope.Json(callerBytes);

        callerBytes[0] = (byte)'!';

        ProviderContentPartDigestInput projected = envelope.ToDigestInput();

        return projected.Bytes.AsSpan().SequenceEqual("{\"a\":\"é\",\"b\":1}"u8)
            && CovenantDigests.ProviderCall(CreateSinglePartCall(projected)) == FromHex("347BBA8ECFF0FC4319D5351DE2D30AD2CB8760FC2E9251B44ED50A2DA9701CEE");
    }

    private static ProviderCallEnvelope CreateProjectionCall(
        CovenantDigest? structuredOutputSchemaDigest,
        ReadOnlySpan<byte> canonicalStructuredOutputSchemaBytes)
    {
        GenerationProvenance provenance = GenerationProvenance.CreateExact([G1]);
        CovenantDigest sensitivityDigest = CovenantDigests.Sensitivity(new SensitivityDigestInput(ContentSensitivity.CovenantDerived, GenerationProvenanceMode.Exact, [G1], default));
        ProviderCallSensitivity sensitivity = new(ContentSensitivity.CovenantDerived, provenance, sensitivityDigest);

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
            [new ProviderMessageEnvelope(CovenantProviderRole.Assistant, null, null, [ProviderContentPartEnvelope.TextReasoning("r", ReadOnlySpan<byte>.Empty)])],
            [],
            structuredOutputSchemaDigest,
            canonicalStructuredOutputSchemaBytes);
    }

    private static void CreateMismatchedOptionsProjection()
    {
        ProviderOptionsDigestInput input = MinimalProviderOptions() with
        {
            ResponseFormat = ProviderResponseFormat.JsonSchema,
            JsonSchemaName = "s",
            CanonicalJsonSchemaDigest = D(1)
        };

        _ = FrozenProviderOptions.Create(input, "{\"a\":1}"u8);
    }

    private static void CreateMismatchedDescriptionProjection()
    {
        CovenantDigest inputDigest = FromHex("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");

        _ = new ProviderToolDefinitionEnvelope("tool", "description", D(1), "{\"a\":1}"u8, inputDigest, [], null, CovenantToolRiskIdentity.Ordinary);
    }

    private static void CreateMismatchedToolSchemaProjection()
    {
        CovenantDigest descriptionDigest = FromHex("C9046F7A37AD0EA7CEE73355984FA5428982F8B37C8F7BCEC91F7AC71A7CD104");

        _ = new ProviderToolDefinitionEnvelope("tool", "description", descriptionDigest, "{\"a\":1}"u8, D(1), [], null, CovenantToolRiskIdentity.Ordinary);
    }

    private static void CreateMismatchedToolOutputSchemaProjection()
    {
        CovenantDigest descriptionDigest = FromHex("C9046F7A37AD0EA7CEE73355984FA5428982F8B37C8F7BCEC91F7AC71A7CD104");
        CovenantDigest inputDigest = FromHex("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862");

        _ = new ProviderToolDefinitionEnvelope("tool", "description", descriptionDigest, "{\"a\":1}"u8, inputDigest, "{\"b\":2}"u8, D(1), CovenantToolRiskIdentity.Ordinary);
    }

    private static void CreateNoncanonicalOptionsProjection()
    {
        ProviderOptionsDigestInput input = MinimalProviderOptions() with
        {
            ResponseFormat = ProviderResponseFormat.JsonSchema,
            JsonSchemaName = "s",
            CanonicalJsonSchemaDigest = FromHex("015ABD7F5CC57A2DD94B7590F04AD8084273905EE33EC5CEBEAE62276A97F862")
        };

        _ = FrozenProviderOptions.Create(input, "{ \"a\" : 1 }"u8);
    }

    private static bool WriterParity()
    {
        byte[] fixedValue = Enumerable.Range(0, CovenantLimits.DigestBytes).Select(static value => (byte)value).ToArray();
        CovenantCanonicalEncoder buffered = new(512);

        WriteFixture(buffered, fixedValue);

        using CovenantCanonicalHashWriter streaming = new();

        WriteFixture(streaming, fixedValue);

        return new CovenantDigest(SHA256.HashData(buffered.WrittenSpan)) == streaming.FinalizeDigest();
    }

    private static void WriteFixture(CovenantCanonicalEncoder writer, byte[] fixedValue)
    {
        writer.WriteDomainTag(CovenantDomainTag.ProviderCall);
        writer.WriteByte(0x7f);
        writer.WriteSByte(-1);
        writer.WriteUInt16(0x1234);
        writer.WriteInt16(-2);
        writer.WriteUInt32(0x10203040);
        writer.WriteInt32(-3);
        writer.WriteUInt64(0x0123456789abcdef);
        writer.WriteInt64(-5);
        writer.WriteGuid(G1);
        writer.WriteFixed32(fixedValue);
        writer.WriteUtf8("é😀");
        writer.WriteBytes([0xaa, 0xbb]);
        writer.WriteBinary64(-0d);
        writer.WriteCount(2);
        writer.WriteOptional<int>(null, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteOptional<int>(42, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteOptionalReference<string>(null, static (valueWriter, value) => valueWriter.WriteUtf8(value));
        writer.WriteOptionalReference("", static (valueWriter, value) => valueWriter.WriteUtf8(value));
        writer.WriteList([3, 4], static (valueWriter, value) => valueWriter.WriteInt32(value));
    }

    private static void WriteFixture(CovenantCanonicalHashWriter writer, byte[] fixedValue)
    {
        writer.WriteDomainTag(CovenantDomainTag.ProviderCall);
        writer.WriteByte(0x7f);
        writer.WriteSByte(-1);
        writer.WriteUInt16(0x1234);
        writer.WriteInt16(-2);
        writer.WriteUInt32(0x10203040);
        writer.WriteInt32(-3);
        writer.WriteUInt64(0x0123456789abcdef);
        writer.WriteInt64(-5);
        writer.WriteGuid(G1);
        writer.WriteFixed32(fixedValue);
        writer.WriteUtf8("é😀");
        writer.WriteBytes([0xaa, 0xbb]);
        writer.WriteBinary64(-0d);
        writer.WriteCount(2);
        writer.WriteOptional<int>(null, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteOptional<int>(42, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteOptionalReference<string>(null, static (valueWriter, value) => valueWriter.WriteUtf8(value));
        writer.WriteOptionalReference("", static (valueWriter, value) => valueWriter.WriteUtf8(value));
        writer.WriteList([3, 4], static (valueWriter, value) => valueWriter.WriteInt32(value));
    }

    private static bool WriterFaults(Action<CovenantCanonicalHashWriter> action)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteByte(0x42);

        try
        {
            action(writer);

            return false;
        }
        catch (Exception)
        {
            return Rejects<InvalidOperationException>(() => writer.WriteByte(0x43))
                && Rejects<InvalidOperationException>(() => writer.FinalizeDigest());
        }
    }

    private static bool FaultedFinalizeRejects()
    {
        using CovenantCanonicalHashWriter writer = new();

        try
        {
            writer.WriteFixed32([]);
        }
        catch (ArgumentException)
        {
        }

        return Rejects<InvalidOperationException>(() => writer.FinalizeDigest());
    }

    private static bool DisposedWriterRejects()
    {
        CovenantCanonicalHashWriter writer = new();

        writer.WriteByte(1);
        writer.Dispose();

        return Rejects<ObjectDisposedException>(() => writer.WriteByte(2))
            && Rejects<ObjectDisposedException>(() => writer.FinalizeDigest());
    }

    private static bool FinalizedWriterRejects()
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteByte(1);
        _ = writer.FinalizeDigest();

        return Rejects<InvalidOperationException>(() => writer.WriteByte(2))
            && Rejects<InvalidOperationException>(() => writer.FinalizeDigest());
    }

    private static CovenantDisclosureState State(
        CovenantDisclosureCountKind countKind,
        ulong count,
        long timestamp,
        byte[] setBits) =>
        new(CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, countKind, true, count, timestamp, Bloom(setBits));

    private static byte[] Bloom(params byte[] prefix)
    {
        byte[] bloom = new byte[CovenantLimits.DisclosureEvidenceBloomBytes];

        prefix.CopyTo(bloom, 0);

        return bloom;
    }

    private static bool Rejects<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();

            return false;
        }
        catch (Exception exception)
        {
            return exception is TException;
        }
    }

    private static CovenantDigest D(byte value) =>
        new(Enumerable.Repeat(value, CovenantLimits.DigestBytes).ToArray());

    private static CovenantDigest FromHex(string value) =>
        new(Convert.FromHexString(value));

    private enum CorpusCategory : byte
    {
        Domain = 1,
        Section = 2,
        Provider = 3,
        Optional = 4,
        Ordering = 5,
        Chain = 6,
        Writer = 7,
        Disclosure = 8,
        Refusal = 9
    }

    private sealed class CorpusRecorder : IDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly int[] _counts = new int[9];

        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

        private readonly IncrementalHash _manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private readonly IncrementalHash _results = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        private bool _disposed;

        private bool _succeeded = true;

        private string? _firstFailureCaseId;

        public void RecordDigest(CorpusCategory category, string id, CovenantDigest actual, string expectedHex)
        {
            RecordId(category, id);
            AppendResultHeader(category, 1);
            _results.AppendData(actual.Span);
            Accept(id, actual == FromHex(expectedHex));
        }

        public void RecordCheck(CorpusCategory category, string id, bool result)
        {
            RecordId(category, id);
            AppendResultHeader(category, 2);
            _results.AppendData([result ? (byte)1 : (byte)0]);
            Accept(id, result);
        }

        public void RecordRefusal<TException>(CorpusCategory category, string id, Action action)
            where TException : Exception
        {
            bool refused = Rejects<TException>(action);

            RecordId(category, id);
            AppendResultHeader(category, 3);
            _results.AppendData([refused ? (byte)1 : (byte)0]);
            Accept(id, refused);
        }

        public CovenantDigestCorpusResult Complete()
        {
            CovenantDigestCorpusCategoryCounts counts = new(_counts[0], _counts[1], _counts[2], _counts[3], _counts[4], _counts[5], _counts[6], _counts[7], _counts[8]);
            CovenantDigest manifest = new(_manifest.GetHashAndReset());
            CovenantDigest results = new(_results.GetHashAndReset());
            using IncrementalHash aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            aggregate.AppendData(manifest.Span);
            aggregate.AppendData(results.Span);

            Span<byte> countBytes = stackalloc byte[sizeof(uint)];

            foreach (int count in _counts)
            {
                BinaryPrimitives.WriteUInt32BigEndian(countBytes, checked((uint)count));
                aggregate.AppendData(countBytes);
            }

            return new CovenantDigestCorpusResult(_succeeded, _firstFailureCaseId, counts.Total, counts, manifest, results, new CovenantDigest(aggregate.GetHashAndReset()));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _manifest.Dispose();
            _results.Dispose();
            _disposed = true;
        }

        private void RecordId(CorpusCategory category, string id)
        {
            Accept(id, _ids.Add(id));

            byte[] encoded = StrictUtf8.GetBytes(id);
            Span<byte> header = stackalloc byte[1 + sizeof(uint)];

            header[0] = (byte)category;
            BinaryPrimitives.WriteUInt32BigEndian(header[1..], checked((uint)encoded.Length));
            _manifest.AppendData(header);
            _manifest.AppendData(encoded);

            int index = checked((int)category - 1);

            _counts[index] = checked(_counts[index] + 1);
        }

        private void AppendResultHeader(CorpusCategory category, byte kind)
        {
            Span<byte> header = stackalloc byte[2];

            header[0] = (byte)category;
            header[1] = kind;
            _results.AppendData(header);
        }

        private void Accept(string id, bool result)
        {
            if (!result)
            {
                _firstFailureCaseId ??= id;
            }

            _succeeded &= result;
        }
    }
}

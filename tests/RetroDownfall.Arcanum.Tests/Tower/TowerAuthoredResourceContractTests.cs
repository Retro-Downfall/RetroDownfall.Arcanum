using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Tower;

/// <summary>
/// Campaigns, Sessions, Prompts, Spells and the Codex are what an operator authors, names and manages,
/// so they are declared in <c>RetroDownfall.Arcanum.Core.Tower</c>. The desktop application that
/// consumed them first is not their domain, and its name is not theirs.
/// </summary>
/// <remarks>
/// <para>The inventory is named rather than discovered. A type quietly dropped from the move would
/// shrink a discovered sample and pass; naming all of them makes the omission the failure.</para>
/// <para>The wire assertions exist because the namespace move is invisible to
/// <c>System.Text.Json</c> — registration is by type — which is exactly why nothing would report a
/// bundle whose property names or ordering had drifted for some other reason in the same change. The
/// expected documents were captured from the pre-move build and are compared byte for byte through
/// both contexts that serialize a Campaign bundle: the Api wire context and the Core bundle context.</para>
/// </remarks>
public sealed class TowerAuthoredResourceContractTests
{

    private const string TowerNamespace = "RetroDownfall.Arcanum.Core.Tower";

    private const string RetiredNamespace = "RetroDownfall.Arcanum.Core.TheForge";

    /// <summary>Every public type declared by the twenty-five authored-resource files.</summary>
    private static readonly Type[] AuthoredResourceTypes =
    [
        typeof(Campaign),

        typeof(CampaignDto),

        typeof(CampaignExportDto),

        typeof(CampaignExportExclusionsDto),

        typeof(CampaignExportSpellDto),

        typeof(CampaignExportScriptDto),

        typeof(CampaignImportRequest),

        typeof(CampaignImportResultDto),

        typeof(CampaignSettings),

        typeof(CanonicalCampaignContext),

        typeof(CanonicalCampaignResolutionPolicy),

        typeof(CampaignPathIdentityPolicy),

        typeof(CodexContentDto),

        typeof(CodexPutRequest),

        typeof(CampaignPathMarkerPolicy),

        typeof(CampaignPathMarkerContent),

        typeof(ICampaignPathMarkerCodec),

        typeof(ICampaignRepository),

        typeof(CanonicalCampaignResolutionRequest),

        typeof(ICanonicalCampaignContextResolver),

        typeof(SessionCampaignBindingRecord),

        typeof(RegisteredCampaignIdentity),

        typeof(ISessionCampaignBindingReader),

        typeof(ICampaignPathIdentityReader),

        typeof(ICampaignAvailabilityReader),

        typeof(IPromptRepository),

        typeof(ISessionRepository),

        typeof(Prompt),

        typeof(PromptSummaryDto),

        typeof(PromptDetailDto),

        typeof(PromptVersionDto),

        typeof(CreatePromptRequest),

        typeof(UpdatePromptRequest),

        typeof(PromptRenderRequest),

        typeof(PromptRenderResultDto),

        typeof(PromptTestResultDto),

        typeof(ResolvedSpellInfoDto),

        typeof(PromptExportDto),

        typeof(PromptImportRequest),

        typeof(ClonePromptRequest),

        typeof(PromptExecuteRequest),

        typeof(RegisterCampaignRequest),

        typeof(SessionCampaignBindingKind),

        typeof(SessionCampaignBinding),

        typeof(SemanticSearchRequest),

        typeof(SemanticSessionSearchResult),

        typeof(SemanticSearchResult),

        typeof(CreateSessionRequest),

        typeof(UpdateSessionRequest),

        typeof(AppendEntryRequest),

        typeof(SessionSummaryDto),

        typeof(SessionDetailDto),

        typeof(ForkSessionRequest),

        typeof(EntryDto),

        typeof(SessionQueryRequest),

        typeof(SessionQueryResult),

        typeof(SessionAnalytics),

        typeof(SessionExportFormat),

        typeof(SessionExportResult),

        typeof(SessionExportPayload),

        typeof(CompactResult),

        typeof(SessionEntryCountDto),

        typeof(SessionAttachmentDto),

        typeof(CreateSessionAttachmentReferenceRequest),

        typeof(CreateSessionContextPinRequest),

        typeof(SessionContextPinDto),

        typeof(SkillMetadata),

        typeof(SpellValidationResultDto),

        typeof(SpellExportDto),

        typeof(SpellExportScriptDto),

        typeof(SpellImportRequest),

        typeof(TestPromptRequest),

        typeof(UpdateCampaignRequest),

    ];

    /// <summary>
    /// One Campaign bundle, serialized by the pre-move build. Every property name, every ordering, and
    /// the omission of the null legacy <c>skillJson</c> alias are part of the file format an operator
    /// can already have on disk.
    /// </summary>
    private const string ExpectedCampaignBundleJson =
        """{"campaign":{"id":"11111111-1111-1111-1111-111111111111","name":"Fixture Campaign","path":"/campaigns/fixture","type":"campaign","description":"A campaign used to pin the export wire shape.","settings":{"defaultModel":"gpt-4o","modelMap":{"fast":"gpt-4o-mini"},"mcpServerProfiles":["local"],"spellRoots":["/spells"],"loreNamespace":"keep","allowedTools":["read_file"]},"createdAt":"2026-01-02T03:04:05+00:00","updatedAt":"2026-06-07T08:09:10+00:00"},"spells":[{"name":"fixture-spell","spellJson":"{\u0022name\u0022:\u0022fixture-spell\u0022}","fullContent":"# Fixture Spell","scripts":[{"fileName":"run.sh","base64Content":"ZWNobyBoaQ=="}]}],"prompts":[{"name":"fixture-prompt","version":"1.0.0","description":"A prompt used to pin the export wire shape.","tags":["fixture","wire"],"template":"Say {{word}}.","parameterSchema":{"type":"object"},"defaultParameters":{"word":"hello"},"model":"gpt-4o","provider":"openai-compatible","temperature":0.25,"topP":0.9,"maxOutputTokens":256,"campaignId":"11111111-1111-1111-1111-111111111111"}],"exclusions":{"covenantEntryCount":3,"taintedArtifactCount":4}}""";

    public static TheoryData<Type> AuthoredResourceTypeData()
    {

        TheoryData<Type> data = [];

        foreach (Type type in AuthoredResourceTypes)
        {

            data.Add(type);

        }

        return data;

    }

    [Theory]
    [MemberData(nameof(AuthoredResourceTypeData))]
    public void Authored_resource_type_declares_the_tower_namespace(Type type)
    {

        Assert.Equal(TowerNamespace, type.Namespace);

    }

    /// <summary>
    /// Nothing at all is left under the retired namespace, asserted over the whole Core assembly rather
    /// than over the inventory above, so a type nobody remembered is caught as well as one that was.
    /// </summary>
    [Fact]
    public void Core_declares_no_type_in_the_retired_namespace()
    {

        string[] strays = typeof(Campaign).Assembly
            .GetTypes()
            .Where(static type => type.Namespace is string ns
                && (string.Equals(ns, RetiredNamespace, StringComparison.Ordinal)
                    || ns.StartsWith(RetiredNamespace + ".", StringComparison.Ordinal)))
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(strays);

    }

    /// <summary>
    /// "Forge" in a type name is the same collision one level down, and a namespace move would not have
    /// caught it. Compiler-generated closures and iterators are excluded because their names come from
    /// the method they were generated for, not from a decision anyone made.
    /// </summary>
    [Fact]
    public void No_core_type_is_named_for_the_desktop_application()
    {

        string[] offenders = typeof(Campaign).Assembly
            .GetTypes()
            .Where(static type => !type.Name.StartsWith('<'))
            .Where(static type => type.Name.Contains("Forge", StringComparison.Ordinal))
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);

    }

    /// <summary>
    /// The Api wire context and the Core bundle context both register <see cref="CampaignExportDto"/>,
    /// and an operator's exported file has to survive whichever one produced it.
    /// </summary>
    [Fact]
    public void Campaign_export_bundle_is_byte_identical_through_both_registered_contexts()
    {

        CampaignExportDto bundle = FixtureBundle();

        Assert.Equal(
            ExpectedCampaignBundleJson,
            JsonSerializer.Serialize(bundle, ArcanumCoreJsonContext.Default.CampaignExportDto));

        Assert.Equal(
            ExpectedCampaignBundleJson,
            JsonSerializer.Serialize(bundle, ArcanumJsonContext.Default.CampaignExportDto));

    }

    /// <summary>
    /// The import half of the same contract: a bundle written before the move still binds, including
    /// the settings default that absence has to mean rather than <c>default(bool)</c>.
    /// </summary>
    [Fact]
    public void Campaign_import_accepts_a_bundle_written_before_the_move()
    {

        CampaignImportRequest? request = JsonSerializer.Deserialize(
            $$"""{"strategy":"merge","payload":{{ExpectedCampaignBundleJson}}}""",
            ArcanumJsonContext.Default.CampaignImportRequest);

        Assert.NotNull(request);

        Assert.Equal("merge", request!.Strategy);

        CampaignExportDto payload = Assert.IsType<CampaignExportDto>(request.Payload);

        Assert.Equal("Fixture Campaign", payload.Campaign.Name);

        Assert.Equal(WorkspaceType.Campaign, payload.Campaign.Type);

        Assert.Equal("{\"name\":\"fixture-spell\"}", Assert.Single(payload.Spells).ResolvedSpellJson);

        Assert.Equal("fixture-prompt", Assert.Single(payload.Prompts).Name);

        Assert.Equal(3, payload.Exclusions?.CovenantEntryCount);

        Assert.Equal(4, payload.Exclusions?.TaintedArtifactCount);

    }

    private static CampaignExportDto FixtureBundle()
    {

        CampaignSettings settings = new(
            DefaultModel: "gpt-4o",
            ModelMap: new Dictionary<string, string> { ["fast"] = "gpt-4o-mini" },
            McpServerProfiles: ["local"],
            SpellRoots: ["/spells"],
            LoreNamespace: "keep",
            AllowedTools: ["read_file"]);

        CampaignDto campaign = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Fixture Campaign",
            "/campaigns/fixture",
            WorkspaceType.Campaign,
            "A campaign used to pin the export wire shape.",
            settings,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.Zero));

        CampaignExportSpellDto spell = new(
            "fixture-spell",
            SpellJson: "{\"name\":\"fixture-spell\"}",
            FullContent: "# Fixture Spell",
            Scripts: [new CampaignExportScriptDto("run.sh", "ZWNobyBoaQ==")]);

        PromptExportDto prompt = new(
            "fixture-prompt",
            "1.0.0",
            "A prompt used to pin the export wire shape.",
            ["fixture", "wire"],
            "Say {{word}}.",
            JsonDocument.Parse("""{"type":"object"}"""),
            JsonDocument.Parse("""{"word":"hello"}"""),
            "gpt-4o",
            "openai-compatible",
            0.25,
            0.9,
            256,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

        return new CampaignExportDto(campaign, [spell], [prompt], new CampaignExportExclusionsDto(3, 4));

    }

}

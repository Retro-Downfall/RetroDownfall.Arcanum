using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Issue #88 — the bounded request shapes every public Covenant surface accepts.
/// </summary>
/// <remarks>
/// The defect these prevent is a request that reaches storage before anybody asked whether it made
/// sense. A scope selection omitted from a JSON body deserializes to the zero value, and a zero value
/// that fell through to the store would be an installation-wide read nobody requested — which is
/// exactly the "never searches every Campaign by accidental omission" rule. Validation lives on the
/// request record rather than in an endpoint so that the CLI, the Compendium, and a future caller all
/// get the same answer, and so the rule cannot be forgotten by the second surface to use the shape.
/// </remarks>
public sealed class CovenantPublicContractTests
{

    private static readonly Guid Campaign = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly string Digest = new('a', 64);

    private static readonly string Token = new('t', 64);

    [Fact]
    public void An_omitted_scope_selection_is_refused_rather_than_read_as_all_scopes()
    {

        CovenantListRequest request = new(
            Scope: default,
            CampaignId: null,
            Lane: null,
            Lifecycle: CovenantLifecycle.Set,
            EffectiveForCampaignId: null,
            Limit: 0,
            Cursor: null);

        Result validated = request.Validate();

        Assert.True(validated.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, validated.Error.Code);

    }

    [Fact]
    public void A_campaign_scoped_list_without_a_campaign_is_refused()
    {

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.Campaign,
            CampaignId: null,
            Lane: null,
            Lifecycle: CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            Limit: 50,
            Cursor: null);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, request.Validate().Error.Code);

    }

    [Fact]
    public void A_global_list_carrying_a_campaign_is_refused()
    {

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.Global,
            Campaign,
            Lane: null,
            Lifecycle: CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            Limit: 50,
            Cursor: null);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, request.Validate().Error.Code);

    }

    [Theory]
    [InlineData(0, CovenantLimits.DefaultPageSize)]
    [InlineData(-5, CovenantLimits.DefaultPageSize)]
    [InlineData(1, 1)]
    [InlineData(200, 200)]
    [InlineData(5000, CovenantLimits.MaxPageSize)]
    public void A_page_size_is_clamped_rather_than_refused(int requested, int expected)
    {

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.AllScopes,
            CampaignId: null,
            Lane: null,
            Lifecycle: CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            requested,
            Cursor: null);

        Assert.True(request.Validate().IsSuccess);

        Assert.Equal(expected, request.EffectiveLimit);

    }

    /// <summary>
    /// A <c>with</c> expression has to reclamp. An init-only property carrying its clamp in an
    /// initializer is assigned only by the primary constructor, so a derived request would keep the
    /// original page size while reporting a new one — the same defect the rebuild checkpoint
    /// deliberately avoids by validating in accessors rather than initializers.
    /// </summary>
    [Fact]
    public void A_derived_request_reclamps_its_page_size()
    {

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.Global,
            CampaignId: null,
            Lane: null,
            CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            10,
            Cursor: null);

        Assert.Equal(10, request.EffectiveLimit);

        Assert.Equal(CovenantLimits.MaxPageSize, (request with { Limit = 5_000 }).EffectiveLimit);

        Assert.Equal(CovenantLimits.DefaultPageSize, (request with { Limit = 0 }).EffectiveLimit);

    }

    [Fact]
    public void An_all_scope_list_may_still_name_one_evaluation_campaign()
    {

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.AllScopes,
            CampaignId: null,
            CovenantLane.Confirmed,
            CovenantLifecycle.Set,
            Campaign,
            50,
            Cursor: null);

        Assert.True(request.Validate().IsSuccess);

    }

    [Fact]
    public void An_empty_evaluation_campaign_identity_is_refused()
    {

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.Global,
            CampaignId: null,
            Lane: null,
            CovenantLifecycle.Any,
            Guid.Empty,
            50,
            Cursor: null);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, request.Validate().Error.Code);

    }

    [Fact]
    public void An_oversized_cursor_is_refused_before_anything_decodes_it()
    {

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.Global,
            CampaignId: null,
            Lane: null,
            CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            50,
            new string('A', CovenantLimits.MaxEnvelopeEncodedBytes + 1));

        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, request.Validate().Error.Code);

    }

    [Fact]
    public void A_query_over_the_byte_bound_is_refused()
    {

        CovenantQueryRequest request = new(
            CovenantCursorScopeSelection.Global,
            CampaignId: null,
            new string('q', CovenantLimits.MaxSearchQueryBytes + 1),
            Lane: null,
            CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            50,
            Cursor: null);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, request.Validate().Error.Code);

    }

    [Fact]
    public void A_query_is_measured_in_utf8_bytes_not_characters()
    {

        // 256 astral characters are 256 UTF-16 pairs and 1,024 UTF-8 bytes. A character-counting
        // bound would let this through at four times its declared cost.
        string astral = string.Concat(Enumerable.Repeat("\U0001F600", 256));

        Assert.Equal(1024, Encoding.UTF8.GetByteCount(astral));

        CovenantQueryRequest request = new(
            CovenantCursorScopeSelection.Global,
            CampaignId: null,
            astral,
            Lane: null,
            CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            50,
            Cursor: null);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, request.Validate().Error.Code);

    }

    [Fact]
    public void A_query_over_the_term_bound_is_refused()
    {

        string terms = string.Join(' ', Enumerable.Range(0, CovenantLimits.MaxSearchQueryTerms + 1).Select(static index => $"t{index}"));

        CovenantQueryRequest request = new(
            CovenantCursorScopeSelection.Global,
            CampaignId: null,
            terms,
            Lane: null,
            CovenantLifecycle.Any,
            EffectiveForCampaignId: null,
            50,
            Cursor: null);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, request.Validate().Error.Code);

    }

    [Fact]
    public void An_all_scope_detail_lookup_is_unrepresentable()
    {

        // The same key can exist in Global and in every Campaign, so "the" entry would be a guess.
        // The request carries CovenantScope, which has no all-scopes member at all.
        Assert.Equal(
            [CovenantScope.Global, CovenantScope.Campaign],
            Enum.GetValues<CovenantScope>());

    }

    [Theory]
    [InlineData("Uppercase")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("")]
    public void A_key_outside_the_grammar_is_refused_as_an_invalid_key(string key)
    {

        CovenantDetailRequest request = new(CovenantScope.Global, CampaignId: null, key);

        Assert.Equal(ErrorCodes.Covenant.InvalidKey, request.Validate().Error.Code);

    }

    [Fact]
    public void A_wellformed_detail_request_validates()
    {

        CovenantDetailRequest request = new(CovenantScope.Campaign, Campaign, "build.commands");

        Assert.True(request.Validate().IsSuccess);

    }

    [Fact]
    public void Authored_content_over_the_compiler_bound_is_refused_as_invalid_content()
    {

        CovenantSetPrepareRequest request = new(
            CovenantScope.Global,
            CampaignId: null,
            "build.commands",
            new string('x', CovenantLimits.MaxAuthoredContentBytes + 1),
            ExpectedRevision: 0,
            Guid.NewGuid(),
            Reactivate: false);

        Assert.Equal(ErrorCodes.Covenant.InvalidContent, request.Validate().Error.Code);

    }

    [Fact]
    public void A_negative_expected_revision_is_refused()
    {

        CovenantSetPrepareRequest request = new(
            CovenantScope.Global,
            CampaignId: null,
            "build.commands",
            "run builds from the repository root",
            ExpectedRevision: -1,
            Guid.NewGuid(),
            Reactivate: false);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, request.Validate().Error.Code);

    }

    [Fact]
    public void A_global_proposed_retirement_is_unrepresentable_in_the_wire_contract()
    {

        CovenantRetirePrepareRequest request = new(
            CovenantScope.Global,
            CampaignId: null,
            "build.commands",
            CovenantLane.Proposed,
            ExpectedRevision: 3,
            Guid.NewGuid());

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, request.Validate().Error.Code);

    }

    [Fact]
    public void A_set_commit_without_its_preflight_token_is_refused()
    {

        CovenantSetRequest request = new(
            CovenantScope.Campaign,
            Campaign,
            "build.commands",
            "run builds from the repository root",
            ExpectedRevision: 0,
            Guid.NewGuid(),
            Reactivate: false,
            PreflightToken: "   ");

        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, request.Validate().Error.Code);

    }

    [Fact]
    public void A_set_commit_without_a_mutation_identity_is_refused()
    {

        CovenantSetRequest request = new(
            CovenantScope.Campaign,
            Campaign,
            "build.commands",
            "run builds from the repository root",
            ExpectedRevision: 0,
            Guid.Empty,
            Reactivate: false,
            Token);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, request.Validate().Error.Code);

    }

    [Fact]
    public void A_wellformed_set_commit_validates()
    {

        CovenantSetRequest request = new(
            CovenantScope.Campaign,
            Campaign,
            "build.commands",
            "run builds from the repository root",
            ExpectedRevision: 4,
            Guid.NewGuid(),
            Reactivate: true,
            Token);

        Assert.True(request.Validate().IsSuccess);

    }

    [Fact]
    public void A_deregistration_that_carries_a_path_is_refused()
    {

        CampaignPathPrepareRequest request = new(
            Guid.NewGuid(),
            CampaignPathIdentityOperation.Deregister,
            "/some/where");

        Assert.Equal(ErrorCodes.Campaign.InvalidPath, request.Validate().Error.Code);

    }

    [Fact]
    public void A_registration_without_a_path_is_refused()
    {

        CampaignPathPrepareRequest request = new(
            Guid.NewGuid(),
            CampaignPathIdentityOperation.Register,
            Path: null);

        Assert.Equal(ErrorCodes.Campaign.InvalidPath, request.Validate().Error.Code);

    }

    [Theory]
    [InlineData(CampaignPathIdentityOperation.Register)]
    [InlineData(CampaignPathIdentityOperation.Update)]
    [InlineData(CampaignPathIdentityOperation.RepairMoved)]
    [InlineData(CampaignPathIdentityOperation.TakeoverOrphan)]
    public void Every_path_bearing_operation_validates_with_its_path(CampaignPathIdentityOperation operation)
    {

        CampaignPathPrepareRequest request = new(Guid.NewGuid(), operation, "/some/where");

        Assert.True(request.Validate().IsSuccess);

    }

    [Fact]
    public void An_apply_request_whose_digest_is_not_a_full_hex_digest_is_refused()
    {

        CampaignPathApplyRequest request = new(Guid.NewGuid(), "abc", Token);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, request.Validate().Error.Code);

    }

    [Fact]
    public void An_apply_request_binds_operation_digest_and_token_together()
    {

        CampaignPathApplyRequest request = new(Guid.NewGuid(), Digest, Token);

        Assert.True(request.Validate().IsSuccess);

    }

    [Fact]
    public void An_apply_request_without_its_operation_identity_is_refused()
    {

        CampaignPathApplyRequest request = new(Guid.Empty, Digest, Token);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, request.Validate().Error.Code);

    }

    [Fact]
    public void A_campaign_binding_resolution_to_campaign_requires_the_campaign()
    {

        SessionCampaignBindingPrepareRequest request = new(
            Guid.NewGuid(),
            Other,
            SessionCampaignBindingKind.Campaign,
            CampaignId: null);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, request.Validate().Error.Code);

    }

    [Fact]
    public void A_global_only_binding_resolution_may_not_name_a_campaign()
    {

        SessionCampaignBindingPrepareRequest request = new(
            Guid.NewGuid(),
            Other,
            SessionCampaignBindingKind.GlobalOnly,
            Campaign);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, request.Validate().Error.Code);

    }

    [Fact]
    public void A_binding_resolution_can_never_target_the_unresolved_state_itself()
    {

        SessionCampaignBindingPrepareRequest request = new(
            Guid.NewGuid(),
            Other,
            SessionCampaignBindingKind.LegacyUnresolved,
            CampaignId: null);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, request.Validate().Error.Code);

    }

    [Fact]
    public void The_family_reinitialize_apply_request_binds_its_prepared_operation()
    {

        CovenantFamilyReinitializeApplyRequest request = new(Guid.NewGuid(), Digest, Token);

        Assert.True(request.Validate().IsSuccess);

        Assert.Equal(
            ErrorCodes.Validation.InvalidBody,
            new CovenantFamilyReinitializeApplyRequest(Guid.NewGuid(), Digest[..63], Token).Validate().Error.Code);

    }

    [Fact]
    public void A_schema_repair_names_exactly_one_supported_action()
    {

        Assert.Equal(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            new CovenantSchemaRepairRequest((CovenantSchemaRepairAction)99).Validate().Error.Code);

        Assert.True(
            new CovenantSchemaRepairRequest(CovenantSchemaRepairAction.InstallAbsentCanonicalFamily)
                .Validate()
                .IsSuccess);

    }

    /// <summary>
    /// The repair action and phase codes are persisted in the always-present core journal, so the
    /// enum and the table's CHECK constraint have to agree. They are compared against the checked-in
    /// DDL rather than against each other, because two constants in the same file always agree.
    /// </summary>
    [Fact]
    public void The_repair_journal_codes_match_the_shipped_schema()
    {

        Assert.Equal(1, (int)CovenantSchemaRepairAction.InstallAbsentCanonicalFamily);

        Assert.Equal(2, (int)CovenantSchemaRepairAction.RepairExistingFamily);

        Assert.Equal(3, (int)CovenantSchemaRepairAction.RepairOrdinaryIndex);

        Assert.Equal(1, (int)CovenantSchemaRepairPhase.Prepared);

        Assert.Equal(2, (int)CovenantSchemaRepairPhase.CatalogCommitted);

        Assert.Equal(3, (int)CovenantSchemaRepairPhase.HealthVerified);

        Assert.Equal(4, (int)CovenantSchemaRepairPhase.ReopenPending);

        Assert.Equal(5, (int)CovenantSchemaRepairPhase.Completed);

        Assert.Equal(6, (int)CovenantSchemaRepairPhase.Abandoned);

        string ddl = File.ReadAllText(
            Path.Combine(
                RepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                "Data",
                "Schema",
                "Tables",
                "covenant_schema_repair_intents.sql"));

        Assert.Contains("RepairActionCode IN (1, 2, 3)", ddl, StringComparison.Ordinal);

        Assert.Contains("PhaseCode IN (1, 2, 3, 4, 5, 6)", ddl, StringComparison.Ordinal);

    }

    private static string RepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
        {

            directory = directory.Parent;

        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root could not be located from the test base directory.");

    }

}

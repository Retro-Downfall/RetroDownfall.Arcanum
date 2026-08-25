using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #88 — the frozen Covenant error contract and its exact HTTP status mapping.
/// </summary>
/// <remarks>
/// The defect these prevent is a typed refusal that reaches an operator as an untyped 500. Three
/// codes already shipped without a mapper arm — a Campaign-binding conflict, an unresolved legacy
/// binding, and the host-process-tools transition block — and every one of them is a decision the
/// caller can act on. A 500 says "Arcanum broke"; these say "you asked for something the
/// installation will not do, and here is which one".
///
/// <para>The table is spelled out rather than derived from the mapper, because a test that asked the
/// mapper what it thought would agree with any answer it gave.</para>
/// </remarks>
public sealed class CovenantErrorContractTests
{

    /// <summary>
    /// Every code the approved contract freezes, with the exact wire string. A renamed constant is
    /// invisible to a compiler and catastrophic to a client.
    /// </summary>
    public static TheoryData<string, string> FrozenCodes =>
        new()
        {
            { ErrorCodes.Covenant.Unavailable, "Covenant.Unavailable" },
            { ErrorCodes.Covenant.InvalidScope, "Covenant.InvalidScope" },
            { ErrorCodes.Covenant.InvalidKey, "Covenant.InvalidKey" },
            { ErrorCodes.Covenant.InvalidContent, "Covenant.InvalidContent" },
            { ErrorCodes.Covenant.InvalidCursor, "Covenant.InvalidCursor" },
            { ErrorCodes.Covenant.NotFound, "Covenant.NotFound" },
            { ErrorCodes.Covenant.ArtifactErased, "Covenant.ArtifactErased" },
            { ErrorCodes.Covenant.RevisionConflict, "Covenant.RevisionConflict" },
            { ErrorCodes.Covenant.LifecycleConflict, "Covenant.LifecycleConflict" },
            { ErrorCodes.Covenant.StaleSnapshot, "Covenant.StaleSnapshot" },
            { ErrorCodes.Covenant.StaleCursor, "Covenant.StaleCursor" },
            { ErrorCodes.Covenant.CapacityExceeded, "Covenant.CapacityExceeded" },
            { ErrorCodes.Covenant.IneligibleTurn, "Covenant.IneligibleTurn" },
            { ErrorCodes.Covenant.ForbiddenAuthority, "Covenant.ForbiddenAuthority" },
            { ErrorCodes.Covenant.OperatorAuthorityUnavailable, "Covenant.OperatorAuthorityUnavailable" },
            { ErrorCodes.Covenant.SensitiveHistoryRequiresContext, "Covenant.SensitiveHistoryRequiresContext" },
            { ErrorCodes.Covenant.SensitiveEgressRequiresApproval, "Covenant.SensitiveEgressRequiresApproval" },
            { ErrorCodes.Covenant.PlaintextExportRefused, "Covenant.PlaintextExportRefused" },
            { ErrorCodes.Covenant.MaintenanceFailed, "Covenant.MaintenanceFailed" },
            { ErrorCodes.Covenant.ManualArtifactErasureRequired, "Covenant.ManualArtifactErasureRequired" },
            { ErrorCodes.Covenant.ManualRecoveryRequired, "Covenant.ManualRecoveryRequired" },
            { ErrorCodes.Covenant.ErasureIncomplete, "Covenant.ErasureIncomplete" },
            { ErrorCodes.Covenant.IntegrityFailure, "Covenant.IntegrityFailure" },
            { ErrorCodes.Covenant.CampaignBindingConflict, "Covenant.CampaignBindingConflict" },
            { ErrorCodes.Covenant.HostToolsTransitionRequired, "Covenant.HostToolsTransitionRequired" },
            { ErrorCodes.Hub.SessionTurnBusy, "Hub.SessionTurnBusy" },
            { ErrorCodes.Hub.SessionHistoryChanged, "Hub.SessionHistoryChanged" },
            { ErrorCodes.Hub.SessionTurnRestoredInterrupted, "Hub.SessionTurnRestoredInterrupted" },
            { ErrorCodes.Session.CampaignBindingRequired, "Session.CampaignBindingRequired" },
            { ErrorCodes.Campaign.PathIdentityRequired, "Campaign.PathIdentityRequired" },
        };

    /// <summary>
    /// The exact status every frozen code resolves to, from the approved error contract.
    /// </summary>
    public static TheoryData<string, int> FrozenStatuses =>
        new()
        {
            { ErrorCodes.Covenant.InvalidScope, StatusCodes.Status400BadRequest },
            { ErrorCodes.Covenant.InvalidKey, StatusCodes.Status400BadRequest },
            { ErrorCodes.Covenant.InvalidContent, StatusCodes.Status400BadRequest },
            { ErrorCodes.Covenant.InvalidCursor, StatusCodes.Status400BadRequest },
            { ErrorCodes.Validation.InvalidQuery, StatusCodes.Status400BadRequest },

            { ErrorCodes.Covenant.ForbiddenAuthority, StatusCodes.Status403Forbidden },
            { ErrorCodes.Covenant.SensitiveEgressRequiresApproval, StatusCodes.Status403Forbidden },

            // 403 rather than 409. A tainted Session is not a state a caller can retry past, and
            // "conflict" would invite exactly the retry loop that has no successful ending.
            { ErrorCodes.Covenant.PlaintextExportRefused, StatusCodes.Status403Forbidden },

            { ErrorCodes.Covenant.NotFound, StatusCodes.Status404NotFound },

            { ErrorCodes.Covenant.ArtifactErased, StatusCodes.Status410Gone },

            { ErrorCodes.Covenant.RevisionConflict, StatusCodes.Status409Conflict },
            { ErrorCodes.Covenant.LifecycleConflict, StatusCodes.Status409Conflict },
            { ErrorCodes.Covenant.StaleSnapshot, StatusCodes.Status409Conflict },
            { ErrorCodes.Covenant.StaleCursor, StatusCodes.Status409Conflict },
            { ErrorCodes.Covenant.CapacityExceeded, StatusCodes.Status409Conflict },
            { ErrorCodes.Covenant.SensitiveHistoryRequiresContext, StatusCodes.Status409Conflict },
            { ErrorCodes.Covenant.CampaignBindingConflict, StatusCodes.Status409Conflict },
            { ErrorCodes.Security.IdempotencyConflict, StatusCodes.Status409Conflict },
            { ErrorCodes.Hub.SessionTurnBusy, StatusCodes.Status409Conflict },
            { ErrorCodes.Hub.SessionHistoryChanged, StatusCodes.Status409Conflict },
            { ErrorCodes.Hub.SessionTurnRestoredInterrupted, StatusCodes.Status409Conflict },
            { ErrorCodes.Session.CampaignBindingRequired, StatusCodes.Status409Conflict },
            { ErrorCodes.Campaign.PathIdentityRequired, StatusCodes.Status409Conflict },

            { ErrorCodes.Hub.ContextBudgetExceeded, StatusCodes.Status429TooManyRequests },

            { ErrorCodes.Covenant.Unavailable, StatusCodes.Status503ServiceUnavailable },
            { ErrorCodes.Covenant.OperatorAuthorityUnavailable, StatusCodes.Status503ServiceUnavailable },
            { ErrorCodes.Covenant.HostToolsTransitionRequired, StatusCodes.Status503ServiceUnavailable },
            { ErrorCodes.Covenant.MaintenanceFailed, StatusCodes.Status503ServiceUnavailable },
            { ErrorCodes.Covenant.ManualArtifactErasureRequired, StatusCodes.Status503ServiceUnavailable },
            { ErrorCodes.Covenant.ManualRecoveryRequired, StatusCodes.Status503ServiceUnavailable },
            { ErrorCodes.Covenant.ErasureIncomplete, StatusCodes.Status503ServiceUnavailable },
            { ErrorCodes.Covenant.IntegrityFailure, StatusCodes.Status503ServiceUnavailable },
        };

    [Theory]
    [MemberData(nameof(FrozenCodes))]
    public void Every_frozen_error_code_keeps_its_exact_wire_string(string actual, string expected) =>
        Assert.Equal(expected, actual);

    [Theory]
    [MemberData(nameof(FrozenStatuses))]
    public void Every_frozen_error_code_resolves_to_its_contract_status(string code, int expected) =>
        Assert.Equal(expected, ArcanumErrorMapper.ResolveStatusCode(code));

    /// <summary>
    /// The bad-request-defaulting overload must not quietly downgrade a Covenant refusal that has a
    /// deliberate non-400 meaning, which is exactly what an unmapped code would have done.
    /// </summary>
    [Theory]
    [MemberData(nameof(FrozenStatuses))]
    public void No_frozen_covenant_status_is_downgraded_by_the_bad_request_default(string code, int expected) =>
        Assert.Equal(expected, ArcanumErrorMapper.ResolveStatusCodeDefaultBadRequest(code));

    /// <summary>
    /// <c>Covenant.IneligibleTurn</c> is MCP-only by contract: no HTTP route can produce it, because
    /// the operator API arrives with authenticated authority and never has to ask whether the turn
    /// carried a staging capability. Giving it an HTTP arm would invite a route to start returning it.
    /// </summary>
    [Fact]
    public void The_mcp_only_code_has_no_http_arm()
    {

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            ArcanumErrorMapper.ResolveStatusCode(ErrorCodes.Covenant.IneligibleTurn));

    }

}

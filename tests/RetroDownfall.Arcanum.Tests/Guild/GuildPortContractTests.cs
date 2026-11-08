using System.Reflection;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildPortContractTests
{
    [Theory]
    [InlineData("IGuildRoster")]
    [InlineData("ISignetAuthority")]
    [InlineData("IGuildQuestCoordinator")]
    [InlineData("IQuestStore")]
    [InlineData("ICircle")]
    [InlineData("IGuildPort")]
    [InlineData("IGuildCohortService")]
    [InlineData("IGuildWorkspaceLeaseService")]
    [InlineData("IGuildTreeSpendLedger")]
    public void Shared_ports_require_cancellation_and_result_flow(string name)
    {
        var type = typeof(Result).Assembly.GetType($"RetroDownfall.Arcanum.Core.Guild.{name}");

        Assert.NotNull(type);

        Assert.True(type.IsInterface);

        Assert.NotEmpty(type.GetMethods());

        foreach (var method in type.GetMethods())
        {
            Assert.EndsWith("Async", method.Name);

            Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);

            Assert.True(method.ReturnType.IsGenericType);

            Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());

            Assert.True(typeof(Result).IsAssignableFrom(method.ReturnType.GenericTypeArguments[0]));

            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType.Name is
                "GuildCallerContext" or "GuildExecutionHandle" or "GuildVerifiedAuthority" or
                "GuildAdmissionContext" or "GuildRecoveryContext" or "GuildInboxClaim" or "GuildMutationPermit");
        }
    }

    [Fact]
    public void Shared_error_family_matches_the_resolved_catalog()
    {
        var expected = new[]
        {
            "InvalidRequest", "ConvergenceInvalid", "CursorInvalid", "UnsupportedContent", "UnknownTool",
            "Forbidden", "GrantWidening", "AuthorityUnavailable", "AuthorityRevoked", "AuthorityExpired",
            "AuthorityMismatch", "ToolNotGranted", "ToolBoundaryUnavailable", "MemoryNotGranted",
            "PeerGrantRequired", "LeaseNotHeld", "NotFound", "OwnerUnavailable", "RevisionConflict",
            "IdempotencyConflict", "ResultConflict", "GrantExceeded", "PriceUnavailable",
            "BudgetCapabilityUnavailable", "DelegationCycle", "LegacyRunUnavailable", "LegacyToolName",
            "ReconciliationRequired", "LocalExecutionInterrupted", "AwaitingInput", "UnsupportedTransportState", "SequenceExhausted", "MembershipConflict",
            "CohortClosed", "NoEligibleBidder", "BidConflict", "OfferClosed", "DestinationChanged",
            "LeaseConflict", "LeaseLapsed", "WorkspaceIdentityUnavailable", "LeaseReconciliationRequired",
            "SourceConflict", "ContentErased", "CursorGone", "PayloadTooLarge", "Unavailable",
            "StoragePressure", "MaintenanceDeferred", "Rejected"
        };

        var family = typeof(ErrorCodes).GetNestedType("Guild");

        Assert.NotNull(family);

        var fields = family.GetFields(BindingFlags.Public | BindingFlags.Static);

        Assert.Equal(expected.Order(), fields.Select(field => field.Name).Order());

        Assert.All(fields, field => Assert.Equal($"Guild.{field.Name}", field.GetRawConstantValue()));

        Assert.Null(typeof(ErrorCodes).GetNestedType("Circle"));
    }
}

using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The invocation-authority boundary: which execution surface may carry Covenant authority at all,
/// and the proof that none of these values can travel over a wire.
/// </summary>
public sealed class ArcanumInvocationContextTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Campaign = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    [Fact]
    public void None_IsAuthorityFreeAndContextDisabled()
    {

        ArcanumInvocationContext context = ArcanumInvocationContext.None;

        Assert.Equal(ArcanumExecutionSurface.InternalBackground, context.Surface);
        Assert.Equal(CovenantContextPolicy.None, context.ContextPolicy);
        Assert.Equal(InvocationAttendance.Unattended, context.Attendance);
        Assert.Equal(ToolPolicy.NoTools, context.ToolPolicy);
        Assert.Null(context.Campaign);
        Assert.Null(context.ReadAuthorityEpoch);
        Assert.False(context.CanReadCovenant);
        Assert.False(context.CanStageCovenantMutation);

    }

    /// <summary>
    /// <c>CanStageCovenantMutation</c> asks only whether the policy "is not NoTools", so an undefined
    /// policy passed that gate. The wire converter refuses undefined values, but an in-process caller
    /// can still construct one, and the covenant-authority view must never be the place that treats an
    /// unrecognized restriction as permission.
    /// </summary>
    [Theory]
    [InlineData((ToolPolicy)99)]
    [InlineData((ToolPolicy)(-1))]
    public void An_undefined_tool_policy_resolves_to_no_tools(ToolPolicy undefined)
    {

        PingRequest request = new("hello", SessionId: Campaign, ToolPolicy: undefined);

        ArcanumInvocationContext context = ArcanumInvocationContexts.ForTurn(
            new DefaultHttpContext(),
            request);

        Assert.Equal(ToolPolicy.NoTools, context.ToolPolicy);

        Assert.False(context.CanStageCovenantMutation);

    }

    [Fact]
    public void Create_AttendedSessionTurnCarriesCanonicalCampaignAndEpoch()
    {

        CanonicalCampaignContext campaign = CampaignContext();

        CovenantReadAuthorityEpoch epoch = CovenantReadAuthorityEpoch.CreateForTests(Installation, 7);

        Result<ArcanumInvocationContext> result = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            campaign,
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            epoch);

        Assert.True(result.IsSuccess);
        Assert.Equal(campaign, result.Value.Campaign);
        Assert.Same(epoch, result.Value.ReadAuthorityEpoch);
        Assert.True(result.Value.CanReadCovenant);
        Assert.True(result.Value.CanStageCovenantMutation);

    }

    [Fact]
    public void Create_RejectsAuthorityForNonOperatorSurface()
    {

        Result<ArcanumInvocationContext> result = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.Subagent,
            campaign: null,
            InvocationAttendance.Unattended,
            CovenantContextPolicy.None,
            ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Installation, 7));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);

    }

    [Theory]
    [InlineData(ArcanumExecutionSurface.Subagent)]
    [InlineData(ArcanumExecutionSurface.A2A)]
    [InlineData(ArcanumExecutionSurface.Batch)]
    [InlineData(ArcanumExecutionSurface.Recovery)]
    [InlineData(ArcanumExecutionSurface.InternalBackground)]
    public void Create_UnattendedSurfacesCannotReadOrStageCovenant(ArcanumExecutionSurface surface)
    {

        Result<ArcanumInvocationContext> result = ArcanumInvocationContext.Create(
            surface,
            CampaignContext(),
            InvocationAttendance.Unattended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            readAuthorityEpoch: null);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.CanReadCovenant);
        Assert.False(result.Value.CanStageCovenantMutation);

    }

    [Fact]
    public void Create_ExplicitNoContextPolicySuppressesCovenantOnAnOperatorSurface()
    {

        Result<ArcanumInvocationContext> result = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CampaignContext(),
            InvocationAttendance.Attended,
            CovenantContextPolicy.None,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(Installation, 7));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.CanReadCovenant);
        Assert.False(result.Value.CanStageCovenantMutation);

    }

    [Fact]
    public void Create_StatelessAndInspectionSurfacesReadButNeverStage()
    {

        foreach (ArcanumExecutionSurface surface in new[]
        {
            ArcanumExecutionSurface.StatelessOperatorTurn,
            ArcanumExecutionSurface.ContextInspection,
        })
        {
            Result<ArcanumInvocationContext> result = ArcanumInvocationContext.Create(
                surface,
                CampaignContext(),
                InvocationAttendance.Attended,
                CovenantContextPolicy.Default,
                ToolPolicy.AllTools,
                CovenantReadAuthorityEpoch.CreateForTests(Installation, 7));

            Assert.True(result.IsSuccess);
            Assert.True(result.Value.CanReadCovenant);
            Assert.False(result.Value.CanStageCovenantMutation);
        }

    }

    [Fact]
    public void Create_GlobalOnlyAndUnattendedSessionTurnsCannotStage()
    {

        Result<ArcanumInvocationContext> globalOnly = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CanonicalCampaignContext.GlobalOnly,
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(Installation, 7));

        Assert.True(globalOnly.Value.CanReadCovenant);
        Assert.False(globalOnly.Value.CanStageCovenantMutation);

        Result<ArcanumInvocationContext> unattended = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CampaignContext(),
            InvocationAttendance.Unattended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(Installation, 7));

        Assert.True(unattended.Value.CanReadCovenant);
        Assert.False(unattended.Value.CanStageCovenantMutation);

        Result<ArcanumInvocationContext> toolless = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CampaignContext(),
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Installation, 7));

        Assert.True(toolless.Value.CanReadCovenant);
        Assert.False(toolless.Value.CanStageCovenantMutation);

    }

    [Fact]
    public void Create_OperatorSurfaceWithoutAnEpochCannotReadCovenant()
    {

        Result<ArcanumInvocationContext> result = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CampaignContext(),
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            readAuthorityEpoch: null);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.CanReadCovenant);
        Assert.False(result.Value.CanStageCovenantMutation);

    }

    [Fact]
    public void ReadAuthorityEpoch_IsIssuedOnlyFromACleanAuthoritySnapshot()
    {

        Result<CovenantReadAuthorityEpoch> clean = CovenantReadAuthorityEpoch.Create(
            Snapshot(CovenantHostToolsState.Clean));

        Assert.True(clean.IsSuccess);
        Assert.Equal(11, clean.Value.AuthorityEpoch);
        Assert.Equal(Installation.ToString().ToUpperInvariant(), clean.Value.InstallationIdentity);

        foreach (CovenantHostToolsState state in new[]
        {
            CovenantHostToolsState.PendingHostToolsTaint,
            CovenantHostToolsState.HostToolsTainted,
        })
        {
            Result<CovenantReadAuthorityEpoch> tainted = CovenantReadAuthorityEpoch.Create(Snapshot(state));

            Assert.False(tainted.IsSuccess);
            Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, tainted.Error.Code);
        }

    }

    [Fact]
    public void OperatorAuthorityContext_IsNonSerializableAndBindsRequirementEpochAndKeyVersion()
    {

        OperatorAuthorityContext context = OperatorAuthorityContext.CreateForTests(
            CovenantAuthorityRequirement.CovenantManage,
            Installation,
            authorityEpoch: 11,
            masterKeyVersion: 4);

        Assert.Equal(CovenantAuthorityRequirement.CovenantManage, context.Requirement);
        Assert.Equal(11, context.AuthorityEpoch);
        Assert.Equal(4u, context.MasterKeyVersion);
        Assert.Equal(Installation.ToString().ToUpperInvariant(), context.InstallationIdentity);
        Assert.NotEqual(Guid.Empty, context.IssuerNonce);

        OperatorAuthorityContext second = OperatorAuthorityContext.CreateForTests(
            CovenantAuthorityRequirement.CovenantManage,
            Installation,
            authorityEpoch: 11,
            masterKeyVersion: 4);

        Assert.NotEqual(context.IssuerNonce, second.IssuerNonce);

        AssertNonSerializable(typeof(OperatorAuthorityContext));

    }

    [Fact]
    public void AuthorityRequirement_CodesAreImmutableAndContextsCannotCrossRequirements()
    {

        Assert.Equal((byte)1, (byte)CovenantAuthorityRequirement.ProtectedRead);
        Assert.Equal((byte)2, (byte)CovenantAuthorityRequirement.CovenantManage);
        Assert.Equal((byte)3, (byte)CovenantAuthorityRequirement.CampaignPathManage);
        Assert.Equal((byte)4, (byte)CovenantAuthorityRequirement.SessionBindingResolve);
        Assert.Equal((byte)5, (byte)CovenantAuthorityRequirement.LifecycleManage);
        Assert.Equal((byte)6, (byte)CovenantAuthorityRequirement.SensitivityRetentionPurge);

        Assert.Equal(6, Enum.GetValues<CovenantAuthorityRequirement>().Length);

        OperatorAuthorityContext read = OperatorAuthorityContext.CreateForTests(
            CovenantAuthorityRequirement.ProtectedRead,
            Installation,
            authorityEpoch: 11,
            masterKeyVersion: 4);

        Assert.True(read.Satisfies(CovenantAuthorityRequirement.ProtectedRead));

        foreach (CovenantAuthorityRequirement other in Enum.GetValues<CovenantAuthorityRequirement>())
        {
            if (other == CovenantAuthorityRequirement.ProtectedRead)
            {
                continue;
            }

            Assert.False(read.Satisfies(other));
        }

    }

    [Fact]
    public void TurnAuthority_UnprotectedHasNoLeaseAndProtectedCasesRequireExactlyOneTurnLease()
    {

        CovenantTurnAuthority unprotected = CovenantTurnAuthority.Unprotected;

        Assert.Null(unprotected.Lease);
        Assert.False(unprotected.CanReadProtectedHistory);
        Assert.False(unprotected.CanAdmitCovenantContent);
        Assert.False(unprotected.CanStageCovenantMutation);

        CovenantTurnLease lease = new(new StubLeaseRegistration());

        CovenantTurnAuthority history = CovenantTurnAuthority.ForProtectedHistory(lease);

        Assert.Same(lease, history.Lease);
        Assert.True(history.CanReadProtectedHistory);
        Assert.False(history.CanAdmitCovenantContent);
        Assert.False(history.CanStageCovenantMutation);

        CovenantTurnAuthority current = CovenantTurnAuthority.ForCurrentCovenant(lease);

        Assert.Same(lease, current.Lease);
        Assert.True(current.CanReadProtectedHistory);
        Assert.True(current.CanAdmitCovenantContent);
        Assert.True(current.CanStageCovenantMutation);

        _ = Assert.Throws<ArgumentNullException>(
            () => CovenantTurnAuthority.ForProtectedHistory(null!));

        _ = Assert.Throws<ArgumentNullException>(
            () => CovenantTurnAuthority.ForCurrentCovenant(null!));

    }

    [Fact]
    public void TurnAuthority_HierarchyIsClosedToOutsideImplementations()
    {

        ConstructorInfo[] constructors = typeof(CovenantTurnAuthority)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.All(constructors, constructor => Assert.True(constructor.IsPrivate));

        Assert.All(
            typeof(CovenantTurnAuthority).Assembly
                .GetTypes()
                .Where(type => type.IsSubclassOf(typeof(CovenantTurnAuthority))),
            type => Assert.True(type.IsSealed));

    }

    [Theory]
    [InlineData(typeof(ArcanumInvocationContext))]
    [InlineData(typeof(CovenantReadAuthorityEpoch))]
    [InlineData(typeof(OperatorAuthorityContext))]
    [InlineData(typeof(CovenantTurnAuthority))]
    [InlineData(typeof(ArcanumExecutionSurface))]
    [InlineData(typeof(CovenantAuthorityRequirement))]
    public void InvocationContext_IsAbsentFromAllJsonSourceGenerationContexts(Type authorityType)
    {

        AssertNonSerializable(authorityType);

        foreach (JsonSerializerContext context in SourceGeneratedContexts())
        {
            JsonTypeInfo? typeInfo = context.GetTypeInfo(authorityType);

            Assert.True(
                typeInfo is null,
                $"{context.GetType().Name} registers the authority type {authorityType.Name}.");
        }

    }

    /// <summary>
    /// Every source-generated context reachable from the first-party assemblies, discovered rather
    /// than listed so a new context cannot quietly acquire an authority type.
    /// </summary>
    private static IEnumerable<JsonSerializerContext> SourceGeneratedContexts()
    {

        IEnumerable<Assembly> assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("RetroDownfall.", StringComparison.Ordinal) == true);

        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (!type.IsSubclassOf(typeof(JsonSerializerContext)) || type.IsAbstract)
                {
                    continue;
                }

                PropertyInfo? defaultProperty = type.GetProperty(
                    "Default",
                    BindingFlags.Public | BindingFlags.Static);

                if (defaultProperty?.GetValue(null) is JsonSerializerContext context)
                {
                    yield return context;
                }
            }
        }

    }

    private static void AssertNonSerializable(Type type)
    {

        Assert.Empty(type.GetCustomAttributes<JsonConverterAttribute>(inherit: false));

        Assert.All(
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Empty(property.GetCustomAttributes<JsonPropertyNameAttribute>(inherit: false)));

    }

    private static CanonicalCampaignContext CampaignContext() =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(Campaign),
            campaignAvailabilityGeneration: 3,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    private static CovenantAuthoritySnapshot Snapshot(CovenantHostToolsState state) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            AuthorityEpoch: 11,
            MasterKeyVersion: 4,
            RecoveryEnvelopeEpoch: 2,
            state,
            state == CovenantHostToolsState.Clean
                ? null
                : Guid.Parse("A1B2C3D4-E5F6-4708-9A0B-1C2D3E4F5061").ToString().ToUpperInvariant());

    private sealed class StubLeaseRegistration : ICovenantLeaseRegistration
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.Parse("5F6E7D8C-9B0A-4132-8455-667788990011"),
            Kind: CovenantLeaseKind.Turn,
            Coverage: CovenantLeaseCoverage.Scoped,
            Scope: CovenantOperationScope.Global,
            DatasetGeneration: Guid.Parse("0D1E2F30-4152-4637-8899-AABBCCDDEEFF"),
            CapabilityGeneration: 1,
            AuthorityEpoch: 11,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    }

}

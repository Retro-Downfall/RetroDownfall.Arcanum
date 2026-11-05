using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildCohortContractTests
{
    public static TheoryData<string> ContractNames => new()
    {
        "GuildRevisionRequest", "GuildResultContract", "GuildConvergencePolicy",
        "CreateCohortRequest", "AdmitCohortMemberRequest", "CohortDto", "CohortMemberDto",
        "CohortMemberCommissionDto", "CohortContributionDto", "CohortConvergenceDto",
        "PostWorkOfferRequest", "SubmitBidRequest", "AwardWorkRequest", "WorkOfferDto",
        "WorkBidDto", "WorkAwardDto", "AppendSharedNoteRequest", "SharedNoteDto",
        "AcquireWorkspaceLeaseRequest", "GuildPathScope", "RenewWorkspaceLeaseRequest",
        "ReleaseWorkspaceLeaseRequest", "WorkspaceLeaseDto", "CohortPageDto",
        "CohortMemberPageDto", "CohortMemberCommissionPageDto", "CohortContributionPageDto",
        "WorkOfferPageDto", "WorkBidPageDto", "SharedNotePageDto", "WorkspaceLeasePageDto",
    };

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void CohortContract_HasNamedPublicDataShape(string name)
    {
        Type? contract = typeof(Result).Assembly.GetType($"RetroDownfall.Arcanum.Core.Guild.{name}");

        Assert.NotNull(contract);

        Assert.True(contract.IsPublic);

        Assert.True(contract.IsSealed);

        Assert.NotEmpty(contract.GetProperties(BindingFlags.Public | BindingFlags.Instance));

        Assert.NotNull(GuildJsonContext.Default.GetTypeInfo(contract));
    }

    [Theory]
    [MemberData(nameof(ContractNames))]
    public void DataShape_CountersAndVersionsUseInt64(string name)
    {
        Type type = typeof(Result).Assembly.GetType($"RetroDownfall.Arcanum.Core.Guild.{name}")!;

        PropertyInfo[] counters = type.GetProperties().Where(property =>
            property.Name.EndsWith("Count", StringComparison.Ordinal)
            || property.Name.EndsWith("Sequence", StringComparison.Ordinal)
            || property.Name.EndsWith("Revision", StringComparison.Ordinal)
            || property.Name.EndsWith("Version", StringComparison.Ordinal)
            || property.Name.EndsWith("Watermark", StringComparison.Ordinal)
            || property.Name == "Epoch").ToArray();

        Assert.All(counters, property => Assert.Equal(typeof(long), Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType));
    }

    public static TheoryData<Type, string[]> EnumContracts => new()
    {
        { typeof(GuildCohortState), ["open", "closed", "draining", "completed", "failed", "canceled"] },
        { typeof(GuildCohortMemberState), ["active", "withdrawn"] },
        { typeof(GuildMemberAssociationKind), ["primary", "award-work"] },
        { typeof(GuildContributionOutcome), ["accepted", "failed", "canceled", "rejected", "withdrawn", "invalid"] },
        { typeof(GuildConvergenceKind), ["all", "first", "quorum"] },
        { typeof(GuildConvergenceDecision), ["success", "impossible", "canceled"] },
        { typeof(GuildWorkOfferState), ["open", "awarded", "withdrawn", "closed"] },
        { typeof(GuildWorkBidState), ["submitted", "withdrawn"] },
        { typeof(GuildWorkAwardState), ["committed"] },
        { typeof(GuildNoteKind), ["observation", "proposal", "correction"] },
        { typeof(GuildAuthorityKind), ["operator", "parent", "peer", "child"] },
        { typeof(GuildWorkspaceLeaseState), ["held", "draining", "released", "reconciliation-required"] },
        { typeof(GuildWorkspaceLeaseOwnerKind), ["commission", "operator"] },
        { typeof(GuildPathScopeKind), ["file", "subtree"] },
    };

    [Theory]
    [MemberData(nameof(EnumContracts))]
    public void CohortEnum_UsesExplicitClosedStringMetadata(Type type, string[] wireNames)
    {
        JsonConverterAttribute? converter = type.GetCustomAttribute<JsonConverterAttribute>();

        Assert.NotNull(converter);

        Assert.Equal(typeof(StringOnlyJsonStringEnumConverter<>).MakeGenericType(type), converter.ConverterType);

        FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);

        Assert.Equal(wireNames, fields.Select(field => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name));

        JsonTypeInfo metadata = Assert.IsAssignableFrom<JsonTypeInfo>(GuildJsonContext.Default.GetTypeInfo(type));

        for (int index = 0; index < fields.Length; index++)
        {
            object value = fields[index].GetValue(null)!;

            string json = $"\"{wireNames[index]}\"";

            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));

            Assert.Equal(value, JsonSerializer.Deserialize(json, metadata));
        }

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("1", metadata));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"unknown-future-value\"", metadata));
    }

    [Fact]
    public void TrustedAuthority_RejectsLegacyUnknownProvenance()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"legacyUnknown\"", GuildJsonContext.Default.GuildAuthorityKind));
    }

    public static TheoryData<Type, string[]> RequestProperties => new()
    {
        { typeof(CreateCohortRequest), ["IdempotencyKey", "IssuerCommissionId", "Name", "Policy", "ResultContract", "RequestedGrant"] },
        { typeof(AdmitCohortMemberRequest), ["IdempotencyKey", "ExpectedRevision", "Commission"] },
        { typeof(PostWorkOfferRequest), ["IdempotencyKey", "ExpectedCohortRevision", "SkillId", "Goal", "RequestedGrant", "ResultContract"] },
        { typeof(SubmitBidRequest), ["IdempotencyKey", "ExpectedRevision", "Note", "ClaimedCostUsd"] },
        { typeof(AwardWorkRequest), ["IdempotencyKey", "ExpectedRevision", "ExpectedCohortRevision", "BidId"] },
        { typeof(AppendSharedNoteRequest), ["IdempotencyKey", "ExpectedRevision", "Kind", "SubjectId", "Body", "CorrectsNoteId"] },
        { typeof(AcquireWorkspaceLeaseRequest), ["IdempotencyKey", "WorkspaceId", "Scopes"] },
        { typeof(RenewWorkspaceLeaseRequest), ["IdempotencyKey", "ExpectedRevision", "Epoch"] },
        { typeof(ReleaseWorkspaceLeaseRequest), ["IdempotencyKey", "ExpectedRevision", "Epoch"] },
        { typeof(GuildRevisionRequest), ["IdempotencyKey", "ExpectedRevision"] },
    };

    [Theory]
    [MemberData(nameof(RequestProperties))]
    public void MutationRequests_ExposeOnlyTheirDeclaredDataAndConcurrencyFields(Type type, string[] properties)
    {
        Assert.Equal(properties.Order(), type.GetProperties().Select(property => property.Name).Order());

        Assert.All(type.GetProperties(), property => Assert.Contains(
            typeof(System.Runtime.CompilerServices.IsExternalInit),
            property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()));
    }

    public static TheoryData<Type, Type> Pages => new()
    {
        { typeof(CohortPageDto), typeof(CohortDto) },
        { typeof(CohortMemberPageDto), typeof(CohortMemberDto) },
        { typeof(CohortMemberCommissionPageDto), typeof(CohortMemberCommissionDto) },
        { typeof(CohortContributionPageDto), typeof(CohortContributionDto) },
        { typeof(WorkOfferPageDto), typeof(WorkOfferDto) },
        { typeof(WorkBidPageDto), typeof(WorkBidDto) },
        { typeof(SharedNotePageDto), typeof(SharedNoteDto) },
        { typeof(WorkspaceLeasePageDto), typeof(WorkspaceLeaseDto) },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void Pages_ExposeImmutableNamedItemsAndLivePerPageConsistency(Type pageType, Type itemType)
    {
        Assert.Equal(
            new[] { "Items", "NextCursor", "SnapshotSequence", "StateConsistency" },
            pageType.GetProperties().Select(property => property.Name));

        Assert.Equal(typeof(ImmutableArray<>).MakeGenericType(itemType), pageType.GetProperty("Items")!.PropertyType);

        Assert.Equal(typeof(long), pageType.GetProperty("SnapshotSequence")!.PropertyType);

        Assert.Equal("livePerPage", pageType.GetConstructors().Single().GetParameters().Single(parameter => parameter.Name == "StateConsistency").DefaultValue);

        Assert.NotNull(GuildJsonContext.Default.GetTypeInfo(pageType));
    }

    [Theory]
    [InlineData(typeof(CohortDto))]
    [InlineData(typeof(CohortMemberDto))]
    [InlineData(typeof(WorkOfferDto))]
    [InlineData(typeof(WorkBidDto))]
    [InlineData(typeof(WorkAwardDto))]
    [InlineData(typeof(SharedNoteDto))]
    [InlineData(typeof(WorkspaceLeaseDto))]
    public void MutationResults_DeclareNullableReceiptForSharedReadProjection(Type type)
    {
        PropertyInfo? receipt = type.GetProperty("Receipt");

        Assert.NotNull(receipt);

        Assert.Equal(typeof(GuildMutationReceiptDto), receipt.PropertyType);

        Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(receipt).ReadState);
    }

    [Fact]
    public void WorkspaceLeasePage_RoundTripsOnlyThePublicObservationAndImmutableScopes()
    {
        WorkspaceLeaseDto lease = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            GuildWorkspaceLeaseOwnerKind.Operator,
            null,
            null,
            [new GuildPathScope("src/contracts", GuildPathScopeKind.Subtree)],
            GuildWorkspaceLeaseState.ReconciliationRequired,
            4,
            7,
            DateTimeOffset.Parse("2026-11-05T12:00:00+00:00"),
            1,
            "Guild.WorkspaceIdentityUnavailable");

        WorkspaceLeasePageDto page = new([lease], "next", 11);

        string json = JsonSerializer.Serialize(page, GuildJsonContext.Default.WorkspaceLeasePageDto);

        WorkspaceLeasePageDto restored = Assert.IsType<WorkspaceLeasePageDto>(JsonSerializer.Deserialize(json, GuildJsonContext.Default.WorkspaceLeasePageDto));

        Assert.Equal("livePerPage", restored.StateConsistency);

        Assert.Equal("next", restored.NextCursor);

        Assert.Equal(11, restored.SnapshotSequence);

        WorkspaceLeaseDto restoredLease = Assert.Single(restored.Items);

        Assert.Equal(lease.Id, restoredLease.Id);

        Assert.Equal(lease.State, restoredLease.State);

        Assert.Null(restoredLease.Receipt);

        Assert.Equal(lease.Scopes.ToArray(), restoredLease.Scopes.ToArray());

        using JsonDocument document = JsonDocument.Parse(json);

        string[] leaseFields = document.RootElement.GetProperty("items")[0].EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
            new[] { "id", "workspaceId", "ownerKind", "commissionId", "rootCommissionId", "scopes", "state", "epoch", "revision", "expiresAt", "inFlightMutationCount", "reasonCode", "receipt" },
            leaseFields);
    }
}

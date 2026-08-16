using System.Reflection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The one exhaustive artifact-deletion policy table, and the proof that it is the only one.
/// </summary>
public sealed class CovenantSensitiveArtifactPurgePolicyTests
{

    [Theory]
    [InlineData(SensitiveArtifactKind.AssistantEntry, 1)]
    [InlineData(SensitiveArtifactKind.TurnEvidence, 2)]
    [InlineData(SensitiveArtifactKind.Summary, 3)]
    [InlineData(SensitiveArtifactKind.ToolArtifact, 4)]
    [InlineData(SensitiveArtifactKind.SessionTitle, 5)]
    [InlineData(SensitiveArtifactKind.Saga, 6)]
    [InlineData(SensitiveArtifactKind.Lexicon, 7)]
    [InlineData(SensitiveArtifactKind.Embedding, 8)]
    [InlineData(SensitiveArtifactKind.SearchProjection, 9)]
    [InlineData(SensitiveArtifactKind.AuditProjection, 10)]
    [InlineData(SensitiveArtifactKind.Notification, 11)]
    [InlineData(SensitiveArtifactKind.ManagedWorkspaceFile, 12)]
    [InlineData(SensitiveArtifactKind.IdempotencyClaim, 13)]
    public void Purge_registry_has_one_literal_rule_for_every_artifact_kind(
        SensitiveArtifactKind kind,
        byte code)
    {

        Result<CovenantSensitiveArtifactPurgeRule> resolved = CovenantSensitiveArtifactPurgePolicy.Resolve(kind);

        Assert.True(resolved.IsSuccess);

        Assert.Equal(kind, resolved.Value.Kind);

        Assert.Equal(code, resolved.Value.Code);

        Assert.Equal(code, (byte)kind);

        Assert.False(string.IsNullOrWhiteSpace(resolved.Value.Policy));

    }

    [Fact]
    public void Purge_registry_is_exhaustive_over_the_thirteen_kinds_and_rejects_anything_else()
    {

        Assert.Equal(13, CovenantSensitiveArtifactPurgePolicy.All.Count);

        Assert.Equal(
            Enum.GetValues<SensitiveArtifactKind>().OrderBy(static kind => (byte)kind),
            CovenantSensitiveArtifactPurgePolicy.All.Select(static rule => rule.Kind));

        Assert.False(CovenantSensitiveArtifactPurgePolicy.IsCovered((SensitiveArtifactKind)0));

        Assert.False(CovenantSensitiveArtifactPurgePolicy.IsCovered((SensitiveArtifactKind)14));

        Result<CovenantSensitiveArtifactPurgeRule> unknown =
            CovenantSensitiveArtifactPurgePolicy.Resolve((SensitiveArtifactKind)14);

        Assert.True(unknown.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, unknown.Error.Code);

    }

    [Fact]
    public void Managed_workspace_file_is_the_only_delegated_kind_and_the_only_deferred_label_removal()
    {

        IReadOnlyList<CovenantSensitiveArtifactPurgeRule> delegated =
        [
            .. CovenantSensitiveArtifactPurgePolicy.All
                .Where(static rule => rule.Executor == CovenantArtifactPurgeExecutor.ManagedFileKernel),
        ];

        Assert.Equal(SensitiveArtifactKind.ManagedWorkspaceFile, Assert.Single(delegated).Kind);

        Assert.All(
            CovenantSensitiveArtifactPurgePolicy.All,
            static rule => Assert.Equal(
                rule.Executor == CovenantArtifactPurgeExecutor.DatabaseTransaction,
                rule.RemovesLabelWithContent));

    }

    [Fact]
    public void Only_the_assistant_entry_replaces_its_deleted_content_with_a_receipt()
    {

        IReadOnlyList<SensitiveArtifactKind> receipted =
        [
            .. CovenantSensitiveArtifactPurgePolicy.All
                .Where(static rule => rule.AppendsErasureReceipt)
                .Select(static rule => rule.Kind),
        ];

        Assert.Equal([SensitiveArtifactKind.AssistantEntry], receipted);

    }

    [Fact]
    public void Current_pointer_repair_is_exactly_the_two_session_scoped_singletons()
    {

        IReadOnlyList<SensitiveArtifactKind> pointerRepairing =
        [
            .. CovenantSensitiveArtifactPurgePolicy.All
                .Where(static rule => rule.RepairsCurrentPointer)
                .Select(static rule => rule.Kind),
        ];

        Assert.Equal(
            [SensitiveArtifactKind.Summary, SensitiveArtifactKind.SessionTitle],
            pointerRepairing.OrderBy(static kind => (byte)kind));

        Assert.All(
            CovenantSensitiveArtifactPurgePolicy.All.Where(static rule => rule.RepairsCurrentPointer),
            static rule => Assert.True(rule.RepairsSessionSensitivityState));

    }

    [Fact]
    public void Evidence_preserving_kinds_never_delete_before_their_terminal_proof_is_durable()
    {

        IReadOnlyList<SensitiveArtifactKind> guarded =
        [
            .. CovenantSensitiveArtifactPurgePolicy.All
                .Where(static rule => rule.RequiresDurableTerminalEvidenceFirst)
                .Select(static rule => rule.Kind),
        ];

        Assert.Equal(
            [SensitiveArtifactKind.TurnEvidence, SensitiveArtifactKind.IdempotencyClaim],
            guarded.OrderBy(static kind => (byte)kind));

        Assert.All(
            CovenantSensitiveArtifactPurgePolicy.All.Where(static rule => rule.PreservesReplayDenialEvidence),
            static rule => Assert.True(rule.PreservesFinalizationAndClaimEvidence));

    }

    /// <summary>
    /// A second table over the same enum is the defect this registry exists to make impossible, so the
    /// suite looks for one rather than trusting that nobody wrote it.
    /// </summary>
    /// <remarks>
    /// There is exactly one declared exception, and the assertion proves it honest rather than merely
    /// listing it. <c>CovenantArtifactPurgePlans</c> answers "where do this kind's rows live in the
    /// schema this build installs", which changes as storage lands; the registry answers "what must
    /// happen when it is deleted", which is the contract every erasure path shares. The proof that the
    /// exception is not a policy table in disguise is that its value type carries none of the policy's
    /// decisions.
    /// </remarks>
    [Fact]
    public void One_registry_owns_the_policy_and_its_single_exception_carries_no_policy()
    {

        IReadOnlyList<Type> tables =
        [
            .. typeof(CovenantSensitiveArtifactPurgePolicy).Assembly
                .GetTypes()
                .Concat(typeof(RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantStore).Assembly.GetTypes())
                .Where(static type => type != typeof(CovenantSensitiveArtifactPurgePolicy))
                .Where(static type => type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(static field => IsKindKeyedTable(field.FieldType))),
        ];

        Type exception = Assert.Single(tables);

        Assert.Equal(
            "RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantArtifactPurgePlans",
            exception.FullName);

        IReadOnlyList<string> planProperties =
        [
            .. exception
                .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Select(static field => field.FieldType)
                .Where(IsKindKeyedTable)
                .SelectMany(static field => field.GetGenericArguments()[1].GetProperties())
                .Select(static property => property.Name),
        ];

        Assert.NotEmpty(planProperties);

        IReadOnlyList<string> policyDecisions =
        [
            .. typeof(CovenantSensitiveArtifactPurgeRule)
                .GetProperties()
                .Where(static property => property.PropertyType == typeof(bool)
                    || property.PropertyType == typeof(CovenantArtifactPurgeExecutor))
                .Select(static property => property.Name),
        ];

        Assert.All(policyDecisions, decision => Assert.DoesNotContain(decision, planProperties));

    }

    private static bool IsKindKeyedTable(Type type) =>
        type.IsGenericType
        && type.GetGenericArguments().FirstOrDefault() == typeof(SensitiveArtifactKind)
        && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

}

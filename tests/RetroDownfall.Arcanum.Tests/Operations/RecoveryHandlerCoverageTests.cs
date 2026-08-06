using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #40, requirement 1 and its acceptance criterion: "the registry has no implemented
/// long-running operation kind without an explicit policy" — and none without an owning handler.
/// </summary>
/// <remarks>
/// This asserts against the real <see cref="IServiceCollection"/> registrations rather than a built
/// provider, so it catches both halves of the failure mode (a kind nobody handles, and a handler
/// that exists but was never registered) without constructing a Grimoire.
/// </remarks>
public sealed class RecoveryHandlerCoverageTests
{
    /// <summary>
    /// Kind → the type that owns its recovery. Deliberately spelled out: adding a kind to
    /// <see cref="LongRunningOperationKinds"/> without deciding who recovers it should fail here,
    /// at the point the decision is missing.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> ExpectedHandlers =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [LongRunningOperationKinds.InferenceRun] = typeof(InferenceRunRecoveryHandler),
            [LongRunningOperationKinds.Subagent] = typeof(SubagentRecoveryHandler),
            [LongRunningOperationKinds.BudgetReservation] = typeof(BudgetReservationRecoveryHandler),
            [LongRunningOperationKinds.Batch] = typeof(BatchOperationRecoveryHandler),
            [LongRunningOperationKinds.Apprentice] = typeof(ApprenticeRecoveryHandler),
            [LongRunningOperationKinds.AttachmentPromotion] = typeof(AttachmentPromotionRecoveryHandler),
            [LongRunningOperationKinds.WorkspaceIndex] = typeof(WorkspaceIndexRecoveryHandler),
            [LongRunningOperationKinds.IdempotencyClaim] = typeof(IdempotencyClaimRecoveryHandler),
            [LongRunningOperationKinds.BlobEncryptionMigration] = typeof(BlobEncryptionMigrationRecoveryHandler),
            [LongRunningOperationKinds.BlobEncryptionKeyRotation] = typeof(BlobEncryptionKeyRotationRecoveryHandler),
            [LongRunningOperationKinds.BackupCreate] = typeof(BackupCreateRecoveryHandler),
            [LongRunningOperationKinds.DataRetentionPrune] = typeof(DataRetentionRecoveryHandler),
            [LongRunningOperationKinds.DataRetentionMutation] = typeof(DataRetentionMutationRecoveryHandler),
            [LongRunningOperationKinds.DataRetentionFactoryReset] = typeof(DataRetentionFactoryResetRecoveryHandler),
            [LongRunningOperationKinds.A2AInboundSending] = typeof(A2AInboundSendingRecoveryHandler),
            [LongRunningOperationKinds.A2AOutboundSending] = typeof(A2AOutboundSendingRecoveryHandler),
        };

    private static IReadOnlyList<Type> RegisteredHandlerTypes()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = [];

        // AddArcanumApiServices composes Infrastructure itself; this is the serve host's real graph.
        _ = services.AddArcanumApiServices(configuration);

        return
        [
            .. services
                .Where(static descriptor => descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler))
                .Select(static descriptor => descriptor.ImplementationType)
                .OfType<Type>(),
        ];
    }

    [Fact]
    public void Every_registered_kind_has_a_named_owning_handler()
    {
        string[] unowned =
        [
            .. LongRunningOperationRecoveryRegistry.Descriptors.Keys
                .Where(static kind => !ExpectedHandlers.ContainsKey(kind)),
        ];

        Assert.Empty(unowned);
    }

    [Fact]
    public void Every_owning_handler_is_wired_into_the_real_container()
    {
        IReadOnlyList<Type> registered = RegisteredHandlerTypes();

        Type[] missing =
        [
            .. ExpectedHandlers.Values.Where(handler => !registered.Contains(handler)),
        ];

        Assert.Empty(missing);
    }

    /// <summary>
    /// A handler registered twice would run recovery twice per pass; the reconciler's kind-keyed
    /// dictionary would also throw on construction.
    /// </summary>
    [Fact]
    public void No_recovery_handler_is_registered_more_than_once()
    {
        IReadOnlyList<Type> registered = RegisteredHandlerTypes();

        Type[] duplicates =
        [
            .. registered.GroupBy(static handler => handler)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key),
        ];

        Assert.Empty(duplicates);
    }
}

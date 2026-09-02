using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
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
            [LongRunningOperationKinds.CovenantIndexRebuild] = typeof(CovenantIndexRebuildRecoveryHandler),
            [LongRunningOperationKinds.CovenantFamilyReinitialize] =
                typeof(CovenantFamilyReinitializeRecoveryHandler),
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

    /// <summary>
    /// Every registered <see cref="ILongRunningOperationRecoveryHandler"/> descriptor beyond one per
    /// expected kind, closed-generic or factory alike. A factory registration's
    /// <see cref="ServiceDescriptor.ImplementationType"/> is null, so grouping by that property — the
    /// way <see cref="RegisteredHandlerTypes"/> projects it — silently drops a factory-registered
    /// duplicate from the inventory; counting descriptors for the service type instead is what keeps
    /// one visible regardless of how it was registered.
    /// </summary>
    private static string[] DuplicateHandlerRegistrations(IServiceCollection services)
    {
        int registeredDescriptors = services.Count(
            static descriptor => descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler));

        return registeredDescriptors > ExpectedHandlers.Count
            ? [$"{registeredDescriptors} handler descriptors are registered for {ExpectedHandlers.Count} expected kinds."]
            : [];
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
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = [];
        _ = services.AddArcanumApiServices(configuration);

        Assert.Empty(DuplicateHandlerRegistrations(services));
    }

    /// <summary>
    /// <see cref="ServiceDescriptor.ImplementationType"/> is null for a factory registration, so
    /// grouping by that property — as a naive duplicate guard would — drops it from the inventory
    /// entirely: a handler that already has a closed-generic registration and gains a second,
    /// factory-based one looks like a single registration. <see cref="DuplicateHandlerRegistrations"/>
    /// counts descriptors instead, so it still catches this one.
    /// </summary>
    [Fact]
    public void A_factory_registered_duplicate_of_an_existing_handler_is_flagged()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = [];
        _ = services.AddArcanumApiServices(configuration);

        // Stands in for an existing handler (e.g. BatchOperationRecoveryHandler, already registered by
        // closed generic above) gaining a second registration through an implementation factory; the
        // probe never builds a provider, so the factory delegate itself is never invoked.
        services.AddScoped<ILongRunningOperationRecoveryHandler>(
            static _ => throw new NotSupportedException("This probe registration is never resolved."));

        Assert.NotEmpty(DuplicateHandlerRegistrations(services));
    }
}

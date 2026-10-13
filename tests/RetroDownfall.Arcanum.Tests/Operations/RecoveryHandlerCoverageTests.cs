using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Support;

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
    private static readonly Type[] SelfSettlingHandlers =
    [
        typeof(DataRetentionMutationRecoveryHandler),
        typeof(DataRetentionFactoryResetRecoveryHandler),
    ];

    /// <summary>
    /// Kind → the type that owns its recovery. Deliberately spelled out: adding a kind to
    /// <see cref="LongRunningOperationKinds"/> without deciding who recovers it should fail here,
    /// at the point the decision is missing.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> ExpectedHandlers =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [LongRunningOperationKinds.LongRestSweep] = typeof(LongRestSweepRecoveryHandler),
            [LongRunningOperationKinds.Subagent] = typeof(SubagentRecoveryHandler),
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

    /// <summary>
    /// Kind → the production source file that creates its ledger rows. A descriptor and handler for a
    /// kind nothing creates is a recovery path for work that is never recorded, and a guard that reads
    /// such a kind protects nothing, so a kind is registered only together with the place that produces it.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ProductionProducers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LongRunningOperationKinds.LongRestSweep] =
                "src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRest/LongRestSweepCoordinator.cs",
            [LongRunningOperationKinds.Subagent] =
                "src/RetroDownfall.Arcanum.Api/Intelligence/Subagents/SubagentRunner.cs",
            [LongRunningOperationKinds.BlobEncryptionMigration] =
                "src/RetroDownfall.Arcanum.Infrastructure/Storage/BlobEncryptionLifecycleService.cs",
            [LongRunningOperationKinds.BlobEncryptionKeyRotation] =
                "src/RetroDownfall.Arcanum.Infrastructure/Storage/BlobEncryptionLifecycleService.cs",
            [LongRunningOperationKinds.BackupCreate] =
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupService.cs",
            [LongRunningOperationKinds.DataRetentionPrune] =
                "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs",
            [LongRunningOperationKinds.DataRetentionMutation] =
                "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs",
            [LongRunningOperationKinds.DataRetentionFactoryReset] =
                "src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.FactoryReset.cs",
            [LongRunningOperationKinds.CovenantIndexRebuild] =
                "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantIndexRebuildCoordinator.cs",
            [LongRunningOperationKinds.CovenantFamilyReinitialize] =
                "src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantFamilyReinitializeCoordinator.cs",
            [LongRunningOperationKinds.A2AInboundSending] =
                "src/RetroDownfall.Arcanum.Infrastructure/A2A/A2ASendingLedger.cs",
            [LongRunningOperationKinds.A2AOutboundSending] =
                "src/RetroDownfall.Arcanum.Infrastructure/A2A/A2ASendingLedger.cs",
        };

    [Fact]
    public void Every_registry_kind_has_a_production_producer()
    {
        string[] unproduced =
        [
            .. LongRunningOperationRecoveryRegistry.Descriptors.Keys
                .Where(static kind => !ProductionProducers.ContainsKey(kind))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Empty(unproduced);
    }

    /// <summary>
    /// Keeps the allow-list honest: each named producer must still name its kind, so a producer that
    /// stops creating a kind cannot leave the descriptor behind on the strength of a stale entry.
    /// </summary>
    [Fact]
    public void Every_named_producer_still_names_its_kind()
    {
        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        string[] stale =
        [
            .. ProductionProducers
                .Where(pair => !sources.Any(source =>
                    source.IsExactOwner(pair.Value)
                    && source.Names($"LongRunningOperationKinds.{KindConstantName(pair.Key)}")))
                .Select(static pair => $"{pair.Key} -> {pair.Value}")
                .Order(StringComparer.Ordinal),
        ];

        Assert.Empty(stale);
    }

    private static string KindConstantName(string kind) =>
        typeof(LongRunningOperationKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Single(field => field.IsLiteral && string.Equals((string?)field.GetRawConstantValue(), kind, StringComparison.Ordinal))
            .Name;

    private static IReadOnlyList<Type> RegisteredHandlerTypes()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = [];

        // AddArcanumApiServices composes Infrastructure itself; this is the serve host's real graph.
        _ = services.AddArcanumApiServices(configuration);

        Type[] direct = services
            .Where(static descriptor =>
                descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler))
            .Select(static descriptor => descriptor.ImplementationType)
            .OfType<Type>()
            .ToArray();

        return [.. direct, .. SelfSettlingHandlers];
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

    [Fact]
    public void Owner_bound_handlers_are_one_scoped_instance_aliased_to_both_contracts()
    {
        ServiceCollection services = RealServices();

        Assert.Empty(OwnerBoundAliasFailures(services));
    }

    [Fact]
    public void A_missing_authenticated_alias_is_rejected()
    {
        ServiceCollection services = RealServices();

        services.RemoveAll<IAuthenticatedCovenantErasureRecoveryHandler>();

        Assert.NotEmpty(OwnerBoundAliasFailures(services));
    }

    [Fact]
    public void A_separate_authenticated_handler_instance_is_rejected()
    {
        ServiceCollection services = RealServices();

        services.RemoveAll<IAuthenticatedCovenantErasureRecoveryHandler>();

        services.AddScoped<IAuthenticatedCovenantErasureRecoveryHandler>(static sp =>
            ActivatorUtilities.CreateInstance<DataRetentionMutationRecoveryHandler>(sp));

        services.AddScoped<IAuthenticatedCovenantErasureRecoveryHandler>(static sp =>
            ActivatorUtilities.CreateInstance<DataRetentionFactoryResetRecoveryHandler>(sp));

        Assert.NotEmpty(OwnerBoundAliasFailures(services));
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

        // Stands in for an existing handler (e.g. SubagentRecoveryHandler, already registered by
        // closed generic above) gaining a second registration through an implementation factory; the
        // probe never builds a provider, so the factory delegate itself is never invoked.
        services.AddScoped<ILongRunningOperationRecoveryHandler>(
            static _ => throw new NotSupportedException("This probe registration is never resolved."));

        Assert.NotEmpty(DuplicateHandlerRegistrations(services));
    }

    /// <summary>
    /// The recovery registry is unkeyed. A keyed descriptor for the handler contract, for a concrete
    /// handler, or whose implementation is a handler would give the same handler a second door that the
    /// reconciler's <c>GetServices</c> set never sees, so the serving container carries none.
    /// </summary>
    [Fact]
    public void Recovery_registry_has_no_keyed_handler_descriptors()
    {
        Assert.Empty(KeyedHandlerDescriptors(RealServices()));
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("contract-implementation")]
    [InlineData("contract-factory")]
    [InlineData("concrete-handler")]
    [InlineData("authenticated-contract")]
    [InlineData("handler-enumerable")]
    [InlineData("object-keyed-by-handler-implementation")]
    public void A_keyed_handler_descriptor_of_any_shape_is_flagged(string shape)
    {
        ServiceCollection services = RealServices();

        switch (shape)
        {
            case "contract":
                services.AddKeyedScoped<ILongRunningOperationRecoveryHandler, BackupCreateRecoveryHandler>(
                    LongRunningOperationKinds.BackupCreate);

                break;

            case "contract-implementation":
                services.AddKeyedScoped(
                    typeof(ILongRunningOperationRecoveryHandler),
                    LongRunningOperationKinds.BackupCreate,
                    typeof(BackupCreateRecoveryHandler));

                break;

            case "contract-factory":
                services.AddKeyedScoped<ILongRunningOperationRecoveryHandler>(
                    LongRunningOperationKinds.BackupCreate,
                    static (_, _) => throw new NotSupportedException("Descriptor-only control."));

                break;

            case "concrete-handler":
                services.AddKeyedScoped<BackupCreateRecoveryHandler>(LongRunningOperationKinds.BackupCreate);

                break;

            case "authenticated-contract":
                services.AddKeyedScoped<IAuthenticatedCovenantErasureRecoveryHandler>(
                    LongRunningOperationKinds.DataRetentionMutation,
                    static (_, _) => throw new NotSupportedException("Descriptor-only control."));

                break;

            case "handler-enumerable":
                services.AddKeyedScoped<IEnumerable<ILongRunningOperationRecoveryHandler>>(
                    LongRunningOperationKinds.BackupCreate,
                    static (_, _) => throw new NotSupportedException("Descriptor-only control."));

                break;

            default:
                services.AddKeyedScoped<object, BackupCreateRecoveryHandler>(LongRunningOperationKinds.BackupCreate);

                break;
        }

        Assert.Single(KeyedHandlerDescriptors(services));
    }

    /// <summary>
    /// Each owning handler is reachable through exactly one unkeyed registration, and that registration
    /// is its own: a plain scoped <c>ILongRunningOperationRecoveryHandler</c> descriptor naming the
    /// handler type, with no second concrete door. The two owner-bound Covenant handlers keep the one
    /// documented shape, a scoped concrete registration with one alias per contract.
    /// </summary>
    [Fact]
    public void Every_serving_handler_has_exactly_one_unkeyed_registration()
    {
        Assert.Empty(UnkeyedRegistrationFailures(RealServices()));
    }

    [Fact]
    public void Repeated_shared_backup_composition_keeps_exactly_one_unkeyed_registration()
    {
        ServiceCollection services = [];

        services.AddArcanumBackup();

        services.AddArcanumBackup();

        ServiceDescriptor registration = Assert.Single(
            services,
            static descriptor => descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler));

        Assert.False(registration.IsKeyedService);

        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);

        Assert.Equal(typeof(BackupCreateRecoveryHandler), registration.ImplementationType);

        Assert.DoesNotContain(services, static descriptor => descriptor.ServiceType == typeof(BackupCreateRecoveryHandler));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("duplicate-factory")]
    [InlineData("extra-concrete-door")]
    [InlineData("keyed-only")]
    [InlineData("factory-alias-of-plain-handler")]
    [InlineData("singleton-lifetime")]
    [InlineData("factory-only")]
    [InlineData("instance-only")]
    [InlineData("owner-bound-plain")]
    public void A_handler_without_exactly_one_plain_unkeyed_registration_is_flagged(string shape)
    {
        ServiceCollection services = RealServices();

        Assert.Empty(UnkeyedRegistrationFailures(services));

        int expectedDescriptorCount = services.Count;

        ServiceDescriptor subagent = Assert.Single(
            services,
            static descriptor => !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler)
                && descriptor.ImplementationType == typeof(SubagentRecoveryHandler));

        switch (shape)
        {
            case "missing":
                services.Remove(subagent);

                break;

            case "owner-bound-plain":
                // The descriptor count is unchanged, so only the owner-bound alias branch can flag this.
                ReplaceOwnerBoundAliasWithPlainRegistration(services);

                Assert.Equal(expectedDescriptorCount, services.Count);

                Assert.Contains(
                    "has 0 unkeyed aliases of its concrete instance",
                    Assert.Single(UnkeyedRegistrationFailures(services)),
                    StringComparison.Ordinal);

                break;

            case "duplicate":
                services.AddScoped<ILongRunningOperationRecoveryHandler, SubagentRecoveryHandler>();

                break;

            case "duplicate-factory":
                services.AddScoped<ILongRunningOperationRecoveryHandler>(
                    static _ => throw new NotSupportedException("This probe registration is never resolved."));

                break;

            case "extra-concrete-door":
                services.AddScoped<SubagentRecoveryHandler>();

                break;

            case "factory-only":
                // A factory or an instance descriptor has no ImplementationType, so it can never be the
                // handler's own plain registration; the descriptor count stays at one per kind.
                services.Remove(subagent);

                services.AddScoped<ILongRunningOperationRecoveryHandler>(
                    static _ => throw new NotSupportedException("This probe registration is never resolved."));

                Assert.Equal(expectedDescriptorCount, services.Count);

                break;

            case "instance-only":
                services.Remove(subagent);

                services.AddSingleton<ILongRunningOperationRecoveryHandler>(
                    (SubagentRecoveryHandler)RuntimeHelpers.GetUninitializedObject(typeof(SubagentRecoveryHandler)));

                Assert.Equal(expectedDescriptorCount, services.Count);

                break;

            case "keyed-only":
                services.Remove(subagent);

                services.AddKeyedScoped<ILongRunningOperationRecoveryHandler, SubagentRecoveryHandler>(
                    LongRunningOperationKinds.Subagent);

                break;

            case "factory-alias-of-plain-handler":
                services.Remove(subagent);

                services.AddScoped<SubagentRecoveryHandler>();

                services.AddScoped<ILongRunningOperationRecoveryHandler>(
                    static provider => provider.GetRequiredService<SubagentRecoveryHandler>());

                break;

            default:
                services.Remove(subagent);

                services.AddSingleton<ILongRunningOperationRecoveryHandler, SubagentRecoveryHandler>();

                break;
        }

        Assert.NotEmpty(UnkeyedRegistrationFailures(services));
    }

    /// <summary>
    /// Swaps the owner-bound mutation handler's factory alias for a plain
    /// <c>AddScoped&lt;IH, DataRetentionMutationRecoveryHandler&gt;()</c> registration, which keeps the
    /// unkeyed handler descriptor count at one per kind while taking away the alias of the shared
    /// concrete instance.
    /// </summary>
    private static void ReplaceOwnerBoundAliasWithPlainRegistration(IServiceCollection services)
    {
        object concrete = RuntimeHelpers.GetUninitializedObject(typeof(DataRetentionMutationRecoveryHandler));

        AliasProbeProvider probe = new(typeof(DataRetentionMutationRecoveryHandler), concrete);

        ServiceDescriptor alias = Assert.Single(
            services,
            descriptor => !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler)
                && ResolvesProbedConcrete(descriptor, probe, concrete));

        services.Remove(alias);

        services.AddScoped<ILongRunningOperationRecoveryHandler, DataRetentionMutationRecoveryHandler>();
    }

    private static string[] KeyedHandlerDescriptors(IServiceCollection services) =>
    [
        .. services
            .Where(static descriptor => descriptor.IsKeyedService
                && (IsHandlerContractOrImplementation(descriptor.ServiceType)
                    || IsHandlerContractOrImplementation(descriptor.KeyedImplementationType)))
            .Select(static descriptor =>
                $"{descriptor.ServiceType.Name} keyed by '{descriptor.ServiceKey}'"),
    ];

    /// <remarks>
    /// <see cref="IAuthenticatedCovenantErasureRecoveryHandler"/> extends the handler contract, so
    /// the first clause already covers it and the <c>authenticated-contract</c> twin pins that: it turns
    /// red if the interface ever stops extending the contract. The enumerable is the one handler-shaped
    /// service type that is not assignable to the contract.
    /// </remarks>
    private static bool IsHandlerContractOrImplementation(Type? type) =>
        type is not null
        && (typeof(ILongRunningOperationRecoveryHandler).IsAssignableFrom(type)
            || type == typeof(IEnumerable<ILongRunningOperationRecoveryHandler>));

    private static bool ResolvesProbedConcrete(ServiceDescriptor descriptor, IServiceProvider probe, object concrete)
    {
        if (descriptor.Lifetime != ServiceLifetime.Scoped || descriptor.ImplementationFactory is null)
        {
            return false;
        }

        try
        {
            return ReferenceEquals(concrete, descriptor.ImplementationFactory(probe));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string[] UnkeyedRegistrationFailures(IServiceCollection services)
    {
        ServiceDescriptor[] unkeyed =
        [
            .. services.Where(static descriptor => !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler)),
        ];

        List<string> failures = [];

        if (unkeyed.Length != ExpectedHandlers.Count)
        {
            failures.Add($"{unkeyed.Length} unkeyed handler descriptors are registered for {ExpectedHandlers.Count} expected kinds.");
        }

        foreach ((string kind, Type handlerType) in ExpectedHandlers)
        {
            if (Array.IndexOf(SelfSettlingHandlers, handlerType) >= 0)
            {
                object concrete = RuntimeHelpers.GetUninitializedObject(handlerType);

                AliasProbeProvider probe = new(handlerType, concrete);

                int aliases = unkeyed.Count(descriptor => ResolvesProbedConcrete(descriptor, probe, concrete));

                if (aliases != 1)
                {
                    failures.Add($"{kind}: {handlerType.Name} has {aliases} unkeyed aliases of its concrete instance, not one.");
                }

                continue;
            }

            ServiceDescriptor[] own =
            [
                .. unkeyed.Where(descriptor => descriptor.ImplementationType == handlerType),
            ];

            // A factory or instance descriptor has no ImplementationType, so it never matches `own`: it
            // surfaces as own.Length == 0 here (factory-only, instance-only) or as an extra descriptor in
            // the count check above (duplicate-factory). Only the lifetime is left to check on a match.
            if (own.Length != 1 || own[0].Lifetime != ServiceLifetime.Scoped)
            {
                failures.Add($"{kind}: {handlerType.Name} needs exactly one plain scoped unkeyed handler registration, found {own.Length}.");
            }

            if (services.Any(descriptor => descriptor.ServiceType == handlerType))
            {
                failures.Add($"{kind}: {handlerType.Name} must not also be registered as its own service type.");
            }
        }

        return [.. failures];
    }

    private static ServiceCollection RealServices()
    {
        ServiceCollection services = [];

        _ = services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        return services;
    }

    private static string[] OwnerBoundAliasFailures(ServiceCollection services)
    {
        List<string> failures = [];

        foreach (Type handlerType in SelfSettlingHandlers)
        {
            ServiceDescriptor[] concrete = services
                .Where(descriptor => descriptor.ServiceType == handlerType)
                .ToArray();

            if (concrete.Length != 1
                || concrete[0].Lifetime != ServiceLifetime.Scoped
                || concrete[0].ImplementationType != handlerType)
            {
                failures.Add($"{handlerType.Name} must have exactly one scoped concrete descriptor.");
            }
        }

        foreach (Type handlerType in SelfSettlingHandlers)
        {
            object concrete = RuntimeHelpers.GetUninitializedObject(handlerType);

            AliasProbeProvider probe = new(handlerType, concrete);

            object[] normalMatches = InvokeAliases(
                services,
                typeof(ILongRunningOperationRecoveryHandler),
                probe);

            object[] authenticatedMatches = InvokeAliases(
                services,
                typeof(IAuthenticatedCovenantErasureRecoveryHandler),
                probe);

            if (normalMatches.Length != 1 || !ReferenceEquals(concrete, normalMatches[0]))
            {
                failures.Add($"{handlerType.Name} has no exact normal-handler alias.");
            }

            if (authenticatedMatches.Length != 1
                || !ReferenceEquals(concrete, authenticatedMatches[0]))
            {
                failures.Add($"{handlerType.Name} has no exact authenticated-handler alias.");
            }
        }

        return failures.ToArray();
    }

    private static object[] InvokeAliases(
        ServiceCollection services,
        Type serviceType,
        IServiceProvider probe) =>
        services
            .Where(descriptor => descriptor.ServiceType == serviceType
                && descriptor.Lifetime == ServiceLifetime.Scoped
                && descriptor.ImplementationFactory is not null)
            .Select(descriptor =>
            {
                try
                {
                    return descriptor.ImplementationFactory!(probe);
                }
                catch (Exception)
                {
                    return null;
                }
            })
            .OfType<object>()
            .ToArray();

    private sealed class AliasProbeProvider(Type handlerType, object handler) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == handlerType ? handler : null;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class RecoveryHandlerCoverageTests
{
    [Fact]
    public void Every_serving_handler_has_one_concrete_instance_with_exact_unkeyed_and_kind_aliases()
    {
        ServiceCollection services = RealServices();

        Assert.Empty(RecoveryAliasFailures(services, ExpectedHandlers));

        // Authenticated aliases remain attached to those same concrete instances too.
        Assert.Empty(OwnerBoundAliasFailures(services));
    }

    [Fact]
    public void Repeated_shared_backup_composition_preserves_one_concrete_and_both_aliases()
    {
        ServiceCollection services = [];

        services.AddArcanumBackup();

        services.AddArcanumBackup();

        IReadOnlyDictionary<string, Type> backup = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [LongRunningOperationKinds.BackupCreate] = typeof(BackupCreateRecoveryHandler),
        };

        Assert.Empty(RecoveryAliasFailures(services, backup));

        Assert.Equal(2, services.Count(static descriptor =>
            descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler)));
    }

    [Fact]
    public void A_keyed_implementation_descriptor_does_not_count_as_an_unkeyed_handler_registration()
    {
        ServiceCollection services = [];

        services.AddKeyedScoped<ILongRunningOperationRecoveryHandler, BackupCreateRecoveryHandler>(
            LongRunningOperationKinds.BackupCreate);

        // Also rules out crediting an unused concrete descriptor without an actual unkeyed alias.
        services.AddScoped<BackupCreateRecoveryHandler>();

        Assert.Empty(RegisteredHandlerTypes(services));
    }

    [Fact]
    public void A_keyed_factory_duplicate_is_counted_even_without_an_implementation_type()
    {
        ServiceCollection services = [];

        services.AddKeyedScoped<ILongRunningOperationRecoveryHandler>(
            LongRunningOperationKinds.BackupCreate,
            static (_, _) => throw new NotSupportedException("Descriptor-only control."));

        services.AddKeyedScoped<ILongRunningOperationRecoveryHandler>(
            LongRunningOperationKinds.BackupCreate,
            static (_, _) => throw new NotSupportedException("Descriptor-only control."));

        string failure = Assert.Single(DuplicateHandlerRegistrations(services));

        Assert.Contains(LongRunningOperationKinds.BackupCreate, failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing-key")]
    [InlineData("duplicate-key")]
    [InlineData("wrong-kind")]
    [InlineData("separate-instance")]
    [InlineData("duplicate-outer")]
    [InlineData("missing-outer")]
    public void Serving_reconciler_factory_validates_aliases_after_preserving_outer_kind_validation(string shape)
    {
        ServiceDescriptor production = Assert.Single(
            RealServices(),
            static descriptor => !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(LongRunningOperationReconciler));

        Assert.Equal(ServiceLifetime.Scoped, production.Lifetime);

        Assert.NotNull(production.ImplementationFactory);

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        RecordingRecoveryHandler[] handlers =
        [
            .. ExpectedHandlers.Keys.Select(kind => new RecordingRecoveryHandler(
                kind, LongRunningOperationRecoveryRegistry.Descriptors[kind].MaxCheckpointVersion)),
        ];

        RecordingRecoveryHandler selected = Assert.Single(handlers, static handler =>
            handler.Kind == LongRunningOperationKinds.LongRestSweep);

        RecordingRecoveryHandler foreign = Assert.Single(handlers, static handler =>
            handler.Kind == LongRunningOperationKinds.BlobEncryptionMigration);

        List<string> keyedRequests = [];

        IServiceCollection services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(time);

        services.AddSingleton<ILogger<LongRunningOperationReconciler>>(NullLogger<LongRunningOperationReconciler>.Instance);

        services.AddSingleton(new LongRunningOperationOwnership());

        services.AddScoped<ILongRunningOperationStore>(_ => store);

        services.AddScoped<ILongRunningOperationGenericRecoveryDiscovery>(_ => store);

        services.AddScoped<ILongRunningOperationClassifiedRecoveryLeaseAcquisition>(_ => store);

        foreach (RecordingRecoveryHandler handler in handlers)
        {
            if (shape != "missing-outer" || !ReferenceEquals(handler, selected))
            {
                services.AddScoped<ILongRunningOperationRecoveryHandler>(_ => handler);
            }

            if (ReferenceEquals(handler, selected) && (shape is "missing-key" or "missing-outer"))
            {
                continue;
            }

            services.AddKeyedScoped<ILongRunningOperationRecoveryHandler>(handler.Kind, (_, key) =>
            {
                keyedRequests.Add(Assert.IsType<string>(key));

                if (ReferenceEquals(handler, selected))
                {
                    return shape switch
                    {
                        "wrong-kind" => foreign,
                        "separate-instance" => new RecordingRecoveryHandler(handler.Kind, handler.SupportedCheckpointVersion),
                        _ => handler,
                    };
                }

                return handler;
            });
        }

        if (shape == "duplicate-key")
        {
            // The duplicate even returns the SAME object: de-duplicating by reference is insufficient.
            services.AddKeyedScoped<ILongRunningOperationRecoveryHandler>(selected.Kind, (_, key) =>
            {
                keyedRequests.Add(Assert.IsType<string>(key));

                return selected;
            });
        }

        if (shape == "duplicate-outer")
        {
            services.AddScoped<ILongRunningOperationRecoveryHandler>(_ => selected);
        }

        // Execute the real serving factory, not a test copy of its proposed validation logic.
        services.Add(production);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using IServiceScope scope = provider.CreateScope();

        if (shape == "duplicate-outer")
        {
            Assert.Throws<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<LongRunningOperationReconciler>());

            Assert.Empty(keyedRequests);
        }
        else if (shape is "valid" or "missing-outer")
        {
            LongRunningOperationReconciler reconciler = scope.ServiceProvider.GetRequiredService<LongRunningOperationReconciler>();

            if (shape == "missing-outer")
            {
                Assert.Equal(selected.Kind, Assert.Single(reconciler.MissingHandlerKinds));
            }
            else
            {
                Assert.Empty(reconciler.MissingHandlerKinds);
            }

            string[] expectedKeys = handlers
                .Where(handler => shape != "missing-outer" || !ReferenceEquals(handler, selected))
                .Select(static handler => handler.Kind)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedKeys, keyedRequests.Order(StringComparer.Ordinal).ToArray());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                scope.ServiceProvider.GetRequiredService<LongRunningOperationReconciler>());
        }

        Assert.All(handlers, handler => Assert.Empty(handler.Invocations));

        Assert.Empty(store.LeaseAcquisitions);
    }

    private static bool HasSingleScopedConcreteHandler(ServiceCollection services, Type handlerType)
    {
        ServiceDescriptor[] concrete = services
            .Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == handlerType)
            .ToArray();

        return concrete.Length == 1
            && concrete[0].Lifetime == ServiceLifetime.Scoped
            && concrete[0].ImplementationType == handlerType;
    }

    private static string[] RecoveryAliasFailures(
        ServiceCollection services,
        IReadOnlyDictionary<string, Type> expected)
    {
        List<string> failures = [];

        foreach ((string kind, Type handlerType) in expected)
        {
            if (!HasSingleScopedConcreteHandler(services, handlerType))
            {
                failures.Add($"{kind} must own exactly one scoped concrete {handlerType.Name}.");
            }

            object concrete = RuntimeHelpers.GetUninitializedObject(handlerType);

            AliasProbeProvider probe = new(handlerType, concrete);

            object[] unkeyed = InvokeAliases(services, typeof(ILongRunningOperationRecoveryHandler), probe);

            if (unkeyed.Length != 1 || !ReferenceEquals(concrete, unkeyed[0]))
            {
                failures.Add($"{kind} must have exactly one unkeyed alias to its concrete instance.");
            }

            ServiceDescriptor[] keyed = services
                .Where(descriptor => descriptor.IsKeyedService
                    && descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler)
                    && Equals(descriptor.ServiceKey, kind))
                .ToArray();

            if (keyed.Length != 1
                || keyed[0].Lifetime != ServiceLifetime.Scoped
                || keyed[0].KeyedImplementationFactory is not { } factory)
            {
                failures.Add($"{kind} must have exactly one scoped keyed factory alias.");

                continue;
            }

            try
            {
                if (!ReferenceEquals(concrete, factory(probe, kind)))
                {
                    failures.Add($"{kind} keyed alias returned a different instance.");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{kind} keyed alias did not resolve its concrete instance: {exception.GetType().Name}.");
            }
        }

        foreach (ServiceDescriptor keyed in services.Where(static descriptor => descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(ILongRunningOperationRecoveryHandler)))
        {
            if (keyed.ServiceKey is not string key || !expected.ContainsKey(key))
            {
                failures.Add($"Unexpected keyed recovery handler '{keyed.ServiceKey}'.");
            }
        }

        return failures.ToArray();
    }
}

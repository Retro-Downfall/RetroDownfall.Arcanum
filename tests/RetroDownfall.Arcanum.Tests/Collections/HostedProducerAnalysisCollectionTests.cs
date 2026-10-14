using System.Reflection;

using RetroDownfall.Arcanum.Tests.Operations;

using RetroDownfall.Arcanum.Tests.Cli;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Collections;

public sealed class HostedProducerAnalysisCollectionTests
{

    [Fact]
    public void Production_graph_lane_is_closed_and_complete()
    {

        static bool HasProductionTrait(MethodInfo method) =>
            method.GetCustomAttributesData().Any(static attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments.Count == 2
                && attribute.ConstructorArguments[0].Value as string == "Category"
                && attribute.ConstructorArguments[1].Value as string
                    == "HostedProducerProductionAnalysis");

        Type[] consumers =
        [
            typeof(HostedGrimoireProducerInventoryTests),
            typeof(CovenantArchitectureBoundaryTests),
            typeof(ArcanumAuthenticatedHttpSenderTests),
        ];

        string[] actual = consumers
            .SelectMany(static consumer => consumer.GetMethods(
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly))
            .Where(HasProductionTrait)
            .Select(static method => method.DeclaringType!.FullName
                + "."
                + method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "RetroDownfall.Arcanum.Tests.Cli.ArcanumAuthenticatedHttpSenderTests.Every_cli_http_client_construction_belongs_to_the_closed_transport_composition",
            "RetroDownfall.Arcanum.Tests.Cli.ArcanumAuthenticatedHttpSenderTests.Every_cli_http_send_uses_the_authenticated_sender_or_an_exact_narrow_allowance",
            "RetroDownfall.Arcanum.Tests.Cli.ArcanumAuthenticatedHttpSenderTests.Semantic_client_inventory_recognizes_named_factory_method_groups_and_target_typed_new",
            "RetroDownfall.Arcanum.Tests.Cli.ArcanumAuthenticatedHttpSenderTests.Semantic_transport_inventory_ignores_spelling_shadows",
            "RetroDownfall.Arcanum.Tests.Cli.ArcanumAuthenticatedHttpSenderTests.Semantic_transport_inventory_recognizes_real_static_conditional_and_method_group_sends",
            "RetroDownfall.Arcanum.Tests.Covenant.CovenantArchitectureBoundaryTests.Every_effectful_recovery_handler_has_the_required_source_effects",
            "RetroDownfall.Arcanum.Tests.Covenant.CovenantArchitectureBoundaryTests.Owner_evidence_has_exactly_two_path_specific_issuers",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ActualLongRestReadinessAndOrdinaryRootsRetainPhysicalDispatchCapsules",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.BackupLiveSourceHasExactCliCallerAuthority",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.EveryApplicationHostedServiceHasExactlyOneEntry",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.EveryDiscoveredProducerSiteIsCataloguedExactlyOnce",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.FrameworkCollectionIndexerUsesProductionReferencePackIdentity",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.LiveSourceCannotBecomeReachableFromHostedOrUnadmittedCaller",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.NonHostedBackupChainsDoNotEnterHostedRegistrationBijection",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionBatchCarrierHasOneExceptionSafePublicationLifetime",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionCompilationsResolveGeneratedJsonSymbols",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionCompilerGraphUsesCanonicalProjectReferences",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionCovenantHostedSweepRetainsExactCleanupProvenanceAtBoundedStateCap",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionDataRetentionSweepGraphRetainsItsCandidateEffectFrontier",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionDataRetentionSweepGraphRetainsItsExactWorkLease",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionGraphsRetainExactCollectionIndexerProvenance",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionImmutableArraySequenceEqualityUsesDefaultValueEquality",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionIncludesEveryFirstPartyDependencyAndApiCliRoots",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionNonHostedGraphsRetainTheirExactLifetimes",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionOrdinaryHostedGraphsRetainTheirExactLifetimes",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionRecoveryGraphsRetainTheirClosedExactAuthority",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionReferencePackResolvesThreadingMechanicalCleanupIdentities",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionRegistrationsMatchRuntimeDescriptorsAndClosedVocabulary",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionSchemaLazyFactoriesAreProvenAsBoundedData",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionSequenceOperationUsesTheSameReviewedComparerProof",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_allocates_the_context_in_the_installation_reset_and_in_a_design_time_factory_only",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_context_universe_is_the_registrations_and_the_installation_reset_allocation",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_declares_no_authored_logger_and_never_selects_a_logger_factory",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_interceptor_set_is_closed_and_is_exactly_the_reviewed_enrolment_interceptor",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_never_spells_an_EF_event_data_type_outside_a_parameter_declaration",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_registrations_supply_the_compiled_model_so_the_model_hooks_cannot_run",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_subscribes_exactly_one_named_handler_to_connection_state_changes",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_universe_satisfies_AuthoredEnumerablesAbsentByUse",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_universe_satisfies_DbProviderTypesClosedByUse",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_universe_satisfies_SqliteSealedByUse",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.RestoreSafetyBackupHasExactStoppedHostOrOwner",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.SchemaBackfillStrategyTableMatchesEveryConfiguredConcreteImplementation",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.The_production_entry_runs_only_the_named_roots_under_the_production_premise",
        ];

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void Additional_production_graph_lane_has_exactly_the_four_known_consumers()
    {

        static bool HasAdditionalTrait(MethodInfo method) =>
            method.GetCustomAttributesData().Any(static attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments.Count == 2
                && attribute.ConstructorArguments[0].Value as string == "Category"
                && attribute.ConstructorArguments[1].Value as string
                    == "HostedProducerAdditionalAnalysis");

        Type[] consumers =
        [
            typeof(HostedGrimoireProducerInventoryTests),
            typeof(CovenantArchitectureBoundaryTests),
            typeof(ArcanumAuthenticatedHttpSenderTests),
        ];

        string[] actual = consumers
            .SelectMany(static consumer => consumer.GetMethods(
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.DeclaredOnly))
            .Where(HasAdditionalTrait)
            .Select(static method => method.DeclaringType!.FullName
                + "."
                + method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "RetroDownfall.Arcanum.Tests.Covenant.CovenantArchitectureBoundaryTests.Every_effectful_recovery_handler_has_the_required_source_effects",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionImmutableArraySequenceEqualityUsesDefaultValueEquality",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionSchemaLazyFactoriesAreProvenAsBoundedData",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionSequenceOperationUsesTheSameReviewedComparerProof",
        ];

        Assert.Equal(expected, actual);

    }

    [Theory]
    [InlineData(typeof(HostedGrimoireProducerInventoryTests))]
    [InlineData(typeof(CovenantArchitectureBoundaryTests))]
    [InlineData(typeof(ArcanumAuthenticatedHttpSenderTests))]
    public void Production_compilation_consumers_are_isolated_from_parallel_suite(Type consumer)
    {

        CustomAttributeData collection = Assert.Single(
            consumer.GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(CollectionAttribute));

        Assert.Equal(
            "HostedProducerAnalysis",
            collection.ConstructorArguments[0].Value);

        CollectionDefinitionAttribute definition = Assert.IsType<CollectionDefinitionAttribute>(
            typeof(HostedProducerAnalysisCollection)
                .GetCustomAttribute<CollectionDefinitionAttribute>());

        Assert.True(definition.DisableParallelization);

    }

}

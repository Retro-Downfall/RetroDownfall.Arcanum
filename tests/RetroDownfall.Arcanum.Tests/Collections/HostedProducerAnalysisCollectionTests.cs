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
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.RestoreSafetyBackupHasExactStoppedHostOrOwner",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.SchemaBackfillStrategyTableMatchesEveryConfiguredConcreteImplementation",
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

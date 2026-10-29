using System.Reflection;

using RetroDownfall.Arcanum.Tests.Build;

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
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ActualLongRestHostedRootRetainsPhysicalDispatchCapsules",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.BackupLiveSourceHasExactCliCallerAuthority",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.EveryApplicationHostedServiceHasExactlyOneEntry",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.EveryDiscoveredProducerSiteIsCataloguedExactlyOnce",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Every_additional_root_stays_within_its_ratchet_ceiling",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Every_production_root_stays_within_its_ratchet_ceiling",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ExplicitWriterDisposal_actual_batch_preserves_physical_rows_and_records_each_logical_call",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.FrameworkCollectionIndexerUsesProductionReferencePackIdentity",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedClosureActualRetentionRecoveryRetainsTheLoggedCheckpointZeroTerminalProof",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedClosureActualRetentionWeakTableFactoryCompletesSynchronously",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedClosureActualScalarOperationsRetainClosedFrameworkSemantics",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualAsyncDigestRetainsItsIteratorAndTransactionWork",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualBatchProductionSubsetRetainsPhysicalWriterCleanup",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualChatLeaseCleanupHasExactClientOwnership",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualCommandCleanupHasExactProviderAndConnectionOrigins",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualCommandProductionSubsetKeepsRealSqlWork",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualConfigurationSubscriptionKeepsTheClosedDefaultMonitorComposition",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualDigestProductionSubsetRetainsRealSqlWork",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualLeaseProductionSubsetKeepsAdapterAndBorrowedTransportProvenance",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualProductionSemanticProofHasNoUnresolvedFamily",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualProviderFactoriesRetainClosedSdkPolicySetup",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualSdkPolicySetupRetainsIndependentSendObligations",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualStartupAndOwnerRecoveryKeepClosedDispatchAfterCompositionProof",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.HostedContinuationActualWriterCleanupRetainsConstructionAndOwnedFieldEffects",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.InheritedGuardProductionInstallationResetRetainsConstructedGuardEffects",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.LiveSourceCannotBecomeReachableFromHostedOrUnadmittedCaller",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.NonHostedBackupChainsDoNotEnterHostedRegistrationBijection",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionAttachmentExtractionRetainsItsExactAdmittedLifetime",
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
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_analysis_observations_share_discovery_and_only_selection_contracts_reenter",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_connection_state_subscriptions_match_the_closed_named_handler_registry",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_context_universe_is_the_registrations_and_the_installation_reset_allocation",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_declares_no_authored_logger_and_never_selects_a_logger_factory",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_interceptor_set_is_closed_and_is_exactly_the_reviewed_enrolment_interceptor",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_never_spells_an_EF_event_data_type_outside_a_parameter_declaration",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_registrations_supply_the_compiled_model_so_the_model_hooks_cannot_run",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_root_observations_preserve_shared_snapshot_and_compiler_model",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_universe_satisfies_AuthoredEnumerablesAbsentByUse",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_universe_satisfies_DbProviderTypesClosedByUse",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Production_universe_satisfies_SqliteSealedByUse",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.RestoreSafetyBackupHasExactStoppedHostOrOwner",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ReviewedCapsuleManifestExactlyMatchesProductionDiscovery",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.SchemaBackfillStrategyTableMatchesEveryConfiguredConcreteImplementation",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.SchemaReadiness_ActualProductionSitesUseOnlyTheReviewedSignatures",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.TheLongRestChatAndEmbeddingCalleesAreDiscoveredUnderTheHostedRoot",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.TheLongRestHostedRootOwnsItsWaitItsLeaseAndItsPaidSendsWithNoEscapeOrMissingFrontier",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.The_production_entry_runs_only_the_named_roots_under_the_production_premise",
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Additional_production_graph_lane_has_exactly_its_known_consumers()
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
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Every_additional_root_stays_within_its_ratchet_ceiling",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionImmutableArraySequenceEqualityUsesDefaultValueEquality",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionSchemaLazyFactoriesAreProvenAsBoundedData",
            "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ProductionSequenceOperationUsesTheSameReviewedComparerProof",
        ];

        Assert.Equal(expected, actual);
    }

    // The fixtures that run in the serve host partition: each case of these theories analyses the production compilations with trees of the
    // serving host replaced, so each case is a whole discovery. The runner reads this category to split the fixtures between CI jobs, so the
    // set is closed here. The two worker theories (ServeWorkerMutationHasExplicitConditionalSourceAssociation,
    // CanonicalServeCallbackHasConditionalBuildStartWorkerSourceAssociation) are whole discoveries too and run with the Build scans in the
    // build fixture partition (Build_fixture_lane_is_closed_and_disjoint_from_every_other_lane).
    [Fact]
    public void Serve_host_fixture_lane_is_closed_and_holds_no_production_method()
    {
        static bool HasCategory(MethodInfo method, string category) =>
            method.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments.Count == 2
                && attribute.ConstructorArguments[0].Value as string == "Category"
                && attribute.ConstructorArguments[1].Value as string == category);

        MethodInfo[] methods = [.. typeof(HostedGrimoireProducerInventoryTests).GetMethods(
            BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly)];

        string[] actual = [.. methods
            .Where(method => HasCategory(method, "HostedProducerServeHostAnalysis"))
            .Select(static method => method.Name)
            .Order(StringComparer.Ordinal)];

        string[] expected =
        [
            "CanonicalServeBuildHasConditionalScopedFactorySourceAssociation",
            "ServeBuildMutationHasExplicitConditionalSourceAssociation",
            "ServeCompositionMutationHasExplicitConditionalSourceAssociation",
            "ServeScopedConsumerHasExplicitConditionalSourceOrigin",
        ];

        Assert.Equal(expected, actual);

        Assert.DoesNotContain(
            methods.Where(method => HasCategory(method, "HostedProducerServeHostAnalysis")),
            method => HasCategory(method, "HostedProducerProductionAnalysis")
                || HasCategory(method, "HostedProducerAdditionalAnalysis")
                || HasCategory(method, "HostedProducerBuildAnalysis"));

        Assert.Contains(
            typeof(HostedGrimoireProducerInventoryTests).GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments[0].Value as string == "Category"
                && attribute.ConstructorArguments[1].Value as string == "HostedProducerAnalysis");
    }

    // The fixtures that run in the build fixture partition: the four Build scan classes bind every tree of the production compilations, and the
    // two Serve worker theories analyse the production compilations with the worker's tree replaced, so each of them reads the production source
    // as a whole. The runner reads this category to split the fixtures between CI jobs, so the set is closed here. No test carries two lane
    // categories, which is what keeps the lanes disjoint.
    [Fact]
    public void Build_fixture_lane_is_closed_and_disjoint_from_every_other_lane()
    {
        const string lane = "HostedProducerBuildAnalysis";

        string[] otherLanes =
        [
            "HostedProducerProductionAnalysis",
            "HostedProducerAdditionalAnalysis",
            "HostedProducerServeHostAnalysis",
        ];

        static bool HasCategory(MemberInfo member, string category) =>
            member.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType == typeof(TraitAttribute)
                && attribute.ConstructorArguments.Count == 2
                && attribute.ConstructorArguments[0].Value as string == "Category"
                && attribute.ConstructorArguments[1].Value as string == category);

        const BindingFlags declared = BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        Type[] types = [.. typeof(HostedProducerAnalysisCollectionTests).Assembly.GetTypes()];

        Type[] laneClasses = [.. types
            .Where(type => HasCategory(type, lane))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)];

        Assert.Equal(
            [
                "RetroDownfall.Arcanum.Tests.Build.AdmittedEntryCensusTests",
                "RetroDownfall.Arcanum.Tests.Build.AuthorityCarrierBanTests",
                "RetroDownfall.Arcanum.Tests.Build.CheckerLimitPinTests",
                "RetroDownfall.Arcanum.Tests.Build.SingleGateCensusTests",
            ],
            laneClasses.Select(static type => type.FullName));

        MethodInfo[] laneMethods = [.. types
            .SelectMany(static type => type.GetMethods(declared))
            .Where(method => HasCategory(method, lane))
            .OrderBy(static method => method.DeclaringType!.FullName + "." + method.Name, StringComparer.Ordinal)];

        Assert.Equal(
            [
                "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.CanonicalServeCallbackHasConditionalBuildStartWorkerSourceAssociation",
                "RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.ServeWorkerMutationHasExplicitConditionalSourceAssociation",
            ],
            laneMethods.Select(static method => method.DeclaringType!.FullName + "." + method.Name));

        // A class of the lane is a fixture of the analysis collection, and neither it nor any method it declares is a production method or a
        // member of another lane.
        foreach (Type type in laneClasses)
        {
            Assert.True(HasCategory(type, "HostedProducerAnalysis"), type.FullName);

            CustomAttributeData collection = Assert.Single(
                type.GetCustomAttributesData(),
                static attribute => attribute.AttributeType == typeof(CollectionAttribute));

            Assert.Equal(HostedProducerAnalysisCollection.Name, collection.ConstructorArguments[0].Value);

            Assert.DoesNotContain(otherLanes, other => HasCategory(type, other));

            Assert.DoesNotContain(
                type.GetMethods(declared),
                method => otherLanes.Any(other => HasCategory(method, other)));
        }

        // A method of the lane sits in a fixture class of the analysis collection and is neither a production method nor a member of another
        // lane, whether the category is on the method or on the class that declares it.
        foreach (MethodInfo method in laneMethods)
        {
            Assert.True(HasCategory(method.DeclaringType!, "HostedProducerAnalysis"), method.Name);

            Assert.DoesNotContain(otherLanes, other => HasCategory(method, other) || HasCategory(method.DeclaringType!, other));
        }
    }

    [Theory]
    [InlineData(typeof(HostedGrimoireProducerInventoryTests))]
    [InlineData(typeof(CovenantArchitectureBoundaryTests))]
    [InlineData(typeof(ArcanumAuthenticatedHttpSenderTests))]
    [InlineData(typeof(AuthorityCarrierBanTests))]
    [InlineData(typeof(AdmittedEntryCensusTests))]
    [InlineData(typeof(SingleGateCensusTests))]
    [InlineData(typeof(CheckerLimitPinTests))]
    [InlineData(typeof(MirroredCredentialConstructionTests))]
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

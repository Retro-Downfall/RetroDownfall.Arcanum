using System.Reflection;

using System.Text;

using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Lexicon;

using RetroDownfall.Arcanum.Infrastructure.Memory;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A constructor parameter of interface type that defaults to <c>null</c> is where production
/// reachability quietly dies: a factory registration that omits it, or a test that omits it, gets a
/// null (or a substitute that refuses every call) and every guard behind it is dead. This inventory
/// names every such parameter under <c>src/</c> and requires each one to be either removed or listed
/// here with the reason it is legitimately optional.
/// </summary>
public sealed class NullableInterfaceConstructorDefaultTests
{
    /// <summary>
    /// The parameters this inventory does not fail on, each with the reason a reviewer can check.
    /// </summary>
    /// <remarks>
    /// A baseline, not an endorsement. Every entry was present when this inventory was written, and
    /// each reason states what was checked rather than a judgement that the site is harmless: whether
    /// the null coalesces to a constructed default, whether every use of it is null-safe, whether the
    /// container activates the owner, and whether anything registers the interface at all.
    ///
    /// <para>Registration is read from the files that build a container - which includes the two Ux
    /// composition roots and the CLI application factory, not only the files that take an
    /// <c>IServiceCollection</c> parameter - and container intrinsics such as
    /// <c>IServiceScopeFactory</c> are treated as supplied, because nothing registers them and the
    /// container provides them anyway. An earlier pass read registration from the whole tree, which
    /// matched <c>Task&lt;IFoo&gt;</c> and every other generic argument, and reported nine sites as the
    /// V-1 shape that are nothing of the kind.</para>
    ///
    /// <para>Several sites here are dependencies whose absence would disable a refusal rather than
    /// an observation - the two labelled-artifact guards and the two sensitive-artifact purgers. The
    /// workspace registry behind the attachment root check used to be a fifth, and is not any more: the
    /// resolver requires it, and the offline-maintenance composition supplies a registry that knows no
    /// workspace, so a claimed root is refused there as an explicit registration rather than by a null
    /// that nobody supplied. None of them is a live bypass: each
    /// reason names the registration or the single construction site that supplies it, so the claim
    /// this list makes is that the container is what keeps the refusal reachable, not that the
    /// parameter is harmless when null. What the list buys is the ninety-ninth entry - a new optional
    /// interface dependency cannot enter <c>src/</c> without someone writing down which of those four
    /// things is true of it, and which of those two claims it is making.</para>
    /// </remarks>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:fileEncryptionRuntimeStatus"] = "owner is container-activated and IFileEncryptionRuntimeStatus is registered; the container supplies its constant-time snapshot in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:operationDiagnosticsSource"] = "every use of the IDurableOperationDiagnostics is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:providerApiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:workspaceCheckCapabilityReporter"] = "every use of the IWorkspaceCheckCapabilityReporter is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:ChatClientFactory:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:ChatClientFactory:familiarProcessRunner"] = "the null coalesces to a constructed default at the use site, so no host runs without a IFamiliarProcessRunner",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:ChatClientFactory:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:authoritySnapshot"] = "inspection and default-off fixtures need no runtime authority; the live eligible path refuses before claims or maintenance if authority is absent, and the composed host registers the snapshot provider",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:claimBegin"] = "read-only inspection needs no claim begin; eligible live preparation refuses before payment if this port is absent, and the composed host registers the atomic claim begin store",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:claims"] = "read-only inspection needs no claim coordinator; eligible live preparation refuses before payment if absent, and the composed host registers the durable coordinator",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:lookup"] = "read-only inspection and unkeyed gate-off fixtures need no accepted-key lookup; keyed ineligible requests refuse if it is absent, and the composed host registers the content-free lookup",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:maintenance"] = "read-only inspection needs no maintenance service; eligible live preparation refuses before payment if absent, and ApiBootstrapper registers the shared service",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:protectedReplies"] = "the composed host registers both the atomic replay store and protected reader; inspection does not replay, and a replay requiring protected content refuses rather than reading raw content when no authorized reader exists",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:replayStore"] = "inspection does not replay; absence selects the explicitly clean-proven fallback or an authorized protected reader, never a raw protected read, and the composed host registers the atomic replay store",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnPreparer:sessionBegin"] = "inspection and already-bound Session fixtures need no Session creation; a live request omitting Session identity refuses if absent, and the composed host registers the Session begin store",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupTurnPreparer.cs:CampaignRollupTurnSnapshot:claims"] = "preview snapshots own no claim and need no coordinator; every live snapshot is constructed by the preparer with its required registered coordinator, which terminalizes an unbegun claim on disposal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:ContextCompressionService:modelTokenEstimator"] = "the null coalesces to a constructed default at the use site, so no host runs without a IModelTokenEstimator",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:ContextCompressionService:purger"] = "the owner is registered by ApiBootstrapper (IContextCompressionService, scoped) and ICovenantSensitiveArtifactPurger by the Covenant registrations in AddArcanumInfrastructure; the `_purger is null` check in the compaction path skips the group-safe purge entirely (and is what keeps the null-forgiving dereference in PurgeSelectedEntriesAsync unreached), so the container supplying it is what keeps that purge reachable",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingGeneratorFactory.cs:EmbeddingGeneratorFactory:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Familiars/CodexCliChatClient.cs:CodexCliChatClient:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:GrimoireTurnWriter:turnCommitter"] = "legacy unclaimed clean fixtures retain their old persistence lane; claimed or protected finalization refuses without the atomic committer, and the composed host supplies it",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:GrimoireTurnWriter:claimedBeginStore"] = "legacy unclaimed fixtures need only the ordinary begin port; supplying a claim selects required atomic begin and refuses if the claim port is absent, and the composed host supplies it",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:GrimoireTurnWriter:claims"] = "legacy unclaimed fixtures own no durable claim; supplying a claim requires this coordinator before begin and finalization, and the composed host supplies it",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs:ModelCallExecutor:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:ToolExecutionPipeline:attachmentSourceResolver"] = "every use of the IAttachmentSourceResolver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:ToolExecutionPipeline:covenantAuthority"] = "every use of the ICovenantAuthoritySnapshotProvider is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:ToolExecutionPipeline:toolResultMaterializer"] = "every use of the IToolResultMaterializer is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumReadUrlTool.cs:ArcanumReadUrlTool:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:ArcanumSpellScriptTool:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:ArcanumSpellScriptTool:resourceLimiter"] = "the only construction in src is the `new ArcanumSpellScriptTool(` in WizardIntelligenceProvider's tool assembly, which passes the resolved IProcessResourceLimiter",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:ArcanumSpellScriptTool:sanctumGuard"] = "the null coalesces to a constructed default at the use site, so no host runs without a ISanctumGuard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumWebSearchTool.cs:ArcanumWebSearchTool:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/BuiltInToolRegistry.cs:BuiltInToolRegistry:browseWebLogger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/TurnEngine/TurnEngine.cs:TurnEngine:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:attachmentMemoryProvenanceStore"] = "every use of the IAttachmentMemoryProvenanceStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:budgetReservationService"] = "owner is container-activated and IBudgetReservationService is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:healthTracker"] = "every use of the IProviderHealthTracker is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:modelCallExecutor"] = "owner is container-activated and IModelCallExecutor is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:modelTokenEstimator"] = "owner is container-activated and IModelTokenEstimator is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:sessionAttachmentRetrieval"] = "every use of the ISessionAttachmentRetrievalService is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:subagentRunner"] = "every use of the ISubagentRunner is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:tapestryStore"] = "every use of the ITapestryStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:turnRunWriter"] = "owner is container-activated and ITurnRunWriter is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:WizardIntelligenceProvider:webResearchProviderCatalog"] = "every use of the IWebResearchProviderCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Mcp/DiagnosticMcpInvocationService.cs:DiagnosticMcpInvocationService:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:CliSessionManager:contextStore"] = "every use of the ICliContextStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:CliSessionManager:mutationBoundary"] = "every use of the IArcanumClientMutationBoundary is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:CliSessionManager:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Cli/Services/ConsoleAskHumanCoordinator.cs:ConsoleAskHumanCoordinator:diagnosticConsole"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupPrompt.cs:ConsoleSetupPrompt:secretPrompt"] = "the null coalesces to a constructed default at the use site, so no host runs without a IBackupPassphrasePrompt",

        ["src/RetroDownfall.Arcanum.Core/Configuration/ConfigurationValidator.cs:ConfigurationValidator:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AClientService.cs:A2AClientService:scopeFactory"] = "every use of the IServiceScopeFactory is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Caching/KeyedLock.cs:KeyedLock:comparer"] = "the null coalesces to a constructed default at the use site (`comparer ?? EqualityComparer<TKey>.Default`), so no instance runs without an IEqualityComparer",

        ["src/RetroDownfall.Arcanum.Infrastructure/CommLink/CommLinkMultiplexer.cs:CommLinkMultiplexer:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Configuration/ConfigurationPresetService.cs:ConfigurationPresetService:credentialStore"] = "every use of the IWebResearchCredentialStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs:CampaignPathMarkerLifecycle:recoveryKeys"] = "the only construction in src is the ICampaignPathMarkerLifecycle factory in ServiceCollectionExtensions, which passes the registered ICampaignRootIdentityRecoveryKeyProvider",

        ["src/RetroDownfall.Arcanum.Infrastructure/Covenant/CovenantManagementService.cs:CovenantManagementService:searchIndex"] = "the ICovenantManagementService factory in ServiceCollectionExtensions passes the required ICovenantSearchIndex and compiler; omission is a restricted list/status test seam, and QueryAsync fails closed with Covenant.Unavailable when either search dependency is absent",

        ["src/RetroDownfall.Arcanum.Infrastructure/Covenant/GrimoireSchemaTransitionCoordinator.cs:GrimoireSchemaTransitionCoordinator:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:attachmentStore"] = "every use of the ISessionAttachmentStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:covenantErasureEffectDigests"] = "the null coalesces to a constructed default at the use site, so no host runs without a ICovenantErasureEffectDigestCalculator",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:covenantGate"] = "owner is container-activated and ICovenantOperationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:daemonExecutions"] = "every use of the IDaemonExecutionRepository is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:daemonMutationGate"] = "owner is container-activated and IDaemonExecutionMutationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:factoryApplyRequestDigests"] = "the null coalesces to a constructed default at the use site, so no host runs without a ICovenantFactoryErasureApplyRequestDigestCalculator",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:managedLogMutationGate"] = "owner is container-activated and IManagedLogMutationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:policyStore"] = "the null coalesces to a constructed default at the use site, so no host runs without a IDataRetentionPolicyStore",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:sameOwnerLeaseResumption"] = "owner is container-activated and ILongRunningOperationSameOwnerLeaseResumption is registered from the same scoped LongRunningOperationStore; an omitted test seam fails closed into reconciliation instead of resuming without proof",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaInstaller.cs:GrimoireSchemaInstaller:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:SessionAttachmentStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Familiars/FamiliarProcessRunner.cs:FamiliarProcessRunner:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/ApprenticeService.cs:ApprenticeService:executionCapacity"] = "the null coalesces to a process-owned DefaultApprenticeExecutionCapacity, so every host has the atomic concurrency gate while focused reliability tests can inject a controlled implementation",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:LongRunningOperationStore:covenantDrain"] = "every use of the ICovenantConnectionDrain is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:SagaMemoryStore:releaseAuthority"] = "the owner is registered in AddArcanumInfrastructure and IOperatorAuthorityContextIssuer by AddCovenantAuthority in the same composition; a null makes MemoryErasureFingerprintRelease.OperatorMayRelease answer no, so a correction lifts no erasure fingerprint and reports false (SagaMemoryStore.Curation.cs CorrectAsync) - the safe direction, and the container supplying it is what keeps an operator correction's release reachable",
        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:SagaMemoryStore:labeledArtifactGuard"] = "the owner is registered by `AddScoped<ISagaMemoryStore, SagaMemoryStore>()` and the parameter is the transaction form, ICovenantLabeledArtifactTransactionGuard, which the one scoped guard registration beside the Covenant purger supplies; a null skips the label guard in DeleteAsync and DeleteAllAsync, so the container supplying it is what keeps those refusals reachable",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:SessionAttachmentStore:blobStore"] = "owner is container-activated and IEncryptedBlobStore is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:SessionAttachmentStore:indexQueue"] = "every use of the ISessionAttachmentIndexQueue is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:SessionAttachmentStore:sourceResolver"] = "every use of the IAttachmentSourceResolver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs:GrimoireDatabaseHostedService:transitionRecovery"] = "IGrimoireOfflineTransitionStartupRecovery is registered at ServiceCollectionExtensions.cs and the composed host factory passes it; absence does not open a bypass - the else arm falls back to InstallationResetHostStartupAdmission.LeavesTransitionUnfinished, which is the refusal this parameter replaced, so a null resumes nothing and admits nothing",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs:GrimoireDatabaseHostedService:startupProbe"] = "the null coalesces to InstallationStartupProbe.CreateDefault() in the `_startupProbe` field initializer, and the only construction in src - the GrimoireDatabaseHostedService factory in ServiceCollectionExtensions - never passes one, so that default is the production probe",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs:GrimoireDatabaseHostedService:postTopologyStartupAction"] = "optional startup presentation hook; absence skips console redirection or listen-any acknowledgement persistence, not database initialization, recovery, admission, or a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs:HostToolsMarkerPairResetCoordinator:managedFiles"] = "every use of the IFullInstallationResetManagedFileReconciler is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:identityReader"] = "owner is container-activated and IInstallationResetDatabaseIdentityReader is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:pairReader"] = "owner is container-activated and IInstallationResetHostProcessToolsPairReader is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:preDataMutation"] = "owner is container-activated and IInstallationResetPreDataMutation is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:remediationVerifier"] = "every use of the IFullInstallationResetRemediationAttestationVerifier is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:stateRoots"] = "owner is container-activated and IInstallationResetStateRoots is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:stoppedHostDataService"] = "every use of the IInstallationResetStoppedHostDataService is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:stoppedHostPairReader"] = "every use of the IInstallationResetStoppedHostProcessToolsPairReader is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:workspaceResolver"] = "every use of the IInstallationResetWorkspaceResolver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:deferredServices"] = "the composed host registers IInstallationResetDeferredServices; omission is a restricted test seam that stops full reset at durable admitted/recovery-required state instead of bypassing marker-pair or terminal verification",

        ["src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellCatalogService.cs:SpellCatalogService:progressObserver"] = "every use of the ISpellCatalogProgressObserver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Intelligence/WebResearch/LocalHttpWebProvider.cs:LocalHttpWebProvider:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Intelligence/WebResearch/PerplexityWebProvider.cs:PerplexityWebProvider:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs:LexiconService:labeledArtifactGuard"] = "the owner is registered by `AddScoped<LexiconService>()` and ICovenantLabeledArtifactGuard by the scoped forwarder to the one transaction guard, which CovenantLabeledArtifactGuardCompositionTests resolves from the real container; a null skips the label guard in the delete's `_labeledArtifactGuard is { } guard` branch, so the container supplying it is what keeps that refusal reachable",

        ["src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs:LexiconService:reviewTokenCodec"] = "the null coalesces to a MemoryReviewTokenCodec using TimeProvider.System at the field initializer, while the composed host supplies the registered IMemoryReviewTokenCodec; no instance runs without an authenticated review-token codec",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs:ArcanumInternalToolServer:workspaceCheckRuntime"] = "the null coalesces to a constructed default at the use site, so no host runs without a IWorkspaceCheckRuntime",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs:ArcanumInternalToolServer:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/InProcessMcpTransport.cs:InProcessMcpTransport:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:McpBridgeTool:fallbackClient"] = "every use of the IMcpClient is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:McpBridgeTool:fallbackLogger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/SdkMcpClientWrapper.cs:SdkMcpClientWrapper:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs:LongRunningOperationReconciler:scopeFactory"] = "every use of the IServiceScopeFactory is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Platform/ProcessResourceLimiter.cs:ProcessResourceLimiter:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:SessionRepository:attachmentIndexQueue"] = "every use of the ISessionAttachmentIndexQueue is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/OsKeychainSecretStore.cs:OsKeychainSecretStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/ProviderCredentialStore.cs:ProviderCredentialStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/SanctumGuard.cs:SanctumGuard:dnsResolver"] = "the null coalesces to a constructed default at the use site, so no host runs without a IDnsResolver",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/WebResearchCredentialStore.cs:WebResearchCredentialStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs:EmbeddingsResetService:purger"] = "the only construction in src is the EmbeddingsResetService factory in ServiceCollectionExtensions, which passes the registered ICovenantSensitiveArtifactPurger; a null would make PurgeLabeledScopeAsync return an empty purge outcome, so the factory passing it is what keeps the purge reachable",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceCheckRuntime.cs:WorkspaceCheckRuntime:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:WorkspaceSearchEngine:progressObserver"] = "every use of the IWorkspaceSearchProgressObserver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:WorkspaceSearchEngine:spillObserver"] = "every use of the IWorkspaceSearchLineSpillObserver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:ConfigurationViewModel:presetService"] = "owner is container-activated and IConfigurationPresetService is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:ConfigurationViewModel:probeClient"] = "owner is container-activated and IFamiliarProbeClient is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ProvidersSectionViewModel.cs:ProviderViewModel:probeClient"] = "every use of the IFamiliarProbeClient is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ProvidersSectionViewModel.cs:ProvidersSectionViewModel:probeClient"] = "every use of the IFamiliarProbeClient is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.TheForge.Core/Services/ComparisonRunStore.cs:ComparisonRunStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Core/Services/DiagnosticMcpFixtureStore.cs:DiagnosticMcpFixtureStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Core/Services/InferenceTraceStore.cs:InferenceTraceStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Core/Services/TheForgeSettingsStore.cs:TheForgeSettingsStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Core/Services/TrialSuiteStore.cs:TrialSuiteStore:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:MarkdigAstAvaloniaRenderer:highlighter"] = "the null coalesces to a constructed default at the use site, so no host runs without a IMarkdownCodeHighlighter",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:MarkdigAstAvaloniaRenderer:images"] = "the null coalesces to a constructed default at the use site, so no host runs without a IMarkdownImageResolver",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Docking/DockLayoutViewModel.cs:DockLayoutViewModel:settingsStore"] = "every use of the ITheForgeSettingsStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Docking/DockLayoutViewModel.cs:DockLayoutViewModel:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/FoundryFloor/FoundryFloorViewModel.cs:FoundryFloorViewModel:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/MainViewModel.cs:MainViewModel:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/MarkdownDocumentViewModel.cs:MarkdownDocumentViewModel:contentStore"] = "every use of the IMarkdownDocumentContentStore is null-safe; absence disables an observation, not a refusal",
    };

    /// <summary>
    /// A nullable interface parameter that defaults to <c>null</c>, generic or not.
    /// </summary>
    /// <remarks>
    /// The type name takes an optional generic argument list, one nesting level deep, so
    /// <c>IOptionsMonitor&lt;ArcanumSettings&gt;? settings = null</c> and
    /// <c>ILogger&lt;Owner&gt;? logger = null</c> are read as the interface defaults they are. The
    /// pattern used to need an identifier directly before the question mark, which let exactly the
    /// settings dependency whose absence turns a denylist into an empty one slip past. The collection
    /// interfaces are excluded, by whole name: an optional <c>IReadOnlyList&lt;T&gt;?</c> is a data value
    /// the owner reads, not a collaborator whose omission disables a guard. The exclusion is anchored to
    /// the end of the name (a <c>&lt;</c> or the <c>?</c> must follow it), because a prefix test also hides
    /// every first-party interface that merely begins with one of those words (<c>ISetupPrompt</c>,
    /// <c>ISettingsStore</c>, <c>IListener</c>), and a collaborator hidden here defaults to null unseen.
    /// </remarks>
    private static readonly Regex NullableInterfaceDefault = new(
        @"\b(I(?!(?:ReadOnlyList|ReadOnlyCollection|ReadOnlyDictionary|ReadOnlySet|Enumerable|AsyncEnumerable|List|Collection|Dictionary|Set)[<?])[A-Z]\w*(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>)?)\?\s+(\w+)\s*=\s*null\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The inventory is only as good as its pattern, and the pattern used to be blind to every generic
    /// interface — including <c>IOptionsMonitor&lt;ArcanumSettings&gt;? settings = null</c>, the exact
    /// shape that turns a secret denylist into an empty one. Pin the spellings it must read.
    /// </summary>
    [Theory]
    [InlineData("IFoo? foo = null", "IFoo", "foo")]
    [InlineData("IOptionsMonitor<ArcanumSettings>? settings = null", "IOptionsMonitor<ArcanumSettings>", "settings")]
    [InlineData("IOptions<ArcanumSettings>? settings=null", "IOptions<ArcanumSettings>", "settings")]
    [InlineData("ILogger<Owner>? logger = null", "ILogger<Owner>", "logger")]
    [InlineData("IEqualityComparer<Dictionary<string, int>>? comparer = null", "IEqualityComparer<Dictionary<string, int>>", "comparer")]
    // Collaborators whose names merely begin with a collection word. The exclusions are whole names, so
    // none of these may be hidden: ISetupPrompt is a first-party interface, and so is every ISetup* sibling.
    [InlineData("ISetupPrompt? prompt = null", "ISetupPrompt", "prompt")]
    [InlineData("ISetupCommitter? committer = null", "ISetupCommitter", "committer")]
    [InlineData("ISettingsStore? settings = null", "ISettingsStore", "settings")]
    [InlineData("IListener? listener = null", "IListener", "listener")]
    [InlineData("ICollectionSource? source = null", "ICollectionSource", "source")]
    [InlineData("IDictionaryProvider<Owner>? provider = null", "IDictionaryProvider<Owner>", "provider")]
    [InlineData("IReadOnlyStore? store = null", "IReadOnlyStore", "store")]
    [InlineData("IEnumerableSource? source = null", "IEnumerableSource", "source")]
    public void The_pattern_reads_every_spelling_of_a_nullable_interface_default(
        string parameter,
        string expectedType,
        string expectedName)
    {
        Match match = NullableInterfaceDefault.Match($"({parameter})");

        Assert.True(match.Success, $"'{parameter}' was not read as a nullable interface default.");

        Assert.Equal(expectedType, match.Groups[1].Value);

        Assert.Equal(expectedName, match.Groups[2].Value);
    }

    [Theory]
    [InlineData("IReadOnlyList<string>? values = null")]
    [InlineData("IReadOnlySet<Guid>? ids = null")]
    [InlineData("IEnumerable<string>? names = null")]
    [InlineData("IEnumerable? names = null")]
    [InlineData("IAsyncEnumerable<string>? names = null")]
    [InlineData("IReadOnlyCollection<string>? values = null")]
    [InlineData("IReadOnlyDictionary<string, int>? values = null")]
    [InlineData("IList<string>? values = null")]
    [InlineData("ICollection<string>? values = null")]
    [InlineData("IDictionary<string, int>? values = null")]
    [InlineData("ISet<string>? values = null")]
    [InlineData("Func<IFoo>? factory = null")]
    [InlineData("IFoo foo")]
    [InlineData("IOptionsMonitor<ArcanumSettings> settings")]
    public void The_pattern_ignores_collection_data_and_parameters_that_are_not_nullable_defaults(string parameter) =>
        Assert.False(
            NullableInterfaceDefault.IsMatch($"({parameter})"),
            $"'{parameter}' should not be read as a nullable interface default.");

    [Fact]
    public void Every_nullable_interface_constructor_default_is_removed_or_allowed_with_a_reason()
    {
        List<string> offenders =
        [
            .. NullableInterfaceDefaults()
                .Where(static found => !Allowed.ContainsKey(found.Key))
                .Select(static found => $"{found.Key} is a {found.InterfaceType} defaulting to null"),
        ];

        // Named rather than counted. Assert.Empty truncates each entry at fifty characters and prints
        // at most five of them, so a real regression arrived as a directory prefix and an ellipsis -
        // and two offenders under the same directory were indistinguishable from each other.
        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// An allow-list entry that no longer names a nullable interface default is removed, so the list only
    /// shrinks. A parameter that was made required (or removed) leaves its excuse behind otherwise, and the
    /// stale reason - "the container supplies it", "every use is null-safe" - stays on the record about a
    /// parameter that is no longer optional.
    /// </summary>
    [Fact]
    public void Every_allowed_entry_still_names_a_nullable_interface_default()
    {
        HashSet<string> present = [.. NullableInterfaceDefaults().Select(static found => found.Key)];

        string[] stale = [.. Allowed.Keys.Where(key => !present.Contains(key)).Order(StringComparer.Ordinal)];

        Assert.True(
            stale.Length == 0,
            string.Join("\n", stale.Select(static key => $"{key} is allowed but is no longer a nullable interface default")));
    }

    /// <summary>
    /// Every constructor parameter under <c>src/</c> that is an interface type defaulting to null, by the
    /// key the allow-list uses and the interface it names.
    /// </summary>
    private static List<(string Key, string InterfaceType)> NullableInterfaceDefaults()
    {
        List<(string Key, string InterfaceType)> found = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {
            foreach (ConstructorParameters constructor in ConstructorParameterLists.Of(source.Text))
            {
                foreach (Match match in NullableInterfaceDefault.Matches(constructor.ParameterList))
                {
                    found.Add(($"{source.RelativePath}:{constructor.DeclaringType}:{match.Groups[2].Value}", match.Groups[1].Value));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The Grimoire repository's composition is closed: one constructor, not public, and no parameter
    /// a caller may leave out.
    /// </summary>
    /// <remarks>
    /// The source inventory above reads text, so it can only see the shape it was written to match.
    /// This reads the compiled type, and it is the assertion that survives a rewrite of the parameter
    /// list into any spelling the regex would miss — a default written with a cast, a second
    /// constructor added beside the first. Both of the dependencies named here were supplied
    /// by nobody at some point in this repository's history: the ordinary-connection factory reached
    /// production as a stand-in that refused every acquisition, and the labelled-artifact guard reached
    /// it as a null that made an entire refusal path unreachable.
    /// </remarks>
    [Fact]
    public void The_Grimoire_repository_composes_through_one_closed_constructor()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(GrimoireRepository).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        Assert.True(
            constructor.IsAssembly,
            "The Grimoire repository's only constructor must stay internal: a public one would put the "
                + "Covenant mutation kernel and connection admission on the assembly's public surface.");

        ParameterInfo[] parameters = constructor.GetParameters();

        Assert.Contains(parameters, static parameter => parameter.ParameterType == typeof(IGrimoireOrdinaryConnectionFactory));

        Assert.Contains(parameters, static parameter => parameter.ParameterType == typeof(ICovenantLabeledArtifactTransactionGuard));

        Assert.Empty(
            parameters
                .Where(static parameter => parameter.HasDefaultValue)
                .Select(static parameter => $"{parameter.Name} is optional")
                .ToArray());
    }

    /// <summary>
    /// The attachment source resolver cannot be composed without a workspace registry, in any spelling.
    /// </summary>
    /// <remarks>
    /// A claimed workspace root is caller-asserted, and the registry is the only thing that can prove it
    /// names a registered workspace. An optional parameter let a composition omit it and leave the
    /// resolver with nothing to prove a claim against; the source inventory above would catch the usual
    /// spelling of that default, and this reads the compiled constructors so any other spelling, or a
    /// second constructor without the registry, is caught too.
    /// </remarks>
    [Fact]
    public void The_attachment_source_resolver_requires_a_workspace_registry() =>
        Assert.All(
            typeof(AttachmentSourceResolver).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static constructor => Assert.Contains(
                constructor.GetParameters(),
                static parameter => parameter.ParameterType == typeof(IWorkspaceRegistry) && !parameter.HasDefaultValue));

    /// <summary>
    /// The types that decide whether an automatic write may record a memory an operator erased.
    /// </summary>
    /// <remarks>
    /// Closed on purpose: a later chokepoint owner joins this list in the change that makes it one.
    /// </remarks>
    public static TheoryData<Type> ErasureChokepointOwners => new()
    {
        typeof(SagaMemoryStore),
        typeof(SagaErasureWriteGate),
        typeof(LexiconService),
        typeof(CovenantMutationKernel),
        typeof(CovenantStore),
        typeof(BackupRestoreService),
    };

    /// <summary>
    /// Every constructor of an erasure chokepoint owner takes the key provider, and none lets a caller
    /// leave it out.
    /// </summary>
    /// <remarks>
    /// The source inventory above would catch a nullable default spelled the usual way. This reads the
    /// compiled constructors, so a second constructor without the provider, or a default spelled any
    /// other way, is caught too: an owner composed without the provider could not tell an installation
    /// with evidence from one without, and would have to write either way.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ErasureChokepointOwners))]
    public void Every_erasure_chokepoint_owner_requires_the_key_provider(Type owner) =>
        Assert.All(
            owner.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.Contains(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == typeof(IMemoryErasureKeyProvider) && !parameter.HasDefaultValue));

    /// <summary>
    /// The types whose fault logging is the only trace of a storage failure mapped to a typed write
    /// failure or of a contained revocation callback.
    /// </summary>
    /// <remarks>
    /// Closed on purpose, and read from the compiled constructors because the source inventory above
    /// accepts an optional <c>ILogger&lt;T&gt;?</c> with the reason "diagnostic sink", which is true of
    /// most loggers and false of these: an owner built without its logger drops the one line that says a
    /// Saga or Covenant review write failed, which is every hand-built instance in a test and any
    /// factory that leaves the argument out. Listing one of these owners in the allow-list above would
    /// not make it optional here.
    /// </remarks>
    public static TheoryData<Type> FaultLoggingOwners => new()
    {
        typeof(SagaMemoryReviewService),
        typeof(CovenantMemoryReviewService),
        typeof(CovenantOperationGate),
    };

    /// <summary>
    /// Every constructor of a fault-logging owner takes its own <c>ILogger&lt;T&gt;</c>, and none lets a
    /// caller leave it out.
    /// </summary>
    [Theory]
    [MemberData(nameof(FaultLoggingOwners))]
    public void Every_fault_logging_owner_requires_its_logger(Type owner)
    {
        Type logger = typeof(ILogger<>).MakeGenericType(owner);

        Assert.All(
            owner.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.Contains(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == logger && !parameter.HasDefaultValue));
    }
}

/// <summary>
/// One constructor declaration: the type that declares it, and its parenthesised parameter list.
/// </summary>
internal readonly record struct ConstructorParameters(string DeclaringType, string ParameterList);

/// <summary>
/// The constructor parameter lists of one authored source file, each with its declaring type.
/// </summary>
/// <remarks>
/// The inventory's question is about constructors, so the text it matches has to be constructors. Run
/// over whole-file text the same pattern also matches a local initialized to <c>null</c> and an
/// optional method argument - twelve locals and two dozen method parameters in this tree - and an
/// inventory that reports those as constructor defaults makes a false statement in every line of its
/// own failure message, which is how a guard rail stops being read.
///
/// <para>The declaring type travels with the list because a file is not a type. One file here
/// declares both <c>ModelCommands</c> and <c>ProviderCommands</c>, and a key built from the file name
/// alone collapsed their two constructors into one entry - so removing either site left the other
/// silently exempt from an inventory that still looked complete.</para>
/// </remarks>
internal static class ConstructorParameterLists
{
    private static readonly Regex TypeName = new(
        @"\b(?:class|record|struct)\s+(\w+)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex PrimaryConstructor = new(
        @"\b(?:class|record|struct)\s+(\w+)\s*(?:<[^>\n]*>)?\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// A constructor declaration, whose modifiers are all optional.
    /// </summary>
    /// <remarks>
    /// Requiring a leading access modifier let <c>protected internal Foo(</c> and a modifier-less
    /// constructor - which is legal C#, and private by default - past the inventory entirely. The
    /// declared-type check below is what keeps the loosened pattern honest: an identifier followed by
    /// an open parenthesis is only read as a constructor when it names a type this file declares.
    /// </remarks>
    private static readonly Regex OrdinaryConstructor = new(
        @"(?:^|\n)[ \t]*(?:(?:public|internal|private|protected|static|unsafe|extern|partial|sealed|abstract)[ \t]+)*(\w+)[ \t]*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Yields every constructor declared in the supplied source, with its declaring type.
    /// </summary>
    internal static IReadOnlyList<ConstructorParameters> Of(string text)
    {
        HashSet<string> declaredTypes = new(StringComparer.Ordinal);

        foreach (Match match in TypeName.Matches(text))
        {
            _ = declaredTypes.Add(match.Groups[1].Value);
        }

        List<ConstructorParameters> constructors = [];

        foreach (Match match in PrimaryConstructor.Matches(text))
        {
            constructors.Add(new ConstructorParameters(
                match.Groups[1].Value,
                ParenthesisedRun(text, match.Index + match.Length - 1)));
        }

        foreach (Match match in OrdinaryConstructor.Matches(text))
        {
            // A method cannot share its enclosing type's name, so an identifier that does name a
            // declared type and is immediately called is a constructor declaration.
            if (declaredTypes.Contains(match.Groups[1].Value))
            {
                constructors.Add(new ConstructorParameters(
                    match.Groups[1].Value,
                    ParenthesisedRun(text, match.Index + match.Length - 1)));
            }
        }

        return constructors;
    }

    /// <summary>
    /// The text from an opening parenthesis to the one that closes it.
    /// </summary>
    /// <remarks>
    /// Depth-counted rather than matched to the next <c>)</c>, because a parameter's default value can
    /// itself be parenthesised and a scanner that stopped at the first close would cut the list short -
    /// silently exempting every parameter after it.
    /// </remarks>
    private static string ParenthesisedRun(string text, int opening)
    {
        StringBuilder run = new();

        int depth = 0;

        for (int index = opening; index < text.Length; index++)
        {
            _ = run.Append(text[index]);

            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    break;
                }
            }
        }

        return run.ToString();
    }
}

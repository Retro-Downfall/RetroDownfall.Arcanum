using System.Reflection;

using System.Text;

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

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
    /// an observation - the two labelled-artifact guards, the two sensitive-artifact purgers, and the
    /// workspace registry behind the attachment root check. None of them is a live bypass: each
    /// reason names the registration or the single construction site that supplies it, so the claim
    /// this list makes is that the container is what keeps the refusal reachable, not that the
    /// parameter is harmless when null. What the list buys is the ninety-ninth entry - a new optional
    /// interface dependency cannot enter <c>src/</c> without someone writing down which of those four
    /// things is true of it, and which of those two claims it is making.</para>
    /// </remarks>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:encryptedBlobDiagnostics"] = "owner is container-activated and IEncryptedBlobDiagnostics is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:operationDiagnosticsSource"] = "every use of the IDurableOperationDiagnostics is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:providerApiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:ArcanumHealthChecker:workspaceCheckCapabilityReporter"] = "every use of the IWorkspaceCheckCapabilityReporter is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:ChatClientFactory:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:ChatClientFactory:familiarProcessRunner"] = "the null coalesces to a constructed default at the use site, so no host runs without a IFamiliarProcessRunner",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:ChatClientFactory:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:ContextCompressionService:modelTokenEstimator"] = "the null coalesces to a constructed default at the use site, so no host runs without a IModelTokenEstimator",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:ContextCompressionService:purger"] = "the owner is registered at ApiBootstrapper.cs:426 and ICovenantSensitiveArtifactPurger at ServiceCollectionExtensions.cs:1956; the null check at ContextCompressionService.cs:228 skips the group-safe purge entirely (and is what keeps the null-forgiving dereference at :92 unreached), so the container supplying it is what keeps that purge reachable",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingGeneratorFactory.cs:EmbeddingGeneratorFactory:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:GrimoireTurnWriter:turnCommitter"] = "every use of the IGrimoireTurnCommitter is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs:ModelCallExecutor:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:ToolExecutionPipeline:attachmentSourceResolver"] = "every use of the IAttachmentSourceResolver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:ToolExecutionPipeline:covenantAuthority"] = "every use of the ICovenantAuthoritySnapshotProvider is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:ToolExecutionPipeline:toolResultMaterializer"] = "every use of the IToolResultMaterializer is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumReadUrlTool.cs:ArcanumReadUrlTool:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:ArcanumSpellScriptTool:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:ArcanumSpellScriptTool:resourceLimiter"] = "the only construction in src is WizardIntelligenceProvider.cs:6525, which passes the resolved IProcessResourceLimiter",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:ArcanumSpellScriptTool:sanctumGuard"] = "the null coalesces to a constructed default at the use site, so no host runs without a ISanctumGuard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumWebSearchTool.cs:ArcanumWebSearchTool:logger"] = "diagnostic sink; absence degrades logging, not a guard",

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

        ["src/RetroDownfall.Arcanum.Cli/Commands/Conclave/ApprenticeCommands.cs:ApprenticeCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Configuration/ModelProviderCommands.cs:ModelCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Configuration/ModelProviderCommands.cs:ProviderCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/CampaignCommands.cs:CampaignCodexCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/CampaignCommands.cs:CampaignCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs:MemoryCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/PromptCommands.cs:PromptCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/SessionCommands.cs:SessionCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/SpellCommands.cs:SpellCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Commands/WatchCommands.cs:WatchCommands:resourceCatalog"] = "every use of the ICliResourceCatalog is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:CliSessionManager:contextStore"] = "every use of the ICliContextStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:CliSessionManager:mutationBoundary"] = "every use of the IArcanumClientMutationBoundary is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Cli/Services/ConsoleAskHumanCoordinator.cs:ConsoleAskHumanCoordinator:diagnosticConsole"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupPrompt.cs:ConsoleSetupPrompt:secretPrompt"] = "the null coalesces to a constructed default at the use site, so no host runs without a IBackupPassphrasePrompt",

        ["src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AClientService.cs:A2AClientService:scopeFactory"] = "every use of the IServiceScopeFactory is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Configuration/ConfigurationPresetService.cs:ConfigurationPresetService:credentialStore"] = "every use of the IWebResearchCredentialStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs:CampaignPathMarkerLifecycle:recoveryKeys"] = "the only construction in src is the DI factory at ServiceCollectionExtensions.cs:1621, which passes the registered ICampaignRootIdentityRecoveryKeyProvider",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:attachmentStore"] = "every use of the ISessionAttachmentStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:covenantErasureEffectDigests"] = "the null coalesces to a constructed default at the use site, so no host runs without a ICovenantErasureEffectDigestCalculator",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:covenantGate"] = "owner is container-activated and ICovenantOperationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:daemonExecutions"] = "every use of the IDaemonExecutionRepository is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:daemonMutationGate"] = "owner is container-activated and IDaemonExecutionMutationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:factoryApplyRequestDigests"] = "the null coalesces to a constructed default at the use site, so no host runs without a ICovenantFactoryErasureApplyRequestDigestCalculator",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:managedLogMutationGate"] = "owner is container-activated and IManagedLogMutationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:DataRetentionService:policyStore"] = "the null coalesces to a constructed default at the use site, so no host runs without a IDataRetentionPolicyStore",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:LongRunningOperationStore:covenantDrain"] = "every use of the ICovenantConnectionDrain is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:SagaMemoryStore:labeledArtifactGuard"] = "the owner is registered at ServiceCollectionExtensions.cs:1225 and ICovenantLabeledArtifactGuard at :1949; a null skips the label guard in DeleteAsync (SagaMemoryStore.cs:518) and DeleteAllAsync (:622), so the container supplying it is what keeps those refusals reachable",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:SessionAttachmentStore:blobStore"] = "owner is container-activated and IEncryptedBlobStore is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:SessionAttachmentStore:indexQueue"] = "every use of the ISessionAttachmentIndexQueue is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:SessionAttachmentStore:sourceResolver"] = "every use of the IAttachmentSourceResolver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs:GrimoireDatabaseHostedService:transitionRecovery"] = "IGrimoireOfflineTransitionStartupRecovery is registered at ServiceCollectionExtensions.cs and the composed host factory passes it; absence does not open a bypass - the else arm falls back to InstallationResetHostStartupAdmission.LeavesTransitionUnfinished, which is the refusal this parameter replaced, so a null resumes nothing and admits nothing",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs:GrimoireDatabaseHostedService:startupProbe"] = "the null coalesces to InstallationStartupProbe.CreateDefault() at GrimoireDatabaseHostedService.cs:37, and the only construction in src - the DI factory at ServiceCollectionExtensions.cs:1058 - never passes one, so that default is the production probe",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs:HostToolsMarkerPairResetCoordinator:managedFiles"] = "every use of the IFullInstallationResetManagedFileReconciler is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:identityReader"] = "owner is container-activated and IInstallationResetDatabaseIdentityReader is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:pairReader"] = "owner is container-activated and IInstallationResetHostProcessToolsPairReader is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:preDataMutation"] = "owner is container-activated and IInstallationResetPreDataMutation is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:remediationVerifier"] = "every use of the IFullInstallationResetRemediationAttestationVerifier is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:stateRoots"] = "owner is container-activated and IInstallationResetStateRoots is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:stoppedHostDataService"] = "every use of the IInstallationResetStoppedHostDataService is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:stoppedHostPairReader"] = "every use of the IInstallationResetStoppedHostProcessToolsPairReader is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:InstallationResetService:workspaceResolver"] = "every use of the IInstallationResetWorkspaceResolver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellCatalogService.cs:SpellCatalogService:progressObserver"] = "every use of the ISpellCatalogProgressObserver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs:LexiconService:labeledArtifactGuard"] = "the owner is registered at ServiceCollectionExtensions.cs:1234 and ICovenantLabeledArtifactGuard at :1949; a null skips the label guard on delete (LexiconService.cs:236), so the container supplying it is what keeps that refusal reachable",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs:ArcanumInternalToolServer:workspaceCheckRuntime"] = "the null coalesces to a constructed default at the use site, so no host runs without a IWorkspaceCheckRuntime",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/InProcessMcpTransport.cs:InProcessMcpTransport:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:McpBridgeTool:fallbackClient"] = "every use of the IMcpClient is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:McpBridgeTool:fallbackLogger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/SdkMcpClientWrapper.cs:SdkMcpClientWrapper:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs:LongRunningOperationReconciler:scopeFactory"] = "every use of the IServiceScopeFactory is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:SessionRepository:attachmentIndexQueue"] = "every use of the ISessionAttachmentIndexQueue is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Resilience/ProviderHealthProbe.cs:ProviderHealthProbe:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/AttachmentSourceResolver.cs:AttachmentSourceResolver:workspaceRegistry"] = "the owner is registered at ServiceCollectionExtensions.cs:322 and :1217 and IWorkspaceRegistry at :1404; a null returns Success(claimedRoot) at AttachmentSourceResolver.cs:890 on a claim a registry can answer Unsafe for, so the container supplying it is what keeps that refusal reachable",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/SanctumGuard.cs:SanctumGuard:dnsResolver"] = "the null coalesces to a constructed default at the use site, so no host runs without a IDnsResolver",

        ["src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs:EmbeddingsResetService:purger"] = "the only construction in src is the DI factory at ServiceCollectionExtensions.cs:1015, which passes the ICovenantSensitiveArtifactPurger registered at :1956; a null would return an empty purge outcome (EmbeddingsResetService.cs:107), so the factory passing it is what keeps the purge reachable",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceCheckRuntime.cs:WorkspaceCheckRuntime:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:WorkspaceSearchEngine:progressObserver"] = "every use of the IWorkspaceSearchProgressObserver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:WorkspaceSearchEngine:spillObserver"] = "every use of the IWorkspaceSearchLineSpillObserver is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Compendium.Ux/Services/FamiliarProbeClient.cs:FamiliarProbeClient:httpClientFactory"] = "the null coalesces to a constructed default at the use site, so no host runs without a IHttpClientFactory",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:ConfigurationViewModel:presetService"] = "owner is container-activated and IConfigurationPresetService is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:ConfigurationViewModel:probeClient"] = "owner is container-activated and IFamiliarProbeClient is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ProvidersSectionViewModel.cs:ProviderViewModel:probeClient"] = "every use of the IFamiliarProbeClient is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ProvidersSectionViewModel.cs:ProvidersSectionViewModel:probeClient"] = "every use of the IFamiliarProbeClient is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:MarkdigAstAvaloniaRenderer:highlighter"] = "the null coalesces to a constructed default at the use site, so no host runs without a IMarkdownCodeHighlighter",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:MarkdigAstAvaloniaRenderer:images"] = "the null coalesces to a constructed default at the use site, so no host runs without a IMarkdownImageResolver",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Docking/DockLayoutViewModel.cs:DockLayoutViewModel:settingsStore"] = "every use of the ITheForgeSettingsStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/ComparisonWorkbenchViewModel.cs:ComparisonWorkbenchViewModel:traceStore"] = "IInferenceTraceStore is registered, and the owner is hand-constructed only by WorkbenchDocumentFactory (IWorkbenchDocumentFactory.cs:258, container-activated), which always passes the registered store",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/InferenceTraceViewModel.cs:InferenceTraceViewModel:fileDialog"] = "every use of the IArtifactFileDialogService is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/InferenceTraceViewModel.cs:InferenceTraceViewModel:store"] = "every use of the IInferenceTraceStore is null-safe; absence disables an observation, not a refusal",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/MarkdownDocumentViewModel.cs:MarkdownDocumentViewModel:contentStore"] = "every use of the IMarkdownDocumentContentStore is null-safe; absence disables an observation, not a refusal",
    };

    private static readonly Regex NullableInterfaceDefault = new(
        @"\b(I[A-Z]\w+)\?\s+(\w+)\s*=\s*null\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_nullable_interface_constructor_default_is_removed_or_allowed_with_a_reason()
    {

        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {

            foreach (ConstructorParameters constructor in ConstructorParameterLists.Of(source.Text))
            {

                foreach (Match match in NullableInterfaceDefault.Matches(constructor.ParameterList))
                {

                    string key = $"{source.RelativePath}:{constructor.DeclaringType}:{match.Groups[2].Value}";

                    if (!Allowed.ContainsKey(key))
                    {

                        offenders.Add($"{key} is a {match.Groups[1].Value} defaulting to null");

                    }

                }

            }

        }

        // Named rather than counted. Assert.Empty truncates each entry at fifty characters and prints
        // at most five of them, so a real regression arrived as a directory prefix and an ellipsis -
        // and two offenders under the same directory were indistinguishable from each other.
        Assert.True(
            offenders.Count == 0,
            string.Join("\n", offenders.Order(StringComparer.Ordinal)));

    }

    /// <summary>
    /// The Grimoire repository's composition is closed: one constructor, not public, and no parameter
    /// a caller may leave out.
    /// </summary>
    /// <remarks>
    /// The source inventory above reads text, so it can only see the shape it was written to match.
    /// This reads the compiled type, and it is the assertion that survives a rewrite of the parameter
    /// list into any spelling the regex would miss — a generic interface, a differently-spaced default,
    /// a second constructor added beside the first. Both of the dependencies named here were supplied
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

        Assert.Contains(parameters, static parameter => parameter.ParameterType == typeof(ICovenantLabeledArtifactGuard));

        Assert.Empty(
            parameters
                .Where(static parameter => parameter.HasDefaultValue)
                .Select(static parameter => $"{parameter.Name} is optional")
                .ToArray());

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

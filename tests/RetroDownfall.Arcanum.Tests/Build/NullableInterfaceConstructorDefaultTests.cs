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
    /// A baseline, not an endorsement. Every entry was present when this inventory was written and
    /// each reason states the evidence that put it here rather than a judgement that it is harmless:
    /// a diagnostic sink whose absence costs a log line; an owner the container activates whose
    /// dependency is registered, which therefore receives it in any composed host; or an interface no
    /// composition registers at all, which means the default is what every host receives - the shape
    /// that hid the labelled-artifact guard, and a candidate for the packet that owns its file.
    /// Registration is read from the files that wire a container, not from the whole tree: a type
    /// named as a generic argument somewhere under <c>src/</c> is not a type the container can supply. The list only ever shrinks. What it buys today is the ninety-sixth
    /// entry: a new optional interface dependency cannot be added anywhere under <c>src/</c> without
    /// someone writing down why.
    /// </remarks>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:encryptedBlobDiagnostics"] = "owner is container-activated and IEncryptedBlobDiagnostics is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:operationDiagnosticsSource"] = "owner is container-activated and IDurableOperationDiagnostics is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:providerApiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:workspaceCheckCapabilityReporter"] = "owner is container-activated and IWorkspaceCheckCapabilityReporter is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:familiarProcessRunner"] = "owner is container-activated and IFamiliarProcessRunner is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:modelTokenEstimator"] = "owner is container-activated and IModelTokenEstimator is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:purger"] = "owner is container-activated and ICovenantSensitiveArtifactPurger is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingGeneratorFactory.cs:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:turnCommitter"] = "owner is container-activated and IGrimoireTurnCommitter is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:attachmentSourceResolver"] = "owner is container-activated and IAttachmentSourceResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:covenantAuthority"] = "owner is container-activated and ICovenantAuthoritySnapshotProvider is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:toolResultMaterializer"] = "owner is container-activated and IToolResultMaterializer is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumReadUrlTool.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:resourceLimiter"] = "owner is constructed by hand in src; whether each site passes IProcessResourceLimiter is unverified - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:sanctumGuard"] = "owner is constructed by hand in src; whether each site passes ISanctumGuard is unverified - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumWebSearchTool.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:attachmentMemoryProvenanceStore"] = "IAttachmentMemoryProvenanceStore is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:budgetReservationService"] = "IBudgetReservationService is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:healthTracker"] = "IProviderHealthTracker is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:modelCallExecutor"] = "IModelCallExecutor is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:modelTokenEstimator"] = "IModelTokenEstimator is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:sessionAttachmentRetrieval"] = "ISessionAttachmentRetrievalService is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:subagentRunner"] = "ISubagentRunner is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:tapestryStore"] = "ITapestryStore is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:turnRunWriter"] = "ITurnRunWriter is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:webResearchProviderCatalog"] = "IWebResearchProviderCatalog is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Conclave/ApprenticeCommands.cs:resourceCatalog"] = "owner is container-activated and ICliResourceCatalog is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Configuration/ModelProviderCommands.cs:resourceCatalog"] = "ICliResourceCatalog is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/CampaignCommands.cs:resourceCatalog"] = "owner is container-activated and ICliResourceCatalog is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs:resourceCatalog"] = "owner is container-activated and ICliResourceCatalog is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/PromptCommands.cs:resourceCatalog"] = "owner is container-activated and ICliResourceCatalog is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/SessionCommands.cs:resourceCatalog"] = "owner is container-activated and ICliResourceCatalog is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/SpellCommands.cs:resourceCatalog"] = "owner is container-activated and ICliResourceCatalog is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Commands/WatchCommands.cs:resourceCatalog"] = "owner is container-activated and ICliResourceCatalog is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:contextStore"] = "owner is container-activated and ICliContextStore is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:mutationBoundary"] = "owner is container-activated and IArcanumClientMutationBoundary is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Services/ConsoleAskHumanCoordinator.cs:diagnosticConsole"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupPrompt.cs:secretPrompt"] = "IBackupPassphrasePrompt has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AClientService.cs:scopeFactory"] = "IServiceScopeFactory has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Configuration/ConfigurationPresetService.cs:credentialStore"] = "owner is container-activated and IWebResearchCredentialStore is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs:recoveryKeys"] = "owner is constructed by hand in src; whether each site passes ICampaignRootIdentityRecoveryKeyProvider is unverified - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:attachmentStore"] = "owner is container-activated and ISessionAttachmentStore is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:covenantErasureEffectDigests"] = "owner is container-activated and ICovenantErasureEffectDigestCalculator is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:covenantGate"] = "owner is container-activated and ICovenantOperationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:daemonExecutions"] = "owner is container-activated and IDaemonExecutionRepository is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:daemonMutationGate"] = "owner is container-activated and IDaemonExecutionMutationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:factoryApplyRequestDigests"] = "owner is container-activated and ICovenantFactoryErasureApplyRequestDigestCalculator is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:managedLogMutationGate"] = "owner is container-activated and IManagedLogMutationGate is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:policyStore"] = "owner is container-activated and IDataRetentionPolicyStore is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:covenantDrain"] = "owner is container-activated and ICovenantConnectionDrain is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:labeledArtifactGuard"] = "owner is container-activated and ICovenantLabeledArtifactGuard is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:blobStore"] = "owner is container-activated and IEncryptedBlobStore is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:indexQueue"] = "owner is container-activated and ISessionAttachmentIndexQueue is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:sourceResolver"] = "owner is container-activated and IAttachmentSourceResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs:startupProbe"] = "owner is constructed by hand in src; whether each site passes IInstallationStartupProbe is unverified - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs:managedFiles"] = "owner is constructed by hand in src; whether each site passes IFullInstallationResetManagedFileReconciler is unverified - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:identityReader"] = "owner is container-activated and IInstallationResetDatabaseIdentityReader is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:pairReader"] = "owner is container-activated and IInstallationResetHostProcessToolsPairReader is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:preDataMutation"] = "owner is container-activated and IInstallationResetPreDataMutation is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:remediationVerifier"] = "owner is container-activated and IFullInstallationResetRemediationAttestationVerifier is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:stateRoots"] = "owner is container-activated and IInstallationResetStateRoots is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:stoppedHostDataService"] = "owner is container-activated and IInstallationResetStoppedHostDataService is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:stoppedHostPairReader"] = "owner is container-activated and IInstallationResetStoppedHostProcessToolsPairReader is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:workspaceResolver"] = "owner is container-activated and IInstallationResetWorkspaceResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellCatalogService.cs:progressObserver"] = "ISpellCatalogProgressObserver has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs:labeledArtifactGuard"] = "owner is container-activated and ICovenantLabeledArtifactGuard is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs:workspaceCheckRuntime"] = "IWorkspaceCheckRuntime has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/InProcessMcpTransport.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:fallbackClient"] = "IMcpClient has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:fallbackLogger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/SdkMcpClientWrapper.cs:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs:scopeFactory"] = "IServiceScopeFactory has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:attachmentIndexQueue"] = "owner is constructed by hand in src; whether each site passes ISessionAttachmentIndexQueue is unverified - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Resilience/ProviderHealthProbe.cs:apiKeyResolver"] = "owner is container-activated and IProviderApiKeyResolver is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/AttachmentSourceResolver.cs:workspaceRegistry"] = "owner is container-activated and IWorkspaceRegistry is registered; the container supplies it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/SanctumGuard.cs:dnsResolver"] = "IDnsResolver has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs:purger"] = "owner is constructed by hand in src; whether each site passes ICovenantSensitiveArtifactPurger is unverified - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceCheckRuntime.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:progressObserver"] = "IWorkspaceSearchProgressObserver has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:spillObserver"] = "IWorkspaceSearchLineSpillObserver has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Compendium.Ux/Services/FamiliarProbeClient.cs:httpClientFactory"] = "IHttpClientFactory has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:presetService"] = "IConfigurationPresetService is registered, but the owner is neither container-activated nor constructed in src",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:probeClient"] = "IFamiliarProbeClient has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ProvidersSectionViewModel.cs:probeClient"] = "IFamiliarProbeClient has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:highlighter"] = "IMarkdownCodeHighlighter has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:images"] = "IMarkdownImageResolver has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Docking/DockLayoutViewModel.cs:settingsStore"] = "ITheForgeSettingsStore has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/ComparisonWorkbenchViewModel.cs:traceStore"] = "IInferenceTraceStore has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/InferenceTraceViewModel.cs:fileDialog"] = "IArtifactFileDialogService has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/InferenceTraceViewModel.cs:store"] = "IInferenceTraceStore has no registration in any composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/MarkdownDocumentViewModel.cs:contentStore"] = "IMarkdownDocumentContentStore has no registration in any composition; the default is what every host receives - same shape as V-1",
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

            foreach (string parameterList in ConstructorParameterLists.Of(source.Text))
            {

                foreach (Match match in NullableInterfaceDefault.Matches(parameterList))
                {

                    string key = $"{source.RelativePath}:{match.Groups[2].Value}";

                    if (!Allowed.ContainsKey(key))
                    {

                        offenders.Add($"{key} is a {match.Groups[1].Value} defaulting to null");

                    }

                }

            }

        }

        Assert.Empty(offenders);

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
/// The constructor parameter lists of one authored source file.
/// </summary>
/// <remarks>
/// The inventory's question is about constructors, so the text it matches has to be constructors. Run
/// over whole-file text the same pattern also matches a local initialized to <c>null</c> and an
/// optional method argument — twelve locals and two dozen method parameters in this tree — and an
/// inventory that reports those as constructor defaults makes a false statement in every line of its
/// own failure message, which is how a guard rail stops being read.
///
/// <para>Both declaration shapes are yielded: the primary constructor on the type declaration, which
/// is how this repository composes services, and the ordinary constructor body, which is how the
/// types with an internal composition declare theirs.</para>
/// </remarks>
internal static class ConstructorParameterLists
{

    private static readonly Regex TypeName = new(
        @"\b(?:class|record|struct)\s+(\w+)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex PrimaryConstructor = new(
        @"\b(?:class|record|struct)\s+\w+\s*(?:<[^>\n]*>)?\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex OrdinaryConstructor = new(
        @"(?:^|\n)[ \t]*(?:public|internal|private|protected)(?:[ \t]+(?:sealed|partial|static|unsafe|abstract|override|extern))*[ \t]+(\w+)[ \t]*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Yields the parenthesised parameter list of every constructor declared in the supplied source.
    /// </summary>
    internal static IReadOnlyList<string> Of(string text)
    {

        HashSet<string> declaredTypes = new(StringComparer.Ordinal);

        foreach (Match match in TypeName.Matches(text))
        {

            _ = declaredTypes.Add(match.Groups[1].Value);

        }

        List<int> openings = [];

        foreach (Match match in PrimaryConstructor.Matches(text))
        {

            openings.Add(match.Index + match.Length - 1);

        }

        foreach (Match match in OrdinaryConstructor.Matches(text))
        {

            // A method named like a type is not a constructor, and a constructor is the only member
            // whose name is its own type's. Anything else with this shape is a method declaration
            // whose return type happens to precede it, and it is not what this inventory asks about.
            if (declaredTypes.Contains(match.Groups[1].Value))
            {

                openings.Add(match.Index + match.Length - 1);

            }

        }

        List<string> parameterLists = [];

        foreach (int opening in openings)
        {

            parameterLists.Add(ParenthesisedRun(text, opening));

        }

        return parameterLists;

    }

    /// <summary>
    /// The text from an opening parenthesis to the one that closes it.
    /// </summary>
    /// <remarks>
    /// Depth-counted rather than matched to the next <c>)</c>, because a parameter's default value can
    /// itself be parenthesised and a scanner that stopped at the first close would cut the list short —
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

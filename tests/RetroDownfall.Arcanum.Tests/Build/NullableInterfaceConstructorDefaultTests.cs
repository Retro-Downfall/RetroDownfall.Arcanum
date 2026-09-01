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
    /// a diagnostic sink whose absence costs a log line; an owner the container activates, which
    /// therefore receives the dependency in any composed host; or an owner whose callers construct it
    /// by hand, which is the shape that hid the labelled-artifact guard and is a candidate for the
    /// packet that owns its file. The list only ever shrinks. What it buys today is the ninety-sixth
    /// entry: a new optional interface dependency cannot be added anywhere under <c>src/</c> without
    /// someone writing down why.
    /// </remarks>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:encryptedBlobDiagnostics"] = "owner is type-registered and IEncryptedBlobDiagnostics is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:operationDiagnosticsSource"] = "owner is type-registered and IDurableOperationDiagnostics is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:providerApiKeyResolver"] = "owner is type-registered and IProviderApiKeyResolver is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Health/ArcanumHealthChecker.cs:workspaceCheckCapabilityReporter"] = "owner is type-registered and IWorkspaceCheckCapabilityReporter is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:apiKeyResolver"] = "owner is type-registered and IProviderApiKeyResolver is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:familiarProcessRunner"] = "owner is type-registered and IFamiliarProcessRunner is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ChatClientFactory.cs:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:modelTokenEstimator"] = "owner is type-registered and IModelTokenEstimator is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ContextCompressionService.cs:purger"] = "owner is type-registered and ICovenantSensitiveArtifactPurger is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/EmbeddingGeneratorFactory.cs:apiKeyResolver"] = "owner is type-registered and IProviderApiKeyResolver is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/GrimoireTurnWriter.cs:turnCommitter"] = "owner is type-registered and IGrimoireTurnCommitter is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ModelCallExecutor.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:attachmentSourceResolver"] = "owner is type-registered and IAttachmentSourceResolver is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:covenantAuthority"] = "owner is type-registered and ICovenantAuthoritySnapshotProvider is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/ToolExecutionPipeline.cs:toolResultMaterializer"] = "owner is type-registered and IToolResultMaterializer is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumReadUrlTool.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:resourceLimiter"] = "owner is constructed by hand in src and its call sites pass IProcessResourceLimiter; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumSpellScriptTool.cs:sanctumGuard"] = "owner is constructed by hand in src and its call sites pass ISanctumGuard; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/Tools/ArcanumWebSearchTool.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:attachmentMemoryProvenanceStore"] = "pre-existing optional IAttachmentMemoryProvenanceStore; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:budgetReservationService"] = "pre-existing optional IBudgetReservationService; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:healthTracker"] = "pre-existing optional IProviderHealthTracker; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:modelCallExecutor"] = "pre-existing optional IModelCallExecutor; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:modelTokenEstimator"] = "pre-existing optional IModelTokenEstimator; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:sessionAttachmentRetrieval"] = "pre-existing optional ISessionAttachmentRetrievalService; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:subagentRunner"] = "pre-existing optional ISubagentRunner; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:tapestryStore"] = "pre-existing optional ITapestryStore; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:turnRunWriter"] = "pre-existing optional ITurnRunWriter; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Api/Intelligence/WizardIntelligenceProvider.cs:webResearchProviderCatalog"] = "pre-existing optional IWebResearchProviderCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Conclave/ApprenticeCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Configuration/ModelProviderCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/CampaignCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/MemoryCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/PromptCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/SessionCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/Tower/SpellCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Commands/WatchCommands.cs:resourceCatalog"] = "pre-existing optional ICliResourceCatalog; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:contextStore"] = "owner is type-registered and ICliContextStore is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs:mutationBoundary"] = "owner is type-registered and IArcanumClientMutationBoundary is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Cli/Services/ConsoleAskHumanCoordinator.cs:diagnosticConsole"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupPrompt.cs:secretPrompt"] = "IBackupPassphrasePrompt is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/A2A/A2AClientService.cs:scopeFactory"] = "owner is type-registered and IServiceScopeFactory is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Configuration/ConfigurationPresetService.cs:credentialStore"] = "owner is type-registered and IWebResearchCredentialStore is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Covenant/CampaignPathMarkerLifecycle.cs:recoveryKeys"] = "owner is constructed by hand in src and its call sites pass ICampaignRootIdentityRecoveryKeyProvider; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:attachmentStore"] = "owner is type-registered and ISessionAttachmentStore is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:covenantErasureEffectDigests"] = "owner is type-registered and ICovenantErasureEffectDigestCalculator is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:covenantGate"] = "owner is type-registered and ICovenantOperationGate is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:daemonExecutions"] = "owner is type-registered and IDaemonExecutionRepository is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:daemonMutationGate"] = "owner is type-registered and IDaemonExecutionMutationGate is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:factoryApplyRequestDigests"] = "owner is type-registered and ICovenantFactoryErasureApplyRequestDigestCalculator is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:managedLogMutationGate"] = "owner is type-registered and IManagedLogMutationGate is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/DataRetentionService.cs:policyStore"] = "owner is type-registered and IDataRetentionPolicyStore is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/LongRunningOperationStore.cs:covenantDrain"] = "owner is type-registered and ICovenantConnectionDrain is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SagaMemoryStore.cs:labeledArtifactGuard"] = "owner is type-registered and ICovenantLabeledArtifactGuard is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:blobStore"] = "owner is type-registered and IEncryptedBlobStore is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:indexQueue"] = "owner is type-registered and ISessionAttachmentIndexQueue is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Data/SessionAttachmentStore.cs:sourceResolver"] = "owner is type-registered and IAttachmentSourceResolver is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Hosting/GrimoireDatabaseHostedService.cs:startupProbe"] = "owner is constructed by hand in src and its call sites pass IInstallationStartupProbe; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/HostToolsMarkerPairResetCoordinator.cs:managedFiles"] = "owner is constructed by hand in src and its call sites pass IFullInstallationResetManagedFileReconciler; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:identityReader"] = "owner is type-registered and IInstallationResetDatabaseIdentityReader is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:pairReader"] = "owner is type-registered and IInstallationResetHostProcessToolsPairReader is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:preDataMutation"] = "owner is type-registered and IInstallationResetPreDataMutation is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:remediationVerifier"] = "owner is type-registered and IFullInstallationResetRemediationAttestationVerifier is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:stateRoots"] = "owner is type-registered and IInstallationResetStateRoots is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:stoppedHostDataService"] = "owner is type-registered and IInstallationResetStoppedHostDataService is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:stoppedHostPairReader"] = "owner is type-registered and IInstallationResetStoppedHostProcessToolsPairReader is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/InstallationReset/InstallationResetService.cs:workspaceResolver"] = "owner is type-registered and IInstallationResetWorkspaceResolver is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Intelligence/Spells/SpellCatalogService.cs:progressObserver"] = "ISpellCatalogProgressObserver is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Lexicon/LexiconService.cs:labeledArtifactGuard"] = "owner is type-registered and ICovenantLabeledArtifactGuard is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/ArcanumInternalToolServer.cs:workspaceCheckRuntime"] = "IWorkspaceCheckRuntime is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/InProcessMcpTransport.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:fallbackClient"] = "owner is constructed by hand in src and its call sites pass IMcpClient; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/McpBridgeTool.cs:fallbackLogger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Mcp/SdkMcpClientWrapper.cs:loggerFactory"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Operations/LongRunningOperationReconciler.cs:scopeFactory"] = "owner is type-registered and IServiceScopeFactory is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs:attachmentIndexQueue"] = "owner is constructed by hand in src and its call sites pass ISessionAttachmentIndexQueue; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Resilience/ProviderHealthProbe.cs:apiKeyResolver"] = "owner is type-registered and IProviderApiKeyResolver is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/AttachmentSourceResolver.cs:workspaceRegistry"] = "owner is type-registered and IWorkspaceRegistry is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Arcanum.Infrastructure/Security/SanctumGuard.cs:dnsResolver"] = "IDnsResolver is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Weave/EmbeddingsResetService.cs:purger"] = "owner is constructed by hand in src and its call sites pass ICovenantSensitiveArtifactPurger; same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceCheckRuntime.cs:logger"] = "diagnostic sink; absence degrades logging, not a guard",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:progressObserver"] = "IWorkspaceSearchProgressObserver is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Arcanum.Infrastructure/Workspaces/CodingTools/WorkspaceSearchEngine.cs:spillObserver"] = "IWorkspaceSearchLineSpillObserver is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Compendium.Ux/Services/FamiliarProbeClient.cs:httpClientFactory"] = "IHttpClientFactory is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:presetService"] = "owner is type-registered and IConfigurationPresetService is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ConfigurationViewModel.cs:probeClient"] = "owner is type-registered and IFamiliarProbeClient is registered; the container injects it in a composed host",

        ["src/RetroDownfall.Compendium.Ux/ViewModels/ProvidersSectionViewModel.cs:probeClient"] = "owner is constructed by hand in src and its call sites pass IFamiliarProbeClient; same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:highlighter"] = "IMarkdownCodeHighlighter is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/Markdown/MarkdigAstAvaloniaRenderer.cs:images"] = "IMarkdownImageResolver is registered in no composition; the default is what every host receives - same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Docking/DockLayoutViewModel.cs:settingsStore"] = "owner is constructed by hand in src and its call sites pass ITheForgeSettingsStore; same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/ComparisonWorkbenchViewModel.cs:traceStore"] = "pre-existing optional IInferenceTraceStore; owner is neither type-registered nor constructed in src",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/InferenceTraceViewModel.cs:fileDialog"] = "owner is constructed by hand in src and its call sites pass IArtifactFileDialogService; same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/InferenceTraceViewModel.cs:store"] = "owner is constructed by hand in src and its call sites pass IInferenceTraceStore; same shape as V-1",

        ["src/RetroDownfall.TheForge.Ux/ViewModels/Workbench/MarkdownDocumentViewModel.cs:contentStore"] = "owner is constructed by hand in src and its call sites pass IMarkdownDocumentContentStore; same shape as V-1",
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

using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// The isolated installation's configuration. The chat models and the embedding model live in two separate
/// provider entries at the same endpoint, because a provider entry carries one <c>contextWindowLimit</c> and the
/// embedding model's window (2,048 tokens for <c>embeddinggemma</c>) is smaller than the chat models': Long Rest
/// derives its summary bound from the entry <c>integrations.embeddings.provider</c> names.
/// </summary>
internal static class LongRestQualificationConfiguration
{
    public const string ChatProviderName = "Ollama";

    public const string EmbeddingProviderName = "Ollama Embeddings";

    /// <summary>The credential reference of the chat entry. The value is a synthetic placeholder the test owns.</summary>
    public const string ChatCredentialVariable = "ARCANUM_OLLAMA_LONG_REST_CHAT_PROVIDER_KEY";

    /// <summary>The embedding entry's reference: a different variable, because two entries may not share one.</summary>
    public const string EmbeddingCredentialVariable = "ARCANUM_OLLAMA_LONG_REST_EMBEDDING_PROVIDER_KEY";

    public const string ChatCredentialPlaceholder = "local-ollama-long-rest-chat-placeholder";

    public const string EmbeddingCredentialPlaceholder = "local-ollama-long-rest-embedding-placeholder";

    public const int ChatContextWindow = 131_072;

    /// <summary>Synthetic prices, so that the budget snapshot's spend is a nonzero figure the test can compare with the traffic it saw.</summary>
    public const decimal InputPricePerMillion = 1000m;

    public const decimal OutputPricePerMillion = 1000m;

    /// <summary>A cached-input rate unlike both others, so the comparison with the provider's cached-token count has something to find.</summary>
    public const decimal CachedPricePerMillion = 250m;

    /// <summary>
    /// The budget snapshot reports spend only while a daily limit is enforced, so one is: large enough that no qualification call is
    /// ever refused by it (at the synthetic prices the whole run costs a few hundred dollars), small enough to be a real limit.
    /// </summary>
    public const decimal DailyLimitUsd = 10_000m;

    /// <param name="providerEndpoint">The literal-loopback <c>/v1</c> endpoint both entries use (the recording forwarder in front of Ollama).</param>
    /// <param name="sagaExtraction">Seeding needs the host's own extraction; the sweeps and the ordinary turn do not.</param>
    /// <param name="longRest">Off while the corpus is seeded, so a seeding turn schedules no sweep.</param>
    public static ArcanumConfigurationFile Create(
        LongRestQualificationOptions options,
        int hostPort,
        Uri providerEndpoint,
        string workspace,
        bool sagaExtraction,
        bool longRest)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(providerEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        return new ArcanumConfigurationFile
        {
            Arcanum = new ArcanumSettings
            {
                Host = new HostSettings
                {
                    Port = hostPort,
                    ListenAny = false,
                },
                DefaultModel = options.DefaultModel,
                FastModel = options.ChatModel,
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = ChatProviderName,
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = providerEndpoint.AbsoluteUri,
                        CredentialEnvironmentVariable = ChatCredentialVariable,
                        Models =
                        [
                            // The fast model declares reasoning, so Long Rest asks it to answer an adjudication with reasoning off (DESIGN §21.16.4).
                            new ModelEntry(options.ChatModel, SupportsVision: true, SupportsTools: true, Reasoning: new ModelReasoningSettings { WireDialect = ReasoningWireDialect.Standard }),
                            new ModelEntry(options.DefaultModel, SupportsVision: true, SupportsTools: true),
                        ],
                        ContextWindowLimit = ChatContextWindow,
                    },
                    new ProviderSettings
                    {
                        Name = EmbeddingProviderName,
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = providerEndpoint.AbsoluteUri,
                        CredentialEnvironmentVariable = EmbeddingCredentialVariable,
                        Models = [new ModelEntry(options.EmbeddingModel, SupportsVision: false)],
                        ContextWindowLimit = options.EmbeddingWindow,
                    },
                ],
                Features = new FeatureSettings
                {
                    Apprentices = false,
                    Lexicon = false,
                    ArchiveSearch = false,
                    Metrics = true,
                    Embeddings = true,
                    SessionSearch = false,
                    CodebaseRetrieval = false,
                    AttachmentRetrieval = false,
                    Saga = true,
                    SagaExtraction = sagaExtraction,
                    Tapestry = false,
                    SemanticSpellRouting = false,
                    Scrying = false,
                    Attachments = false,
                    ClientTools = false,
                    WebBrowsing = false,
                    Reasoning = false,
                    ReasoningSummaries = false,
                    Guardrails = false,
                    WorkspaceChecks = false,
                    MemoryManagement = false,
                    Covenant = false,
                    CampaignScopedMemory = false,
                    CampaignRollups = false,
                    Annals = true,
                    Conclave = false,
                    A2AServer = false,
                    A2AClient = false,
                    LongRest = longRest,
                },
                Integrations = new IntegrationSettings
                {
                    Embeddings = new EmbeddingIntegrationSettings
                    {
                        Provider = EmbeddingProviderName,
                        Model = options.EmbeddingModel,
                        Dimensions = options.EmbeddingDimensions,
                    },
                },
                Cost = new CostSettings
                {
                    Budget = new BudgetPolicySettings
                    {
                        Enabled = true,
                        DailyLimitUsd = DailyLimitUsd,
                    },
                    Pricing = new PricingSettings
                    {
                        DefaultPricing = new ModelPricingEntry
                        {
                            InputPer1M = InputPricePerMillion,
                            OutputPer1M = OutputPricePerMillion,
                            CachedPer1M = CachedPricePerMillion,
                        },
                    },
                },
                Workspaces = new WorkspaceSettings
                {
                    DefaultRoot = workspace,
                    EnableFileWrite = false,
                },
            },
        };
    }

    public static async Task WriteAsync(string path, ArcanumConfigurationFile configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configuration);

        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        await JsonSerializer.SerializeAsync(
            stream,
            configuration,
            ConfigurationJsonContext.Default.ArcanumConfigurationFile);
    }
}

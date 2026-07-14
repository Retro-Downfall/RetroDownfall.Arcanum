using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class IntelligenceSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _executeCommandTimeoutSeconds;

    [ObservableProperty] private int _semanticRouterPreflightTimeoutSeconds;

    [ObservableProperty] private int _semanticRouterMaxTokens;

    [ObservableProperty] private float _semanticRouterTemperature;

    [ObservableProperty] private int _listDirectoryMaxPaths;

    [ObservableProperty] private bool _enableLoreSystem;

    [ObservableProperty] private bool _enableLexiconSystem;

    [ObservableProperty] private bool _enableArchiveSearch;

    [ObservableProperty] private int _archiveSearchMaxResults;

    [ObservableProperty] private int _archiveSearchMaxQueryLength;

    [ObservableProperty] private int _campaignLogThreshold;

    [ObservableProperty] private int _campaignLogIdleTimeoutMinutes;

    [ObservableProperty] private int _campaignLogSweepIntervalMinutes;

    [ObservableProperty] private int _contextWindowCompressionThreshold;

    [ObservableProperty] private bool _enableContextCompression;

    [ObservableProperty] private bool _enableTokenTracking;

    [ObservableProperty] private long _toolOutputCapBytes;

    [ObservableProperty] private int _maxToolInferenceRounds;

    [ObservableProperty] private int _compressionPreflightMinMessages;

    [ObservableProperty] private int _perMessageTemplateOverheadTokens;

    [ObservableProperty] private string _tokenizerEncoding = string.Empty;

    [ObservableProperty] private int _maxOpenApiMessages;

    [ObservableProperty] private int _maxStatelessMessages;

    [ObservableProperty] private int _maxContentPartsPerMessage;

    [ObservableProperty] private int _maxPingPromptChars;

    [ObservableProperty] private int _maxPlanSteps;

    [ObservableProperty] private int _inferenceTimeoutSeconds;

    [ObservableProperty] private bool _useFastModelForSpellRouting;

    private IntelligenceSettings _snapshot = new();

    public void LoadFrom(IntelligenceSettings settings)
    {

        _snapshot = settings;

        ExecuteCommandTimeoutSeconds = settings.ExecuteCommandTimeoutSeconds;

        SemanticRouterPreflightTimeoutSeconds = settings.SemanticRouterPreflightTimeoutSeconds;

        SemanticRouterMaxTokens = settings.SemanticRouterMaxTokens;

        SemanticRouterTemperature = settings.SemanticRouterTemperature;

        ListDirectoryMaxPaths = settings.ListDirectoryMaxPaths;

        EnableLoreSystem = settings.EnableLoreSystem;

        EnableLexiconSystem = settings.EnableLexiconSystem;

        EnableArchiveSearch = settings.EnableArchiveSearch;

        ArchiveSearchMaxResults = settings.ArchiveSearchMaxResults;

        ArchiveSearchMaxQueryLength = settings.ArchiveSearchMaxQueryLength;

        CampaignLogThreshold = settings.CampaignLogThreshold;

        CampaignLogIdleTimeoutMinutes = settings.CampaignLogIdleTimeoutMinutes;

        CampaignLogSweepIntervalMinutes = settings.CampaignLogSweepIntervalMinutes;

        ContextWindowCompressionThreshold = settings.ContextWindowCompressionThreshold;

        EnableContextCompression = settings.EnableContextCompression;

        EnableTokenTracking = settings.EnableTokenTracking;

        ToolOutputCapBytes = settings.ToolOutputCapBytes;

        MaxToolInferenceRounds = settings.MaxToolInferenceRounds;

        CompressionPreflightMinMessages = settings.CompressionPreflightMinMessages;

        PerMessageTemplateOverheadTokens = settings.PerMessageTemplateOverheadTokens;

        TokenizerEncoding = settings.TokenizerEncoding;

        MaxOpenApiMessages = settings.MaxOpenApiMessages;

        MaxStatelessMessages = settings.MaxStatelessMessages;

        MaxContentPartsPerMessage = settings.MaxContentPartsPerMessage;

        MaxPingPromptChars = settings.MaxPingPromptChars;

        MaxPlanSteps = settings.MaxPlanSteps;

        InferenceTimeoutSeconds = settings.InferenceTimeoutSeconds;

        UseFastModelForSpellRouting = settings.UseFastModelForSpellRouting;

    }

    public IntelligenceSettings Build() => _snapshot with
    {

        ExecuteCommandTimeoutSeconds = ExecuteCommandTimeoutSeconds,

        SemanticRouterPreflightTimeoutSeconds = SemanticRouterPreflightTimeoutSeconds,

        SemanticRouterMaxTokens = SemanticRouterMaxTokens,

        SemanticRouterTemperature = SemanticRouterTemperature,

        ListDirectoryMaxPaths = ListDirectoryMaxPaths,

        EnableLoreSystem = EnableLoreSystem,

        EnableLexiconSystem = EnableLexiconSystem,

        EnableArchiveSearch = EnableArchiveSearch,

        ArchiveSearchMaxResults = ArchiveSearchMaxResults,

        ArchiveSearchMaxQueryLength = ArchiveSearchMaxQueryLength,

        CampaignLogThreshold = CampaignLogThreshold,

        CampaignLogIdleTimeoutMinutes = CampaignLogIdleTimeoutMinutes,

        CampaignLogSweepIntervalMinutes = CampaignLogSweepIntervalMinutes,

        ContextWindowCompressionThreshold = ContextWindowCompressionThreshold,

        EnableContextCompression = EnableContextCompression,

        EnableTokenTracking = EnableTokenTracking,

        ToolOutputCapBytes = ToolOutputCapBytes,

        MaxToolInferenceRounds = MaxToolInferenceRounds,

        CompressionPreflightMinMessages = CompressionPreflightMinMessages,

        PerMessageTemplateOverheadTokens = PerMessageTemplateOverheadTokens,

        TokenizerEncoding = TokenizerEncoding,

        MaxOpenApiMessages = MaxOpenApiMessages,

        MaxStatelessMessages = MaxStatelessMessages,

        MaxContentPartsPerMessage = MaxContentPartsPerMessage,

        MaxPingPromptChars = MaxPingPromptChars,

        MaxPlanSteps = MaxPlanSteps,

        InferenceTimeoutSeconds = InferenceTimeoutSeconds,

        UseFastModelForSpellRouting = UseFastModelForSpellRouting,

    };

}

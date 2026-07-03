using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ForgeSectionViewModel : ObservableObject
{

    [ObservableProperty] private string _spellsAllowedWorkspaceRoots = string.Empty;

    [ObservableProperty] private long _spellsMaxFileSizeBytes;

    [ObservableProperty] private int _spellsMetadataScanCacheTtlSeconds;

    [ObservableProperty] private int _spellsMaxDependencies;

    [ObservableProperty] private int _spellsMaxDeclaredTools;

    [ObservableProperty] private int _spellsMaxResonantDependencies;

    [ObservableProperty] private int _spellsMaxResonantBytes;

    [ObservableProperty] private string _campaignsAllowedRoots = string.Empty;

    [ObservableProperty] private int _campaignsMaxCampaigns;

    [ObservableProperty] private int _perceptionMaxEnumerationSteps;

    [ObservableProperty] private int _perceptionMaxTableOfContentsLines;

    [ObservableProperty] private string _perceptionAllowedWorkspaceRoots = string.Empty;

    [ObservableProperty] private int _promptsMaxParameterValueChars;

    [ObservableProperty] private long _codexMaxSizeBytes;

    private SpellSettings _spellsSnapshot = new();

    private CampaignsSettings _campaignsSnapshot = new();

    private PerceptionSettings _perceptionSnapshot = new();

    private PromptSettings _promptsSnapshot = new();

    private CodexSettings _codexSnapshot = new();

    public void LoadFrom(
        SpellSettings spells,
        CampaignsSettings campaigns,
        PerceptionSettings perception,
        PromptSettings prompts,
        CodexSettings codex)
    {

        _spellsSnapshot = spells;

        _campaignsSnapshot = campaigns;

        _perceptionSnapshot = perception;

        _promptsSnapshot = prompts;

        _codexSnapshot = codex;

        SpellsAllowedWorkspaceRoots = spells.AllowedWorkspaceRoots.JoinCsv();

        SpellsMaxFileSizeBytes = spells.MaxFileSizeBytes;

        SpellsMetadataScanCacheTtlSeconds = spells.MetadataScanCacheTtlSeconds;

        SpellsMaxDependencies = spells.MaxDependencies;

        SpellsMaxDeclaredTools = spells.MaxDeclaredTools;

        SpellsMaxResonantDependencies = spells.MaxResonantDependencies;

        SpellsMaxResonantBytes = spells.MaxResonantBytes;

        CampaignsAllowedRoots = campaigns.AllowedRoots.JoinCsv();

        CampaignsMaxCampaigns = campaigns.MaxCampaigns;

        PerceptionMaxEnumerationSteps = perception.MaxEnumerationSteps;

        PerceptionMaxTableOfContentsLines = perception.MaxTableOfContentsLines;

        PerceptionAllowedWorkspaceRoots = perception.AllowedWorkspaceRoots.JoinCsv();

        PromptsMaxParameterValueChars = prompts.MaxParameterValueChars;

        CodexMaxSizeBytes = codex.MaxSizeBytes;

    }

    public SpellSettings BuildSpells() => _spellsSnapshot with
    {

        AllowedWorkspaceRoots = SpellsAllowedWorkspaceRoots.SplitCsv(),

        MaxFileSizeBytes = SpellsMaxFileSizeBytes,

        MetadataScanCacheTtlSeconds = SpellsMetadataScanCacheTtlSeconds,

        MaxDependencies = SpellsMaxDependencies,

        MaxDeclaredTools = SpellsMaxDeclaredTools,

        MaxResonantDependencies = SpellsMaxResonantDependencies,

        MaxResonantBytes = SpellsMaxResonantBytes,

    };

    public CampaignsSettings BuildCampaigns() => _campaignsSnapshot with
    {

        AllowedRoots = CampaignsAllowedRoots.SplitCsv(),

        MaxCampaigns = CampaignsMaxCampaigns,

    };

    public PerceptionSettings BuildPerception() => _perceptionSnapshot with
    {

        MaxEnumerationSteps = PerceptionMaxEnumerationSteps,

        MaxTableOfContentsLines = PerceptionMaxTableOfContentsLines,

        AllowedWorkspaceRoots = PerceptionAllowedWorkspaceRoots.SplitCsv(),

    };

    public PromptSettings BuildPrompts() => _promptsSnapshot with
    {

        MaxParameterValueChars = PromptsMaxParameterValueChars,

    };

    public CodexSettings BuildCodex() => _codexSnapshot with
    {

        MaxSizeBytes = CodexMaxSizeBytes,

    };

}

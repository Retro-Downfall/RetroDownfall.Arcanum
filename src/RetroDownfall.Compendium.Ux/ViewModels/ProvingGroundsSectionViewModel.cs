using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ProvingGroundsSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _maxInquisitorsPerTrial;

    [ObservableProperty] private int _semanticJudgeMaxTokens;

    [ObservableProperty] private int _semanticJudgeTimeoutSeconds;

    private ProvingGroundsSettings _snapshot = new();

    public void LoadFrom(ProvingGroundsSettings settings)
    {

        _snapshot = settings;

        MaxInquisitorsPerTrial = settings.MaxInquisitorsPerTrial;

        SemanticJudgeMaxTokens = settings.SemanticJudgeMaxTokens;

        SemanticJudgeTimeoutSeconds = settings.SemanticJudgeTimeoutSeconds;

    }

    public ProvingGroundsSettings Build() => _snapshot with
    {

        MaxInquisitorsPerTrial = MaxInquisitorsPerTrial,

        SemanticJudgeMaxTokens = SemanticJudgeMaxTokens,

        SemanticJudgeTimeoutSeconds = SemanticJudgeTimeoutSeconds,

    };

}

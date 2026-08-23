using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>Data-source seam for Comparison Workbench runs and pricing.</summary>
public interface IComparisonWorkbenchDataSource
{

    IAsyncEnumerable<IntelligenceEvent> RunFreePromptAsync(
        string prompt,
        string? model,
        float? temperature,
        float? topP,
        int? maxOutputTokens,
        CancellationToken cancellationToken);

    IAsyncEnumerable<IntelligenceEvent> RunPromptAsync(
        Guid promptId,
        PromptExecuteRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<IntelligenceEvent> RunSpellAsync(
        string spellName,
        SpellExecuteRequest request,
        CancellationToken cancellationToken);

    Task<PricingSettings?> GetPricingAsync(CancellationToken cancellationToken);

}

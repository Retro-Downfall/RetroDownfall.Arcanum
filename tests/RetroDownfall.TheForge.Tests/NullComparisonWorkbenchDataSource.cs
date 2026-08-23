using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Tests;

internal sealed class NullComparisonWorkbenchDataSource : IComparisonWorkbenchDataSource
{

    public IAsyncEnumerable<IntelligenceEvent> RunFreePromptAsync(
        string prompt,
        string? model,
        float? temperature,
        float? topP,
        int? maxOutputTokens,
        CancellationToken cancellationToken) =>
        Empty(cancellationToken);

    public IAsyncEnumerable<IntelligenceEvent> RunPromptAsync(
        Guid promptId,
        PromptExecuteRequest request,
        CancellationToken cancellationToken) =>
        Empty(cancellationToken);

    public IAsyncEnumerable<IntelligenceEvent> RunSpellAsync(
        string spellName,
        SpellExecuteRequest request,
        CancellationToken cancellationToken) =>
        Empty(cancellationToken);

    public Task<PricingSettings?> GetPricingAsync(CancellationToken cancellationToken) =>
        Task.FromResult<PricingSettings?>(null);

    private static async IAsyncEnumerable<IntelligenceEvent> Empty(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        await Task.CompletedTask;

        yield break;

    }

}

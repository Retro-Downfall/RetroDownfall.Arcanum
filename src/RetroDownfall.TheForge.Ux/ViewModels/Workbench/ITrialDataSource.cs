using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.ProvingGrounds;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// ViewModel seam for The Proving Grounds: run ephemeral Trials and load picker catalogs.
/// Implementations wrap route services — ViewModels never touch <c>HttpClient</c>.
/// </summary>
public interface ITrialDataSource
{

    Task<DataSourceResult<TrialResult>> RunAsync(Trial trial, CancellationToken cancellationToken);

    Task<DataSourceResult<IReadOnlyList<string>>> ListSpellNamesAsync(string? workspace, CancellationToken cancellationToken);

    Task<DataSourceResult<IReadOnlyList<PromptSummaryDto>>> ListPromptsAsync(CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="ITrialDataSource"/> — wraps <see cref="TrialService"/>, <see cref="SpellService"/>, and <see cref="PromptService"/>.</summary>
public sealed class TrialDataSource : ITrialDataSource
{

    private readonly TrialService _trialService;

    private readonly SpellService _spellService;

    private readonly PromptService _promptService;

    public TrialDataSource(TrialService trialService, SpellService spellService, PromptService promptService)
    {

        _trialService = trialService;

        _spellService = spellService;

        _promptService = promptService;

    }

    public async Task<DataSourceResult<TrialResult>> RunAsync(Trial trial, CancellationToken cancellationToken)
    {

        ApiResponse<TrialResult>? response =
            await _trialService.RunAsync(trial, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<TrialResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<IReadOnlyList<string>>> ListSpellNamesAsync(
        string? workspace,
        CancellationToken cancellationToken)
    {

        ApiResponse<SpellSummary[]>? response =
            await _spellService.ListAsync(workspace, cancellationToken).ConfigureAwait(false);

        DataSourceResult<SpellSummary[]> mapped = DataSourceResult<SpellSummary[]>.FromResponse(response);

        if (!mapped.Success)
        {

            return new DataSourceResult<IReadOnlyList<string>>(null, false, mapped.ErrorCode, mapped.ErrorMessage);

        }

        IReadOnlyList<string> names = (mapped.Data ?? [])
            .Select(static s => s.Name)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DataSourceResult<IReadOnlyList<string>>(names, true, null, null);

    }

    public async Task<DataSourceResult<IReadOnlyList<PromptSummaryDto>>> ListPromptsAsync(
        CancellationToken cancellationToken)
    {

        ApiResponse<ListPageResult<PromptSummaryDto>>? response =
            await _promptService.ListAsync(null, null, null, null, null, cancellationToken).ConfigureAwait(false);

        DataSourceResult<ListPageResult<PromptSummaryDto>> mapped =
            DataSourceResult<ListPageResult<PromptSummaryDto>>.FromResponse(response);

        if (!mapped.Success)
        {

            return new DataSourceResult<IReadOnlyList<PromptSummaryDto>>(
                null,
                false,
                mapped.ErrorCode,
                mapped.ErrorMessage);

        }

        IReadOnlyList<PromptSummaryDto> items = mapped.Data?.Items ?? [];

        return new DataSourceResult<IReadOnlyList<PromptSummaryDto>>(items, true, null, null);

    }

}

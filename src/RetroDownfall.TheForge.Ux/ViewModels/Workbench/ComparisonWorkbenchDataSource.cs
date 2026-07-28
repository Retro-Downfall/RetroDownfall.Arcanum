using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>API-backed Comparison Workbench data source.</summary>
public sealed class ComparisonWorkbenchDataSource : IComparisonWorkbenchDataSource
{

    private readonly SessionService _sessionService;

    private readonly PromptService _promptService;

    private readonly SpellService _spellService;

    private readonly ConfigService _configService;

    public ComparisonWorkbenchDataSource(
        SessionService sessionService,
        PromptService promptService,
        SpellService spellService,
        ConfigService configService)
    {

        _sessionService = sessionService;

        _promptService = promptService;

        _spellService = spellService;

        _configService = configService;

    }

    public IAsyncEnumerable<IntelligenceEvent> RunFreePromptAsync(
        string prompt,
        string? model,
        float? temperature,
        float? topP,
        int? maxOutputTokens,
        CancellationToken cancellationToken)
    {

        PingRequest request = new(
            Prompt: prompt,
            Model: model,
            Temperature: temperature,
            TopP: topP,
            MaxOutputTokens: maxOutputTokens);

        return _sessionService.PingStreamAsync(request, cancellationToken);

    }

    public IAsyncEnumerable<IntelligenceEvent> RunPromptAsync(
        Guid promptId,
        PromptExecuteRequest request,
        CancellationToken cancellationToken) =>
        _promptService.ExecuteStreamAsync(promptId, request, cancellationToken);

    public IAsyncEnumerable<IntelligenceEvent> RunSpellAsync(
        string spellName,
        SpellExecuteRequest request,
        CancellationToken cancellationToken) =>
        _spellService.ExecuteStreamAsync(spellName, request, cancellationToken);

    public async Task<PricingSettings?> GetPricingAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ArcanumSettings>? response = await _configService
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);

        return response?.Data?.Cost.Pricing;

    }

}

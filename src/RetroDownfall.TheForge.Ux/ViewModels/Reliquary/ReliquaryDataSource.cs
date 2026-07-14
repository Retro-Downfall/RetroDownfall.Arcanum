using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Reliquary;

/// <summary>API-backed <see cref="IReliquaryDataSource"/> — wraps <see cref="LlamaService"/>.</summary>
public sealed class ReliquaryDataSource : IReliquaryDataSource
{

    private readonly LlamaService _llamaService;

    public ReliquaryDataSource(LlamaService llamaService)
    {

        _llamaService = llamaService;

    }

    public async Task<IReadOnlyList<CachedModelInfo>> ListCachedModelsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<CachedModelInfo[]>? response = await _llamaService.ListModelsAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } models } ? models : [];

    }

    public async Task<IReadOnlyList<LlamaServerInfo>> ListServersAsync(CancellationToken cancellationToken)
    {

        ApiResponse<LlamaServerInfo[]>? response = await _llamaService.ListServersAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } servers } ? servers : [];

    }

    public async Task<LlamaServerInfo?> StartServerAsync(string cacheKey, CancellationToken cancellationToken)
    {

        ApiResponse<LlamaServerInfo>? response = await _llamaService.StartServerAsync(cacheKey, null, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } server } ? server : null;

    }

    public async Task<bool> StopServerAsync(string cacheKey, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _llamaService.StopServerAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: true };

    }

    public async Task<WarmupResultDto?> WarmupServerAsync(string cacheKey, CancellationToken cancellationToken)
    {

        ApiResponse<WarmupResultDto>? response = await _llamaService.WarmupAsync(cacheKey, new WarmupRequestDto(), cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } result } ? result : null;

    }

    public IAsyncEnumerable<LlamaPullProgress> PullModelAsync(PullModelRequestDto request, CancellationToken cancellationToken) =>
        _llamaService.PullModelAsync(request, cancellationToken);

}

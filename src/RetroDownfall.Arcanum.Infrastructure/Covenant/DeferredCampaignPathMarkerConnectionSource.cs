using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Resolves the marker lifecycle's live database source only when a live marker operation needs it.
/// </summary>
/// <remarks>
/// Archive inspection and restore planning compose the marker lifecycle without opening the live
/// Grimoire. The captured provider is the lifecycle's original scope; it still owns the real source
/// and its admission lease, and every live request resolves that same required scoped service.
/// </remarks>
internal sealed class DeferredCampaignPathMarkerConnectionSource(IServiceProvider serviceProvider)
    : ICovenantConnectionSource
{
    private readonly IServiceProvider _services =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
        _services.GetRequiredService<ICovenantConnectionSource>().GetOpenConnectionAsync(cancellationToken);

    public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
        _services.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(cancellationToken);
}

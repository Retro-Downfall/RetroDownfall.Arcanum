using System.Data;
using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Reads the Campaign-scoped-memory gate and one Session's canonical binding, and produces the scope
/// every memory surface uses.
/// </summary>
/// <remarks>
/// Scoped, because it borrows the request's <see cref="ArcanumDbContext"/> connection the way every
/// other raw-SQL store here does. <c>session_campaign_bindings</c> is not in the compiled EF model.
/// </remarks>
internal sealed class MemoryScopeResolver(
    ArcanumDbContext db,
    IOptionsMonitor<ArcanumSettings> options) : IMemoryScopeResolver
{

    public bool IsCampaignScopingEnabled => options.CurrentValue.Features.CampaignScopedMemory;

    public MemoryScope ForResolvedCampaign(Guid? campaignId) =>
        MemoryScope.Resolve(IsCampaignScopingEnabled, campaignId);

    public async ValueTask<MemoryScope> ResolveForSessionAsync(
        Guid? sessionId,
        CancellationToken cancellationToken)
    {

        if (!IsCampaignScopingEnabled)
        {

            // Nothing is narrowed, so the binding is not worth a read. This also keeps a disabled
            // installation from touching the table at all, which is what "the default is the guarantee"
            // has to mean at the storage layer too.
            return MemoryScope.Installation;

        }

        return ForResolvedCampaign(
            await ReadBoundCampaignAsync(sessionId, cancellationToken).ConfigureAwait(false));

    }

    /// <summary>
    /// The Campaign a Session is bound to, or <see langword="null"/> for every other binding state.
    /// </summary>
    /// <remarks>
    /// Only a <see cref="SessionCampaignBindingKind.Campaign"/> binding supplies a Campaign. Global-only
    /// carries none by definition, and legacy-unresolved carries no authority at all, so both leave a
    /// turn drawing on the installation-scoped memories alone.
    /// </remarks>
    private async Task<Guid?> ReadBoundCampaignAsync(Guid? sessionId, CancellationToken cancellationToken)
    {

        if (sessionId is not { } owner)
        {

            return null;

        }

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT CampaignId
            FROM session_campaign_bindings
            WHERE SessionId = @sessionId AND BindingKindCode = @campaignKind
            LIMIT 1;
            """;

        AddParameter(command, "@sessionId", owner.ToString());

        AddParameter(command, "@campaignKind", (int)SessionCampaignBindingKind.Campaign);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string bound && Guid.TryParse(bound, out Guid campaignId) ? campaignId : null;

    }

    private static void AddParameter(DbCommand command, string name, object value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);

    }

}

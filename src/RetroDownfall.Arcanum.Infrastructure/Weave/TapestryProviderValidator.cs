using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// One indexed source observation under the caller's existing admitted effect group. This never
/// holds a database lock across provider I/O; exact publication validation closes the build later.
/// </summary>
internal sealed class TapestryProviderValidator(IServiceScopeFactory scopes,
    IOptionsMonitor<ArcanumSettings> settings) : ITapestryProviderValidator
{
    public async Task<Result> ValidateAsync(string provider, string model, bool embedding, CancellationToken cancellationToken)
    {
        TapestryBuildObservation? observation = TapestryBuildContext.Current;

        if (observation is null) return Result.Success();

        cancellationToken.ThrowIfCancellationRequested();

        if (!MatchesIdentity(settings.CurrentValue, observation, provider, model, embedding)) return Refused();

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        // EF opens through its configured admission interceptor. The caller's existing work/effect
        // group prevents maintenance from advancing past its drain to close connection admission.
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT CorpusRevision,InvalidationRevision FROM tapestry_scope_revisions WHERE ScopeKind=@kind AND ScopeId=@scope";

        AddParameter(command, "@kind", observation.Snapshot.Scope.Kind.ToString());

        AddParameter(command, "@scope", observation.Snapshot.Scope.Id);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) < observation.Snapshot.CorpusRevision
            || reader.GetInt64(1) != observation.Snapshot.InvalidationRevision)
        {
            return Refused();
        }

        // Options may have changed while the admitted source read was awaiting I/O.
        return MatchesIdentity(settings.CurrentValue, observation, provider, model, embedding) ? Result.Success() : Refused();
    }

    private static bool MatchesIdentity(ArcanumSettings settings, TapestryBuildObservation observation,
        string provider, string model, bool embedding)
    {
        EmbeddingSettings embeddings = settings.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.TapestryEnabled) return false;

        string expectedProvider = embedding ? observation.EmbeddingProvider : observation.SummaryProvider;

        string expectedModel = embedding ? observation.EmbeddingModel : observation.SummaryModel;

        if (provider != expectedProvider || model != expectedModel) return false;

        if (embedding)
        {
            return !string.IsNullOrWhiteSpace(embeddings.Provider)
                && ProviderResolver.TryResolveProviderByName(settings, embeddings.Provider, out ProviderSettings? resolved)
                && resolved?.Name == provider && embeddings.Model == model;
        }

        if (observation.SummaryPreparation is { } prepared
            && TapestrySummarizer.RecipeFingerprint(settings) != prepared.RecipeFingerprint) return false;

        string? selected = !string.IsNullOrWhiteSpace(embeddings.Tapestry.SummaryModel) ? embeddings.Tapestry.SummaryModel.Trim()
            : !string.IsNullOrWhiteSpace(settings.FastModel) ? settings.FastModel.Trim() : settings.DefaultModel?.Trim();

        return selected is not null
            && ProviderResolver.TryResolveProviderForModel(settings, selected, out ProviderSettings? summary, out string canonical)
            && summary?.Name == provider && canonical == model;
    }

    private static Result Refused() => Result.Failure(new Error(ErrorCodes.Hub.InvalidAdmission,
        "The Tapestry source or provider identity changed before dispatch."));

    private static void AddParameter(DbCommand command, string name, string value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);
    }
}

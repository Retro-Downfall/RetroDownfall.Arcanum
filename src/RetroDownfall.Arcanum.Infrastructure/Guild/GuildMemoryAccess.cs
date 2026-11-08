using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using System.Data.Common;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed class GuildMemoryAccessException(Error error) : Exception(error.Message)
{
    internal Error Error { get; } = error;
}

internal sealed record GuildMemoryInvocationContext(SignetAuthority Authority, GuildExecutionHandle Execution);

internal static class GuildMemoryInvocationAmbient
{
    private static readonly AsyncLocal<Binding?> Value = new();

    internal static GuildMemoryInvocationContext? Current => Value.Value?.Context;

    internal static bool IsActive => Value.Value?.IsActive == true;

    internal static IDisposable Begin(GuildMemoryInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Binding? previous = Value.Value;

        Binding binding = new(context);

        Value.Value = binding;

        return new Scope(binding, previous);
    }

    private sealed class Binding(GuildMemoryInvocationContext context)
    {
        private int _closed;

        internal GuildMemoryInvocationContext Context => context;

        internal bool IsActive => Volatile.Read(ref _closed) == 0;

        internal bool Close() => Interlocked.Exchange(ref _closed, 1) == 0;
    }

    private sealed class Scope(Binding binding, Binding? previous) : IDisposable
    {
        public void Dispose()
        {
            if (binding.Close())
            {
                Value.Value = previous;
            }
        }
    }
}

internal static class GuildMemoryAccess
{
    private static readonly AsyncLocal<Admission?> Current = new();

    internal static bool IsGuildInvocation => GuildMemoryInvocationAmbient.Current is not null
        || GuildToolInvocationAmbient.Current is not null || Current.Value is not null;

    internal static void DemandOperator()
    {
        if (IsGuildInvocation)
        {
            throw Unavailable();
        }
    }

    internal static string SqlScope(DbCommand command, GuildMemoryStore store, GuildMemoryOperation operation,
        string campaignExpression, string? classificationExpression = null)
    {
        if (!IsGuildInvocation)
        {
            return "1=1";
        }

        Admission admission = Require(store, operation);

        List<string> predicates = [];

        foreach (GuildMemoryGrant grant in admission.Grants)
        {
            if (grant.Scope.Kind == GuildMemoryScopeKind.Global)
            {
                predicates.Add(classificationExpression is null ? $"{campaignExpression} = ''"
                    : $"({classificationExpression} = 1 AND {campaignExpression} IS NULL)");
            }
            else if (grant.Scope.Kind == GuildMemoryScopeKind.Campaign)
            {
                string parameter = Add(command, grant.Scope.Id!.Value.ToString("D").ToUpperInvariant());

                predicates.Add(classificationExpression is null ? $"{campaignExpression} = {parameter} COLLATE NOCASE"
                    : $"({classificationExpression} = 2 AND {campaignExpression} = {parameter} COLLATE NOCASE)");
            }
        }

        return predicates.Count == 0 ? "0=1" : "(" + string.Join(" OR ", predicates) + ")";
    }

    internal static string SqlSession(DbCommand command, GuildMemoryStore store, GuildMemoryOperation operation,
        string sessionExpression)
    {
        if (!IsGuildInvocation)
        {
            return "1=1";
        }

        Admission admission = Require(store, operation);

        if (store is not (GuildMemoryStore.Transcript or GuildMemoryStore.Attachment or GuildMemoryStore.Tapestry))
        {
            throw Unavailable();
        }

        return SessionPredicate(command, admission.Grants, admission.CampaignId, sessionExpression);
    }

    private static string SessionPredicate(DbCommand command, IEnumerable<GuildMemoryGrant> grants, Guid? campaignId, string sessionExpression)
    {
        List<string> predicates = [];

        foreach (GuildMemoryGrant grant in grants)
        {
            if (grant.Scope.Kind == GuildMemoryScopeKind.Global)
            {
                predicates.Add("(guild_binding.BindingKindCode=1 AND guild_binding.CampaignId IS NULL)");
            }
            else if (grant.Scope.Kind == GuildMemoryScopeKind.Campaign)
            {
                string parameter = Add(command, grant.Scope.Id!.Value.ToString("D").ToUpperInvariant());

                predicates.Add($"(guild_binding.BindingKindCode=2 AND guild_binding.CampaignId={parameter} COLLATE NOCASE)");
            }
            else if (grant.Scope.Kind == GuildMemoryScopeKind.Session)
            {
                string parameter = Add(command, grant.Scope.Id!.Value.ToString("D").ToUpperInvariant());

                string owner = campaignId is { } campaign
                    ? $"guild_binding.BindingKindCode=2 AND guild_binding.CampaignId={Add(command, campaign.ToString("D").ToUpperInvariant())} COLLATE NOCASE"
                    : "guild_binding.BindingKindCode=1 AND guild_binding.CampaignId IS NULL";

                predicates.Add($"(guild_binding.SessionId={parameter} COLLATE NOCASE AND {owner})");
            }
        }

        return predicates.Count == 0 ? "0=1" : $"""
            EXISTS(SELECT 1 FROM session_campaign_bindings guild_binding
              JOIN Sessions guild_session ON guild_session.Id=guild_binding.SessionId
              WHERE guild_binding.SessionId={sessionExpression} COLLATE NOCASE
                AND guild_session.CampaignId IS guild_binding.CampaignId COLLATE NOCASE
                AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=2 AND d.OwnerId=guild_binding.SessionId COLLATE NOCASE)
                AND NOT EXISTS(SELECT 1 FROM guild_owner_cleanup d WHERE d.OwnerKindCode=2 AND d.OwnerId=guild_binding.SessionId COLLATE NOCASE)
                AND (guild_binding.CampaignId IS NULL OR (
                  EXISTS(SELECT 1 FROM Campaigns c WHERE c.Id=guild_binding.CampaignId COLLATE NOCASE)
                  AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=1 AND d.OwnerId=guild_binding.CampaignId COLLATE NOCASE)
                  AND NOT EXISTS(SELECT 1 FROM guild_owner_cleanup d WHERE d.OwnerKindCode=1 AND d.OwnerId=guild_binding.CampaignId COLLATE NOCASE)))
                AND ({string.Join(" OR ", predicates)}))
            """;
    }

    internal static bool IsScopeAllowed(GuildMemoryStore store, GuildMemoryOperation operation, Guid? campaignId, bool explicitlyGlobal)
    {
        if (!IsGuildInvocation)
        {
            return true;
        }

        return Require(store, operation).Grants.Any(grant => explicitlyGlobal
            ? campaignId is null && grant.Scope.Kind == GuildMemoryScopeKind.Global
            : campaignId is not null && grant.Scope.Kind == GuildMemoryScopeKind.Campaign && grant.Scope.Id == campaignId);
    }

    internal static async Task<T> RunAsync<T>(GuildMemoryStore store, GuildMemoryOperation operation,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (GuildMemoryInvocationAmbient.Current is not { } invocation)
        {
            DemandOperator();

            return await action(cancellationToken).ConfigureAwait(false);
        }

        if (Current.Value is { } active)
        {
            using IDisposable borrow = Require(store, operation).Borrow();

            T value = await action(cancellationToken).ConfigureAwait(false);

            active.Check();

            return value;
        }

        Result<T> result = await invocation.Authority.AdmitMemoryAsync(invocation.Execution, store, operation,
            action, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? result.Value : throw new GuildMemoryAccessException(result.Error);
    }

    internal static async Task<Result<T>> RunResultAsync<T>(GuildMemoryStore store, GuildMemoryOperation operation,
        Func<CancellationToken, Task<Result<T>>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(store, operation, action, cancellationToken).ConfigureAwait(false);
        }
        catch (GuildMemoryAccessException ex)
        {
            return ex.Error;
        }
    }

    internal static async Task ValidateMutationAsync(DbConnection connection, DbTransaction? transaction,
        GuildMemoryStore store, GuildMemoryOperation operation, Guid? campaignId, bool explicitlyGlobal, CancellationToken ct)
    {
        if (!IsGuildInvocation)
        {
            return;
        }

        Admission admission = Require(store, operation);

        if (operation == GuildMemoryOperation.Read || !IsScopeAllowed(store, operation, campaignId, explicitlyGlobal))
        {
            throw new GuildMemoryAccessException(new(ErrorCodes.Guild.MemoryNotGranted, "The canonical memory target is not granted."));
        }

        Result<GuildGrant> current = await admission.Revalidate(connection, transaction, ct).ConfigureAwait(false);

        admission.Check();

        if (current.IsFailure)
        {
            throw new GuildMemoryAccessException(current.Error);
        }

        bool allowed = current.Value.Memory.Any(grant => grant.Store == store && grant.Operation == operation
            && (explicitlyGlobal ? campaignId is null && grant.Scope.Kind == GuildMemoryScopeKind.Global
                : campaignId is not null && grant.Scope.Kind == GuildMemoryScopeKind.Campaign && grant.Scope.Id == campaignId));

        if (!allowed)
        {
            throw new GuildMemoryAccessException(new(ErrorCodes.Guild.MemoryNotGranted, "The canonical memory target is no longer granted."));
        }

        admission.ValidatedConnection = connection;

        admission.ValidatedTransaction = transaction;
    }

    internal static async Task RecordAttributionAsync(DbConnection connection, DbTransaction? transaction,
        GuildMemoryStore store, string subjectId, string immutableVersionId, CancellationToken ct)
    {
        if (!IsGuildInvocation)
        {
            return;
        }

        Admission admission = Require(store, GuildMemoryOperation.Append);

        if (!ReferenceEquals(connection, admission.ValidatedConnection) || !ReferenceEquals(transaction, admission.ValidatedTransaction))
        {
            throw Unavailable();
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        string storeParameter = Add(command, (int)store);

        string subject = Add(command, subjectId);

        string version = Add(command, immutableVersionId);

        GuildExecutionHandle execution = admission.Invocation.Execution;

        string signet = Add(command, execution.SignetId.ToString("D").ToUpperInvariant());

        string agent = Add(command, execution.AgentId.ToString("D").ToUpperInvariant());

        string run = Add(command, execution.RunId.ToString("D").ToUpperInvariant());

        string quest = Add(command, execution.QuestId.ToString("D").ToUpperInvariant());

        command.CommandText = $"""
            INSERT INTO guild_memory_attribution(StoreCode,SubjectId,ImmutableVersionId,SignetId,AgentId,RunId,QuestId)
            SELECT {storeParameter},{subject},v.VersionId,{signet},{agent},{run},{quest}
            FROM annal_versions v JOIN annal_claims c ON c.ClaimId=v.ClaimId
            WHERE v.VersionId={version} AND c.SubjectStoreCode={storeParameter} AND c.SubjectId={subject}
            """;

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw Unavailable();
        }
    }

    internal static async Task ValidateSourceProvenanceAsync(DbConnection connection, DbTransaction? transaction,
        GuildMemoryStore store, AttachmentMemoryProvenance? provenance, ArcanumSettings settings, CancellationToken ct)
    {
        if (!IsGuildInvocation)
        {
            return;
        }

        Admission admission = Require(store, GuildMemoryOperation.Append);

        if (provenance is null)
        {
            if (AttachmentMemoryGateAmbient.HasMaterializedAttachmentContent
                || AttachmentMemoryGateAmbient.HasUnprovenancedAttachmentContent)
            {
                throw new GuildMemoryAccessException(new(ErrorCodes.Guild.MemoryNotGranted, "Guild memory append requires its exact source materialization."));
            }

            return;
        }

        if (provenance.Availability != AttachmentSourceAvailability.Available
            || admission.SessionId != provenance.SessionId
            || !AttachmentMemoryGateAmbient.TryResolve(provenance.AttachmentId, out AttachmentMemoryProvenance current)
            || current != provenance)
        {
            throw new GuildMemoryAccessException(new(ErrorCodes.Guild.MemoryNotGranted, "The attachment source is not a live materialization of this Guild invocation."));
        }

        Result<GuildGrant> live = await admission.Revalidate(connection, transaction, ct).ConfigureAwait(false);

        admission.Check();

        if (live.IsFailure)
        {
            throw new GuildMemoryAccessException(live.Error);
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        string scope = SessionPredicate(command, live.Value.Memory.Where(grant => grant.Store == GuildMemoryStore.Attachment
            && grant.Operation == GuildMemoryOperation.Read), admission.CampaignId, "guild_source.SessionId");

        string attachment = Add(command, provenance.AttachmentId.ToString("D").ToUpperInvariant());

        string session = Add(command, provenance.SessionId.ToString("D").ToUpperInvariant());

        string version = Add(command, provenance.Version);

        string digest = Add(command, provenance.ContentHash);

        command.CommandText = $"""
            SELECT guild_source.MimeType FROM SessionAttachments guild_source
            WHERE guild_source.Id={attachment} COLLATE NOCASE AND guild_source.SessionId={session} COLLATE NOCASE
              AND guild_source.State='Bound' AND guild_source.Version={version} AND guild_source.ContentSha256={digest}
              AND {scope}
              AND {SessionAttachmentGuildReadPolicy.Eligible("guild_source")}
            LIMIT 1
            """;

        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string mimeType
            || (SessionAttachmentComprehensionPolicy.TryGetFormat(mimeType, out _) && !SessionAttachmentComprehensionPolicy.IsEnabled(settings)))
        {
            throw new GuildMemoryAccessException(new(ErrorCodes.Guild.MemoryNotGranted, "The attachment source is no longer eligible for this Guild append."));
        }
    }

    internal static async Task<T> WithinAsync<T>(GuildMemoryStore store, GuildMemoryOperation operation,
        GuildGrant grant, Guid? campaignId, Guid? sessionId, Func<DbConnection, DbTransaction?, CancellationToken, Task<Result<GuildGrant>>> revalidate,
        Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        GuildMemoryInvocationContext invocation = GuildMemoryInvocationAmbient.Current ?? throw Unavailable();

        Admission admission = new(invocation, store, operation, grant, campaignId, sessionId, revalidate);

        Admission? previous = Current.Value;

        Current.Value = admission;

        try
        {
            T result = await action(ct).ConfigureAwait(false);

            admission.Check();

            return result;
        }
        finally
        {
            await admission.CloseAsync().ConfigureAwait(false);

            Current.Value = previous;
        }
    }

    private static Admission Require(GuildMemoryStore store, GuildMemoryOperation operation)
    {
        Admission admission = Current.Value ?? throw Unavailable();

        admission.Check();

        if (!GuildMemoryInvocationAmbient.IsActive || !ReferenceEquals(admission.Invocation, GuildMemoryInvocationAmbient.Current)
            || admission.Store != store || admission.Operation != operation)
        {
            throw Unavailable();
        }

        return admission;
    }

    private static string Add(DbCommand command, object value)
    {
        string name = "$guild_memory_" + command.Parameters.Count;

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);

        return name;
    }

    private static GuildMemoryAccessException Unavailable() => new(new(ErrorCodes.Guild.AuthorityUnavailable,
        "This memory path requires a live host-owned Guild memory admission."));

    private sealed class Admission(GuildMemoryInvocationContext invocation, GuildMemoryStore store,
        GuildMemoryOperation operation, GuildGrant grant, Guid? campaignId, Guid? sessionId,
        Func<DbConnection, DbTransaction?, CancellationToken, Task<Result<GuildGrant>>> revalidate)
    {
        private readonly object _sync = new();

        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _closed;

        private int _borrowers;

        internal GuildMemoryInvocationContext Invocation => invocation;

        internal GuildMemoryStore Store => store;

        internal GuildMemoryOperation Operation => operation;

        internal Guid? CampaignId => campaignId;

        internal Guid? SessionId => sessionId;

        internal IEnumerable<GuildMemoryGrant> Grants => grant.Memory.Where(value => value.Store == store && value.Operation == operation);

        internal Func<DbConnection, DbTransaction?, CancellationToken, Task<Result<GuildGrant>>> Revalidate => revalidate;

        internal DbConnection? ValidatedConnection { get; set; }

        internal DbTransaction? ValidatedTransaction { get; set; }

        internal void Check()
        {
            lock (_sync)
            {
                if (_closed || !GuildMemoryInvocationAmbient.IsActive)
                {
                    throw Unavailable();
                }
            }
        }

        internal IDisposable Borrow()
        {
            lock (_sync)
            {
                Check();

                _borrowers++;

                return new Borrowed(this);
            }
        }

        internal Task CloseAsync()
        {
            lock (_sync)
            {
                _closed = true;

                if (_borrowers == 0)
                {
                    _drained.TrySetResult();
                }

                return _drained.Task;
            }
        }

        private sealed class Borrowed(Admission owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                lock (owner._sync)
                {
                    if (--owner._borrowers == 0 && owner._closed)
                    {
                        owner._drained.TrySetResult();
                    }
                }
            }
        }
    }
}

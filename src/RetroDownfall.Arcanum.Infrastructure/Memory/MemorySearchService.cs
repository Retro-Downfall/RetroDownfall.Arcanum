using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

internal sealed class MemorySearchService(
    ArcanumDbContext db,
    IGrimoireOrdinaryConnectionFactory connections,
    IMemoryScopeResolver scopeResolver,
    ILexiconCurationService lexicon,
    ILexiconService lexiconRecall,
    ISagaMemoryStore sagaStore,
    IOptionsMonitor<ArcanumSettings> options) : IMemorySearchService
{
    internal const int SearchResultLimit = 10_000;

    /// <summary>
    /// Ceiling, in UTF-8 bytes of returned content, on what <c>POST /api/memory/search</c> reads and writes,
    /// shared across every scope of one request as <see cref="SearchResultLimit"/> is.
    /// </summary>
    internal const long SearchResultByteBudget = 8L * 1024L * 1024L;

    /// <summary>
    /// The UTF-8 bytes of content one search has already taken, shared across its scopes.
    /// </summary>
    /// <remarks>
    /// Checked after each row rather than before, so a search overshoots by at most the one row that crossed
    /// the budget. A scope that stops reading because the budget is spent cannot know whether it left rows
    /// behind without reading another one, so it reports <c>hasMore</c>: the answer is never "complete" while
    /// a row may have been left unread.
    /// </remarks>
    private sealed class ResultByteBudget(long maximum = SearchResultByteBudget)
    {
        private long _used;

        internal bool Exhausted => _used >= maximum;

        /// <summary>Charges <paramref name="content"/>; true means the budget is now spent and the scope must stop.</summary>
        internal bool Charge(string content)
        {
            _used += System.Text.Encoding.UTF8.GetByteCount(content);

            if (!Exhausted)
            {
                return false;
            }

            return true;
        }
    }

    private const string SagaRetention = "Durable until explicitly deleted, erased, or reset, or pruned by an enabled `saga-memories` retention rule; pinned memories are exempt from pruning, and retirement keeps a memory inspectable but out of retrieval.";

    private const string LexiconRetention = "Durable until explicitly deleted, erased, or reset, or pruned by an enabled `lexicon-entries` retention rule; pinned entries are exempt from pruning, and retirement keeps an entry inspectable but out of matching.";

    public Task<Result<LexiconInspectionResult<MemorySearchResponse>>> SearchAsync(
        MemorySearchRequest? request, ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default) =>
        SearchCoreAsync(request, readLease, modelInvocation: false, SearchResultByteBudget, cancellationToken);

    public Task<Result<MemorySearchResponse>> SearchForInvocationAsync(
        MemorySearchRequest request, CancellationToken cancellationToken = default) =>
        SearchForInvocationAsync(request, 8192, cancellationToken);

    public async Task<Result<MemorySearchResponse>> SearchForInvocationAsync(
        MemorySearchRequest request, long allocationBytes, CancellationToken cancellationToken = default)
    {
        if (allocationBytes < 1024 || allocationBytes > SearchResultByteBudget)
            return new Error(ErrorCodes.Validation.InvalidBody, "A model search allocation must be between 1024 and 8388608 bytes.");

        if (request.Limit is < 1 or > SearchResultLimit)
            return new Error(ErrorCodes.Validation.InvalidBody, $"Memory search limit must be between 1 and {SearchResultLimit}.");

        int limit = Math.Min(request.Limit ?? 20, (int)Math.Clamp(allocationBytes / 4096, 1, SearchResultLimit));

        Result<LexiconInspectionResult<MemorySearchResponse>> result = await SearchCoreAsync(
            request with { Limit = limit }, null, modelInvocation: true, allocationBytes, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure) return result.Error;

        return MemorySearchToolResponseFormatter.Format(result.Value.Value, allocationBytes);
    }

    private async Task<Result<LexiconInspectionResult<MemorySearchResponse>>> SearchCoreAsync(
        MemorySearchRequest? request, ICovenantSnapshotReadLease? readLease,
        bool modelInvocation, long allocationBytes, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            return new Error(ErrorCodes.Validation.InvalidBody, "A memory search query is required.");
        }

        if (!IsKnownScope(request.Scope))
        {
            return new Error(ErrorCodes.Validation.InvalidBody, "Memory search scope must be session, attachments, workspace, saga, lexicon, or all.");
        }

        if (request.Limit is { } requested && (requested < 1 || requested > SearchResultLimit))
        {
            return new Error(ErrorCodes.Validation.InvalidBody, $"Memory search limit must be between 1 and {SearchResultLimit}.");
        }

        if (!modelInvocation)
        {
            GuildMemoryAccess.DemandOperator();
        }

        if (modelInvocation && scopeResolver.IsCampaignScopingEnabled && request.SessionId is null)
        {
            return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Memory search requires a host-bound Session while Campaign scoping is enabled.");
        }

        await using IGrimoireOrdinaryConnectionLease lease = await OpenConnectionAsync(db, connections, cancellationToken).ConfigureAwait(false);

        SqliteConnection connection = lease.Connection;

        GuildMemoryInvocationContext? invocation = GuildMemoryInvocationAmbient.Current;

        GuildGrant? initialGrant = null;

        if (modelInvocation && GuildMemoryAccess.IsGuildInvocation)
        {
            if (invocation is null) return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Memory search requires its live host-owned invocation.");

            Result<GuildGrant> admitted = await invocation.Authority.RevalidateMemoryAsync(invocation, connection, null, cancellationToken).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            initialGrant = admitted.Value;
        }

        if (request.SessionId is { } sessionId && !await SessionExistsAsync(connection, sessionId, cancellationToken).ConfigureAwait(false))
        {
            return new Error(ErrorCodes.Session.NotFound, "Session was not found.");
        }

        (string Path, string Label)? workspace = null;

        if (Includes(request.Scope, MemorySearchScope.Workspace) && !string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            workspace = modelInvocation
                ? await ResolveInvocationWorkspaceAsync(connection, request.WorkspaceId, cancellationToken).ConfigureAwait(false)
                : await ResolveSearchWorkspaceAsync(connection, request.WorkspaceId, cancellationToken).ConfigureAwait(false);

            if (workspace is null)
            {
                return new Error(ErrorCodes.Workspace.NotFound, "The workspace was not found.");
            }
        }

        string query = request.Query.Trim();

        MemoryScope effectiveScope = await scopeResolver.ResolveForSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);

        string grantBinding = initialGrant is null ? "" : Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(initialGrant, GuildJsonContext.Default.GuildGrant)));

        string binding = MemorySearchContinuationCodec.Binding(query, request, workspace?.Path, effectiveScope)
            + (modelInvocation ? ":model:" + invocation?.Execution.SignetId.ToString("D") + ":" + invocation?.Execution.RunId.ToString("D") + ":" + grantBinding : "");

        MemorySearchPosition position = new(0);

        if (request.Continuation is not null && (!MemorySearchContinuationCodec.TryRead(request.Continuation, binding, out position)
            || !MemorySearchContinuationCodec.AppliesTo(request.Scope, position.Phase)))
        {
            return new Error(ErrorCodes.Validation.InvalidBody, "The memory search continuation is invalid for this request.");
        }

        if (request.Scope == MemorySearchScope.Saga && position.Phase < 4) position = new(4);

        if (request.Scope == MemorySearchScope.Lexicon && position.Phase < 5) position = new(5);

        int budget = request.Limit ?? SearchResultLimit;

        List<MemorySearchResultDto> results = [];

        ResultByteBudget bytes = new(allocationBytes);

        Dictionary<MemorySearchScope, Error> unavailable = [];

        Result availability = await ProbeAvailabilityAsync(connection, request, workspace, modelInvocation,
            unavailable, cancellationToken).ConfigureAwait(false);

        if (availability.IsFailure) return availability.Error;

        MemorySearchTruncationReason? reason = null;

        bool protectedContent = false;

        if (position.Phase < 4)
        {
            if (!modelInvocation)
            {
                Result<NativeMemorySearchPage> native = await NativeMemorySearchReader.ReadAsync(connection, query, request.Scope,
                    request.SessionId, workspace, position, budget, cancellationToken).ConfigureAwait(false);

                if (native.IsSuccess)
                {
                    AcceptNative(native.Value);
                }
                else
                {
                    if (request.Scope != MemorySearchScope.All) return native.Error;

                    foreach (MemorySearchScope source in new[] { MemorySearchScope.Session, MemorySearchScope.Attachments, MemorySearchScope.Workspace })
                        if (Includes(request.Scope, source)) unavailable[source] = native.Error;

                    position = new(4);
                }
            }
            else
            {
                foreach ((MemorySearchScope source, int first, int last, GuildMemoryStore store) in new[] {
                    (MemorySearchScope.Session, 0, 1, GuildMemoryStore.Transcript),
                    (MemorySearchScope.Attachments, 2, 2, GuildMemoryStore.Attachment),
                    (MemorySearchScope.Workspace, 3, 3, GuildMemoryStore.Transcript) })
                {
                    if (!Includes(request.Scope, source) || position.Phase > last) continue;

                    if (unavailable.ContainsKey(source))
                    {
                        position = new(last + 1);

                        continue;
                    }

                    if (source == MemorySearchScope.Session && !options.CurrentValue.Features.ArchiveSearch)
                    {
                        unavailable[source] = new(ErrorCodes.MemorySearch.IndexUnavailable, "Transcript retrieval is disabled.");

                        position = new(last + 1);

                        continue;
                    }

                    if (source == MemorySearchScope.Attachments && !options.CurrentValue.Features.Attachments)
                    {
                        unavailable[source] = new(ErrorCodes.MemorySearch.IndexUnavailable, "Attachment retrieval is disabled.");

                        position = new(last + 1);

                        continue;
                    }

                    if ((source == MemorySearchScope.Workspace && (GuildMemoryAccess.IsGuildInvocation || workspace is null))
                        || (source != MemorySearchScope.Workspace && request.SessionId is null))
                    {
                        unavailable[source] = new(ErrorCodes.Guild.AuthorityUnavailable, "This memory source requires a host-bound Session or a qualified physical workspace source adapter.");

                        position = new(last + 1);

                        continue;
                    }

                    Result<NativeMemorySearchPage> native = await GuildMemoryAccess.RunResultAsync(store, GuildMemoryOperation.Read,
                        ct => NativeMemorySearchReader.ReadAsync(connection, query, source, request.SessionId, workspace,
                            position.Phase < first ? new(first) : position, budget - results.Count, ct, options.CurrentValue, modelDisclosure: true, contentByteLimit: 1024, pageByteLimit: allocationBytes), cancellationToken).ConfigureAwait(false);

                    if (native.IsFailure)
                    {
                        if (IsAuthorityFailure(native.Error)) return native.Error;

                        unavailable[source] = native.Error;

                        position = new(last + 1);
                    }
                    else
                    {
                        AcceptNative(native.Value);

                        if (reason is not null) break;

                        position = new(last + 1);
                    }
                }
            }

            if (Includes(request.Scope, MemorySearchScope.Session))
            {
                await GrimoireScopedConsumerTestSeam.PauseAsync("MemoryEndpoints.SearchSessionAsync",
                    GrimoireScopedConsumerFinalUseKind.ReaderMaterialized, results.Count, cancellationToken).ConfigureAwait(false);
            }
        }

        if (reason is null && position.Phase <= 4)
        {
            if (Includes(request.Scope, MemorySearchScope.Saga))
            {
                int before = results.Count;

                if (unavailable.ContainsKey(MemorySearchScope.Saga))
                {
                    position = new(5);
                }
                else if (before >= budget || bytes.Exhausted)
                {
                    reason = bytes.Exhausted ? MemorySearchTruncationReason.ContentBytes : MemorySearchTruncationReason.ResultLimit;
                }
                else if (modelInvocation && !options.CurrentValue.Features.Saga)
                {
                    unavailable[MemorySearchScope.Saga] = new(ErrorCodes.Guild.AuthorityUnavailable, "Saga retrieval is disabled.");
                }
                else
                {
                    List<SagaMemoryPosition> positions = [];

                    Result<SagaMemoryCurationRow[]> page;

                    if (modelInvocation)
                    {
                        page = await ReadStoreAsync(() => sagaStore.SearchMemoryPageAsync(query, request.SessionId, effectiveScope, budget - before + 1,
                            position.Id is null ? null : new(position.Time!, position.Id), cancellationToken, contentByteLimit: 1024)).ConfigureAwait(false);
                    }
                    else
                    {
                        page = await ReadStoreAsync<SagaMemoryCurationRow[]>(async () => await sagaStore.ListCurationRowsAsync(query, request.SessionId, effectiveScope, budget - before + 1,
                            position.Id is null ? null : new(position.Time!, position.Id), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                    }

                    if (page.IsFailure)
                    {
                        if (IsAuthorityFailure(page.Error)) return page.Error;

                        unavailable[MemorySearchScope.Saga] = page.Error;
                    }
                    else
                    {
                        AddSagaMatches(page.Value, effectiveScope, results, bytes, positions.Add);

                        if (results.Count > budget)
                        {
                            results.RemoveRange(budget, results.Count - budget);

                            reason = MemorySearchTruncationReason.ResultLimit;
                        }
                        else if (bytes.Exhausted) reason = MemorySearchTruncationReason.ContentBytes;

                        int kept = results.Count - before;

                        if (kept > 0)
                        {
                            SagaMemoryPosition last = positions[kept - 1];

                            position = new(4, last.CreatedAt, Id: last.Id);
                        }
                    }
                }
            }

            if (reason is null) position = new(5);
        }

        if (reason is null && position.Phase <= 5 && Includes(request.Scope, MemorySearchScope.Lexicon))
        {
            int left = budget - results.Count;

            if (unavailable.ContainsKey(MemorySearchScope.Lexicon))
            {
                position = new(6);
            }
            else if (left <= 0 || bytes.Exhausted)
            {
                reason = bytes.Exhausted ? MemorySearchTruncationReason.ContentBytes : MemorySearchTruncationReason.ResultLimit;
            }
            else if (!await StoreExistsAsync(connection, "lexicon_entries", cancellationToken).ConfigureAwait(false))
            {
                unavailable[MemorySearchScope.Lexicon] = new(ErrorCodes.MemorySearch.IndexUnavailable, "The Lexicon store is not installed or ready for search.");

                position = new(6);
            }
            else if (modelInvocation && !options.CurrentValue.Features.Lexicon)
            {
                unavailable[MemorySearchScope.Lexicon] = new(ErrorCodes.MemorySearch.IndexUnavailable, "Lexicon retrieval is disabled.");

                position = new(6);
            }
            else
            {
                LexiconSearchPosition? after = position.Id is null ? null : new("", position.Tier!, position.Id);

                Result<LexiconInspectionResult<LexiconSearchRow[]>> page;

                if (modelInvocation)
                {
                    Result<LexiconSearchRow[]> clean = await ReadStoreAsync(() => lexiconRecall.SearchMemoryPageAsync(query, effectiveScope.ToLexiconScope(),
                        left + 1, after, cancellationToken, contentByteLimit: 1024)).ConfigureAwait(false);

                    page = clean.IsFailure ? clean.Error : new LexiconInspectionResult<LexiconSearchRow[]>(clean.Value, false);
                }
                else
                {
                    page = await lexicon.SearchInspectionPageAsync(query, left + 1, after, readLease, cancellationToken).ConfigureAwait(false);
                }

                if (page.IsFailure)
                {
                    if (!modelInvocation || IsAuthorityFailure(page.Error)) return page.Error;

                    unavailable[MemorySearchScope.Lexicon] = page.Error;
                }
                else
                {
                    protectedContent = page.Value.ContainsProtectedContent;

                    int taken = 0;

                    foreach (LexiconSearchRow row in page.Value.Value)
                    {
                        if (taken >= left) { reason = MemorySearchTruncationReason.ResultLimit; break; }

                        int before = results.Count;

                        AddLexiconMatches([row.Entry], query, results, 1, bytes, verifyLiteral: !modelInvocation);

                        taken += results.Count - before;

                        position = new(5, Id: row.Position.Id, Tier: row.Position.ScopeKey);

                        if (bytes.Exhausted) { reason = MemorySearchTruncationReason.ContentBytes; break; }
                    }
                }

                if (reason is null) position = new(6);
            }
        }

        List<MemorySearchScopeStatusDto> statuses = [];

        foreach (MemorySearchScope source in new[] { MemorySearchScope.Session, MemorySearchScope.Attachments, MemorySearchScope.Workspace, MemorySearchScope.Saga, MemorySearchScope.Lexicon })
        {
            if (!Includes(request.Scope, source)) continue;

            int phase = source switch { MemorySearchScope.Session => 1, MemorySearchScope.Attachments => 2, MemorySearchScope.Workspace => 3, MemorySearchScope.Saga => 4, _ => 5 };

            unavailable.TryGetValue(source, out Error error);

            statuses.Add(new(source, results.Count(hit => hit.Scope == source), !unavailable.ContainsKey(source) && reason is not null && phase >= position.Phase,
                !unavailable.ContainsKey(source), unavailable.ContainsKey(source) ? error.Code : null));
        }

        if (initialGrant is not null && invocation is not null)
        {
            Result<GuildGrant> current = await invocation.Authority.RevalidateMemoryAsync(invocation, connection, null, cancellationToken).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            if (initialGrant.Memory.Any(grant => grant.Operation == GuildMemoryOperation.Read && !current.Value.Memory.Contains(grant)))
            {
                return new Error(ErrorCodes.Guild.MemoryNotGranted, "Memory authority narrowed before search disclosure.");
            }
        }

        if (request.Scope != MemorySearchScope.All && unavailable.TryGetValue(request.Scope, out Error unavailableSource))
        {
            return unavailableSource;
        }

        return new LexiconInspectionResult<MemorySearchResponse>(new(query, request.Scope, [.. results], [.. statuses], reason is not null,
            reason is null ? null : MemorySearchContinuationCodec.Write(binding, position), reason), protectedContent);

        void AcceptNative(NativeMemorySearchPage page)
        {
            foreach (NativeMemorySearchHit hit in page.Hits)
            {
                results.Add(hit.Result);

                bytes.Charge(hit.Result.Content);
            }

            position = page.Position;

            reason = page.Reason;
        }
    }

    private async Task<Result> ProbeAvailabilityAsync(DbConnection connection, MemorySearchRequest request,
        (string Path, string Label)? workspace, bool modelInvocation, Dictionary<MemorySearchScope, Error> unavailable,
        CancellationToken cancellationToken)
    {
        Result? native = null;

        ArcanumSettings settings = options.CurrentValue;

        foreach (MemorySearchScope source in new[] { MemorySearchScope.Session, MemorySearchScope.Attachments,
            MemorySearchScope.Workspace, MemorySearchScope.Saga, MemorySearchScope.Lexicon })
        {
            if (!Includes(request.Scope, source)) continue;

            Error? disabled = modelInvocation ? source switch
            {
                MemorySearchScope.Session when !settings.Features.ArchiveSearch =>
                    new(ErrorCodes.MemorySearch.IndexUnavailable, "Transcript retrieval is disabled."),
                MemorySearchScope.Attachments when !settings.Features.Attachments =>
                    new(ErrorCodes.MemorySearch.IndexUnavailable, "Attachment retrieval is disabled."),
                MemorySearchScope.Saga when !settings.Features.Saga =>
                    new(ErrorCodes.Guild.AuthorityUnavailable, "Saga retrieval is disabled."),
                MemorySearchScope.Lexicon when !settings.Features.Lexicon =>
                    new(ErrorCodes.MemorySearch.IndexUnavailable, "Lexicon retrieval is disabled."),
                MemorySearchScope.Workspace when GuildMemoryAccess.IsGuildInvocation || workspace is null =>
                    new(ErrorCodes.Guild.AuthorityUnavailable, "This memory source requires a qualified physical workspace source adapter."),
                MemorySearchScope.Session or MemorySearchScope.Attachments when request.SessionId is null =>
                    new(ErrorCodes.Guild.AuthorityUnavailable, "This memory source requires a host-bound Session."),
                _ => null,
            } : null;

            if (disabled is { } error)
            {
                unavailable[source] = error;

                continue;
            }

            GuildMemoryStore store = source switch
            {
                MemorySearchScope.Attachments => GuildMemoryStore.Attachment,
                MemorySearchScope.Saga => GuildMemoryStore.Saga,
                MemorySearchScope.Lexicon => GuildMemoryStore.Lexicon,
                _ => GuildMemoryStore.Transcript,
            };

            // These probes read capability/schema metadata only. They neither inspect protected
            // payloads nor hydrate candidates, and model probes retain each store's own admission.
            Result<bool> available = modelInvocation
                ? await GuildMemoryAccess.RunResultAsync(store, GuildMemoryOperation.Read, ProbeAsync, cancellationToken).ConfigureAwait(false)
                : await ProbeAsync(cancellationToken).ConfigureAwait(false);

            if (available.IsFailure)
            {
                if (IsAuthorityFailure(available.Error)) return available.Error;

                unavailable[source] = available.Error;
            }

            async Task<Result<bool>> ProbeAsync(CancellationToken ct)
            {
                if (source is MemorySearchScope.Session or MemorySearchScope.Attachments or MemorySearchScope.Workspace)
                {
                    native ??= await NativeMemorySearchReader.CheckAvailabilityAsync(connection, ct).ConfigureAwait(false);

                    if (native.IsFailure) return native.Error;
                }

                string[] tables = source switch
                {
                    MemorySearchScope.Session => ["Entries", "Sessions"],
                    MemorySearchScope.Attachments => ["session_attachment_chunks"],
                    MemorySearchScope.Workspace => ["workspace_file_chunks"],
                    MemorySearchScope.Saga => ["saga_memories"],
                    _ => ["lexicon_entries"],
                };

                foreach (string table in tables)
                {
                    if (!await StoreExistsAsync(connection, table, ct).ConfigureAwait(false))
                        return new Error(ErrorCodes.MemorySearch.IndexUnavailable, "This memory store is not installed or ready for search.");
                }

                return true;
            }
        }

        return Result.Success();
    }

    private static async Task<(string Path, string Label)?> ResolveInvocationWorkspaceAsync(
        DbConnection connection, string path, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Path,Name FROM Campaigns WHERE Path=@path LIMIT 1";

        AddParameter(command, "@path", path);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task<bool> StoreExistsAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=@table)";

        AddParameter(command, "@table", table);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is long found && found == 1;
    }

    private static async Task<Result<T>> ReadStoreAsync<T>(Func<Task<Result<T>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1
            && exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return new Error(ErrorCodes.MemorySearch.IndexUnavailable, "This memory store is not installed or ready for search.");
        }
    }

    private static bool IsAuthorityFailure(Error error) => error.Code.StartsWith("Validation.", StringComparison.Ordinal)
        || error.Code.StartsWith("Covenant.", StringComparison.Ordinal)
        || error.Code.StartsWith("Guild.", StringComparison.Ordinal) && error.Code != ErrorCodes.Guild.MemoryNotGranted;

    private static bool Includes(
        MemorySearchScope requested,
        MemorySearchScope candidate) =>
        requested == MemorySearchScope.All || requested == candidate;

    private static bool IsKnownScope(MemorySearchScope scope) =>
        scope is MemorySearchScope.Session
            or MemorySearchScope.Attachments
            or MemorySearchScope.Workspace
            or MemorySearchScope.Saga
            or MemorySearchScope.Lexicon
            or MemorySearchScope.All;

    private static async Task<(string Path, string Label)?> ResolveSearchWorkspaceAsync(
        DbConnection connection,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        string selector = workspaceId.Trim();

        bool isId = Guid.TryParse(selector, out Guid id);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = isId
            ? "SELECT Path, Name FROM Campaigns WHERE Id = @id LIMIT 1"
            : "SELECT Path, Name FROM Campaigns WHERE NameLower = lower(@id) LIMIT 1";

        AddParameter(command, "@id", isId ? GrimoireEntitySql.Format(id) : selector);

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static void AddSagaMatches(SagaMemoryCurationRow[] rows, MemoryScope scope,
        List<MemorySearchResultDto> results, ResultByteBudget bytes, Action<SagaMemoryPosition> remember)
    {
        MemoryCampaignScopeDto reported = MemoryCampaignScopeReport.Describe(scope);

        foreach (SagaMemoryCurationRow row in rows)
        {
            SagaMemoryDto memory = row.Memory;

            string provenance = memory.SessionId is null
                ? "Saga memory with no originating session"
                : $"Saga memory from session {memory.SessionId.Value:D}";

            if (!string.IsNullOrWhiteSpace(memory.Source))
            {
                provenance += $", source {memory.Source}";
            }

            if (memory.AttachmentProvenance is { } attachmentSource)
            {
                provenance += $"; {FormatAttachmentProvenance(attachmentSource)}";
            }

            // Per memory, so a result set spanning scopes is readable: the row says which Campaign owns
            // it, and the block on it says which scope this search drew from. An unresolved row is listed
            // only while Campaign scoping is off -- the listing narrows by the ownership a turn ranks by
            // whenever scoping is on -- and a turn then ranks it like any other, so the text says when it
            // would be withheld rather than claiming it is withheld now.
            provenance += memory.ScopeKind switch
            {
                SagaMemoryScopeKind.Campaign => $"; campaign {memory.ScopeCampaignId:D}",

                SagaMemoryScopeKind.Global => "; installation-scoped",

                SagaMemoryScopeKind.LegacyUnresolved => "; ownership unresolved, withheld from turns while Campaign scoping is on",

                _ => "; ownership not yet classified",
            };

            if (memory.ConsolidatedIntoMemoryId is { } survivorMemoryId
                && memory.ConsolidatedIntoVersionId is { } survivorVersionId
                && memory.LongRestReceiptId is { } receiptId)
            {
                provenance += $"; consolidated into memory {survivorMemoryId}, version {survivorVersionId}, Long Rest receipt {receiptId}";
            }

            // Last, so it reads as the verdict on everything before it: the row is still here, and no
            // turn can recall it.
            if (row.Lifecycle.RetiredAtUtc is not null)
            {
                provenance += "; retired";
            }

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Saga,
                "Saga memory",
                memory.Content,
                provenance,
                SagaRetention,
                memory.Id,
                reported,
                Action: new MemorySearchActionDto(
                    MemorySearchActionKind.ShowSagaMemory,
                    Saga: new MemorySagaTargetDto(memory.Id)),
                SagaLifecycle: row.Lifecycle,
                SagaEligibility: SagaRetrievalEligibilityClassifier.Classify(row, scope.IsEnforced),
                        SagaRanking: row.Memory.Ranking));

            remember?.Invoke(row.SearchPosition ?? new SagaMemoryPosition(UtcInstantText.Format(memory.CreatedAt), memory.Id));

            if (bytes.Charge(memory.Content))
            {
                break;
            }
        }
    }

    private static void AddLexiconMatches(
        IReadOnlyList<LexiconEntryDto> entries,
        string query,
        List<MemorySearchResultDto> results,
        int limit,
        ResultByteBudget bytes,
        bool verifyLiteral = true)
    {
        int taken = 0;

        foreach (LexiconEntryDto entry in entries)
        {
            if (taken >= limit)
            {
                break;
            }

            if (verifyLiteral && !LexiconMatches(entry, query))
            {
                continue;
            }

            // The tier is part of the entity's identity now: two rows may share a name, and a listing that
            // did not say which scope each belongs to would show them as duplicates.
            string provenance = entry.ScopeCampaignId is { } entryCampaign
                ? $"Lexicon entity: {entry.Name}; campaign {entryCampaign:D}"
                : $"Lexicon entity: {entry.Name}; installation-scoped";

            if (entry.FactProvenance is { Length: > 0 } factSources)
            {
                string sources = string.Join(
                    "; ",
                    factSources
                        .Select(static fact => FormatAttachmentProvenance(fact.Source))
                        .Distinct(StringComparer.Ordinal));

                provenance += $"; {sources}";
            }

            string facts = string.Join("; ", entry.Facts);

            results.Add(new MemorySearchResultDto(
                MemorySearchScope.Lexicon,
                $"{entry.Name} [{entry.Type}]",
                facts,
                provenance,
                LexiconRetention,
                entry.Id.ToString("D"),
                LexiconLifecycle: new LexiconEntryLifecycle(entry.RetiredAtUtc, entry.PinnedAtUtc),
                LexiconEligibility: entry.Eligibility,
                Action: new MemorySearchActionDto(
                    MemorySearchActionKind.ShowLexiconEntry,
                    Lexicon: new MemoryLexiconTargetDto(
                        entry.Name,
                        entry.ScopeCampaignId is { } campaignId
                            ? new LexiconCurationScope(LexiconScopeKind.Campaign, campaignId)
                            : new LexiconCurationScope(LexiconScopeKind.Global, null)))));

            taken++;

            if (bytes.Charge(facts))
            {
                break;
            }
        }
    }

    private static bool LexiconMatches(
        LexiconEntryDto entry,
        string query) =>
        entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Facts.Any(fact => fact.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static string FormatAttachmentProvenance(
        RetroDownfall.Arcanum.Core.Intelligence.AttachmentMemoryProvenance source) =>
        $"attachment source session {source.SessionId:D}, attachment {source.AttachmentId:D}, logical key {source.LogicalKey}, version {source.Version}, content hash {source.ContentHash}, materialized {source.MaterializedAt:u}, type {source.SourceType}, availability {source.Availability}";

    private static async Task<bool> SessionExistsAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"Sessions\" WHERE \"Id\" = $sessionId);";

        GrimoireEntitySql.AddParameter(command, "$sessionId", sessionId);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is long exists && exists == 1;
    }

    private static async Task<IGrimoireOrdinaryConnectionLease> OpenConnectionAsync(
        ArcanumDbContext db,
        IGrimoireOrdinaryConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        if (db.Database.GetDbConnection() is not SqliteConnection scopedConnection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await connections
            .AcquireScopedAsync(
                scopedConnection,
                CovenantSqliteConnectionMode.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            throw GrimoireConnectionUnavailableException.FromError(acquired.Error);
        }

        return acquired.Value;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        command.Parameters.Add(parameter);
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteScribeLexiconAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ScribeLexiconParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ScribeLexiconParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "scribe_lexicon argument deserialization failed.");

            return ToolError("Invalid arguments for scribe_lexicon.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Name))
        {
            return ToolError("scribe_lexicon requires a non-empty 'name'.");
        }

        if (args.Facts is null || args.Facts.Length == 0)
        {
            return ToolError("scribe_lexicon requires at least one 'facts' entry.");
        }

        string name = args.Name.Trim();

        AttachmentMemoryProvenance? provenance = null;

        if (!string.IsNullOrWhiteSpace(args.AttachmentId))
        {

            if (!Guid.TryParse(args.AttachmentId, out Guid attachmentId)
                || !AttachmentMemoryGateAmbient.TryResolve(attachmentId, out provenance))
            {

                return ToolError(
                    "scribe_lexicon attachment_id must identify attachment content materialized in the current turn.");

            }

        }
        else if (AttachmentMemoryGateAmbient.HasMaterializedAttachmentContent)
        {

            return ToolError(
                "scribe_lexicon requires attachment_id while attachment content is materialized; untrusted attachment instructions cannot authorize memory promotion.");

        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            // The tier written is the turn's, and the turn's alone: with the gate off this is the global
            // tier, which is where every scribe_lexicon fact has always landed.
            LexiconScope lexiconScope = await ResolveLexiconScopeAsync(scope, cancellationToken)
                .ConfigureAwait(false);

            Result<LexiconEntryDto> result = provenance is null
                ? await lexicon
                    .UpsertAsync(name, args.Type, args.Facts, lexiconScope, cancellationToken)
                    .ConfigureAwait(false)
                : await lexicon
                    .UpsertAsync(name, args.Type, args.Facts, provenance, lexiconScope, cancellationToken)
                    .ConfigureAwait(false);

            if (result.IsFailure)
            {
                return ToolError(result.Error.Message);
            }

            LexiconEntryDto entry = result.Value;

            string text = $"Lexicon entry '{entry.Name}' ({entry.Type}) now holds {entry.Facts.Length} fact(s).";

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "scribe_lexicon failed for name {Name}.", name);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteDeleteLexiconAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        DeleteLexiconParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.DeleteLexiconParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "delete_lexicon argument deserialization failed.");

            return ToolError("Invalid arguments for delete_lexicon.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Name))
        {
            return ToolError("delete_lexicon requires a non-empty 'name'.");
        }

        string name = args.Name.Trim();

        if (IsProtectedDaemonStateName(name))
        {
            return ToolError(
                "delete_lexicon cannot remove Unseen Servant daemon_state entries; clear them via daemon job removal or Lexicon admin tooling.");
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            // Deletion is aimed at the tier the turn writes to, so a Forbidden Art cast inside one
            // Campaign can never take the installation's entity of the same name with it.
            Result<bool> result = await lexicon
                .DeleteByNameAsync(
                    name,
                    await ResolveLexiconScopeAsync(scope, cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                return ToolError(result.Error.Message);
            }

            string text = result.Value
                ? $"Lexicon entry '{name}' was removed."
                : $"Lexicon entry '{name}' did not exist; nothing was deleted.";

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "delete_lexicon failed for name {Name}.", name);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private static bool IsProtectedDaemonStateName(string name) =>
        name.StartsWith("daemon_state:", StringComparison.OrdinalIgnoreCase);

    private async Task<McpToolsCallResultWire> ExecuteSearchArchivesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        SearchArchivesParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.SearchArchivesParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "search_archives argument deserialization failed.");

            return ToolError("Invalid arguments for search_archives.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Query))
        {
            return ToolError("search_archives requires a non-empty 'query'.");
        }

        string query = args.Query.Trim();

        int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(_settings.ArchiveSearchMaxQueryLength);

        if (query.Length > maxQueryLen)
        {
            query = query[..maxQueryLen];
        }

        int maxResults = ArcanumSettingClamps.ArchiveSearchMaxResults(_settings.ArchiveSearchMaxResults);

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IGrimoireRepository repo = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            string text = await repo
                .SearchArchivesAsync(query, maxResults, cancellationToken)
                .ConfigureAwait(false);

            return BuildBoundedSearchArchivesResult(text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "search_archives failed for query {Query}.", query);

            return ToolError("An internal error occurred during tool execution.");
        }
    }

    /// <summary>
    /// search_archives is the one text tool with neither a continuation cursor nor a model-settable
    /// result knob — its schema exposes only <c>query</c> — while a single archived entry is allowed to
    /// be as large as the whole tool-result allocation. Letting the global cap reject the response
    /// therefore turns any sufficiently large match into a dead end whose error names no parameter the
    /// caller could change. Return the leading matches that fit instead, cut back to a whole line, and
    /// say plainly that the tail was dropped — the truncated-but-useful shape search_workspace uses.
    /// </summary>
    private McpToolsCallResultWire BuildBoundedSearchArchivesResult(string text)
    {
        long effectiveCap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            _settings.ToolOutputCapBytes,
            _maxJsonRpcLineBytes);

        long byteCount = Encoding.UTF8.GetByteCount(text);

        if (byteCount <= effectiveCap)
        {
            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            };
        }

        string notice =
            $"\n... [TRUNCATED: the matched archives are {byteCount} UTF-8 bytes and one tool result carries at most {effectiveCap}. Narrow 'query' with more specific keywords to reach the rest.]";

        long bodyBudget = effectiveCap - Encoding.UTF8.GetByteCount(notice);

        if (bodyBudget <= 0L)
        {
            return ToolError(
                $"search_archives: output too large ({byteCount} UTF-8 bytes; limit {effectiveCap}). Narrow 'query' and retry.");
        }

        int retained = LongestPrefixWithinUtf8Budget(text, bodyBudget);

        int lastLineBreak = retained > 0
            ? text.LastIndexOf('\n', retained - 1)
            : -1;

        if (lastLineBreak > 0)
        {
            retained = lastLineBreak;
        }

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = string.Concat(text.AsSpan(0, retained), notice) },
            ],
            IsError = false,
        };
    }

    private static int LongestPrefixWithinUtf8Budget(string text, long budget)
    {
        int low = 0;

        int high = text.Length;

        while (low < high)
        {
            int mid = low + ((high - low + 1) / 2);

            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, mid)) <= budget)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        // A prefix ending on a high surrogate encodes as one replacement character, so the budget can
        // admit it; dropping the orphan keeps the emitted text a real prefix of the match.
        if (low > 0 && low < text.Length && char.IsHighSurrogate(text[low - 1]))
        {
            low--;
        }

        return low;
    }

    /// <summary>
    /// The Lexicon tier this tool call belongs to, resolved from the ambient Session's canonical
    /// Campaign binding.
    /// </summary>
    /// <remarks>
    /// The Session identity is the host's, bound to this request before dispatch, never an argument the
    /// model supplied. That is what stops a model from naming a Campaign and writing into - or reading
    /// out of - a scope its turn does not hold.
    /// </remarks>
    private static async Task<LexiconScope> ResolveLexiconScopeAsync(
        AsyncServiceScope scope,
        CancellationToken cancellationToken)
    {

        IMemoryScopeResolver resolver = scope.ServiceProvider.GetRequiredService<IMemoryScopeResolver>();

        MemoryScope resolved = await resolver
            .ResolveForSessionAsync(SessionAttachmentToolAmbient.CurrentSessionId, cancellationToken)
            .ConfigureAwait(false);

        return resolved.ToLexiconScope();

    }

}

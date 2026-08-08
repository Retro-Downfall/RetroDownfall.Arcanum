using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

public interface ICliCompletionResolver
{

    Task<IReadOnlyList<string>> ResolveAsync(
        string provider,
        CancellationToken cancellationToken);

}

/// <summary>
/// Supplies live resource names to shell completion.
///
/// This runs inside the operator's tab-completion keystroke, so its contract is unusually strict:
/// it never starts the host, it gives up after <see cref="Budget"/> rather than making the shell
/// wait, it returns an empty list on any failure instead of printing, and it caches only names for
/// <see cref="CacheLifetime"/> so repeated tabs do not re-hit the API.
///
/// What crosses this boundary is deliberately narrow. Only display names and identifiers that the
/// existing safe catalog projections already expose are returned. Endpoints, credential references,
/// MCP commands/arguments/environment, workspace paths, prompt text, transcript content, and tool
/// arguments are never read here and must never be added — a completion cache is the wrong place
/// for anything an operator would not want written to a shell history or a scrollback buffer.
/// </summary>
public sealed class CliCompletionResolver(
    ArcanumApiClient apiClient,
    TimeProvider timeProvider) : ICliCompletionResolver
{

    /// <summary>
    /// Total wall-clock allowance for one completion. A shell that pauses longer than this is worse
    /// than a shell that offers nothing, so the budget is a hard stop rather than a target.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(750);

    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    private static readonly Lock CacheGate = new();

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Bounded page size. Completion is a convenience, not an inventory: a larger page would slow
    /// the keystroke without helping anyone scanning a dropdown.
    /// </summary>
    private const int PageSize = 100;

    public async Task<IReadOnlyList<string>> ResolveAsync(
        string provider,
        CancellationToken cancellationToken)
    {

        if (!CliCompletionProviders.All.Contains(provider, StringComparer.Ordinal))
        {

            return [];

        }

        if (TryReadCache(provider, out IReadOnlyList<string> cached))
        {

            return cached;

        }

        using CancellationTokenSource budget = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);

        budget.CancelAfter(Budget);

        try
        {

            // Goes straight to the API client, which is a plain authenticated HTTP client and has
            // no launcher wired to it. A stopped host refuses the connection immediately, so the
            // common "host not running" case costs a failed connect rather than the full budget,
            // and nothing here can spawn `arcanum serve`.
            IReadOnlyList<string> values = await FetchAsync(provider, budget.Token)
                .ConfigureAwait(false);

            WriteCache(provider, values);

            return values;

        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {

            // Deliberately silent. A shell completion must never print a diagnostic over the
            // operator's prompt, and a failed lookup is indistinguishable from "no matches".
            return [];

        }

    }

    private async Task<IReadOnlyList<string>> FetchAsync(
        string provider,
        CancellationToken cancellationToken) =>
        provider switch
        {
            CliCompletionProviders.Model => Names(
                await apiClient.GetModelsAsync(cancellationToken).ConfigureAwait(false),
                static models => models.Select(static model => model.Model)),

            CliCompletionProviders.Provider => Names(
                await apiClient.GetProvidersAsync(cancellationToken).ConfigureAwait(false),
                static providers => providers.Select(static entry => entry.Name)),

            CliCompletionProviders.Campaign => Names(
                await apiClient.GetCampaignsPageAsync(null, PageSize, 0, cancellationToken).ConfigureAwait(false),
                static page => page.Items.Select(static campaign => campaign.Name)),

            CliCompletionProviders.Workspace => Names(
                await apiClient.GetWorkspacesAsync(cancellationToken).ConfigureAwait(false),
                static workspaces => workspaces.Select(static workspace => workspace.Name)),

            CliCompletionProviders.Session => Names(
                await apiClient.QuerySessionsAsync(PageSize, null, cancellationToken).ConfigureAwait(false),
                static page => page.Summaries.Select(static session => session.Id.ToString("D"))),

            CliCompletionProviders.Spell => Names(
                await apiClient.GetSpellsAsync(null, cancellationToken).ConfigureAwait(false),
                static spells => spells.Select(static spell => spell.Name)),

            CliCompletionProviders.Prompt => Names(
                await apiClient.GetPromptsAsync(null, null, null, PageSize, 0, cancellationToken).ConfigureAwait(false),
                static page => page.Items.Select(static prompt => prompt.Name)),

            CliCompletionProviders.Apprentice => Names(
                await apiClient.GetApprenticesAsync(null, null, PageSize, null, cancellationToken).ConfigureAwait(false),
                static page => page.Items.Select(static apprentice => apprentice.Name)),

            CliCompletionProviders.McpServer => Names(
                await apiClient.GetMcpServersAsync(cancellationToken).ConfigureAwait(false),
                static servers => servers.Select(static server => server.Name)),

            _ => [],
        };

    /// <summary>
    /// Projects a catalog result to completion words. Anything containing whitespace or a shell
    /// metacharacter is dropped rather than quoted: the generated scripts split on whitespace, and
    /// a value that could change how the shell parses the line has no business in a completion list.
    /// </summary>
    private static IReadOnlyList<string> Names<T>(
        Result<T> result,
        Func<T, IEnumerable<string?>> selector)
        where T : class =>
        result.IsFailure || result.Value is null
            ? []
            : [
                .. selector(result.Value)
                    .Where(static name => !string.IsNullOrWhiteSpace(name) && IsShellSafe(name))
                    .Select(static name => name!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .Take(PageSize),
            ];

    private static bool IsShellSafe(string? value) =>
        value is not null
        && value.All(static character =>
            char.IsLetterOrDigit(character)
            || character is '-' or '_' or '.' or '/' or ':' or '@');

    private bool TryReadCache(string provider, out IReadOnlyList<string> values)
    {

        lock (CacheGate)
        {

            if (Cache.TryGetValue(provider, out CacheEntry entry)
                && timeProvider.GetUtcNow() - entry.StoredAt < CacheLifetime)
            {

                values = entry.Values;

                return true;

            }

        }

        values = [];

        return false;

    }

    private void WriteCache(string provider, IReadOnlyList<string> values)
    {

        lock (CacheGate)
        {

            Cache[provider] = new CacheEntry(values, timeProvider.GetUtcNow());

        }

    }

    internal static void ClearCache()
    {

        lock (CacheGate)
        {

            Cache.Clear();

        }

    }

    private readonly record struct CacheEntry(
        IReadOnlyList<string> Values,
        DateTimeOffset StoredAt);

}

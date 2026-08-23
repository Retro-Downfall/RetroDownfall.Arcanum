using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

public sealed record SpellSearchQuery(
    string? Query,
    string? Tag,
    string? Tool,
    SpellSource? Source,
    Guid? CampaignId,
    string? Workspace,
    IReadOnlyList<Campaign> Campaigns);

internal static partial class SpellSearchSanitizer
{

    [GeneratedRegex(@"[\[\]\\^$.|?*+(){}\-]")]
    private static partial Regex RegexMetaPattern();

    public static string SanitizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        return RegexMetaPattern().Replace(query.Trim(), string.Empty);
    }

}

public sealed class SpellSearchService
{

    private readonly IOptionsMonitor<ArcanumSettings> _settingsMonitor;

    public SpellSearchService(IOptionsMonitor<ArcanumSettings> settingsMonitor)
    {
        _settingsMonitor = settingsMonitor;
    }

    private long GetMaxSpellFileSizeBytes() =>
        ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(_settingsMonitor.CurrentValue);

    public async Task<SpellSummary[]> SearchAsync(SpellSearchQuery query, CancellationToken ct)
    {
        var results = new Dictionary<string, (SpellSummary Summary, int Priority)>(StringComparer.OrdinalIgnoreCase);

        long maxFileSizeBytes = GetMaxSpellFileSizeBytes();

        if (query.Source is null or SpellSource.Builtin)
        {

            await MergeSourceAsync(
                results,
                priority: 1,
                await SpellScanner.ScanSummariesAsync(
                    null,
                    ct,
                    maxFileSizeBytes).ConfigureAwait(false),
                SpellSource.Builtin,
                query,
                ct).ConfigureAwait(false);

        }

        if (!string.IsNullOrWhiteSpace(query.Workspace)
            && (query.Source is null or SpellSource.Workspace))
        {
            await MergeSourceAsync(
                results,
                priority: 2,
                await SpellScanner.ScanSummariesAsync(
                    query.Workspace,
                    ct,
                    maxFileSizeBytes).ConfigureAwait(false),
                SpellSource.Workspace,
                query,
                ct).ConfigureAwait(false);
        }

        IEnumerable<Campaign> campaigns = query.Campaigns;

        if (query.CampaignId is { } campaignId)
        {
            campaigns = campaigns.Where(c => c.Id == campaignId);
        }

        if (query.Source is null or SpellSource.Campaign)
        {

            foreach (Campaign campaign in campaigns)
            {

                await MergeSourceAsync(
                    results,
                    priority: 3,
                    await SpellScanner.ScanSummariesAsync(
                        campaign.Path,
                        ct,
                        maxFileSizeBytes).ConfigureAwait(false),
                    SpellSource.Campaign,
                    query,
                    ct).ConfigureAwait(false);

            }

        }

        string sanitizedQ = SpellSearchSanitizer.SanitizeQuery(query.Query);

        IEnumerable<SpellSummary> filtered = results.Values
            .Select(v => v.Summary)
            .Where(s => MatchesFilters(s, query, sanitizedQ))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

        return filtered.ToArray();
    }

    private static async Task MergeSourceAsync(
        Dictionary<string, (SpellSummary Summary, int Priority)> results,
        int priority,
        IReadOnlyList<SpellSummary> spells,
        SpellSource source,
        SpellSearchQuery query,
        CancellationToken ct)
    {
        if (query.Source is { } sourceFilter && sourceFilter != source)
        {
            return;
        }

        foreach (SpellSummary spell in spells)
        {
            ct.ThrowIfCancellationRequested();

            bool isExpectedRoot = source == SpellSource.Builtin
                ? spell.Source == SpellSource.Builtin
                : spell.Source == SpellSource.Workspace;

            if (!isExpectedRoot)
            {

                continue;

            }

            SpellSummary summary = spell with { Source = source };

            if (results.TryGetValue(summary.Name, out (SpellSummary Summary, int Priority) existing))
            {
                if (priority > existing.Priority)
                {
                    results[summary.Name] = (summary, priority);
                }
            }
            else
            {
                results[summary.Name] = (summary, priority);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool MatchesFilters(SpellSummary summary, SpellSearchQuery query, string sanitizedQ)
    {
        if (!string.IsNullOrEmpty(sanitizedQ))
        {
            bool matches = summary.Name.Contains(sanitizedQ, StringComparison.OrdinalIgnoreCase)
                || (summary.Description?.Contains(sanitizedQ, StringComparison.OrdinalIgnoreCase) ?? false)
                || summary.Tags.Any(t => t.Contains(sanitizedQ, StringComparison.OrdinalIgnoreCase));

            if (!matches)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            if (!summary.Tags.Any(t => string.Equals(t, query.Tag, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Tool))
        {
            if (summary.DeclaredTools is null
                || !summary.DeclaredTools.Any(t => string.Equals(t, query.Tool, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

}

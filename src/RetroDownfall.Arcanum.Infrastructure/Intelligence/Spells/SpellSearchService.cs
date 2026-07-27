using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
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

        SpellSettings spellSettings = ArcanumRuntimeDefaults.Spells;

        int maxDependencies = ArcanumSettingClamps.MaxDependencies(spellSettings.MaxDependencies);

        int maxDeclaredTools = ArcanumSettingClamps.MaxDeclaredTools(spellSettings.MaxDeclaredTools);

        await MergeSourceAsync(
            results,
            priority: 1,
            await SpellScanner.ScanAsync(null, ct, maxFileSizeBytes, maxDependencies, maxDeclaredTools).ConfigureAwait(false),
            SpellSource.Builtin,
            query,
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query.Workspace))
        {
            await MergeSourceAsync(
                results,
                priority: 2,
                await SpellScanner.ScanAsync(query.Workspace, ct, maxFileSizeBytes, maxDependencies, maxDeclaredTools).ConfigureAwait(false),
                SpellSource.Workspace,
                query,
                ct).ConfigureAwait(false);
        }

        IEnumerable<Campaign> campaigns = query.Campaigns;

        if (query.CampaignId is { } campaignId)
        {
            campaigns = campaigns.Where(c => c.Id == campaignId);
        }

        foreach (Campaign campaign in campaigns)
        {
            await MergeSourceAsync(
                results,
                priority: 3,
                await SpellScanner.ScanAsync(campaign.Path, ct, maxFileSizeBytes, maxDependencies, maxDeclaredTools).ConfigureAwait(false),
                SpellSource.Campaign,
                query,
                ct).ConfigureAwait(false);
        }

        string sanitizedQ = SpellSearchSanitizer.SanitizeQuery(query.Query);

        IEnumerable<SpellSummary> filtered = results.Values
            .Select(v => v.Summary)
            .Where(s => MatchesFilters(s, query, sanitizedQ))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(1000);

        return filtered.ToArray();
    }

    private static async Task MergeSourceAsync(
        Dictionary<string, (SpellSummary Summary, int Priority)> results,
        int priority,
        IReadOnlyList<ParsedSpell> spells,
        SpellSource source,
        SpellSearchQuery query,
        CancellationToken ct)
    {
        _ = ct;

        if (query.Source is { } sourceFilter && sourceFilter != source)
        {
            return;
        }

        foreach (ParsedSpell spell in spells)
        {
            SpellSummary summary = SpellRepository.MapToSummary(spell, source);

            if (results.TryGetValue(spell.Name, out (SpellSummary Summary, int Priority) existing))
            {
                if (priority > existing.Priority)
                {
                    results[spell.Name] = (summary, priority);
                }
            }
            else
            {
                results[spell.Name] = (summary, priority);
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

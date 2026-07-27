using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence;

/// <summary>
/// Dry-run spell "cast" preview: assembles the exact system prompt, resonant dependencies, attuned
/// tools, and spell scripts a live execution would produce &#8212; without any LLM inference (no
/// <c>SemanticRouter</c> call, no chat client, no tokens consumed). Mirrors the composition steps in
/// <c>WizardIntelligenceProvider</c> but stops short of dispatching to inference.
/// </summary>
public interface ISpellCastPreviewService
{

    Task<Result<SpellCastResult>> CastAsync(string spellName, string? resolvedWorkspace, CancellationToken cancellationToken);

}

internal sealed class SpellCastPreviewService(
    IMcpConnectionManager mcpConnectionManager,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    ILogger<SpellCastPreviewService> logger) : ISpellCastPreviewService
{

    public async Task<Result<SpellCastResult>> CastAsync(string spellName, string? resolvedWorkspace, CancellationToken cancellationToken)
    {
        ArcanumSettings settings = settingsMonitor.CurrentValue;

        long maxSpellFileSizeBytes = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(settings);

        int maxDependencies = ArcanumSettingClamps.MaxDependencies(
            ArcanumRuntimeDefaults.Spells.MaxDependencies);

        int maxDeclaredTools = ArcanumSettingClamps.MaxDeclaredTools(
            ArcanumRuntimeDefaults.Spells.MaxDeclaredTools);

        IReadOnlyList<ParsedSpell> allSpells = await SpellScanner
            .ScanAsync(resolvedWorkspace, cancellationToken, maxSpellFileSizeBytes, maxDependencies, maxDeclaredTools)
            .ConfigureAwait(false);

        ParsedSpell? primary = FindByName(allSpells, spellName.Trim());

        if (primary is null)
        {
            return Result<SpellCastResult>.Failure(
                new Error(ErrorCodes.Spell.NotFound, "No spell exists with that name in the resolved workspace."));
        }

        int maxResonantDependencies = ArcanumSettingClamps.MaxResonantDependencies(
            ArcanumRuntimeDefaults.Spells.MaxResonantDependencies);

        ResolvedSpell resolved = await SpellDependencyResolver
            .ResolveAsync(primary, resolvedWorkspace, maxSpellFileSizeBytes, cancellationToken, logger, spellCatalog: null, maxResonantDependencies)
            .ConfigureAwait(false);

        long maxCodexBytes = ArcanumSettingClamps.EffectiveCodexMaxSizeBytes(settings);

        string? codexContent = await CodexReader.ReadCodexAsync(resolvedWorkspace, maxCodexBytes, cancellationToken).ConfigureAwait(false);

        PingRequest ping = new(
            Prompt: string.Empty,
            WorkingDirectory: resolvedWorkspace ?? string.Empty);

        int maxResonantBytes = ArcanumSettingClamps.MaxResonantBytes(
            ArcanumRuntimeDefaults.Spells.MaxResonantBytes);

        string systemPrompt = SystemPromptBuilder.Build(
            ping,
            codexContent,
            primary,
            attachedFiles: null,
            campaignSummary: null,
            dependencySpells: resolved.Resonants,
            maxResonantBytes: maxResonantBytes);

        IReadOnlyList<AITool> mcpTools = await mcpConnectionManager
            .GetAvailableToolsAsync(resolvedWorkspace, cancellationToken)
            .ConfigureAwait(false);

        AttunementResult attunement = ArtifactAttunement.ApplyAttunement(mcpTools, primary.SkillMetadata?.DeclaredTools);

        string[] availableTools = attunement.Allowed
            .OfType<AIFunction>()
            .Select(static f => f.Name)
            .ToArray();

        HashSet<string> scripts = new(StringComparer.Ordinal);

        foreach (string script in primary.AvailableScripts)
        {
            scripts.Add(script);
        }

        foreach (ParsedSpell dependency in resolved.Resonants)
        {
            foreach (string script in dependency.AvailableScripts)
            {
                scripts.Add(script);
            }
        }

        string[] resonantNames = resolved.Resonants.Select(static r => r.Name).ToArray();

        SpellCastResult result = new(
            primary.Name,
            string.IsNullOrEmpty(primary.Description) ? null : primary.Description,
            systemPrompt,
            resonantNames,
            availableTools,
            scripts.ToArray(),
            codexContent,
            HasDeclaredToolsFilter: primary.SkillMetadata?.DeclaredTools is { Count: > 0 });

        return Result<SpellCastResult>.Success(result);
    }

    private static ParsedSpell? FindByName(IReadOnlyList<ParsedSpell> spells, string name)
    {
        for (int i = 0; i < spells.Count; i++)
        {
            if (string.Equals(spells[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return spells[i];
            }
        }

        return null;
    }

}

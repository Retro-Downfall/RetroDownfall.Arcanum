using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

public sealed record ResolvedSpell(
    ParsedSpell Primary,
    IReadOnlyList<ParsedSpell> Resonants,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DependencyEdges);

internal static class SpellDependencyResolver
{

    public const int MaxDependencyDepth = 3;

    public static async Task<ResolvedSpell> ResolveAsync(
        ParsedSpell primary,
        string? workspaceRoot,
        long maxFileSizeBytes,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        IReadOnlyList<SpellMetadata>? spellCatalog = null,
        int maxResonantDependencies = int.MaxValue)
    {
        List<string> primaryDependencies = primary.SkillMetadata?.Dependencies ?? [];

        if (primaryDependencies.Count == 0)
        {
            return new ResolvedSpell(primary, [], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        }

        IReadOnlyList<SpellMetadata> catalog = spellCatalog
            ?? await SpellScanner
                .ScanMetadataAsync(workspaceRoot, cancellationToken, maxFileSizeBytes)
                .ConfigureAwait(false);

        Dictionary<string, string> nameToPath = new(StringComparer.OrdinalIgnoreCase);

        foreach (SpellMetadata meta in catalog)
        {
            if (!string.IsNullOrWhiteSpace(meta.Name) && !string.IsNullOrWhiteSpace(meta.FilePath))
            {
                nameToPath[meta.Name] = meta.FilePath;
            }
        }

        var resonants = new List<ParsedSpell>();

        var dependencyEdges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primary.Name };

        var queue = new Queue<(string Name, int Depth)>();

        foreach (string depName in primaryDependencies)
        {
            if (!string.IsNullOrWhiteSpace(depName))
            {
                queue.Enqueue((depName.Trim(), 1));
            }
        }

        dependencyEdges[primary.Name] = primaryDependencies
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Select(static d => d.Trim())
            .ToList();

        while (queue.Count > 0)
        {
            (string depName, int depth) = queue.Dequeue();

            if (depth > MaxDependencyDepth)
            {
                continue;
            }

            if (!visited.Add(depName))
            {
                continue;
            }

            if (!nameToPath.TryGetValue(depName, out string? filePath))
            {
                logger?.LogWarning(
                    "Arcane Resonance: dependency spell '{DependencyName}' was not found in the spell catalog; skipping.",
                    depName);

                continue;
            }

            if (resonants.Count >= maxResonantDependencies)
            {

                logger?.LogWarning(
                    "Arcane Resonance: reached MaxResonantDependencies ({Max}); remaining dependency spells will be skipped.",
                    maxResonantDependencies);

                continue;

            }

            ParsedSpell? loaded = await SpellScanner
                .LoadFullAsync(filePath, cancellationToken, maxFileSizeBytes)
                .ConfigureAwait(false);

            if (loaded is null)
            {
                logger?.LogWarning(
                    "Arcane Resonance: dependency spell '{DependencyName}' could not be loaded from '{FilePath}'; skipping.",
                    depName,
                    filePath);

                continue;
            }

            resonants.Add(loaded);

            List<string> childDeps = loaded.SkillMetadata?.Dependencies ?? [];

            dependencyEdges[loaded.Name] = childDeps
                .Where(static d => !string.IsNullOrWhiteSpace(d))
                .Select(static d => d.Trim())
                .ToList();

            if (depth >= MaxDependencyDepth)
            {
                continue;
            }

            foreach (string childName in childDeps)
            {
                if (string.IsNullOrWhiteSpace(childName))
                {
                    continue;
                }

                queue.Enqueue((childName.Trim(), depth + 1));
            }
        }

        return new ResolvedSpell(primary, resonants, dependencyEdges);
    }

}

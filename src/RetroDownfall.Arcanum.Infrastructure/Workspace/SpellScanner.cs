using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Workspace;

internal sealed record ParsedSpell(
    string Name,
    string Description,
    string FilePath,
    string FullContent,
    string DirectoryPath,
    IReadOnlyList<string> AvailableScripts);
internal static class SpellScanner
{
    private static readonly HashSet<string> HeavyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        "out",
        "dist",
    };
    internal static async Task<IReadOnlyList<ParsedSpell>> ScanAsync(string? workspaceRoot, CancellationToken cancellationToken)
    {
        string globalSpellsDir = Path.Combine(ArcanumPaths.GrimoireDirectory, "spells");
        string globalRoot;
        try
        {
            globalRoot = Path.GetFullPath(globalSpellsDir);
        }
        catch
        {
            globalRoot = string.Empty;
        }

        List<ParsedSpell> globalSpells = [];
        if (globalRoot.Length > 0 && Directory.Exists(globalRoot))
        {
            globalSpells = await Task.Run(
                () => ScanTreeAsync(globalRoot, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        List<ParsedSpell> localSpells = [];
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            string localRoot;
            try
            {
                localRoot = Path.GetFullPath(workspaceRoot.Trim());
            }
            catch
            {
                localRoot = string.Empty;
            }

            if (localRoot.Length > 0 && Directory.Exists(localRoot))
            {
                localSpells = await Task.Run(
                    () => ScanTreeAsync(localRoot, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return MergeSpells(globalSpells, localSpells);
    }

    private static IReadOnlyList<ParsedSpell> MergeSpells(IReadOnlyList<ParsedSpell> globalSpells, IReadOnlyList<ParsedSpell> localSpells)
    {
        if (globalSpells.Count == 0)
        {
            return localSpells;
        }

        if (localSpells.Count == 0)
        {
            return globalSpells;
        }

        var localNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ParsedSpell l in localSpells)
        {
            _ = localNameSet.Add(l.Name);
        }

        var merged = new List<ParsedSpell>(globalSpells.Count + localSpells.Count);
        foreach (ParsedSpell g in globalSpells)
        {
            if (!localNameSet.Contains(g.Name))
            {
                merged.Add(g);
            }
        }
        merged.AddRange(localSpells);
        return merged;
    }

    private static async Task<List<ParsedSpell>> ScanTreeAsync(string rootFullPath, CancellationToken cancellationToken)
    {
        var results = new List<ParsedSpell>();
        var queue = new Queue<string>();
        queue.Enqueue(rootFullPath);
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string currentDir = queue.Dequeue();
            if (!IsPathUnderWorkspaceRoot(rootFullPath, currentDir))
            {
                continue;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(currentDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetFileName(filePath), "SPELL.md", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsPathUnderWorkspaceRoot(rootFullPath, filePath))
                    {
                        continue;
                    }
                    ParsedSpell? parsed = await TryParseSpellFileAsync(filePath, cancellationToken).ConfigureAwait(false);
                    if (parsed is not null)
                    {
                        results.Add(parsed);
                    }
                }

                foreach (string subDir in Directory.EnumerateDirectories(currentDir).OrderBy(p => p, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(subDir);
                    if (name.Length == 0 || name[0] == '.')
                    {
                        continue;
                    }

                    if (HeavyDirectoryNames.Contains(name))
                    {
                        continue;
                    }

                    string fullSub = Path.GetFullPath(subDir);
                    if (!IsPathUnderWorkspaceRoot(rootFullPath, fullSub))
                    {
                        continue;
                    }
                    queue.Enqueue(fullSub);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return results;
    }

    private static bool IsPathUnderWorkspaceRoot(string workspaceRootFull, string candidateFull)
    {
        char sep = Path.DirectorySeparatorChar;
        string normalizedRoot = workspaceRootFull.TrimEnd(sep);
        string prefix = normalizedRoot + sep;
        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidateFull.Equals(normalizedRoot, cmp) || candidateFull.StartsWith(prefix, cmp);
    }

    private static async Task<ParsedSpell?> TryParseSpellFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string fullText;
        try
        {
            fullText = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        string directoryFallbackName = GetSpellDirectoryFallbackName(filePath);
        ExtractFrontmatterFields(fullText, directoryFallbackName, out string name, out string description);
        string spellDirectoryPath = string.Empty;
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                spellDirectoryPath = Path.GetFullPath(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            spellDirectoryPath = string.Empty;
        }

        IReadOnlyList<string> availableScripts = spellDirectoryPath.Length > 0
            ? DiscoverAvailableScripts(spellDirectoryPath, cancellationToken)
            : Array.Empty<string>();
        return new ParsedSpell(name, description, filePath, fullText, spellDirectoryPath, availableScripts);
    }

    private static IReadOnlyList<string> DiscoverAvailableScripts(string spellDirectoryFullPath, CancellationToken cancellationToken)
    {
        string scriptsDir = Path.Combine(spellDirectoryFullPath, "scripts");
        if (!Directory.Exists(scriptsDir))
        {
            return Array.Empty<string>();
        }

        try
        {
            var names = new List<string>();
            foreach (string path in Directory.EnumerateFiles(scriptsDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                names.Add(Path.GetFileName(path));
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string GetSpellDirectoryFallbackName(string filePath)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
        {
            return string.Empty;
        }

        string trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed);
    }

    private static void ExtractFrontmatterFields(string fullText, string directoryFallbackName, out string name, out string description)
    {
        name = directoryFallbackName;
        description = string.Empty;
        ReadOnlySpan<char> text = fullText.AsSpan().TrimStart();
        if (!text.StartsWith("---".AsSpan(), StringComparison.Ordinal))
        {
            return;
        }

        int lineBreak = text.IndexOfAny('\r', '\n');
        if (lineBreak < 0)
        {
            return;
        }
        text = text.Slice(lineBreak).TrimStart("\r\n");
        var yamlLines = new List<string>();
        while (text.Length > 0)
        {
            lineBreak = text.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> line = lineBreak < 0 ? text : text.Slice(0, lineBreak);
            ReadOnlySpan<char> trimmed = line.Trim();
            if (trimmed.SequenceEqual("---".AsSpan()))
            {
                break;
            }
            yamlLines.Add(trimmed.ToString());
            if (lineBreak < 0)
            {
                break;
            }
            text = text.Slice(lineBreak).TrimStart("\r\n");
        }

        foreach (string yamlLine in yamlLines)
        {
            if (yamlLine.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                name = yamlLine["name:".Length..].Trim();
                if (name.Length == 0)
                {
                    name = directoryFallbackName;
                }
            }
            else if (yamlLine.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                description = yamlLine["description:".Length..].Trim();
            }
        }
    }
}

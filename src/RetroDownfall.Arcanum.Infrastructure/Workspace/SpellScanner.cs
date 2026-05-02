namespace RetroDownfall.Arcanum.Infrastructure.Workspace;

internal sealed record ParsedSpell(string Name, string Description, string FilePath, string FullContent);

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

    internal static async Task<IReadOnlyList<ParsedSpell>> ScanAsync(string workingDirectory, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {

            return [];

        }

        string root;

        try
        {

            root = Path.GetFullPath(workingDirectory.Trim());

        }

        catch
        {

            return [];

        }

        if (!Directory.Exists(root))
        {

            return [];

        }

        var results = new List<ParsedSpell>();

        var queue = new Queue<string>();

        queue.Enqueue(root);

        while (queue.Count > 0)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string currentDir = queue.Dequeue();

            if (!IsPathUnderWorkspaceRoot(root, currentDir))
            {

                continue;

            }

            try
            {

                foreach (string filePath in Directory.EnumerateFiles(currentDir, "*.spell.md"))
                {

                    cancellationToken.ThrowIfCancellationRequested();

                    if (!IsPathUnderWorkspaceRoot(root, filePath))
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

                    if (!IsPathUnderWorkspaceRoot(root, fullSub))
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

        ExtractFrontmatterFields(fullText, filePath, out string name, out string description);

        return new ParsedSpell(name, description, filePath, fullText);

    }

    private static void ExtractFrontmatterFields(string fullText, string filePath, out string name, out string description)
    {

        string fallbackName = Path.GetFileNameWithoutExtension(filePath);

        name = fallbackName;

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

                    name = fallbackName;

                }

            }

            else if (yamlLine.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {

                description = yamlLine["description:".Length..].Trim();

            }

        }

    }

}

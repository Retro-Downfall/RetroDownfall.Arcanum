using System.Buffers;

using System.Diagnostics.CodeAnalysis;

using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed record BackupPathRemapValidation(
    BackupPathRemapper? Remapper,
    BackupVerifyIssue[] Issues);

/// <summary>
/// Applies typed machine-specific root rewrites to values captured on another machine — possibly on
/// another operating system.
/// </summary>
/// <remarks>
/// Deliberately not built on <see cref="Path"/>: the stored value may be a Windows drive path being
/// read on Unix (or the reverse), and the framework helpers canonicalize against the *running*
/// platform. Roots are parsed portably, matched with the case sensitivity implied by the source
/// root's own flavour, and rendered with the destination root's separator.
/// </remarks>
internal sealed class BackupPathRemapper
{

    private static readonly string[] WindowsReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static readonly SearchValues<char> WindowsInvalidNameCharacters =
        SearchValues.Create("<>:\"|?*");

    private readonly CompiledMapping[] _mappings;

    private BackupPathRemapper(
        CompiledMapping[] mappings,
        BackupPathMapping[] requested)
    {

        _mappings = mappings;

        Mappings = requested;

    }

    public IReadOnlyList<BackupPathMapping> Mappings { get; }

    public static BackupPathRemapValidation Create(IReadOnlyList<BackupPathMapping> mappings)
    {

        ArgumentNullException.ThrowIfNull(mappings);

        List<BackupVerifyIssue> issues = [];

        List<CompiledMapping> compiled = [];

        foreach (BackupPathMapping mapping in mappings)
        {

            if (!PortablePath.TryParse(mapping.From, out PortablePath from)
                || !PortablePath.TryParse(mapping.To, out PortablePath to))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.restore_mapping_not_absolute",
                    "A path mapping must name a fully qualified root on both sides.",
                    Describe(mapping)));

                continue;

            }

            if (from.HasTraversal
                || to.HasTraversal
                || to.IsUnder(from, from.CaseInsensitive)
                || from.IsUnder(to, to.CaseInsensitive))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.restore_mapping_escapes",
                    "A path mapping may not contain traversal segments or nest one side inside the other.",
                    Describe(mapping)));

                continue;

            }

            if (to.Flavour == PortablePathFlavour.Windows
                && InvalidWindowsSegment(to) is string invalid)
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.restore_mapping_invalid_target_name",
                    $"The destination root contains a name that is not valid on the target platform: {invalid}",
                    Describe(mapping)));

                continue;

            }

            compiled.Add(new CompiledMapping(mapping, from, to));

        }

        AddConflictIssues(compiled, issues);

        return issues.Count > 0
            ? new BackupPathRemapValidation(Remapper: null, [.. issues])
            : new BackupPathRemapValidation(
                new BackupPathRemapper(
                    [.. compiled],
                    [.. mappings]),
                []);

    }

    /// <summary>
    /// Rewrites <paramref name="value"/> when a mapping of <paramref name="kind"/> owns it. A value
    /// no mapping claims is reported as unmapped rather than silently rewritten.
    /// </summary>
    public bool TryRemap(
        BackupPathMappingKind kind,
        string? value,
        [NotNullWhen(true)] out string? remapped)
    {

        remapped = null;

        if (string.IsNullOrWhiteSpace(value)
            || !PortablePath.TryParse(value, out PortablePath parsed)
            || parsed.HasTraversal)
        {

            return false;

        }

        foreach (CompiledMapping mapping in _mappings)
        {

            if (mapping.Mapping.Kind != kind
                || !parsed.IsUnder(mapping.From, mapping.From.CaseInsensitive))
            {

                continue;

            }

            remapped = mapping.To.Append(
                [.. parsed.Segments.Skip(mapping.From.Segments.Count)]);

            return true;

        }

        return false;

    }

    private static void AddConflictIssues(
        IReadOnlyList<CompiledMapping> compiled,
        List<BackupVerifyIssue> issues)
    {

        for (int left = 0; left < compiled.Count; left++)
        {

            for (int right = left + 1; right < compiled.Count; right++)
            {

                CompiledMapping first = compiled[left];

                CompiledMapping second = compiled[right];

                if (first.Mapping.Kind != second.Mapping.Kind)
                {

                    continue;

                }

                bool sameCaseSensitive = first.From.Equals(second.From, caseInsensitive: false);

                bool sameCaseInsensitive = first.From.Equals(second.From, caseInsensitive: true);

                if (!sameCaseSensitive && sameCaseInsensitive)
                {

                    issues.Add(new BackupVerifyIssue(
                        "backup.restore_mapping_case_collision",
                        "Two path mappings differ only by letter case, which cannot be resolved on a "
                        + "case-insensitive destination.",
                        Describe(first.Mapping) + " / " + Describe(second.Mapping)));

                    continue;

                }

                if (sameCaseInsensitive
                    || first.From.IsUnder(second.From, second.From.CaseInsensitive)
                    || second.From.IsUnder(first.From, first.From.CaseInsensitive))
                {

                    issues.Add(new BackupVerifyIssue(
                        "backup.restore_mapping_ambiguous",
                        "Two path mappings of the same kind claim overlapping source roots.",
                        Describe(first.Mapping) + " / " + Describe(second.Mapping)));

                    continue;

                }

                if (first.To.Equals(second.To, caseInsensitive: true))
                {

                    issues.Add(new BackupVerifyIssue(
                        "backup.restore_mapping_target_collision",
                        "Two path mappings of the same kind resolve to one destination root.",
                        Describe(first.Mapping) + " / " + Describe(second.Mapping)));

                }

            }

        }

    }

    private static string? InvalidWindowsSegment(PortablePath path)
    {

        foreach (string segment in path.Segments)
        {

            if (segment.AsSpan().ContainsAny(WindowsInvalidNameCharacters)
                || segment.Any(static character => char.IsControl(character))
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || WindowsReservedNames.Contains(
                    StripExtension(segment),
                    StringComparer.OrdinalIgnoreCase))
            {

                return segment;

            }

        }

        return null;

    }

    private static string StripExtension(string segment)
    {

        int dot = segment.IndexOf('.', StringComparison.Ordinal);

        return dot < 0 ? segment : segment[..dot];

    }

    private static string Describe(BackupPathMapping mapping) =>
        $"{mapping.Kind}: {mapping.From} -> {mapping.To}";

    private sealed record CompiledMapping(
        BackupPathMapping Mapping,
        PortablePath From,
        PortablePath To);

}

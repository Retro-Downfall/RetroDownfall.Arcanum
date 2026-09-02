using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The placement-to-ceiling mapping (<see cref="CovenantLimits"/>'s six Section constants) exists
/// in exactly one file: <c>CovenantSectionCapacity.cs</c>. A second copy -- even one whose values
/// still agree with the first today -- is exactly the defect <c>CovenantSectionCapacity</c>'s own
/// doc warns about: a guard carrying its own copy of the formula would either refuse a write that
/// would have fitted, or accept one that renders over the bound, the day the two copies stop
/// agreeing, and no behavioral test would notice until that day.
/// </summary>
/// <remarks>
/// A source-text scan, not a behavioral test or reflection over compiled IL: the six constants are
/// <c>const int</c>, so a reference to one is baked into the consuming assembly's IL as a literal
/// at compile time and there is nothing left at runtime pointing back to the original field.
/// Grepping source for the qualified name <c>CovenantLimits.&lt;Name&gt;</c> is the only way to see
/// who still holds a copy of the mapping.
///
/// <para>Checking each constant's referencing-file set independently is not enough on its own: one
/// of the six, <c>MaxGlobalConfirmedRenderedBytes</c>, is also legitimately echoed by a
/// status-reporting DTO (<c>CovenantManagementService.cs</c>) that displays the ceiling to an
/// operator and enforces nothing -- not the mirrored guard this test polices. Intersecting all six
/// constants' file sets instead finds only a file that maps every placement to both of its
/// ceilings together, which is the shape unique to a full copy of the mapping.</para>
/// </remarks>
public sealed class CovenantSectionCapacityMappingTests
{

    private static readonly string[] MappedConstantNames =
    [
        "MaxGlobalConfirmedEntries",
        "MaxGlobalConfirmedRenderedBytes",
        "MaxCampaignConfirmedEntries",
        "MaxCampaignConfirmedRenderedBytes",
        "MaxCampaignProposedEntries",
        "MaxCampaignProposedRenderedBytes",
    ];

    [Fact]
    public void Only_CovenantSectionCapacity_maps_every_placement_to_its_entry_and_byte_ceilings()
    {

        string root = NativeSqlCipherTestPaths.RepositoryRoot();

        string[] sourceFiles = SourceFiles(root);

        // Guards the guard: a path that stopped resolving would make the intersection below pass by
        // finding nothing to intersect against, which is the one way a check like this fails silently.
        Assert.True(sourceFiles.Length > 500, $"Only {sourceFiles.Length} source files were scanned; the enumeration is wrong.");

        HashSet<string>? filesReferencingEveryConstant = null;

        foreach (string constantName in MappedConstantNames)
        {

            string qualifiedReference = "CovenantLimits." + constantName;

            HashSet<string> filesReferencingThisConstant =
            [
                .. sourceFiles.Where(path => File.ReadAllText(path).Contains(qualifiedReference, StringComparison.Ordinal)),
            ];

            filesReferencingEveryConstant = filesReferencingEveryConstant is null
                ? filesReferencingThisConstant
                : [.. filesReferencingEveryConstant.Intersect(filesReferencingThisConstant)];

        }

        string[] mappers =
        [
            .. (filesReferencingEveryConstant ?? [])
                .Select(path => Path.GetRelativePath(root, path))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            mappers is [string only] && only.EndsWith("CovenantSectionCapacity.cs", StringComparison.Ordinal),
            "Expected exactly one file mapping every placement to its entry and byte ceilings "
            + $"(CovenantSectionCapacity.cs); found: {(mappers.Length == 0 ? "(none)" : string.Join(", ", mappers))}");

    }

    private static string[] SourceFiles(string root) =>
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

}

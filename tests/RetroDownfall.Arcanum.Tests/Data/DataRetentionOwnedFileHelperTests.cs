using System.Reflection;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The closed inventory of retention's owned-file helpers.
/// </summary>
/// <remarks>
/// Retention removes a managed file in exactly one way: it quarantines it, having first compared the
/// file's identity against the durable mutation journal, so a file that changed between the journal
/// and the deletion is not destroyed. Every other helper in this family only looks — it resolves a
/// path under a root, proves containment, and reports what it found.
///
/// <para>The set is pinned rather than reasoned about. The hazard is not a helper that behaves
/// wrongly, it is a second helper that behaves plausibly: an unjournalled delete sitting beside the
/// journalled one under a shorter name is picked up by the next edit that wants to remove a file,
/// and nothing about the call site would look wrong. A name is enough to catch that, because the
/// helper has to have one.</para>
/// </remarks>
public sealed class DataRetentionOwnedFileHelperTests
{

    [Fact]

    public void Retention_declares_no_owned_file_helper_outside_the_pinned_set()
    {

        string[] declared =
        [
            .. typeof(DataRetentionService)
                .GetMethods(
                    BindingFlags.NonPublic
                    | BindingFlags.Public
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Select(static method => method.Name)
                .Where(static name => name.Contains("OwnedFile", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "ProbeOwnedFile",
                "TryQuarantineOwnedFile",
            ],
            declared);

    }

    /// <summary>
    /// The one probe resolves its root before asking whether a path is under it.
    /// </summary>
    /// <remarks>
    /// The three copies this replaced agreed only because every root they were handed had already
    /// been resolved at construction. <c>WorkspacePathPolicy.IsPathUnderWorkspace</c> trims a
    /// trailing separator from the root and nothing else, while it resolves the candidate in full,
    /// so a root carrying a <c>.</c> or a <c>..</c> segment stops being a prefix of its own contents
    /// — and one of the three passed the root through untouched. A file plainly inside the tree then
    /// read as outside it, which for a reconciliation probe means "already gone".
    /// </remarks>
    [Fact]

    public void The_owned_file_probe_resolves_a_root_before_testing_containment()
    {

        MethodInfo probe = typeof(DataRetentionService).GetMethod(
            "ProbeOwnedFile",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "Retention declares no single owned-file probe; the three copies are still separate.");

        string root = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"owned-file-probe-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(root);

        try
        {

            File.WriteAllBytes(Path.Combine(root, "kept.bin"), [1, 2, 3, 4, 5]);

            // The same directory, spelled with a segment that only full resolution removes.
            string unresolvedRoot = Path.Combine(root, ".");

            Assert.Equal((true, 5L), Invoke(probe, unresolvedRoot, "kept.bin"));

            Assert.Equal(Invoke(probe, root, "kept.bin"), Invoke(probe, unresolvedRoot, "kept.bin"));

            // Containment still decides: a relative path that climbs out of the root is refused
            // whichever spelling the root arrived in.
            Assert.Equal((false, 0L), Invoke(probe, unresolvedRoot, Path.Combine("..", "kept.bin")));

            Assert.Equal((false, 0L), Invoke(probe, root, Path.Combine("..", "kept.bin")));

        }
        finally
        {

            Directory.Delete(root, recursive: true);

        }

    }

    private static (bool Exists, long Bytes) Invoke(
        MethodInfo probe,
        string root,
        string relativePath) =>
        ((bool Exists, long Bytes))probe.Invoke(null, [root, relativePath])!;

}

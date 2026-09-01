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
                "MeasureOwnedFile",
                "OwnedFileExists",
                "TryGetOwnedFileLength",
                "TryQuarantineOwnedFile",
            ],
            declared);

    }

}

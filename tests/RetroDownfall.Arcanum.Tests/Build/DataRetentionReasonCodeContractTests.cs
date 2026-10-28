using System.Reflection;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class DataRetentionReasonCodeContractTests
{
    public static TheoryData<string> AuthoredReasons =>
    [
        "ActiveOperation",
        "AttachmentInFlight",
        "BatchInProgress",
        "BatchReference",
        "BudgetReservationOutstanding",
        "ContextPin",
        "DaemonExecutionActive",
        "IdempotencyLeaseActive",
        "IdempotencyLeaseExpired",
        "InferenceRunActive",
        "PinnedAttachment",
        "PinnedEntry",
        "SessionAccountingAuthority",
        "SessionHold",
        "OperationNeedsAttention",
        "PinnedAfterPlanning",
    ];

    [Theory]
    [MemberData(nameof(AuthoredReasons))]
    public void Each_authored_reason_has_its_exact_public_constant(string member)
    {
        FieldInfo? field = typeof(ErrorCodes.Data).GetField(member, BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(field);

        Assert.True(field.IsLiteral);

        Assert.Equal(typeof(string), field.FieldType);

        Assert.Equal("Data." + member, field.GetRawConstantValue());
    }

    [Fact]
    public void The_API_lists_every_authored_reason_with_its_meaning_and_operator_action()
    {
        string api = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.API.md"));

        const string marker = "**Retention reason codes.**";

        int start = api.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, "The exact nested retention reason-code table is missing.");

        string section = api[(start + marker.Length)..];

        int end = section.IndexOf("\n**", StringComparison.Ordinal);

        if (end >= 0) section = section[..end];

        string[] expected = AuthoredReasons.Cast<object[]>().Select(row => "Data." + (string)row[0]).Order(StringComparer.Ordinal).ToArray();

        string[][] rows = section.Split('\n').Where(line => line.StartsWith("| `Data.", StringComparison.Ordinal))
            .Select(line => line.Split('|').Select(part => part.Trim()).ToArray()).ToArray();

        Assert.Equal(expected, rows.Select(row => row[1].Trim('`')).Order(StringComparer.Ordinal));

        Assert.All(rows, row =>
        {
            Assert.Equal(5, row.Length);

            Assert.False(string.IsNullOrWhiteSpace(row[2]));

            Assert.False(string.IsNullOrWhiteSpace(row[3]));
        });

        Assert.Contains("diagnostic", section, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("HTTP", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Authored_retention_reason_sites_do_not_redeclare_wire_strings()
    {
        string[] owners = [.. ProductionSourceInventory.Sources()
            .Where(source => Path.GetFileName(source.RelativePath).StartsWith("DataRetentionService", StringComparison.Ordinal)
                || source.Is("InstallationResetService.cs"))
            .Select(source => source.RelativePath)];

        Assert.True(owners.Length >= 6);

        string names = string.Join("|", AuthoredReasons.Cast<object[]>().Select(row => Regex.Escape((string)row[0])));

        Regex literal = new("[\"']Data\\.(?:" + names + ")[\"']", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

        string[] duplicates = [.. ProductionSourceInventory.Sources()
            .Where(source => owners.Contains(source.RelativePath, StringComparer.Ordinal))
            .SelectMany(source => literal.Matches(source.Text).Select(match => source.RelativePath + ": " + match.Value))];

        Assert.Empty(duplicates);
    }
}

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed partial class UtcInstantColumnInventoryTests
{
    [Fact]
    public void Every_schema_TEXT_instant_is_classified_once_and_calendar_or_tick_values_are_excluded()
    {
        AssertInventory(
            GrimoireSchemaCatalog.CoreObjects,
            UtcInstantColumnInventory.Core,
            "BudgetReservations.BudgetPeriod");

        AssertInventory(
            GrimoireSchemaCatalog.CovenantCanonicalObjects,
            UtcInstantColumnInventory.CovenantCanonical);

        Assert.Equal(115, UtcInstantColumnInventory.Core.Sum(static table => table.Columns.Count));

        Assert.Equal(
            13,
            UtcInstantColumnInventory.CovenantCanonical.Sum(static table => table.Columns.Count));

        Assert.DoesNotContain(
            UtcInstantColumnInventory.Core,
            static table => table.Columns.Contains("MaxDisclosedAtUtcTicks", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0, "grimoire_utc_instant_columns")]
    [InlineData(1, "covenant_utc_instant_columns")]
    public void The_zero_hot_path_diagnostic_view_publishes_the_same_closed_inventory(
        int tierCode,
        string viewName)
    {
        GrimoireSchemaTransactionTier tier = (GrimoireSchemaTransactionTier)tierCode;

        IReadOnlyList<UtcInstantTable> inventory = tier == GrimoireSchemaTransactionTier.Core
            ? UtcInstantColumnInventory.Core
            : UtcInstantColumnInventory.CovenantCanonical;

        GrimoireSchemaObject view = GrimoireSchemaCatalog.AllObjects.Single(
            definition => definition.TransactionTier == tier && definition.Name == viewName);

        Assert.Equal(GrimoireSchemaCategory.Views, view.Category);

        string[] expected =
        [
            .. inventory.SelectMany(
                static table => table.Columns.Select(column => table.TableName + "." + column)),
        ];

        string[] actual =
        [
            .. InventoryViewRow().Matches(view.Sql).Cast<Match>().Select(
                static match =>
                    match.Groups["table"].Value + "." + match.Groups["column"].Value),
        ];

        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static void AssertInventory(
        IReadOnlyList<GrimoireSchemaObject> objects,
        IReadOnlyList<UtcInstantTable> inventory,
        params string[] exclusions)
    {
        HashSet<string> expected = new(StringComparer.Ordinal);

        foreach (GrimoireSchemaObject table in objects.Where(
                     static definition => definition.Category == GrimoireSchemaCategory.Tables))
        {
            foreach (Match match in TextColumn().Matches(table.Sql).Cast<Match>())
            {
                string column = match.Groups["column"].Value;

                if (IsInstantName(column))
                {
                    _ = expected.Add(table.Name + "." + column);
                }
            }
        }

        expected.ExceptWith(exclusions);

        string[] actual =
        [
            .. inventory.SelectMany(
                static table => table.Columns.Select(column => table.TableName + "." + column)),
        ];

        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static bool IsInstantName(string column) =>
        column.EndsWith("At", StringComparison.Ordinal)
        || column.EndsWith("Utc", StringComparison.Ordinal)
        || column.EndsWith("Time", StringComparison.Ordinal)
        || column.EndsWith("Date", StringComparison.Ordinal)
        || column.EndsWith("Period", StringComparison.Ordinal);

    [GeneratedRegex("(?m)^\\s*\"?(?<column>[A-Za-z][A-Za-z0-9_]*)\"?\\s+TEXT\\b", RegexOptions.CultureInvariant)]
    private static partial Regex TextColumn();

    [GeneratedRegex(
        "'(?<table>[A-Za-z][A-Za-z0-9_]*)'\\s+AS\\s+TableName,\\s*'(?<column>[A-Za-z][A-Za-z0-9_]*)'\\s+AS\\s+ColumnName",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex InventoryViewRow();
}

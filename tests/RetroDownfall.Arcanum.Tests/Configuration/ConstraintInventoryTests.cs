using System.Reflection;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConstraintInventoryTests
{

    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {

        "retain",

        "remove",

        "replace with adaptive behavior",

        "replace with explicit operator cancellation/policy",

    };

    private static readonly HashSet<string> AllowedClassifications = new(StringComparer.Ordinal)
    {

        "Required security boundary",

        "Required data-integrity or protocol invariant",

        "Required physical resource protection",

        "Provider/model constraint",

        "Explicit operator policy",

        "Arbitrary product restriction",

    };

    [Fact]
    public void Inventory_has_complete_classified_actionable_records()
    {

        using JsonDocument inventory = LoadInventory();

        JsonElement constraints = inventory.RootElement.GetProperty("constraints");

        Assert.Equal(JsonValueKind.Array, constraints.ValueKind);

        Assert.NotEmpty(constraints.EnumerateArray());

        HashSet<string> ids = new(StringComparer.Ordinal);

        Dictionary<string, string> symbolOwners = new(StringComparer.Ordinal);

        foreach (JsonElement constraint in constraints.EnumerateArray())
        {

            string id = RequiredString(constraint, "id");

            Assert.True(ids.Add(id), $"Duplicate constraint id '{id}'.");

            _ = RequiredString(constraint, "subsystem");

            _ = RequiredString(constraint, "currentValue");

            _ = RequiredString(constraint, "owner");

            _ = RequiredString(constraint, "enforcementPoint");

            _ = RequiredString(constraint, "userVisibleBehavior");

            _ = RequiredString(constraint, "originalRationale");

            _ = RequiredString(constraint, "threatOrFailureModel");

            string classification = RequiredString(constraint, "classification");

            string action = RequiredString(constraint, "action");

            _ = RequiredString(constraint, "replacementOrContinuation");

            Assert.Contains(classification, AllowedClassifications);

            Assert.Contains(action, AllowedActions);

            Assert.NotEmpty(RequiredStringArray(constraint, "sourceLocations"));

            string[] coveredSymbols = RequiredStringArray(constraint, "coveredSymbols");

            Assert.NotEmpty(coveredSymbols);

            Assert.True(
                constraint.TryGetProperty("symbolValues", out JsonElement symbolValues),
                $"Constraint '{id}' is missing symbolValues.");

            Assert.Equal(JsonValueKind.Object, symbolValues.ValueKind);

            foreach (string symbol in coveredSymbols)
            {

                Assert.True(
                    symbolOwners.TryAdd(symbol, id),
                    $"Covered symbol '{symbol}' is classified by both '{symbolOwners.GetValueOrDefault(symbol)}' and '{id}'.");

                Assert.True(
                    symbolValues.TryGetProperty(symbol, out JsonElement value),
                    $"Constraint '{id}' has no current/former value for '{symbol}'.");

                Assert.Equal(JsonValueKind.String, value.ValueKind);

                Assert.False(
                    string.IsNullOrWhiteSpace(value.GetString()),
                    $"Constraint '{id}' has a blank value for '{symbol}'.");

            }

            Assert.NotEmpty(RequiredStringArray(constraint, "tests"));

            Assert.False(
                classification == "Arbitrary product restriction"
                && action == "retain",
                $"Arbitrary product restriction '{id}' cannot be retained.");

            string serialized = constraint.GetRawText();

            Assert.DoesNotContain("TheForge", serialized, StringComparison.OrdinalIgnoreCase);

        }

    }

    [Fact]
    public void Inventory_classifies_every_public_configuration_number_and_clamp()
    {

        using JsonDocument inventory = LoadInventory();

        HashSet<string> coveredSymbols = inventory.RootElement
            .GetProperty("constraints")
            .EnumerateArray()
            .SelectMany(static constraint => RequiredStringArray(constraint, "coveredSymbols"))
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> requiredSymbols = [];

        CollectNumericConfigurationSymbols(
            typeof(ArcanumSettings),
            "ArcanumSettings",
            requiredSymbols,
            depth: 0);

        foreach (MethodInfo method in typeof(ArcanumSettingClamps).GetMethods(
            BindingFlags.Public
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly))
        {

            if (!method.IsSpecialName)
            {

                requiredSymbols.Add($"ArcanumSettingClamps.{method.Name}");

            }

        }

        string[] missing = requiredSymbols
            .Except(coveredSymbols, StringComparer.Ordinal)
            .OrderBy(static symbol => symbol, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Public limits missing constraint-inventory classification:\n  {string.Join("\n  ", missing)}");

    }

    [Fact]
    public void Markdown_inventory_table_has_exact_json_row_parity()
    {

        using JsonDocument inventory = LoadInventory();

        string repositoryRoot = FindRepositoryRoot();

        string reportPath = Path.Combine(
            repositoryRoot,
            "docs",
            "Arcanum.ConstraintReduction.20260803.md");

        Assert.True(File.Exists(reportPath), $"Missing constraint-reduction report: {reportPath}");

        HashSet<string> jsonIds = inventory.RootElement
            .GetProperty("constraints")
            .EnumerateArray()
            .Select(static constraint => RequiredString(constraint, "id"))
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> markdownIds = [];

        bool inInventory = false;

        foreach (string line in File.ReadLines(reportPath))
        {

            if (line == "<!-- constraint-inventory:start -->")
            {

                inInventory = true;

                continue;

            }

            if (line == "<!-- constraint-inventory:end -->")
            {

                inInventory = false;

                break;

            }

            if (!inInventory
                || !line.StartsWith("| `", StringComparison.Ordinal))
            {

                continue;

            }

            int closingTick = line.IndexOf('`', 3);

            Assert.True(closingTick > 3, $"Malformed inventory row: {line}");

            markdownIds.Add(line[3..closingTick]);

        }

        Assert.Equal(
            jsonIds.OrderBy(static id => id, StringComparer.Ordinal),
            markdownIds.OrderBy(static id => id, StringComparer.Ordinal));

    }

    private static void CollectNumericConfigurationSymbols(
        Type type,
        string prefix,
        HashSet<string> symbols,
        int depth)
    {

        Assert.True(depth <= 12, $"Configuration traversal exceeded depth at {prefix}.");

        foreach (PropertyInfo property in type.GetProperties(
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.DeclaredOnly))
        {

            if (property.SetMethod?.IsPublic != true)
            {

                continue;

            }

            string symbol = $"{prefix}.{ToCamelCase(property.Name)}";

            Type propertyType = Nullable.GetUnderlyingType(property.PropertyType)
                ?? property.PropertyType;

            if (IsNumericOrTime(propertyType))
            {

                symbols.Add(symbol);

                continue;

            }

            if (TryGetDictionaryValue(propertyType, out Type? valueType))
            {

                Type value = Nullable.GetUnderlyingType(valueType!) ?? valueType!;

                if (IsNumericOrTime(value))
                {

                    symbols.Add(symbol + "{}");

                }
                else if (IsConfigurationType(value))
                {

                    CollectNumericConfigurationSymbols(
                        value,
                        symbol + "{}",
                        symbols,
                        depth + 1);

                }

                continue;

            }

            if (TryGetCollectionElement(propertyType, out Type? elementType))
            {

                Type element = Nullable.GetUnderlyingType(elementType!) ?? elementType!;

                if (IsNumericOrTime(element))
                {

                    symbols.Add(symbol + "[]");

                }
                else if (IsConfigurationType(element))
                {

                    CollectNumericConfigurationSymbols(
                        element,
                        symbol + "[]",
                        symbols,
                        depth + 1);

                }

                continue;

            }

            if (IsConfigurationType(propertyType))
            {

                CollectNumericConfigurationSymbols(
                    propertyType,
                    symbol,
                    symbols,
                    depth + 1);

            }

        }

    }

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

    private static bool IsConfigurationType(Type type) =>
        type.Namespace?.StartsWith(
            "RetroDownfall.Arcanum.Core.Configuration",
            StringComparison.Ordinal) == true;

    private static bool IsNumericOrTime(Type type) =>
        type == typeof(byte)
        || type == typeof(sbyte)
        || type == typeof(short)
        || type == typeof(ushort)
        || type == typeof(int)
        || type == typeof(uint)
        || type == typeof(long)
        || type == typeof(ulong)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(decimal)
        || type == typeof(TimeSpan);

    private static JsonDocument LoadInventory()
    {

        string path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "Arcanum.ConstraintInventory.json");

        Assert.True(File.Exists(path), $"Missing structured constraint inventory: {path}");

        return JsonDocument.Parse(File.ReadAllText(path));

    }

    private static string RequiredString(JsonElement element, string propertyName)
    {

        Assert.True(
            element.TryGetProperty(propertyName, out JsonElement property),
            $"Constraint is missing '{propertyName}': {element.GetRawText()}");

        Assert.Equal(JsonValueKind.String, property.ValueKind);

        string? value = property.GetString();

        Assert.False(string.IsNullOrWhiteSpace(value), $"Constraint '{propertyName}' is blank.");

        return value!;

    }

    private static string[] RequiredStringArray(JsonElement element, string propertyName)
    {

        Assert.True(
            element.TryGetProperty(propertyName, out JsonElement property),
            $"Constraint is missing '{propertyName}': {element.GetRawText()}");

        Assert.Equal(JsonValueKind.Array, property.ValueKind);

        return property
            .EnumerateArray()
            .Select(item =>
            {

                Assert.Equal(JsonValueKind.String, item.ValueKind);

                string? value = item.GetString();

                Assert.False(string.IsNullOrWhiteSpace(value), $"Constraint '{propertyName}' contains a blank value.");

                return value!;

            })
            .ToArray();

    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    private static bool TryGetCollectionElement(Type type, out Type? elementType)
    {

        if (type.IsArray)
        {

            elementType = type.GetElementType();

            return true;

        }

        Type? enumerable = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        elementType = enumerable?.GetGenericArguments()[0];

        return elementType is not null;

    }

    private static bool TryGetDictionaryValue(Type type, out Type? valueType)
    {

        Type? dictionary = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        valueType = dictionary?.GetGenericArguments()[1];

        return valueType is not null;

    }

}

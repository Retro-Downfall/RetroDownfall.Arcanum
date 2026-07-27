using System.Reflection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Models;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

public sealed class SettingDescriptorParityTests
{

    [Fact]

    public void Every_numeric_descriptor_clamp_matches_ArcanumSettingClamps_bounds()
    {

        TypeInfo clamps = typeof(ArcanumSettingClamps).GetTypeInfo();

        List<(SettingDescriptor Descriptor, string Reason)> failures = [];

        foreach (SettingDescriptor descriptor in SettingDescriptors.All)

        {

            if (string.IsNullOrEmpty(descriptor.ClampName))

            {

                continue;

            }

            MethodInfo? method = clamps.GetMethod(
                descriptor.ClampName,
                BindingFlags.Public | BindingFlags.Static);

            if (method is null)

            {

                failures.Add((descriptor, $"No ArcanumSettingClamps.{descriptor.ClampName} method found."));

                continue;

            }

            ParameterInfo[] parameters = method.GetParameters();

            if (parameters.Length != 1)

            {

                failures.Add((descriptor, $"{descriptor.ClampName} has {parameters.Length} parameters; expected 1."));

                continue;

            }

            Type paramType = parameters[0].ParameterType;

            object minInput = GetMinValue(paramType);

            object maxInput = GetMaxValue(paramType);

            object clampedMin = method.Invoke(null, [minInput])!;

            object clampedMax = method.Invoke(null, [maxInput])!;

            double actualMin = Convert.ToDouble(clampedMin, System.Globalization.CultureInfo.InvariantCulture);

            double actualMax = Convert.ToDouble(clampedMax, System.Globalization.CultureInfo.InvariantCulture);

            if (actualMin != descriptor.Min)

            {

                failures.Add((descriptor, $"Min mismatch for {descriptor.Key}: descriptor={descriptor.Min}, clamp={actualMin}."));

            }

            if (actualMax != descriptor.Max)

            {

                failures.Add((descriptor, $"Max mismatch for {descriptor.Key}: descriptor={descriptor.Max}, clamp={actualMax}."));

            }

        }

        if (failures.Count > 0)

        {

            string report = string.Join("\n", failures.Select(f => $"  {f.Descriptor.Key}: {f.Reason}"));

            Assert.Fail($"{failures.Count} descriptor/clamp mismatch(es):\n{report}");

        }

    }

    [Fact]

    public void Every_numeric_descriptor_has_a_clamp_name()
    {

        SettingKind[] numericKinds = [SettingKind.Int, SettingKind.Long, SettingKind.Float];

        List<string> missing = SettingDescriptors.All

            .Where(d => numericKinds.Contains(d.Kind) && string.IsNullOrEmpty(d.ClampName))

            .Select(d => d.Key)

            .ToList();

        Assert.True(missing.Count == 0, $"Numeric descriptors missing ClampName: {string.Join(", ", missing)}");

    }

    [Theory]
    [InlineData("providers.models.reasoning.controlSupport", typeof(ReasoningControlSupport))]
    [InlineData("providers.models.reasoning.wireDialect", typeof(ReasoningWireDialect))]
    public void Reasoning_enum_descriptors_match_contract_types(string key, Type enumType)
    {

        SettingDescriptor descriptor = Assert.Single(SettingDescriptors.All, d => d.Key == key);

        Assert.Equal(SettingKind.Enum, descriptor.Kind);
        Assert.Equal(enumType, descriptor.EnumType);

    }

    [Fact]
    public void Reasoning_budget_descriptor_matches_global_request_bounds()
    {

        SettingDescriptor descriptor = Assert.Single(
            SettingDescriptors.All,
            static d => d.Key == "providers.models.reasoning.maxBudgetTokens");

        Assert.Equal(SettingKind.Int, descriptor.Kind);
        Assert.Equal(1, descriptor.Min);
        Assert.Equal(2_097_152, descriptor.Max);
        Assert.Equal(nameof(ArcanumSettingClamps.ReasoningBudgetTokens), descriptor.ClampName);

    }

    [Fact]
    public void Pricing_reasoning_descriptor_uses_output_price_bounds()
    {
        SettingDescriptor descriptor = Assert.Single(
            SettingDescriptors.All,
            static d => d.Key == "pricing.defaultPricing.reasoningPer1M");

        Assert.Equal(SettingKind.Float, descriptor.Kind);
        Assert.Equal(0, descriptor.Min);
        Assert.Equal(1_000_000, descriptor.Max);
        Assert.Equal(nameof(ArcanumSettingClamps.PricingOutputPer1M), descriptor.ClampName);
    }

    [Theory]
    [InlineData(
        "codingTools.search.maxPatternChars",
        1,
        16_384,
        nameof(ArcanumSettingClamps.WorkspaceSearchMaxPatternChars))]
    [InlineData(
        "codingTools.patch.fuzzyMatchWindowLines",
        0,
        1_000,
        nameof(ArcanumSettingClamps.WorkspacePatchFuzzyMatchWindowLines))]
    [InlineData(
        "codingTools.workspaceCheck.timeoutSeconds",
        30,
        1_800,
        nameof(ArcanumSettingClamps.WorkspaceCheckTimeoutSeconds))]
    public void Coding_tool_descriptor_matches_contract_bounds(
        string key,
        double min,
        double max,
        string clampName)
    {
        SettingDescriptor descriptor = Assert.Single(SettingDescriptors.All, d => d.Key == key);

        Assert.Equal(min, descriptor.Min);
        Assert.Equal(max, descriptor.Max);
        Assert.Equal(clampName, descriptor.ClampName);
    }

    private static object GetMinValue(Type type)
    {

        if (type == typeof(int))

        {

            return int.MinValue;

        }

        if (type == typeof(long))

        {

            return long.MinValue;

        }

        if (type == typeof(float))

        {

            return float.NegativeInfinity;

        }

        if (type == typeof(double))

        {

            return double.NegativeInfinity;

        }

        if (type == typeof(decimal))

        {

            return decimal.MinValue;

        }

        throw new NotSupportedException($"Unsupported clamp parameter type {type}");

    }

    private static object GetMaxValue(Type type)
    {

        if (type == typeof(int))

        {

            return int.MaxValue;

        }

        if (type == typeof(long))

        {

            return long.MaxValue;

        }

        if (type == typeof(float))

        {

            return float.PositiveInfinity;

        }

        if (type == typeof(double))

        {

            return double.PositiveInfinity;

        }

        if (type == typeof(decimal))

        {

            return decimal.MaxValue;

        }

        throw new NotSupportedException($"Unsupported clamp parameter type {type}");

    }

}

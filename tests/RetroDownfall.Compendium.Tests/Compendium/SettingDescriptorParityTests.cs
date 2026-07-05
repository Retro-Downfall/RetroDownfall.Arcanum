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

        throw new NotSupportedException($"Unsupported clamp parameter type {type}");

    }

}

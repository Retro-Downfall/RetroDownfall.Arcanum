using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Compendium.Ux.Models;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class GenericSettingFieldViewModel : ObservableObject
{

    public GenericSettingFieldViewModel(SettingDescriptor descriptor, object? value)
    {

        Descriptor = descriptor;

        Group = descriptor.Group
            ?? DeriveGroup(descriptor.Key);

        _value = value;

        if (descriptor.EnumType is not null)
        {

            EnumValues = Enum.GetValues(descriptor.EnumType).Cast<object>().ToArray();

        }

    }

    public SettingDescriptor Descriptor { get; }

    public string Group { get; }

    public IReadOnlyList<object> EnumValues { get; } = [];

    [ObservableProperty]
    private object? _value;

    public bool BoolValue
    {

        get => Value is true;

        set => Value = value;

    }

    public double NumericValue
    {

        get => Value switch
        {
            null => 0,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => Convert.ToDouble(Value),
        };

        set => Value = descriptorKindConvert(value);

    }

    public string StringValue
    {

        get => Value switch
        {
            null => string.Empty,
            string s => s,
            string[] arr => string.Join(", ", arr),
            IDictionary<string, string> dict => string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value}")),
            _ => Value.ToString() ?? string.Empty,
        };

        set
        {

            if (Descriptor.Kind == SettingKind.StringArray)
            {

                Value = string.IsNullOrWhiteSpace(value)
                    ? Array.Empty<string>()
                    : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return;

            }

            Value = value;

        }

    }

    private object? descriptorKindConvert(double value)
    {

        return Descriptor.Kind switch
        {
            SettingKind.Int => (int)Math.Round(value),
            SettingKind.Long => (long)Math.Round(value),
            SettingKind.Float => (float)value,
            _ => value,
        };

    }

    private static string DeriveGroup(string key)
    {

        string[] parts = key.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 3)
        {

            return ToTitle(parts[1]);

        }

        return "General";

    }

    private static string ToTitle(string value)
    {

        if (string.IsNullOrEmpty(value))
        {

            return value;

        }

        return char.ToUpperInvariant(value[0]) + value[1..];

    }

}

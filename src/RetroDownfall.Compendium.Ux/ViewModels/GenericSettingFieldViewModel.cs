using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class GenericSettingFieldViewModel : ObservableObject
{

    public GenericSettingFieldViewModel(SettingDescriptor descriptor, object? value)
    {

        Descriptor = descriptor;

        Group = descriptor.Group
            ?? DeriveGroup(descriptor.Key);

        CollectionTemplatePath = GenericSettingsUpdater.ResolveCollectionTemplatePath(descriptor.Key);

        _value = descriptor.Kind == SettingKind.Dictionary
            ? SerializeDictionary(value)
            : value;

        if (descriptor.EnumType is not null)
        {

            EnumValues = Enum.GetValues(descriptor.EnumType).Cast<object>().ToArray();

        }

        Validate();

    }

    public SettingDescriptor Descriptor { get; }

    public string Group { get; }

    /// <summary>
    /// Dotted path of the collection this descriptor describes an element field of, or <c>null</c> for
    /// an ordinary single-value setting. The generic editor has no per-element UI, so such a field is
    /// rendered read-only rather than as a box whose contents Save would drop.
    /// </summary>
    public string? CollectionTemplatePath { get; }

    public bool IsCollectionTemplate => CollectionTemplatePath is not null;

    public IReadOnlyList<object> EnumValues { get; } = [];

    [ObservableProperty]
    private object? _value;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsSet
    {
        get => Value is not null;
        set
        {
            if (!Descriptor.AllowUnset)
            {
                return;
            }

            if (value && Value is null)
            {
                Value = descriptorKindConvert(0);
            }
            else if (!value)
            {
                Value = null;
            }
        }
    }

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
            Guid[] ids => string.Join(", ", ids),
            IEnumerable<string> items => string.Join(", ", items),
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
            // Float settings are money backed by decimal properties. Narrowing to float would keep only
            // ~7 significant digits, silently rounding a six-figure limit typed to the cent.
            SettingKind.Float => (decimal)value,
            _ => value,
        };

    }

    partial void OnValueChanged(object? oldValue, object? newValue)
    {
        OnPropertyChanged(nameof(StringValue));
        OnPropertyChanged(nameof(BoolValue));

        if (Descriptor.Kind is SettingKind.Int or SettingKind.Long or SettingKind.Float)
        {
            OnPropertyChanged(nameof(NumericValue));
        }

        if ((oldValue is null) != (newValue is null))
        {
            OnPropertyChanged(nameof(IsSet));
        }

        Validate();
    }

    private void Validate()
    {
        var errors = new List<string>();

        // Validate environment variable names
        if (Descriptor.Key.EndsWith("EnvironmentVariable") && Value is string envVarName && !string.IsNullOrWhiteSpace(envVarName))
        {
            if (!ConfigurationInputValidator.TryValidateEnvironmentVariableName(envVarName, out string? error))
            {
                errors.Add(error ?? "Invalid environment variable name");
            }
        }

        // Validate CORS origins
        if (Descriptor.Key == "host.corsAllowedOrigins" && Value is string[] origins)
        {
            if (!ConfigurationInputValidator.TryValidateCorsOrigins(string.Join(", ", origins), out var corsErrors))
            {
                errors.AddRange(corsErrors);
            }
        }

        if (Descriptor.Key == "retention.protectedSessionIds")
        {

            IEnumerable<string> sessionIds = Value switch
            {

                Guid[] ids => ids.Select(static id => id.ToString()),

                IEnumerable<string> ids => ids,

                string text => text.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries),

                _ => [],

            };

            if (sessionIds.Any(static id => !Guid.TryParse(id, out _)))
            {

                errors.Add(
                    "Protected session IDs must be GUID values separated by commas");

            }

        }

        // Validate free-text JSON maps against the exact type Save coerces them to
        if (Descriptor.Kind == SettingKind.Dictionary
            && Value is string dictionaryJson
            && !TryValidateDictionaryJson(dictionaryJson, out string? dictionaryError))
        {
            errors.Add(dictionaryError ?? "Invalid JSON");
        }

        // Validate numeric ranges. An unset value has nothing to range-check: template descriptors such
        // as providers.contextWindowLimit read as null until a provider exists, and NumericValue would
        // otherwise report their placeholder 0 as out of bounds.
        if (Descriptor.Kind is SettingKind.Int or SettingKind.Long or SettingKind.Float
            && Value is not null)
        {
            double numericValue = NumericValue;
            if (numericValue < Descriptor.Min || numericValue > Descriptor.Max)
            {
                errors.Add($"Value must be between {Descriptor.Min} and {Descriptor.Max}");
            }
        }

        // Validate required fields
        if (Descriptor.Kind == SettingKind.String && Value is string strValue && string.IsNullOrWhiteSpace(strValue))
        {
            // Check if this is a required field (not allowing empty)
            if (!Descriptor.AllowUnset && Descriptor.Key.Contains("name"))
            {
                errors.Add("This field is required");
            }
        }

        // Validate URLs
        if (Descriptor.Key.Contains("endpoint") || Descriptor.Key.Contains("url") || Descriptor.Key.Contains("origin"))
        {
            if (Value is string urlValue && !string.IsNullOrWhiteSpace(urlValue))
            {
                if (!Uri.TryCreate(urlValue, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    errors.Add("Must be a valid HTTP or HTTPS URL");
                }
            }
        }

        ErrorMessage = errors.Count > 0 ? string.Join("; ", errors) : null;
        OnPropertyChanged(nameof(HasError));
    }

    private bool TryValidateDictionaryJson(string json, out string? error)
    {

        string text = string.IsNullOrWhiteSpace(json) ? "{}" : json;

        Type? targetType = GenericSettingsUpdater.ResolveValueType(Descriptor.Key);

        try
        {

            if (targetType is null)
            {

                using JsonDocument document = JsonDocument.Parse(text);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {

                    error = "Must be a JSON object";

                    return false;

                }

            }
            else
            {

                _ = JsonSerializer.Deserialize(text, targetType, ConfigurationJsonContext.Default);

            }

            error = null;

            return true;

        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {

            error = $"Invalid JSON: {ex.Message}";

            return false;

        }

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

    private static string SerializeDictionary(object? value)
    {

        if (value is null)
        {

            return "{}";

        }

        return JsonSerializer.Serialize(
            value,
            value.GetType(),
            ConfigurationJsonContext.Default);

    }

}

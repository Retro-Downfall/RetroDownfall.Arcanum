using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Tower;

public sealed partial class PromptRenderer
{

    private readonly IManaMeter _manaMeter;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public PromptRenderer(IManaMeter manaMeter, IOptionsMonitor<ArcanumSettings> settings)
    {

        _manaMeter = manaMeter;

        _settings = settings;

    }

    public Result<PromptRenderResultDto> Render(
        Prompt prompt,
        Dictionary<string, string>? parameters)
    {
        parameters ??= new Dictionary<string, string>(StringComparer.Ordinal);

        Result<Dictionary<string, string>> validated = ValidateParameters(
            prompt.ParameterSchema,
            parameters,
            requireAllRequired: false,
            maxParameterValueChars: ResolveMaxParameterValueChars());

        if (validated.IsFailure)
        {
            return Result<PromptRenderResultDto>.Failure(validated.Error);
        }

        string rendered = Substitute(prompt.Template, validated.Value!);

        int tokenCount = _manaMeter.CountTokens(rendered);

        return Result<PromptRenderResultDto>.Success(new PromptRenderResultDto(rendered, tokenCount));
    }

    public Result<Dictionary<string, string>> ResolveDefaultParameters(Prompt prompt)
    {
        Dictionary<string, string> defaults = new(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(prompt.DefaultParameters))
        {
            using JsonDocument doc = JsonDocument.Parse(prompt.DefaultParameters);

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    defaults[prop.Name] = prop.Value.ToString();
                }
            }
        }

        Result<Dictionary<string, string>> validated = ValidateParameters(
            prompt.ParameterSchema,
            defaults,
            requireAllRequired: true,
            maxParameterValueChars: ResolveMaxParameterValueChars());

        return validated;
    }

    private int ResolveMaxParameterValueChars() =>
        ArcanumSettingClamps.MaxParameterValueChars(
            ArcanumRuntimeDefaults.Prompts.MaxParameterValueChars);

    private static Result<Dictionary<string, string>> ValidateParameters(
        string? parameterSchemaJson,
        Dictionary<string, string> parameters,
        bool requireAllRequired = false,
        int maxParameterValueChars = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(parameterSchemaJson))
        {
            if (parameters.Count > 0)
            {
                return Result<Dictionary<string, string>>.Failure(
                    new Error("Prompt.UnknownParameter", "This prompt does not accept parameters."));
            }

            return Result<Dictionary<string, string>>.Success(parameters);
        }

        try
        {
            using JsonDocument schema = JsonDocument.Parse(parameterSchemaJson);

            JsonElement root = schema.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<Dictionary<string, string>>.Success(parameters);
            }

            HashSet<string> allowed = new(StringComparer.Ordinal);

            if (root.TryGetProperty("properties", out JsonElement properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in properties.EnumerateObject())
                {
                    _ = allowed.Add(prop.Name);
                }
            }
            else
            {
                return Result<Dictionary<string, string>>.Failure(
                    new Error("Prompt.InvalidParameterSchema", "Parameter schema must declare a non-empty properties object."));
            }

            foreach (string key in parameters.Keys)
            {
                if (!allowed.Contains(key))
                {
                    return Result<Dictionary<string, string>>.Failure(
                        new Error("Prompt.UnknownParameter", $"Parameter '{key}' is not declared in the parameter schema."));
                }

                string value = parameters[key];

                if (value.Length > maxParameterValueChars)
                {
                    return Result<Dictionary<string, string>>.Failure(
                        new Error(
                            "Prompt.ParameterValueTooLong",
                            $"Parameter '{key}' exceeds the maximum length ({maxParameterValueChars} characters)."));
                }
            }

            if (root.TryGetProperty("required", out JsonElement required)
                && required.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? name = item.GetString();

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!parameters.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
                    {
                        return Result<Dictionary<string, string>>.Failure(
                            new Error(
                                requireAllRequired ? "Prompt.RequiredParameterMissing" : "Prompt.MissingParameter",
                                $"Required parameter '{name}' was not provided."));
                    }
                }
            }

            return Result<Dictionary<string, string>>.Success(parameters);
        }
        catch (JsonException)
        {
            return Result<Dictionary<string, string>>.Failure(
                new Error("Prompt.InvalidParameterSchema", "Parameter schema is not valid JSON."));
        }
    }

    private static string Substitute(string template, Dictionary<string, string> parameters)
    {
        return PlaceholderRegex().Replace(template, match =>
        {
            string key = match.Groups[1].Value;

            if (!parameters.TryGetValue(key, out string? value))
            {
                return match.Value;
            }

            return JsonSerializer.Serialize(value, ArcanumCoreJsonContext.Default.String);
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderRegex();

}

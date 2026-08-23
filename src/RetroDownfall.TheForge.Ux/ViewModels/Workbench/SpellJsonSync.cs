using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Pure helper for round-tripping known SPELL.json metadata fields between <see cref="SpellDetail"/>,
/// the visual designer, and raw JSON. Unknown sidecar properties are not preserved through
/// <see cref="UpdateSpellRequest"/>.
/// </summary>
public static class SpellJsonSync
{

    public sealed record DesignerState(
        string Version,
        string? ActiveVersion,
        IReadOnlyList<string> Dependencies,
        IReadOnlyList<string> DeclaredTools,
        string InputSchemaJson,
        string OutputSchemaJson);

    public static DesignerState LoadFromSpell(SpellDetail? spell)
    {

        if (spell is null)
        {

            return new DesignerState(
                Version: string.Empty,
                ActiveVersion: null,
                Dependencies: [],
                DeclaredTools: [],
                InputSchemaJson: "{}",
                OutputSchemaJson: "{}");

        }

        return new DesignerState(
            Version: spell.Version ?? string.Empty,
            ActiveVersion: spell.ActiveVersion,
            Dependencies: spell.Dependencies ?? [],
            DeclaredTools: spell.DeclaredTools ?? [],
            InputSchemaJson: SchemaToText(spell.InputSchema),
            OutputSchemaJson: SchemaToText(spell.OutputSchema));

    }

    public static string SerializeKnownFields(SpellDetail spell, DesignerState? designer = null)
    {

        DesignerState state = designer ?? LoadFromSpell(spell);

        TryParseSchemaJson(state.InputSchemaJson, out JsonDocument? inputSchema, out _);

        TryParseSchemaJson(state.OutputSchemaJson, out JsonDocument? outputSchema, out _);

        try
        {

            SkillMetadata metadata = new(
                Name: spell.Name,
                Version: string.IsNullOrWhiteSpace(state.Version) ? string.Empty : state.Version,
                Description: spell.Description,
                Tags: [.. spell.Tags],
                InputSchema: IsEmptyObject(inputSchema) ? null : inputSchema,
                OutputSchema: IsEmptyObject(outputSchema) ? null : outputSchema,
                DeclaredTools: [.. state.DeclaredTools],
                Dependencies: [.. state.Dependencies],
                Model: spell.Model,
                Provider: spell.Provider,
                DefaultParameters: null,
                LastModified: null,
                ActiveVersion: state.ActiveVersion);

            return JsonSerializer.Serialize(metadata, TheForgeJsonContext.Default.SkillMetadata);

        }
        finally
        {

            inputSchema?.Dispose();

            outputSchema?.Dispose();

        }

    }

    public static bool TryParseRaw(string json, out SkillMetadata? metadata, out string? error)
    {

        metadata = null;

        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {

            error = "SPELL.json is empty.";

            return false;

        }

        try
        {

            metadata = JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.SkillMetadata);

            if (metadata is null)
            {

                error = "SPELL.json deserialized to null.";

                return false;

            }

            return true;

        }
        catch (JsonException ex)
        {

            error = ex.Message;

            return false;

        }

    }

    public static bool TryParseSchemaJson(string? json, out JsonDocument? document, out string? error)
    {

        document = null;

        error = null;

        string trimmed = string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();

        try
        {

            document = JsonDocument.Parse(trimmed);

            return true;

        }
        catch (JsonException ex)
        {

            error = ex.Message;

            return false;

        }

    }

    public static bool TryBuildUpdateFields(
        DesignerState designer,
        out string? version,
        out JsonDocument? inputSchema,
        out JsonDocument? outputSchema,
        out string[]? declaredTools,
        out string[]? dependencies,
        out string? error)
    {

        version = string.IsNullOrWhiteSpace(designer.Version) ? designer.Version : designer.Version.Trim();

        inputSchema = null;

        outputSchema = null;

        declaredTools = null;

        dependencies = null;

        error = null;

        if (!TryParseSchemaJson(designer.InputSchemaJson, out inputSchema, out string? inputError))
        {

            error = $"Input schema: {inputError}";

            return false;

        }

        if (!TryParseSchemaJson(designer.OutputSchemaJson, out outputSchema, out string? outputError))
        {

            inputSchema?.Dispose();

            inputSchema = null;

            error = $"Output schema: {outputError}";

            return false;

        }

        declaredTools = [.. designer.DeclaredTools];

        dependencies = [.. designer.Dependencies];

        return true;

    }

    private static string SchemaToText(JsonDocument? schema)
    {

        if (schema is null
            || schema.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {

            return "{}";

        }

        return schema.RootElement.GetRawText();

    }

    private static bool IsEmptyObject(JsonDocument? document)
    {

        if (document is null)
        {

            return true;

        }

        JsonElement root = document.RootElement;

        if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {

            return true;

        }

        return root.ValueKind == JsonValueKind.Object && !root.EnumerateObject().Any();

    }

}

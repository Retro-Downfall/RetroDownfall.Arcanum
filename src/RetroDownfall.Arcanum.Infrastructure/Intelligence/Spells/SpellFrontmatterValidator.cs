using RetroDownfall.Arcanum.Core.Intelligence.Spells;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal static class SpellFrontmatterValidator
{

    public static string? ValidateCreate(CreateSpellRequest request)
    {
        string? error = ValidateScalar(request.Description, "description");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.SystemPrompt, "systemPrompt");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.Template, "template");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.Model, "model");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.Provider, "provider");

        if (error is not null)
        {
            return error;
        }

        error = ValidateArray(request.Tags, "tags");

        if (error is not null)
        {
            return error;
        }

        error = ValidateArray(request.Tools, "tools");

        if (error is not null)
        {
            return error;
        }

        return ValidateArray(request.RequiredMcpServers, "requiredMcpServers");
    }

    public static string? ValidateUpdate(UpdateSpellRequest request)
    {
        string? error = ValidateScalar(request.Description, "description");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.SystemPrompt, "systemPrompt");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.Template, "template");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.Model, "model");

        if (error is not null)
        {
            return error;
        }

        error = ValidateScalar(request.Provider, "provider");

        if (error is not null)
        {
            return error;
        }

        error = ValidateArray(request.Tags, "tags");

        if (error is not null)
        {
            return error;
        }

        error = ValidateArray(request.Tools, "tools");

        if (error is not null)
        {
            return error;
        }

        return ValidateArray(request.RequiredMcpServers, "requiredMcpServers");
    }

    public static string? ValidateParsed(SpellParseResult parsed)
    {

        string? error = ValidateScalar(parsed.Description, "description");

        if (error is not null)
        {

            return error;

        }

        error = ValidateScalar(parsed.SystemPrompt, "systemPrompt");

        if (error is not null)
        {

            return error;

        }

        error = ValidateScalar(parsed.Template, "template");

        if (error is not null)
        {

            return error;

        }

        error = ValidateScalar(parsed.Model, "model");

        if (error is not null)
        {

            return error;

        }

        error = ValidateScalar(parsed.Provider, "provider");

        if (error is not null)
        {

            return error;

        }

        error = ValidateArray(parsed.Tags, "tags");

        if (error is not null)
        {

            return error;

        }

        error = ValidateArray(parsed.Tools, "tools");

        if (error is not null)
        {

            return error;

        }

        return ValidateArray(parsed.RequiredMcpServers, "requiredMcpServers");

    }

    private static string? ValidateScalar(string? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            return $"{fieldName} must not contain line breaks.";
        }

        if (value.TrimStart().StartsWith("---", StringComparison.Ordinal))
        {
            return $"{fieldName} must not start with '---'.";
        }

        return null;
    }

    private static string? ValidateArray(string[]? values, string fieldName)
    {
        if (values is null || values.Length == 0)
        {
            return null;
        }

        for (int i = 0; i < values.Length; i++)
        {
            string? error = ValidateScalar(values[i], fieldName);

            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

}

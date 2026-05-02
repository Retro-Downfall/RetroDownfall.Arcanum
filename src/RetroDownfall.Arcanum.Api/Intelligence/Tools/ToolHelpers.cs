using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

internal static class ToolHelpers
{
    internal static bool TryNormalizeWorkspace(
        string workingDirectory,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? configurationErrorMessage)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            normalized = null;

            configurationErrorMessage = "No workspace directory was provided for this request. The operator should run `arcanum ask` from their project folder so file paths and commands are scoped to that workspace.";

            return false;
        }

        try
        {
            normalized = Path.GetFullPath(workingDirectory.Trim());

            configurationErrorMessage = null;

            return true;
        }
        catch (Exception)
        {
            normalized = null;

            configurationErrorMessage = "The workspace directory on this request could not be resolved. Please ask the operator to use a valid path and try again.";

            return false;
        }
    }

    internal static bool IsPathUnderWorkspace(string workspaceRootFull, string candidateFull)
    {
        char sep = Path.DirectorySeparatorChar;

        string root = workspaceRootFull.TrimEnd(sep);

        string prefix = root + sep;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidateFull.Equals(root, cmp) || candidateFull.StartsWith(prefix, cmp);
    }

    internal static bool TryGetRequiredStringArgument(
        AIFunctionArguments arguments,
        string key,
        [NotNullWhen(true)] out string? value,
        [NotNullWhen(false)] out string? errorMessage)
    {
        if (!arguments.TryGetValue(key, out object? raw) || raw is null)
        {
            value = null;

            errorMessage = $"The tool call is missing the required parameter '{key}'. Please retry with that argument set.";

            return false;
        }

        string? coerced = CoerceToString(raw);

        if (string.IsNullOrEmpty(coerced))
        {
            value = null;

            errorMessage = $"The required parameter '{key}' was empty. Please provide a non-empty value.";

            return false;
        }

        value = coerced;

        errorMessage = null;

        return true;
    }

    internal static bool TryGetOptionalStringArgument(
        AIFunctionArguments arguments,
        string key,
        [NotNullWhen(true)] out string? value)
    {
        if (!arguments.TryGetValue(key, out object? raw) || raw is null)
        {
            value = null;

            return false;
        }

        string? coerced = CoerceToString(raw);

        if (string.IsNullOrWhiteSpace(coerced))
        {
            value = null;

            return false;
        }

        value = coerced.Trim();

        return true;
    }

    private static string? CoerceToString(object raw)
    {
        return raw switch
        {
            string s => s,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => je.GetRawText(),
            },
            _ => raw.ToString(),
        };
    }
}

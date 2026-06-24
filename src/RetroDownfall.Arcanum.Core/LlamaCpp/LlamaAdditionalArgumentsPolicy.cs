namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Validates <c>llama-server</c> additional CLI arguments supplied via configuration.
/// </summary>
public static class LlamaAdditionalArgumentsPolicy
{

    /// <summary>
    /// Returns <c>true</c> when <paramref name="additionalArguments"/> contains <c>--host</c> or <c>--port</c>
    /// tokens that would override Arcanum-managed binding.
    /// </summary>
    public static bool ContainsReservedBindingArgument(string[]? additionalArguments, out string? rejectedToken)
    {

        if (additionalArguments is not { Length: > 0 })
        {

            rejectedToken = null;

            return false;

        }

        for (int i = 0; i < additionalArguments.Length; i++)
        {

            string? raw = additionalArguments[i];

            if (string.IsNullOrWhiteSpace(raw))
            {

                continue;

            }

            string trimmed = raw.Trim();

            if (trimmed.Equals("--host", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("--port", StringComparison.OrdinalIgnoreCase))
            {

                rejectedToken = trimmed;

                return true;

            }

            if (trimmed.StartsWith("--host=", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
            {

                rejectedToken = trimmed;

                return true;

            }

        }

        rejectedToken = null;

        return false;

    }

}

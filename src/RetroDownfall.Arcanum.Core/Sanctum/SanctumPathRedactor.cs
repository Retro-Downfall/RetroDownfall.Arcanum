namespace RetroDownfall.Arcanum.Core.Sanctum;

/// <summary>
/// Reduces a filesystem path to its filename component for display in breach responses, so full
/// directory structure is never echoed back to a model or API consumer. Shared by the Sanctum
/// guard's synthetic-denial mapping and the breach query endpoint's mapping from persisted
/// <see cref="Storage.SanctumBreachRecord"/> rows.
/// </summary>
public static class SanctumPathRedactor
{

    public static string? Redact(string? path)
    {

        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetFileName(path.Trim());
        }
        catch (Exception)
        {
            return "[redacted]";
        }

    }

}

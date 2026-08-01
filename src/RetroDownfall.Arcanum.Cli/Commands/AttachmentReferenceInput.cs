namespace RetroDownfall.Arcanum.Cli.Commands;

internal static class AttachmentReferenceInput
{

    public static bool TryParse(
        string[]? values,
        out List<Guid>? attachmentReferences,
        out string? error)
    {

        attachmentReferences = null;

        error = null;

        if (values is not { Length: > 0 })
        {

            return true;

        }

        List<Guid> parsed = new(values.Length);

        foreach (string value in values)
        {

            if (!Guid.TryParse(value, out Guid attachmentId))
            {

                error = $"--attachment '{value}' must be an attachment GUID.";

                return false;

            }

            parsed.Add(attachmentId);

        }

        attachmentReferences = parsed;

        return true;

    }

}

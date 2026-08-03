using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;


namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public static class GrimoireLimits
{

    public static Error? EnforceEntryLimits(
        int currentEntryCount,
        int entriesToAdd,
        SessionSettings? settings,
        params string?[] contents)
    {

        SessionSettings resolved = settings ?? ArcanumRuntimeDefaults.Sessions;

        int maxBytes = ArcanumSettingClamps.MaxEntryContentBytes(resolved.MaxEntryContentBytes);

        _ = currentEntryCount;

        _ = entriesToAdd;

        foreach (string? content in contents)
        {

            if (content is null)
            {

                continue;

            }

            if (Encoding.UTF8.GetByteCount(content) > maxBytes)
            {

                return new Error(
                    ErrorCodes.Session.EntryTooLarge,
                    $"Session.EntryTooLarge: Entry content cannot exceed {maxBytes} bytes.");

            }

        }

        return null;

    }

}

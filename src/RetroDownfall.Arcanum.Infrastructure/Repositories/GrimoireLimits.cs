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

        SessionSettings resolved = settings ?? new SessionSettings();

        int maxEntries = ArcanumSettingClamps.MaxEntriesPerSession(resolved.MaxEntriesPerSession);

        int maxBytes = ArcanumSettingClamps.MaxEntryContentBytes(resolved.MaxEntryContentBytes);

        if (currentEntryCount + entriesToAdd > maxEntries)
        {

            return new Error(
                ErrorCodes.Session.TooManyEntries,
                $"Session.TooManyEntries: Session cannot exceed {maxEntries} entries.");

        }

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

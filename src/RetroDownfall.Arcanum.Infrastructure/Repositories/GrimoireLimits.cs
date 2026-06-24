using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public static class GrimoireLimits
{

    public static void EnforceEntryLimits(
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

            throw new InvalidOperationException(
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

                throw new InvalidOperationException(
                    $"Session.EntryTooLarge: Entry content cannot exceed {maxBytes} bytes.");

            }

        }

    }

}

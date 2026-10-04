using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Memory;

using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Resolves the erasure key once at startup, before readiness, on an installation that holds erasure
/// fingerprints.
/// </summary>
/// <remarks>
/// <para>Covenant agent writes read the latch and never probe the credential store, so without this a
/// restarted host would withhold every one of them until an operator call happened to resolve the key.
/// Bootstrap calls it on its install handle after the schema is installed and restore authority is
/// revalidated, with no transaction open and no lease or closure held.</para>
///
/// <para>An installation with no fingerprints, or a catalog below Core version 13, reads nothing: on
/// macOS a keychain read can raise a prompt, and an operator who has never erased anything should never
/// see one. When fingerprints exist, the store is asked again whatever is latched, and the answer is
/// published into the latch every chokepoint reads.</para>
///
/// <para>A key that is not present is never a startup failure. It logs one content-free warning, and
/// the chokepoints refuse the automatic writes it would have guarded. A failing evidence read does
/// fail startup, exactly as every other bootstrap read does.</para>
/// </remarks>
internal static class MemoryErasureKeyWarmup
{
    private static readonly MemoryReviewStore[] Stores =
        [MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon];

    internal static async Task RunAsync(
        SqliteConnection connection,
        IMemoryErasureKeyProvider keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(keys);

        if (!await MemoryErasureEvidence.IsInstalledAsync(connection, null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        bool evidence = false;

        foreach (MemoryReviewStore store in Stores)
        {
            if (await MemoryErasureEvidence.AnyAsync(connection, null, store, cancellationToken).ConfigureAwait(false))
            {
                evidence = true;

                break;
            }
        }

        if (!evidence)
        {
            return;
        }

        MemoryErasureKeyOpenResult opened = keys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        // Only the latch matters here; the copy is released at once.
        opened.Key?.Dispose();

        if (opened.State is not MemoryErasureKeyState.Present)
        {
            Log.Warning(
                "Erasure fingerprints exist and the erasure key is {KeyState}; automatic writes to the stores that hold them stay withheld until 'arcanum memory erasure status' reports it present.",
                opened.State);
        }
    }
}

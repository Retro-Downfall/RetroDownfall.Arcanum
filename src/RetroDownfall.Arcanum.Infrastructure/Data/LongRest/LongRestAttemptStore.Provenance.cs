using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestAttemptStore
{
    // At most six JSON escape bytes per identity byte, plus fixed fence/coordinate fields.
    internal const int MaxOriginalWorkBytes = 6 * LongRestPolicy.MaxIdentityBytes + 8192;

    internal static string EncodeOriginalWork(LongRestWorkIdentity work)
    {
        if (LongRestDiscoveryHash.Work(work).IsFailure) throw new InvalidDataException();

        string json = JsonSerializer.Serialize(LongRestStoredWork.From(work), LongRestJsonContext.Default.LongRestStoredWork);

        if (StrictUtf8.GetByteCount(json) > MaxOriginalWorkBytes) throw new InvalidDataException();

        return json;
    }

    internal static LongRestWorkIdentity ReadOriginalWork(string json, CovenantDigest digest)
    {
        if (StrictUtf8.GetByteCount(json) is < 2 or > MaxOriginalWorkBytes) throw new InvalidDataException();

        LongRestWorkIdentity original = (JsonSerializer.Deserialize(json, LongRestJsonContext.Default.LongRestStoredWork)
            ?? throw new InvalidDataException()).ToDomain();

        var hash = LongRestDiscoveryHash.Work(original);

        // Canonical equality also refuses missing, unknown, duplicate or noncanonical JSON
        // fields without changing the interpretation of older retained artifact encodings.
        if (hash.IsFailure || hash.Value != digest || EncodeOriginalWork(original) != json) throw new InvalidDataException();

        return original;
    }
}

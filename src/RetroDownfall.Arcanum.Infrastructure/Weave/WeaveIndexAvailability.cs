namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// RAG Phase 1 — process-wide flag recording whether the sqlite-vec <c>vec0</c> acceleration extension
/// loaded successfully into the Grimoire SQLite connection at bootstrap (see
/// <see cref="WeaveSchemaInitializer"/> and <see cref="SqliteVecExtensionLoader"/>).
///
/// Phase 1 ships managed-only by default: no sqlite-vec NuGet package is referenced anywhere in the
/// solution, so <see cref="IsVecAvailable"/> is <c>false</c> out of the box and
/// <c>DivinationService</c> always uses its managed brute-force cosine fallback over the BLOB
/// source-of-truth tables. If a future change adds the sqlite-vec native asset and
/// <see cref="SqliteVecExtensionLoader.TryLoad"/> succeeds against it, this flips to <c>true</c> at the
/// next bootstrap and <c>DivinationService</c> automatically starts using the accelerated vec0 KNN
/// path — no other code change required. Either way, no RAG feature loses functionality: the vec0
/// index is purely a performance layer over the same data.
/// </summary>
public sealed class WeaveIndexAvailability
{

    private volatile bool _isVecAvailable;

    public bool IsVecAvailable => _isVecAvailable;

    public void SetAvailable(bool available)
    {

        _isVecAvailable = available;

    }

}

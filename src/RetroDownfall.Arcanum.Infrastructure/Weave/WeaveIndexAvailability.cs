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

    /// <summary>Stable wire / meta string when embeddings are disabled.</summary>
    public const string ModeDisabled = "disabled";

    /// <summary>Stable wire / meta string for managed SIMD brute-force fallback.</summary>
    public const string ModeManaged = "managed";

    /// <summary>Stable wire / meta string when sqlite-vec vec0 is loaded.</summary>
    public const string ModeVec0 = "vec0";

    /// <summary>Stable wire / meta string when vector status cannot be determined.</summary>
    public const string ModeUnavailable = "unavailable";

    private volatile bool _isVecAvailable;

    private volatile string _diagnostic =
        "sqlite-vec not shipped; using complete streamed managed SIMD fallback.";

    public bool IsVecAvailable => _isVecAvailable;

    /// <summary>
    /// Runtime mode string: <see cref="ModeVec0"/> or <see cref="ModeManaged"/> after bootstrap
    /// (feature-disabled mapping is applied by health/meta callers).
    /// </summary>
    public string Mode => _isVecAvailable ? ModeVec0 : ModeManaged;

    public string Diagnostic => _diagnostic;

    public void SetAvailable(bool available, string? diagnostic = null)
    {

        _isVecAvailable = available;

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {

            _diagnostic = diagnostic.Trim();

            return;

        }

        _diagnostic = available
            ? "sqlite-vec vec0 acceleration index is loaded."
            : "sqlite-vec not shipped; using complete streamed managed SIMD fallback.";

    }

}

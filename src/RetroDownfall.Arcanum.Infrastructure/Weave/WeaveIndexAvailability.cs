namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Process-wide record of which vector search path The Weave is using.
///
/// Arcanum ships managed-only, permanently as far as this type is concerned. The hermetic SQLCipher
/// runtime is compiled with <c>SQLITE_OMIT_LOAD_EXTENSION</c>, so no dynamic accelerator can be
/// loaded into a Grimoire connection at all; the sqlite-vec probe that used to set this flag has
/// been removed rather than left to fail silently at every bootstrap.
///
/// <see cref="IsVecAvailable"/> is therefore <c>false</c> and <c>DivinationService</c> uses its
/// managed brute-force cosine search over the BLOB source-of-truth tables. No RAG feature loses
/// functionality: acceleration was only ever a performance layer over the same data. A future
/// statically linked accelerator would set this through <see cref="SetAvailable"/> after its own
/// security review.
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
        "No dynamic vector accelerator is loadable; using complete streamed managed SIMD search.";

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

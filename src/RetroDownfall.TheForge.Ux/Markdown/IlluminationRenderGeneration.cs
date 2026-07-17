using System.Threading;

namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Monotonic generation counter that lets a superseded Illumination render detect it is stale
/// before publishing to the preview surface. Extracted for unit testing without Avalonia.
/// </summary>
public sealed class IlluminationRenderGeneration
{

    private int _generation;

    /// <summary>Advances the generation and returns the new current value.</summary>
    public int Begin() => Interlocked.Increment(ref _generation);

    /// <summary>True when <paramref name="generation"/> is still the latest begun generation.</summary>
    public bool IsCurrent(int generation) => Volatile.Read(ref _generation) == generation;

}

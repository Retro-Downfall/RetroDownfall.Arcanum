namespace RetroDownfall.Arcanum.Core.TheForge;

/// <summary>
/// Bounded live check that the Grimoire database still answers queries.
/// Orthogonal to <see cref="IGrimoireDbReadiness"/> (startup latch): readiness is never cleared here.
/// </summary>
public interface IGrimoireLivenessProbe
{

    /// <summary>
    /// Probes the Grimoire with a bounded <c>SELECT 1</c> (results may be cached for a few seconds).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the database answered successfully;
    /// <paramref name="detail"/> is operator-facing status text.
    /// </returns>
    Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken cancellationToken = default);

}

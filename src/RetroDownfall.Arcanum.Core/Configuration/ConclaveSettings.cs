namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Configuration for <strong>The Conclave</strong> &#8212; the overarching multi-agent coordination
/// network in which the Master (Wizard) coordinates multiple Apprentices.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> gates the cross-Apprentice delegation surface ("Cast Sending"). When
/// <c>false</c> (the default), the in-process <c>cast_sending</c> MCP tool is not advertised and the
/// <c>POST /api/apprentices/{id}/cast</c> endpoint refuses delegation, so operators opt in to
/// multi-agent fan-out explicitly. Bound from <c>Arcanum:Conclave</c>.
/// </para>
/// <para>
/// This block replaces the former reserved <c>Arcanum:Bureau</c> placeholder. Documented in
/// DESIGN.md &#167;3.4 / &#167;16.
/// </para>
/// </remarks>
public sealed record ConclaveSettings
{

    /// <summary>
    /// When <c>true</c>, enables The Conclave's cross-Apprentice delegation (Cast Sending). Default <c>false</c>.
    /// </summary>
    public bool Enabled { get; init; } = false;

}

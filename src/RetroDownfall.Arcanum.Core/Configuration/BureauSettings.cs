namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Reserved configuration block for the planned <strong>Bureau</strong> integration
/// (cross-host coordination/registry layer; not yet implemented).
/// </summary>
/// <remarks>
/// <para>
/// This type is intentionally retained on <see cref="ArcanumSettings.Bureau"/> as a stable binding
/// surface so operator configs that already include the section remain valid across upgrades, and
/// so future Bureau wiring can land without a configuration migration. No first-party code
/// currently reads <see cref="Enabled"/>; setting it to <c>true</c> is a no-op today.
/// </para>
/// <para>
/// When the Bureau feature ships, the consuming services will gate on this flag. Documented in
/// DESIGN.md &#167;3.4 / &#167;16 (Known limitations and future work).
/// </para>
/// </remarks>
public sealed record BureauSettings
{

    /// <summary>
    /// Reserved for the future Bureau integration. Currently no-op &#8212; setting to <c>true</c>
    /// has no observable behavior. Configuration is preserved so operator JSON stays valid.
    /// </summary>
    public bool Enabled { get; init; } = false;

}

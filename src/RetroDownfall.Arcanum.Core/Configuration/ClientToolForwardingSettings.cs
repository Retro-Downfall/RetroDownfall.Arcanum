namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for client-declared tools. Activation comes from
/// <c>Arcanum:Features:ClientTools</c>; request-count limits are code-owned.
/// </summary>
public sealed record ClientToolForwardingSettings
{

    public bool Enabled { get; set; }

    public int MaxClientTools { get; set; } = 20;

}

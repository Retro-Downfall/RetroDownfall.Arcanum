namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Code-owned Grimoire hydration, retention, and paging invariants; this is not a public
/// configuration root.
/// </summary>
public sealed record GrimoireSettings
{

    /// <summary>
    /// Maximum entries loaded into memory for a single <c>GetSessionAsync</c> hydration.
    /// Used to bound RAM on very long threads; the hub composes the most recent N messages.
    /// Default 1000; clamp 50&#8211;5000.
    /// </summary>
    public int MaxMessagesPerConversationLoad { get; set; } = 1000;

    /// <summary>
    /// Number of Chronosync <c>WorkspaceContext</c> snapshots retained per workspace path.
    /// Older rows are purged after each new baseline is recorded. Default 10; clamp 1&#8211;1000.
    /// </summary>
    public int WorkspaceContextRetentionCount { get; set; } = 10;

    /// <summary>
    /// Default page size for <c>GET /api/lore</c> when <c>limit</c> is omitted. Clamp 1&#8211;10,000.
    /// </summary>
    public int DefaultLoreListLimit { get; set; } = 100;

}

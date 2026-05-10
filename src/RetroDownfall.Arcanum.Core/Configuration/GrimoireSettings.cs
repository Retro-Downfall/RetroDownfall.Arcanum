namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record GrimoireSettings
{

    /// <summary>
    /// Maximum messages loaded into memory for a single <c>GetConversationAsync</c> hydration.
    /// Used to bound RAM on very long threads; the hub composes the most recent N messages.
    /// Default 1000; clamp 50&#8211;100000.
    /// </summary>
    public int MaxMessagesPerConversationLoad { get; init; } = 1000;

}

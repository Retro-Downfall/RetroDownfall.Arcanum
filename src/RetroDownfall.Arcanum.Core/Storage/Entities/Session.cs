namespace RetroDownfall.Arcanum.Core.Storage.Entities;

public sealed class Session
{

    public Guid Id { get; set; }

    public Guid? CampaignId { get; set; }

    public string? Title { get; set; }

    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? Summary { get; set; }

    public DateTime? LastSummarizedMessageAt { get; set; }

    public long TotalTokensUsed { get; set; }

    public ICollection<Entry> Entries { get; set; } = new List<Entry>();

}

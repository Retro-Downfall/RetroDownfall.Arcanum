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

    /// <summary>
    /// Count of entries after <see cref="LastSummarizedMessageAt"/>. <c>-1</c> means unknown (legacy row pending lazy backfill).
    /// </summary>
    public int UnsummarizedEntryCount { get; set; }

    public ICollection<Entry> Entries { get; set; } = new List<Entry>();

    /// <summary>
    /// W4.1: copies the scalar header fields only — <see cref="Entries"/> is intentionally reset to an
    /// empty collection (not cloned). Named <c>CloneHeader</c> so callers do not expect a deep copy.
    /// </summary>
    public Session CloneHeader()
    {

        return new Session
        {
            Id = Id,
            CampaignId = CampaignId,
            Title = Title,
            Status = Status,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Summary = Summary,
            LastSummarizedMessageAt = LastSummarizedMessageAt,
            TotalTokensUsed = TotalTokensUsed,
            UnsummarizedEntryCount = UnsummarizedEntryCount,
            Entries = new List<Entry>(),
        };

    }

}

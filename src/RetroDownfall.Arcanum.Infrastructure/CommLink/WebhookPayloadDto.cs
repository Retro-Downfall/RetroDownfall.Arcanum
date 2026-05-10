namespace RetroDownfall.Arcanum.Infrastructure.CommLink;

public sealed class WebhookPayloadDto
{

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required string Severity { get; init; }

    public required string Source { get; init; }

    public required string TimestampUtc { get; init; }

}

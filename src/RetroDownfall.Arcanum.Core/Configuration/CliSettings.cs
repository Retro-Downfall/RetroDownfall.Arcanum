namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CliSettings
{

    public long MaxAttachFileSizeBytes { get; init; } = 1_048_576L;

}

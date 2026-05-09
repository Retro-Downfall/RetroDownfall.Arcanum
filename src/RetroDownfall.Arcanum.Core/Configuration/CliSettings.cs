namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CliSettings
{

    public long MaxAttachFileSizeBytes { get; init; } = 1_048_576L;

    public int MaxAttachedFilesPerRequest { get; init; } = 32;

    public int MaxAttachedFileRelativePathChars { get; init; } = 4096;

}

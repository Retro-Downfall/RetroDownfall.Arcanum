namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CliSettings
{

    public long MaxAttachFileSizeBytes { get; init; } = 1_048_576L;

    public int MaxAttachedFilesPerRequest { get; init; } = 32;

    public int MaxAttachedFileRelativePathChars { get; init; } = 4096;

    public ArcanumTheme Theme { get; init; } = ArcanumTheme.SystemDefault;

    public ThemeColors ThemeColors { get; init; } = new();

}


namespace RetroDownfall.TheForge.Ux.Markdown;

public enum MarkdownImageKind
{

    RemoteHttp,

    Relative,

    DataUri,

    Disallowed,

}

public sealed record MarkdownImageReference(
    string? AltText,
    string RawUrl,
    MarkdownImageKind Kind);

public enum MarkdownImageResolveStatus
{

    Placeholder,

    Success,

    Failed,

}

public sealed record MarkdownImageResolveResult(
    MarkdownImageResolveStatus Status,
    byte[]? Bytes,
    string? ContentType,
    string PlaceholderReason);

public interface IMarkdownImageResolver
{

    MarkdownImageReference Classify(string? url);

    Task<MarkdownImageResolveResult> ResolveAsync(
        MarkdownImageReference reference,
        IlluminationImageContext context,
        CancellationToken cancellationToken);

}

/// <summary>Per-document image context for The Illumination.</summary>
public sealed class IlluminationImageContext
{

    public bool LoadRemoteImages { get; init; }

    public string? WorkspaceId { get; init; }

    public string? RelativePath { get; init; }

    public string? BaseRelativeDirectory { get; init; }

}

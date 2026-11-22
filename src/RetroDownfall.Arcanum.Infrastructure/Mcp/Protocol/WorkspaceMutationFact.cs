using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<WorkspaceMutationOperation>))]
public enum WorkspaceMutationOperation
{
    [JsonStringEnumMemberName("write")]
    Write = 1,

    [JsonStringEnumMemberName("edit")]
    Edit,

    [JsonStringEnumMemberName("create")]
    Create,

    [JsonStringEnumMemberName("modify")]
    Modify,

    [JsonStringEnumMemberName("rename")]
    Rename,

    [JsonStringEnumMemberName("delete")]
    Delete,
}

/// <summary>A historical native mutation at one disclosed workspace-relative path.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceMutationFact(string RelativePath, WorkspaceMutationOperation Operation)
{
    internal const string MetadataName = "arcanum/committedWorkspaceMutations";

    internal static bool IsPublishablePath(string? path) =>
        WorkspaceRelativePath.TryNormalize(path, out string? normalized)
        && string.Equals(path, normalized, StringComparison.Ordinal)
        && !normalized.Any(static character => char.IsControl(character) || character is '\u2028' or '\u2029')
        && !WorkspaceProtectedPaths.IsProtectedRelativePath(normalized);
}

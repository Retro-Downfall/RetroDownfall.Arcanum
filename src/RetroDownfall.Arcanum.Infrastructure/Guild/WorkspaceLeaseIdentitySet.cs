using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record WorkspaceLeaseDirectoryIdentity(string Path, FileHandleIdentity Identity, bool CaseSensitive, int Depth);

internal sealed record WorkspaceLeaseScopeIdentity(
    string RelativePath,
    GuildPathScopeKind Kind,
    ImmutableArray<string> Segments,
    ImmutableArray<WorkspaceLeaseDirectoryIdentity> Ancestors,
    FileHandleIdentity? ObjectIdentity,
    bool IsDirectory)
{
    internal bool ConflictsWith(WorkspaceLeaseScopeIdentity other)
    {
        if (ObjectIdentity is { } identity && other.ObjectIdentity == identity
            || Covers(other) || other.Covers(this))
        {
            return true;
        }

        foreach (WorkspaceLeaseDirectoryIdentity own in Ancestors)
        {
            foreach (WorkspaceLeaseDirectoryIdentity candidate in other.Ancestors)
            {
                if (own.Identity != candidate.Identity)
                {
                    continue;
                }

                if (own.CaseSensitive != candidate.CaseSensitive)
                {
                    return true;
                }

                ReadOnlySpan<string> ownTail = Segments.AsSpan()[own.Depth..];

                ReadOnlySpan<string> otherTail = other.Segments.AsSpan()[candidate.Depth..];

                int sharedLength = Math.Min(ownTail.Length, otherTail.Length);

                if (ownTail[..sharedLength].SequenceEqual(otherTail[..sharedLength])
                    && (ownTail.Length == otherTail.Length
                        || ownTail.Length < otherTail.Length && Kind == GuildPathScopeKind.Subtree
                        || otherTail.Length < ownTail.Length && other.Kind == GuildPathScopeKind.Subtree))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal bool Covers(WorkspaceLeaseScopeIdentity other)
    {
        if (Kind == GuildPathScopeKind.File && other.Kind != GuildPathScopeKind.File)
        {
            return false;
        }

        if (Kind == GuildPathScopeKind.Subtree && ObjectIdentity is { } identity
            && (other.ObjectIdentity == identity || other.Ancestors.Any(node => node.Identity == identity)))
        {
            return true;
        }

        WorkspaceLeaseDirectoryIdentity anchor = Ancestors[^1];

        foreach (WorkspaceLeaseDirectoryIdentity candidate in other.Ancestors)
        {
            if (anchor.Identity != candidate.Identity || anchor.CaseSensitive != candidate.CaseSensitive)
            {
                continue;
            }

            ReadOnlySpan<string> ownTail = Segments.AsSpan()[anchor.Depth..];

            ReadOnlySpan<string> otherTail = other.Segments.AsSpan()[candidate.Depth..];

            if (ownTail.Length > otherTail.Length
                || Kind == GuildPathScopeKind.File && ownTail.Length != otherTail.Length)
            {
                continue;
            }

            if (ownTail.SequenceEqual(otherTail[..ownTail.Length]))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record WorkspaceLeaseIdentitySet(
    string WorkspaceRoot,
    FileHandleIdentity RootIdentity,
    ImmutableArray<WorkspaceLeaseScopeIdentity> Scopes)
{
    [JsonIgnore]
    public string Digest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    internal bool ConflictsWith(WorkspaceLeaseIdentitySet other) =>
        Scopes.Any(scope => other.Scopes.Any(candidate => scope.ConflictsWith(candidate)));

    internal bool Covers(WorkspaceLeaseIdentitySet other) =>
        other.Scopes.All(candidate => Scopes.Any(scope => scope.Covers(candidate)));

    internal string ToJson() => JsonSerializer.Serialize(this, WorkspaceLeaseIdentityJsonContext.Default.WorkspaceLeaseIdentitySet);

    internal static Result<WorkspaceLeaseIdentitySet> FromJson(string json)
    {
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > 1024 * 1024)
        {
            return WorkspaceLeaseIdentityResolver.Unavailable;
        }

        try
        {
            WorkspaceLeaseIdentitySet? value = JsonSerializer.Deserialize(json, WorkspaceLeaseIdentityJsonContext.Default.WorkspaceLeaseIdentitySet);

            if (value is null || string.IsNullOrEmpty(value.WorkspaceRoot) || !Path.IsPathFullyQualified(value.WorkspaceRoot)
                || value.Scopes.IsDefaultOrEmpty || value.Scopes.Any(scope => !IsValidScope(value, scope)))
            {
                return WorkspaceLeaseIdentityResolver.Unavailable;
            }

            return value;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return WorkspaceLeaseIdentityResolver.Unavailable;
        }
    }

    private static bool IsValidScope(WorkspaceLeaseIdentitySet set, WorkspaceLeaseScopeIdentity? scope)
    {
        if (scope is null || scope.Kind is not (GuildPathScopeKind.File or GuildPathScopeKind.Subtree)
            || scope.IsDirectory != (scope.Kind == GuildPathScopeKind.Subtree)
            || scope.RelativePath is null || Path.IsPathRooted(scope.RelativePath)
            || scope.Segments.IsDefault || scope.Ancestors.IsDefaultOrEmpty
            || scope.Segments.Any(segment => !WorkspaceLeaseIdentityResolver.IsSegment(segment))
            || scope.Ancestors.Any(node => node is null || string.IsNullOrEmpty(node.Path) || !Path.IsPathFullyQualified(node.Path)))
        {
            return false;
        }

        string[] relative = scope.RelativePath is "" or "." ? [] : scope.RelativePath.Replace('\\', '/').Split('/');

        int rootDepth = scope.Segments.Length - relative.Length;

        if (rootDepth < 0 || rootDepth >= scope.Ancestors.Length
            || relative.Any(part => !WorkspaceLeaseIdentityResolver.IsSegment(part)))
        {
            return false;
        }

        if (scope.Ancestors[rootDepth].Identity != set.RootIdentity || scope.Ancestors[0].Depth != 0
            || Path.GetPathRoot(scope.Ancestors[0].Path) != scope.Ancestors[0].Path)
        {
            return false;
        }

        for (int index = 0; index < scope.Ancestors.Length; index++)
        {
            WorkspaceLeaseDirectoryIdentity node = scope.Ancestors[index];

            if (node.Depth != index || node.Depth > scope.Segments.Length
                || index > 0 && !string.Equals(Path.GetDirectoryName(node.Path), scope.Ancestors[index - 1].Path, StringComparison.Ordinal))
            {
                return false;
            }

            if (index > 0)
            {
                string leaf = Path.GetFileName(node.Path);

                if (scope.Segments[index - 1] != (scope.Ancestors[index - 1].CaseSensitive ? leaf : leaf.ToUpperInvariant()))
                {
                    return false;
                }
            }
        }

        for (int index = 0; index < relative.Length; index++)
        {
            bool sensitive = scope.Ancestors[Math.Min(rootDepth + index, scope.Ancestors.Length - 1)].CaseSensitive;

            if (relative[index].Any(character => character > 127)
                || scope.Segments[rootDepth + index] != (sensitive ? relative[index] : relative[index].ToUpperInvariant()))
            {
                return false;
            }
        }

        return true;
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorkspaceLeaseIdentitySet))]
internal partial class WorkspaceLeaseIdentityJsonContext : JsonSerializerContext;

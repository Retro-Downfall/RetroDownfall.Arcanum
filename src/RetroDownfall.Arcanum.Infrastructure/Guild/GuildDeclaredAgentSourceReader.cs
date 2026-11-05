using System.Collections.Immutable;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildDeclaredAgentObservation(string SourceIdentity, GuildAgentSourceKind Kind, Guid? WorkspaceId,
    GuildParsedAgent? Agent, string? Digest, string? ReasonCode);

internal sealed record GuildDeclaredAgentPage(ImmutableArray<GuildDeclaredAgentObservation> Items, string? NextName);

/// <summary>Discovery retains every directory capability and reads only direct no-follow regular leaves.</summary>
internal sealed class GuildDeclaredAgentSourceReader
{
    internal Task<Result<GuildDeclaredAgentPage>> ReadPageAsync(string ownerRoot, string directory,
        GuildAgentSourceKind kind, Guid? workspaceId, string? afterName, int pageSize, CancellationToken cancellationToken) =>
        ReadCoreAsync(ownerRoot, directory, kind, workspaceId, afterName, pageSize, null, cancellationToken);

    internal Task<Result<GuildDeclaredAgentPage>> ReadFileAsync(string ownerRoot, string directory,
        GuildAgentSourceKind kind, Guid? workspaceId, string name, CancellationToken cancellationToken) =>
        ReadCoreAsync(ownerRoot, directory, kind, workspaceId, null, 1, name, cancellationToken);

    private async Task<Result<GuildDeclaredAgentPage>> ReadCoreAsync(string ownerRoot, string directory,
        GuildAgentSourceKind kind, Guid? workspaceId, string? afterName, int pageSize, string? exactName, CancellationToken cancellationToken)
    {
        if (exactName is not null && (Path.GetFileName(exactName) != exactName || !exactName.EndsWith(".md", StringComparison.Ordinal)))
        {
            return Failure(ErrorCodes.Guild.InvalidRequest);
        }

        if (pageSize is < 1 or > 100 || kind is not (GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace)
            || kind == GuildAgentSourceKind.DeclaredWorkspace && workspaceId is null)
        {
            return Failure(ErrorCodes.Guild.InvalidRequest);
        }

        List<(string Path, SafeFileHandle Handle, FileHandleIdentity Identity)> opened = [];

        try
        {
            string originalOwner = Path.GetFullPath(ownerRoot);

            string originalDirectory = Path.GetFullPath(directory);

            if (!WorkspacePathPolicy.IsPathUnderWorkspace(originalOwner, originalDirectory)
                || !WorkspacePathPolicy.TryCanonicalize(originalOwner, out string? canonicalOwner, out bool exists)
                || !exists
                || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(canonicalOwner, requestEnumeration: true,
                    out SafeFileHandle root, out FileHandleMetadata rootMetadata))
            {
                return Failure(ErrorCodes.Guild.WorkspaceIdentityUnavailable);
            }

            opened.Add((canonicalOwner, root, rootMetadata.Identity));

            string relative = Path.GetRelativePath(originalOwner, originalDirectory);

            string currentPath = canonicalOwner;

            if (relative != ".")
            {
                foreach (string component in relative.Split(Path.DirectorySeparatorChar))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    currentPath = Path.Combine(currentPath, component);

                    if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(opened[^1].Handle, component,
                            requestReadControl: false, requestEnumeration: true, out SafeFileHandle next, out FileHandleMetadata metadata))
                    {
                        next.Dispose();

                        var topology = NoFollowPathTopology.Classify(currentPath);

                        return topology.IsSuccess && topology.Value == NoFollowPathTopologyKind.Absent && Validate(opened)
                            ? Result<GuildDeclaredAgentPage>.Success(new([], null))
                            : Failure(ErrorCodes.Guild.WorkspaceIdentityUnavailable);
                    }

                    opened.Add((currentPath, next, metadata.Identity));
                }
            }

            string[] names;

            bool hasMore = false;

            if (!Validate(opened))
            {
                return Failure(ErrorCodes.Guild.WorkspaceIdentityUnavailable);
            }

            if (exactName is not null)
            {
                names = [exactName];
            }
            else if (!SecureDirectoryNameEnumerator.TryEnumeratePage(opened[^1].Handle, afterName, pageSize, ".md",
                         cancellationToken, out names, out hasMore))
            {
                return Failure(ErrorCodes.Guild.WorkspaceIdentityUnavailable);
            }

            ImmutableArray<GuildDeclaredAgentObservation>.Builder items = ImmutableArray.CreateBuilder<GuildDeclaredAgentObservation>();

            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path = Path.Combine(currentPath, name);

                string source = SourceIdentity(kind, workspaceId, path);

                SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                    opened[^1].Handle, name, out SafeFileHandle? leaf);

                if (status != SecureFileOpenStatus.Success || leaf is null)
                {
                    leaf?.Dispose();

                    items.Add(new(source, kind, workspaceId, null, null, ErrorCodes.Guild.Unavailable));

                    continue;
                }

                using (leaf)
                {
                    if (!SecureFileReader.TryValidateRegularFileHandle(leaf, null, out FileHandleMetadata captured))
                    {
                        items.Add(new(source, kind, workspaceId, null, null, ErrorCodes.Guild.Unavailable));

                        continue;
                    }

                    await using FileStream stream = new(leaf, FileAccess.Read, 4096, isAsync: OperatingSystem.IsWindows());

                    SecureUtf8FileReadResult read = await SecureFileReader.ReadUtf8TextAsync(stream,
                        checked((int)ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes()), cancellationToken,
                        captureBaseline: true).ConfigureAwait(false);

                    if (read.Status != SecureFileReadStatus.Success || !Validate(opened)
                        || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata current)
                        || current.Identity != captured.Identity
                        || !FileHandleIdentityInterop.TryGetHandleKernelPath(leaf, out string? actual)
                        || !WorkspacePathPolicy.IsPathUnderWorkspace(canonicalOwner, actual))
                    {
                        string reason = read.Status == SecureFileReadStatus.TooLarge
                            ? ErrorCodes.Guild.PayloadTooLarge
                            : read.Status == SecureFileReadStatus.InvalidUtf8
                                ? ErrorCodes.Guild.InvalidRequest
                                : ErrorCodes.Guild.Unavailable;

                        items.Add(new(source, kind, workspaceId, null, null, reason));

                        continue;
                    }

                    Result<GuildParsedAgent> parsed = GuildDeclaredAgentParser.Parse(read.Text!);

                    items.Add(parsed.IsSuccess
                        ? new(source, kind, workspaceId, parsed.Value, read.Baseline!.Value.Sha256Hex, null)
                        : new(source, kind, workspaceId, null, null, parsed.Error.Code));
                }
            }

            return Validate(opened)
                ? Result<GuildDeclaredAgentPage>.Success(new(items.ToImmutable(), hasMore ? names[^1] : null))
                : Failure(ErrorCodes.Guild.WorkspaceIdentityUnavailable);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failure(ErrorCodes.Guild.WorkspaceIdentityUnavailable);
        }
        finally
        {
            for (int index = opened.Count - 1; index >= 0; index--)
            {
                opened[index].Handle.Dispose();
            }
        }
    }

    internal static string SourceIdentity(GuildAgentSourceKind kind, Guid? workspaceId, string canonicalPath) =>
        $"{kind}:{workspaceId?.ToString("D") ?? "installation"}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(canonicalPath))}";

    internal static bool TrySourcePath(string sourceIdentity, out string path)
    {
        path = string.Empty;

        int first = sourceIdentity.IndexOf(':');

        int second = first < 0 ? -1 : sourceIdentity.IndexOf(':', first + 1);

        if (second < 0)
        {
            return false;
        }

        try
        {
            path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(sourceIdentity[(second + 1)..]));

            return Path.IsPathFullyQualified(path);
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool Validate(List<(string Path, SafeFileHandle Handle, FileHandleIdentity Identity)> opened) =>
        opened.All(item => !item.Handle.IsClosed
            && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(item.Path, out FileHandleMetadata current)
            && current.Kind == FileSystemObjectKind.Directory && current.Identity == item.Identity);

    private static Result<GuildDeclaredAgentPage> Failure(string code) =>
        Result<GuildDeclaredAgentPage>.Failure(new Error(code, "The declared agent source is unavailable or invalid."));
}

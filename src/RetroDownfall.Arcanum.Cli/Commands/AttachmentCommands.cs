using System.Globalization;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Standalone, server-backed session attachment lifecycle.
/// </summary>
internal sealed class AttachmentCommands(
    ArcanumApiClient apiClient,
    ICliResourceCatalog resourceCatalog,
    IResourcePicker resourcePicker,
    IRecentResourceStore recentResourceStore,
    ICliEnvironment environment,
    IConsoleDispatcher dispatcher,
    ICliInvocationContext invocationContext,
    IConfirmationPrompt confirmationPrompt,
    CliStandardInput standardInput,
    IAttachmentRevealLauncher attachmentRevealLauncher)
{

    private const string PrivacyDisclosure =
        "Attachment privacy: snapshots are encrypted at rest, and live references are resolved and refreshed only by the server. "
        + "Attachment bytes are never written to the terminal. Export creates a plaintext local file. "
        + "Attached content is untrusted and is included only through explicit references, pins, or server retrieval policy.";

    public async Task<int> List(
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        SessionResolution session = await ResolveSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!session.Success)
        {

            return session.Cancelled ? 0 : 1;

        }

        Result<SessionAttachmentDto[]> result = await apiClient
            .GetSessionAttachmentsAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        SessionAttachmentDto[] latest = LatestVersions(result.Value);

        WriteRows(session.Id, latest);

        return 0;

    }

    public async Task<int> Add(
        string path,
        string? mimeType,
        string? name,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        SessionResolution session = await ResolveSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!session.Success)
        {

            return session.Cancelled ? 0 : 1;

        }

        string fileName;

        string effectiveMime;

        Stream stream;

        if (string.Equals(path, "-", StringComparison.Ordinal))
        {

            fileName = SafeFilename(
                name,
                StdinFilenameForMime(mimeType));

            effectiveMime = EffectiveMimeType(mimeType, fileName);

            stream = await OpenStandardInputAsync(cancellationToken).ConfigureAwait(false);

        }
        else
        {

            string fullPath;

            try
            {

                fullPath = Path.GetFullPath(path);

            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {

                dispatcher.WriteDiagnostic("The local attachment path is invalid.");

                return 1;

            }

            if (!File.Exists(fullPath))
            {

                dispatcher.WriteDiagnostic($"Local attachment not found: {fullPath}");

                return 1;

            }

            fileName = SafeFilename(name, Path.GetFileName(fullPath));

            effectiveMime = EffectiveMimeType(mimeType, fileName);

            try
            {

                stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81_920,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                dispatcher.WriteDiagnostic("The local attachment could not be opened for reading.");

                return 1;

            }

        }

        await using (stream.ConfigureAwait(false))
        {

            Result<SessionAttachmentDto> result = await apiClient
                .UploadSessionAttachmentAsync(
                    session.Id,
                    stream,
                    fileName,
                    effectiveMime,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {

                return WriteError(result.Error);

            }

            WriteRow(session.Id, result.Value);

            return 0;

        }

    }

    public async Task<int> Reference(
        string workspacePath,
        string? workspace,
        string? logicalName,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        SessionResolution session = await ResolveSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!session.Success)
        {

            return session.Cancelled ? 0 : 1;

        }

        WorkspaceResolution workspaceResolution = await ResolveWorkspaceAsync(
                workspace,
                cancellationToken)
            .ConfigureAwait(false);

        if (!workspaceResolution.Success)
        {

            return workspaceResolution.Cancelled ? 0 : 1;

        }

        CreateSessionAttachmentReferenceRequest request = new(
            workspacePath,
            workspaceResolution.Id,
            logicalName);

        Result<SessionAttachmentDto> result = await apiClient
            .CreateSessionAttachmentReferenceAsync(
                session.Id,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WriteRow(session.Id, result.Value);

        return 0;

    }

    public async Task<int> Show(
        string? attachmentIdentifier,
        bool privacy,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        if (privacy)
        {

            dispatcher.WritePayload(PrivacyDisclosure);

            return 0;

        }

        AttachmentResolution resolution = await ResolveAttachmentAsync(
                sessionIdentifier,
                attachmentIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        WriteRow(resolution.SessionId, resolution.Value!);

        return 0;

    }

    public async Task<int> Versions(
        string? attachmentIdentifier,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        AttachmentResolution resolution = await ResolveAttachmentAsync(
                sessionIdentifier,
                attachmentIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        SessionAttachmentDto[] versions = resolution.AllRows
            .Where(row => string.Equals(
                row.LogicalKey,
                resolution.Value!.LogicalKey,
                StringComparison.Ordinal))
            .OrderByDescending(static row => row.Version)
            .ThenByDescending(static row => row.CreatedAt)
            .ToArray();

        WriteRows(resolution.SessionId, versions);

        return 0;

    }

    public async Task<int> Refresh(
        string? attachmentIdentifier,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        AttachmentResolution resolution = await ResolveAttachmentAsync(
                sessionIdentifier,
                attachmentIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Result<AttachmentRefreshEvent> result = await apiClient
            .RefreshSessionAttachmentAsync(
                resolution.SessionId,
                resolution.Value!.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                result.Value,
                ArcanumJsonContext.Default.AttachmentRefreshEvent);

        }
        else
        {

            dispatcher.WritePayload(
                $"Refreshed {result.Value.LogicalKey} to v{result.Value.Version.ToString(CultureInfo.InvariantCulture)} "
                + $"({result.Value.ByteLength.ToString(CultureInfo.InvariantCulture)} bytes).");

        }

        return 0;

    }

    public async Task<int> Pin(
        string? attachmentIdentifier,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        AttachmentResolution resolution = await ResolveAttachmentAsync(
                sessionIdentifier,
                attachmentIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        SessionAttachmentDto attachment = resolution.Value!;

        CreateSessionContextPinRequest request = new(
            SessionContextPinKind.Attachment,
            attachment.Id.ToString("D"),
            attachment.LogicalKey,
            attachment.ContentSha256);

        Result<SessionContextPinDto> result = await apiClient
            .CreateSessionContextPinAsync(
                resolution.SessionId,
                request,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WritePin(result.Value, "Pinned");

        return 0;

    }

    public async Task<int> Unpin(
        string? attachmentIdentifier,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        AttachmentResolution resolution = await ResolveAttachmentAsync(
                sessionIdentifier,
                attachmentIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        string target = resolution.Value!.Id.ToString("D");

        Result<SessionContextPinDto[]> pins = await apiClient
            .GetSessionContextPinsAsync(
                resolution.SessionId,
                cancellationToken)
            .ConfigureAwait(false);

        if (pins.IsFailure)
        {

            return WriteError(pins.Error);

        }

        SessionContextPinDto? pin = pins.Value.FirstOrDefault(row =>
            row.Kind == SessionContextPinKind.Attachment
            && string.Equals(
                row.TargetIdentifier,
                target,
                StringComparison.OrdinalIgnoreCase));

        if (pin is null)
        {

            dispatcher.WritePayload($"Attachment {target} is not pinned.");

            return 0;

        }

        Result result = await apiClient
            .DeleteSessionContextPinAsync(
                resolution.SessionId,
                pin.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        WritePin(pin, "Unpinned");

        return 0;

    }

    public async Task<int> Export(
        string? attachmentIdentifier,
        string? output,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        AttachmentResolution resolution = await ResolveAttachmentAsync(
                sessionIdentifier,
                attachmentIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        if (string.Equals(output?.Trim(), "-", StringComparison.Ordinal))
        {

            dispatcher.WriteDiagnostic(
                "Attachment export never writes bytes to stdout; provide a file path with --output.");

            return 1;

        }

        SessionAttachmentDto attachment = resolution.Value!;

        string destination;

        try
        {

            destination = Path.GetFullPath(
                string.IsNullOrWhiteSpace(output)
                    ? SafeFilename(attachment.OriginalFileName, attachment.Id.ToString("D") + ".bin")
                    : output);

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            dispatcher.WriteDiagnostic("The attachment export destination is invalid.");

            return 1;

        }

        bool overwrite = File.Exists(destination);

        if (overwrite
            && !await confirmationPrompt
                .PromptForConfirmationAsync(
                    $"Overwrite existing file {destination}?",
                    cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic(
                "Attachment export cancelled; the existing file was not changed.");

            return 0;

        }

        Result<long> download = await apiClient
            .DownloadSessionAttachmentAsync(
                resolution.SessionId,
                attachment.Id,
                destination,
                overwrite,
                cancellationToken)
            .ConfigureAwait(false);

        if (download.IsFailure)
        {

            return WriteError(download.Error);

        }

        FileDownloadPayload payload = new(
            attachment.Id.ToString("D"),
            destination,
            download.Value);

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                payload,
                CliJsonContext.Default.FileDownloadPayload);

        }
        else
        {

            dispatcher.WritePayload(
                $"Exported {attachment.LogicalKey} v{attachment.Version.ToString(CultureInfo.InvariantCulture)} "
                + $"to {destination} ({download.Value.ToString(CultureInfo.InvariantCulture)} bytes).");

        }

        return 0;

    }

    public async Task<int> Reveal(
        string? attachmentIdentifier,
        string? sessionIdentifier,
        CancellationToken cancellationToken)
    {

        AttachmentResolution resolution = await ResolveAttachmentAsync(
                sessionIdentifier,
                attachmentIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        if (!TryResolveStoredPath(
                resolution.Value!.RelativePath,
                out string? absolutePath))
        {

            dispatcher.WriteDiagnostic(
                "Attachment reveal refused an unsafe stored relative path returned by the API.");

            return 1;

        }

        if (!HasEncryptedBlobEnvelope(absolutePath!))
        {

            dispatcher.WriteDiagnostic(
                "The server attachment artifact is not locally available as an ARCABLOB envelope. "
                + "Use 'arcanum attachment export' to download it instead.");

            return 1;

        }

        string message = attachmentRevealLauncher.TryReveal(
            absolutePath!,
            out bool started);

        if (!started)
        {

            dispatcher.WriteDiagnostic(message);

            return 1;

        }

        dispatcher.WritePayload(message);

        return 0;

    }

    private async Task<SessionResolution> ResolveSessionAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (Guid.TryParse(identifier, out Guid id))
        {

            return SessionResolution.Resolved(id);

        }

        ResourceSelectionResult<SessionSummaryDto> selection = await resourceCatalog
            .SelectSessionAsync(identifier, cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Selected
            && selection.Value is not null)
        {

            return SessionResolution.Resolved(selection.Value.Id);

        }

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return SessionResolution.Cancel();

        }

        dispatcher.WriteDiagnostic(
            selection.Error ?? "A session could not be selected.");

        return SessionResolution.Failure();

    }

    private async Task<WorkspaceResolution> ResolveWorkspaceAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(identifier))
        {

            return WorkspaceResolution.Resolved(null);

        }

        ResourceSelectionResult<WorkspaceInfo> selection = await resourceCatalog
            .SelectWorkspaceAsync(identifier, cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Selected
            && selection.Value is not null)
        {

            return WorkspaceResolution.Resolved(selection.Value.Id);

        }

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return WorkspaceResolution.Cancel();

        }

        dispatcher.WriteDiagnostic(
            selection.Error ?? "A workspace could not be selected.");

        return WorkspaceResolution.Failure();

    }

    private async Task<AttachmentResolution> ResolveAttachmentAsync(
        string? sessionIdentifier,
        string? attachmentIdentifier,
        CancellationToken cancellationToken)
    {

        SessionResolution session = await ResolveSessionAsync(
                sessionIdentifier,
                cancellationToken)
            .ConfigureAwait(false);

        if (!session.Success)
        {

            return session.Cancelled
                ? AttachmentResolution.Cancel()
                : AttachmentResolution.Failure();

        }

        Result<SessionAttachmentDto[]> result = await apiClient
            .GetSessionAttachmentsAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            _ = WriteError(result.Error);

            return AttachmentResolution.Failure();

        }

        SessionAttachmentDto[] all = result.Value;

        if (Guid.TryParse(attachmentIdentifier, out Guid attachmentId))
        {

            SessionAttachmentDto? exact = all.FirstOrDefault(row => row.Id == attachmentId);

            if (exact is null)
            {

                dispatcher.WriteDiagnostic(
                    $"No attachment with id {attachmentId:D} belongs to session {session.Id:D}.");

                return AttachmentResolution.Failure();

            }

            return AttachmentResolution.Resolved(session.Id, exact, all);

        }

        SessionAttachmentDto[] latest = LatestVersions(all);

        ResourceDescriptor<SessionAttachmentDto> descriptor = new(
            "attachment",
            ["Logical key", "Version", "Mode", "MIME", "Size", "Index"],
            static row => row.Id.ToString("D"),
            static row => row.LogicalKey,
            static row => AttachmentSummary(row),
            static row =>
            [
                row.LogicalKey,
                "v" + row.Version.ToString(CultureInfo.InvariantCulture),
                AttachmentMode(row),
                row.MimeType,
                row.ByteLength.ToString(CultureInfo.InvariantCulture),
                row.IndexingStatus.ToString(),
            ]);

        ResourceSelector<SessionAttachmentDto> selector = new(
            resourcePicker,
            recentResourceStore);

        ResourceSelectionResult<SessionAttachmentDto> selection = await selector
            .SelectAsync(
                new ResourceSelectionRequest<SessionAttachmentDto>(
                    "attachment",
                    attachmentIdentifier,
                    environment.IsInteractive,
                    descriptor,
                    (_, _) => Task.FromResult(
                        Result<ResourcePage<SessionAttachmentDto>>.Success(
                            new ResourcePage<SessionAttachmentDto>(latest, null)))),
                cancellationToken)
            .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Selected
            && selection.Value is not null)
        {

            return AttachmentResolution.Resolved(
                session.Id,
                selection.Value,
                all);

        }

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return AttachmentResolution.Cancel();

        }

        dispatcher.WriteDiagnostic(
            selection.Error ?? "An attachment could not be selected.");

        return AttachmentResolution.Failure();

    }

    private void WriteRows(
        Guid sessionId,
        SessionAttachmentDto[] rows)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                rows,
                ArcanumJsonContext.Default.SessionAttachmentDtoArray);

            return;

        }

        if (rows.Length == 0)
        {

            dispatcher.WritePayload($"Session {sessionId:D} has no attachments.");

            return;

        }

        foreach (SessionAttachmentDto row in rows)
        {

            dispatcher.WritePayload(FormatAttachment(sessionId, row));

        }

    }

    private void WriteRow(
        Guid sessionId,
        SessionAttachmentDto row)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                row,
                ArcanumJsonContext.Default.SessionAttachmentDto);

            return;

        }

        dispatcher.WritePayload(FormatAttachment(sessionId, row));

    }

    private void WritePin(
        SessionContextPinDto pin,
        string verb)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                pin,
                ArcanumJsonContext.Default.SessionContextPinDto);

            return;

        }

        dispatcher.WritePayload(
            $"{verb} attachment {pin.TargetIdentifier} in session {pin.SessionId:D}.");

    }

    private int WriteError(Error error)
    {

        dispatcher.WriteDiagnostic($"{error.Code}: {error.Message}");

        return 1;

    }

    private static SessionAttachmentDto[] LatestVersions(
        IEnumerable<SessionAttachmentDto> rows) =>
        rows
            .GroupBy(static row => row.LogicalKey, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static row => row.Version)
                .ThenByDescending(static row => row.CreatedAt)
                .First())
            .OrderBy(static row => row.LogicalKey, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static row => row.Version)
            .ToArray();

    private static string FormatAttachment(
        Guid sessionId,
        SessionAttachmentDto row)
    {

        string result =
            $"Session={row.SessionId ?? sessionId:D}  "
            + $"Id={row.Id:D}  "
            + $"Logical={row.LogicalKey}  "
            + $"File={SanitizeDisplayText(row.OriginalFileName)}  "
            + $"Version={row.Version.ToString(CultureInfo.InvariantCulture)}  "
            + $"Mode={AttachmentMode(row)}  "
            + $"MIME={row.MimeType}  "
            + $"Size={row.ByteLength.ToString(CultureInfo.InvariantCulture)} bytes  "
            + $"Refreshable={row.IsRefreshable}  "
            + $"Source={row.SourceStatus}  "
            + $"Index={row.IndexingStatus}";

        string? reason = SanitizeDiagnosticReason(
            row.SourceDiagnosticReason);

        if (reason is not null)
        {

            result += "  Reason=" + reason;

        }

        return result;

    }

    private static string AttachmentSummary(SessionAttachmentDto row) =>
        $"v{row.Version.ToString(CultureInfo.InvariantCulture)}, "
        + $"{AttachmentMode(row)}, {row.MimeType}, "
        + $"{row.ByteLength.ToString(CultureInfo.InvariantCulture)} bytes, "
        + $"source {row.SourceStatus}, index {row.IndexingStatus}";

    private static string AttachmentMode(SessionAttachmentDto row) =>
        row.SourceKind == AttachmentSourceKind.SnapshotOnly
            ? "Snapshot"
            : row.IsRefreshable
                && row.SourceStatus == AttachmentSourceStatus.Refreshable
                ? "Live"
                : "Stale";

    private static string? SanitizeDiagnosticReason(string? reason)
    {

        if (string.IsNullOrWhiteSpace(reason))
        {

            return null;

        }

        string normalized = SanitizeDisplayText(reason);

        string[] tokens = normalized.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string sanitized = string.Join(
            ' ',
            tokens.Select(static token =>
                token.Contains('/')
                || token.Contains('\\')
                    ? "[path]"
                    : token));

        return sanitized.Length <= 240
            ? sanitized
            : sanitized[..237] + "...";

    }

    private static string SanitizeDisplayText(string value)
    {

        string sanitized = new(value
            .Select(static character => char.IsControl(character)
                ? ' '
                : character)
            .ToArray());

        return sanitized.Length <= 512
            ? sanitized
            : sanitized[..509] + "...";

    }

    private static string EffectiveMimeType(
        string? requested,
        string fileName)
    {

        if (!string.IsNullOrWhiteSpace(requested))
        {

            return requested.Trim();

        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".log" => "text/plain",
            ".json" => "application/json",
            ".jsonl" or ".ndjson" => "application/x-ndjson",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    }

    private static string StdinFilenameForMime(string? mimeType)
    {

        if (string.IsNullOrWhiteSpace(mimeType))
        {

            return "stdin.txt";

        }

        string mediaType = mimeType
            .Split(';', 2, StringSplitOptions.TrimEntries)[0]
            .ToLowerInvariant();

        if (mediaType.StartsWith("text/", StringComparison.Ordinal))
        {

            return "stdin.txt";

        }

        return mediaType switch
        {
            "application/json" => "stdin.json",
            "application/ld+json" => "stdin.json",
            "application/problem+json" => "stdin.json",
            "application/x-ndjson" => "stdin.json",
            "application/jsonl" => "stdin.json",
            "image/png" => "stdin.png",
            "image/jpeg" or "image/jpg" => "stdin.jpg",
            "image/gif" => "stdin.gif",
            "image/webp" => "stdin.webp",
            _ => "stdin.bin",
        };

    }

    private static string SafeFilename(
        string? requested,
        string fallback)
    {

        string candidate = string.IsNullOrWhiteSpace(requested)
            ? fallback
            : requested;

        string leaf = Path.GetFileName(candidate.Replace('\\', '/'));

        if (string.IsNullOrWhiteSpace(leaf)
            || leaf is "." or "..")
        {

            leaf = fallback;

        }

        char[] invalid = Path.GetInvalidFileNameChars();

        string sanitized = new(leaf
            .Select(character =>
                character < ' '
                || character is '/' or '\\'
                || invalid.Contains(character)
                    ? '_'
                    : character)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized)
            || sanitized is "." or ".."
            ? "attachment.bin"
            : sanitized;

    }

    private async Task<Stream> OpenStandardInputAsync(
        CancellationToken cancellationToken)
    {

        if (!ReferenceEquals(Console.In, standardInput.ConfiguredReader))
        {

            string text = await Console.In
                .ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);

            return new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(text),
                writable: false);

        }

        return Console.OpenStandardInput();

    }

    private static bool TryResolveStoredPath(
        string relativePath,
        out string? absolutePath)
    {

        absolutePath = null;

        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {

            return false;

        }

        try
        {

            string root = Path.GetFullPath(ArcanumPaths.AttachmentsDirectory);

            string candidate = Path.GetFullPath(
                Path.Combine(root, relativePath));

            string relative = Path.GetRelativePath(root, candidate);

            if (relative == ".."
                || relative.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {

                return false;

            }

            absolutePath = candidate;

            return true;

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return false;

        }

    }

    private static bool HasEncryptedBlobEnvelope(string absolutePath)
    {

        try
        {

            Span<byte> magic = stackalloc byte[8];

            using FileStream stream = new(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            return stream.Read(magic) == magic.Length
                && magic.SequenceEqual("ARCABLOB"u8);

        }
        catch (IOException)
        {

            return false;

        }
        catch (UnauthorizedAccessException)
        {

            return false;

        }

    }

    private readonly record struct SessionResolution(
        bool Success,
        bool Cancelled,
        Guid Id)
    {

        public static SessionResolution Resolved(Guid id) =>
            new(true, false, id);

        public static SessionResolution Cancel() =>
            new(false, true, default);

        public static SessionResolution Failure() =>
            new(false, false, default);

    }

    private readonly record struct WorkspaceResolution(
        bool Success,
        bool Cancelled,
        string? Id)
    {

        public static WorkspaceResolution Resolved(string? id) =>
            new(true, false, id);

        public static WorkspaceResolution Cancel() =>
            new(false, true, null);

        public static WorkspaceResolution Failure() =>
            new(false, false, null);

    }

    private sealed record AttachmentResolution(
        bool Success,
        bool Cancelled,
        Guid SessionId,
        SessionAttachmentDto? Value,
        SessionAttachmentDto[] AllRows)
    {

        public static AttachmentResolution Resolved(
            Guid sessionId,
            SessionAttachmentDto value,
            SessionAttachmentDto[] allRows) =>
            new(true, false, sessionId, value, allRows);

        public static AttachmentResolution Cancel() =>
            new(false, true, default, null, []);

        public static AttachmentResolution Failure() =>
            new(false, false, default, null, []);

    }

}

internal sealed record CliStandardInput(TextReader ConfiguredReader);

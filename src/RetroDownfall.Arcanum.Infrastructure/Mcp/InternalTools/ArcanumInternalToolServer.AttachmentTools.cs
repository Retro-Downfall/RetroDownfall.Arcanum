using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// <c>attach_session_file</c>: re-attach a bound session attachment into the next model turn.
/// Session id comes from <see cref="SessionAttachmentToolAmbient"/> only — never from tool args.
/// Multimodal bytes are injected by the Master post-tool path, not returned in the tool result text.
/// </summary>
internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteRefreshSessionFileAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        RefreshSessionFileParams? args;
        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.RefreshSessionFileParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "refresh_session_file argument deserialization failed.");
            return ToolError("Invalid arguments for refresh_session_file.");
        }

        bool byId = args?.AttachmentId is { } id && id != Guid.Empty;
        bool byLogical = !string.IsNullOrWhiteSpace(args?.LogicalKey);
        if (byId == byLogical)
        {
            return ToolError("refresh_session_file requires exactly one of 'attachmentId' or 'logicalKey'.");
        }

        if (SessionAttachmentToolAmbient.CurrentSessionId is not { } sessionId)
        {
            return ToolError("No current session; cannot refresh a session file.");
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();
            SessionAttachmentRecord? record;

            if (byId)
            {
                record = await store.GetByIdAsync(args!.AttachmentId!.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                IReadOnlyList<SessionAttachmentRecord> bound = await store
                    .ListLatestBoundAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                List<IGrouping<string, SessionAttachmentRecord>> matches = bound
                    .GroupBy(static row => row.LogicalKey, StringComparer.Ordinal)
                    .Where(group => string.Equals(
                        group.Key,
                        args!.LogicalKey!.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count > 1)
                {
                    return ToolError("The logicalKey selector is ambiguous in the current session.");
                }

                record = matches.Count == 1
                    ? matches[0].OrderByDescending(static row => row.Version).First()
                    : null;
            }

            if (record is null
                || record.State != SessionAttachmentState.Bound
                || record.SessionId != sessionId)
            {
                return ToolError("The selected attachment is not bound to the current session.");
            }

            if (record.Source is not { Kind: AttachmentSourceKind.WorkspaceFile })
            {
                return ToolError("The selected attachment is snapshot-only and cannot be refreshed.");
            }

            return CapToolTextResult(
                $"Refresh accepted for '{record.LogicalKey}'. Secure source processing will complete before the next model request.",
                "refresh_session_file");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "refresh_session_file selector validation failed.");
            return ToolError("An internal error occurred during tool execution.");
        }
    }

    private async Task<McpToolsCallResultWire> ExecuteAttachSessionFileAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        AttachSessionFileParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.AttachSessionFileParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "attach_session_file argument deserialization failed.");

            return ToolError("Invalid arguments for attach_session_file.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.LogicalName))
        {
            return ToolError("attach_session_file requires a non-empty 'logicalName'.");
        }

        Guid? sessionId = SessionAttachmentToolAmbient.CurrentSessionId;

        if (sessionId is null)
        {
            return ToolError("No current session; cannot attach a session file.");
        }

        string logicalName = args.LogicalName.Trim();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            SessionAttachmentRecord? record = await store
                .GetByLogicalAsync(sessionId.Value, logicalName, args.Version, cancellationToken)
                .ConfigureAwait(false);

            if (record is null)
            {
                IReadOnlyList<SessionAttachmentRecord> bound = await store
                    .ListLatestBoundAsync(sessionId.Value, cancellationToken)
                    .ConfigureAwait(false);

                return ToolError(BuildMissingAttachmentMessage(logicalName, args.Version, bound));
            }

            if (record.Kind is not SessionAttachmentKind.Text

                and not SessionAttachmentKind.Image)

            {

                return ToolError(

                    "The selected attachment can be downloaded or exported, but its content cannot be injected into a model turn.");

            }

            string text =
                $"Attached '{record.OriginalFileName}' v{record.Version.ToString(CultureInfo.InvariantCulture)} "
                + $"({record.Kind}, {record.ByteLength.ToString(CultureInfo.InvariantCulture)} bytes). "
                + "Content will be injected into the next model turn.";

            return CapToolTextResult(text, "attach_session_file");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "attach_session_file failed for logicalName {LogicalName}.", logicalName);

            return ToolError("An internal error occurred during tool execution.");
        }

    }

    private static string BuildMissingAttachmentMessage(
        string logicalName,
        int? version,
        IReadOnlyList<SessionAttachmentRecord> bound)
    {

        StringBuilder sb = new();

        sb.Append("No session attachment named '");

        sb.Append(logicalName);

        sb.Append('\'');

        if (version is not null)
        {
            sb.Append(" at version ");

            sb.Append(version.Value.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append('.');

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (SessionAttachmentRecord row in bound)
        {
            _ = names.Add(row.LogicalKey);
        }

        if (names.Count == 0)
        {
            sb.Append(" This session has no bound attachments.");
        }
        else
        {
            sb.Append(" Available logical names: ");

            sb.Append(string.Join(", ", names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)));

            sb.Append('.');
        }

        return sb.ToString();

    }

}

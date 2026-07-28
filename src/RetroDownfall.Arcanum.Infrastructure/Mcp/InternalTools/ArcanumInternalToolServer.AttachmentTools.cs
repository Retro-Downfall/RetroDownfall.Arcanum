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
/// Multimodal bytes are injected by the Wizard post-tool path, not returned in the tool result text.
/// </summary>
internal sealed partial class ArcanumInternalToolServer
{

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
                    .ListBoundAsync(sessionId.Value, cancellationToken)
                    .ConfigureAwait(false);

                return ToolError(BuildMissingAttachmentMessage(logicalName, args.Version, bound));
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

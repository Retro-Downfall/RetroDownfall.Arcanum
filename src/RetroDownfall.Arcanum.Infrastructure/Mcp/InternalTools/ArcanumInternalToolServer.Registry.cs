using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{
    private delegate Task<McpToolsCallResultWire> InternalToolHandler(
        JsonElement arguments,
        CancellationToken cancellationToken);

    private readonly Dictionary<string, InternalToolHandler> _toolHandlers;

    /// <summary>
    /// Registered tool names for test invariant checks (tools/list ↔ handler registry).
    /// </summary>
    internal IReadOnlyCollection<string> RegisteredToolHandlerNamesForTests => _toolHandlers.Keys;

    /// <summary>
    /// Every tool name this server registers a handler for, independent of which of them a given
    /// session's feature gates, workspace root or turn shape actually advertise. <see cref="McpToolMerger"/>
    /// reserves these names against workspace-local servers: a built-in that is gated off for one session
    /// (<c>ask_human</c> on a non-streaming turn, <c>scribe_lexicon</c> with the Lexicon feature off)
    /// is absent from that session's advertised rows, and a name reserved only while its row exists would
    /// be claimable by an approved <c>mcp.json</c> exactly when the built-in is unavailable. Keep this set
    /// in step with <see cref="BuildToolHandlerRegistry"/>; a test compares the two.
    /// </summary>
    internal static IReadOnlySet<string> RegisteredToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "read_file_chunk",

            "replace_text_block",

            "write_file",

            "list_directory",

            ToolRiskClassifier.SearchWorkspaceToolName,

            ToolRiskClassifier.ApplyPatchToolName,

            ToolRiskClassifier.WorkspaceCheckToolName,

            ToolRiskClassifier.ExecuteCommandToolName,

            "read_command_output",

            "adjust_initiative",

            "send_commlink_alert",

            "petition_dungeon_master",

            "cast_sending",

            "dispatch_sending",

            "continue_sending",

            "ask_human",

            "scribe_lexicon",

            "delete_lexicon",

            "search_archives",

            CovenantToolNames.ProposeCovenant,

            CovenantToolNames.RetireCovenant,

            "read_saga",

            "attach_session_file",

            "refresh_session_file",
        };

    private Dictionary<string, InternalToolHandler> BuildToolHandlerRegistry()
    {
        Dictionary<string, InternalToolHandler> handlers = new(StringComparer.Ordinal)
        {
            ["read_file_chunk"] = ExecuteReadFileChunkAsync,

            ["replace_text_block"] = ExecuteReplaceTextBlockAsync,

            ["write_file"] = ExecuteWriteFileAsync,

            ["list_directory"] = ExecuteListDirectoryAsync,

            [ToolRiskClassifier.SearchWorkspaceToolName] = ExecuteSearchWorkspaceAsync,

            [ToolRiskClassifier.ApplyPatchToolName] = ExecuteApplyPatchAsync,

            [ToolRiskClassifier.WorkspaceCheckToolName] = ExecuteWorkspaceCheckAsync,

            ["execute_command"] = ExecuteCommandAsync,

            ["read_command_output"] = ExecuteReadCommandOutputAsync,

            ["adjust_initiative"] = ExecuteAdjustInitiativeAsync,

            ["send_commlink_alert"] = ExecuteSendCommlinkAlertAsync,

            ["petition_dungeon_master"] = ExecutePetitionDungeonMasterAsync,

            ["cast_sending"] = ExecuteCastSendingAsync,

            ["dispatch_sending"] = ExecuteDispatchSendingAsync,
            ["continue_sending"] = ExecuteContinueSendingAsync,

            ["ask_human"] = ExecuteAskHumanAsync,

            ["scribe_lexicon"] = ExecuteScribeLexiconAsync,

            ["delete_lexicon"] = ExecuteDeleteLexiconAsync,

            ["search_archives"] = ExecuteSearchArchivesAsync,

            // Always registered, never unconditionally advertised. Registration keeps runtime
            // enablement and schema-repair recovery working without rebuilding connection
            // partitions; the handlers recheck the live facts and fail closed on their own.
            [CovenantToolNames.ProposeCovenant] = ExecuteProposeCovenantAsync,

            [CovenantToolNames.RetireCovenant] = ExecuteRetireCovenantAsync,

            ["read_saga"] = ExecuteReadSagaAsync,

            ["attach_session_file"] = ExecuteAttachSessionFileAsync,

            ["refresh_session_file"] = ExecuteRefreshSessionFileAsync,
        };

        return handlers;
    }
}

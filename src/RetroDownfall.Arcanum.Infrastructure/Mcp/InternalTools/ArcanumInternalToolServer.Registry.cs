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

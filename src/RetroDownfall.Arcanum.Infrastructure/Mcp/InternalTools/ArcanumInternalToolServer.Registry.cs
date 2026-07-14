using System.Text.Json;
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

            ["execute_command"] = ExecuteCommandAsync,

            ["adjust_initiative"] = ExecuteAdjustInitiativeAsync,

            ["use_commlink"] = ExecuteUseCommlinkAsync,

            ["petition_dungeon_master"] = ExecutePetitionDungeonMasterAsync,

            ["cast_sending"] = ExecuteCastSendingAsync,

            ["dispatch_sending"] = ExecuteDispatchSendingAsync,

            ["ask_human"] = ExecuteAskHumanAsync,

            ["scribe_lexicon"] = ExecuteScribeLexiconAsync,

            ["delete_lexicon"] = ExecuteDeleteLexiconAsync,

            ["search_archives"] = ExecuteSearchArchivesAsync,

            ["read_saga"] = ExecuteReadSagaAsync,

        };

        return handlers;

    }

}

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
    /// (<c>ask_human</c> on a non-streaming turn, <c>save_memory</c> with the Lexicon feature off)
    /// is absent from that session's advertised rows, and a name reserved only while its row exists would
    /// be claimable by an approved <c>mcp.json</c> exactly when the built-in is unavailable. Keep this set
    /// in step with <see cref="BuildToolHandlerRegistry"/>; a test compares the two.
    /// </summary>
    internal static IReadOnlySet<string> RegisteredToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "read_file",

            "edit_file",

            "write_file",

            "list_directory",

            ToolRiskClassifier.SearchWorkspaceToolName,

            ToolRiskClassifier.ApplyPatchToolName,

            ToolRiskClassifier.WorkspaceCheckToolName,

            ToolRiskClassifier.ExecuteCommandToolName,

            "read_command_output",

            "set_task_priority",

            "send_notification",

            "request_intervention",

            "create_agent_task",

            "send_agent_message",

            "get_agent_task",

            "cancel_agent_task",

            "list_agents",

            "create_cohort",

            "admit_cohort_member",

            "close_cohort",

            "remove_cohort_member",

            "post_work_offer",

            "submit_bid",

            "withdraw_bid",

            "award_work",

            "withdraw_work_offer",

            "append_shared_note",

            "read_shared_notes",

            "ask_human",

            "save_memory",

            "delete_memory",

            "search_history",

            CovenantToolNames.ProposeCovenant,

            CovenantToolNames.RetireCovenant,

            "search_memory",

            "attach_session_file",

            "refresh_session_file",
        };

    /// <summary>
    /// The registered names this server advertises to every session that has a workspace root, and to no
    /// session that has none: the file tools. A global server may offer a same-named tool in a session with
    /// no root (a filesystem server's <c>write_file</c> or <c>list_directory</c> is then the only file
    /// tooling there is), and in a session with a root the internal row is added first and wins the name, so
    /// <see cref="McpToolMerger"/> leaves these names open to global servers. A name belongs here only if a
    /// workspace root alone makes the server advertise it: <c>read_command_output</c> also needs host-process
    /// tools, which production allows only in the Development edition, so it is absent from a production
    /// session that has a root and stays reserved. Every other registered name is reserved against global
    /// servers as well as workspace-local ones, whether or not the session advertises it. The names
    /// <see cref="McpToolMerger"/> reserves unconditionally (the workspace and command tools among them)
    /// are reserved either way and are not listed here.
    /// </summary>
    internal static IReadOnlySet<string> WorkspaceRootToolNames { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "read_file",

            "edit_file",

            "write_file",

            "list_directory",
        };

    private Dictionary<string, InternalToolHandler> BuildToolHandlerRegistry()
    {
        Dictionary<string, InternalToolHandler> handlers = new(StringComparer.Ordinal)
        {
            ["read_file"] = ExecuteReadFileChunkAsync,

            ["edit_file"] = ExecuteReplaceTextBlockAsync,

            ["write_file"] = ExecuteWriteFileAsync,

            ["list_directory"] = ExecuteListDirectoryAsync,

            [ToolRiskClassifier.SearchWorkspaceToolName] = ExecuteSearchWorkspaceAsync,

            [ToolRiskClassifier.ApplyPatchToolName] = ExecuteApplyPatchAsync,

            [ToolRiskClassifier.WorkspaceCheckToolName] = ExecuteWorkspaceCheckAsync,

            ["execute_command"] = ExecuteCommandAsync,

            ["read_command_output"] = ExecuteReadCommandOutputAsync,

            ["set_task_priority"] = ExecuteAdjustInitiativeAsync,

            ["send_notification"] = ExecuteSendCommlinkAlertAsync,

            ["request_intervention"] = ExecutePetitionDungeonMasterAsync,

            ["create_agent_task"] = ExecuteUnboundTaskAsync,

            ["send_agent_message"] = ExecuteUnboundTaskAsync,

            ["get_agent_task"] = ExecuteUnboundTaskAsync,

            ["cancel_agent_task"] = ExecuteUnboundTaskAsync,

            ["list_agents"] = ExecuteUnboundTaskAsync,

            ["create_cohort"] = ExecuteUnboundTaskAsync,

            ["admit_cohort_member"] = ExecuteUnboundTaskAsync,

            ["close_cohort"] = ExecuteUnboundTaskAsync,

            ["remove_cohort_member"] = ExecuteUnboundTaskAsync,

            ["post_work_offer"] = ExecuteUnboundTaskAsync,

            ["submit_bid"] = ExecuteUnboundTaskAsync,

            ["withdraw_bid"] = ExecuteUnboundTaskAsync,

            ["award_work"] = ExecuteUnboundTaskAsync,

            ["withdraw_work_offer"] = ExecuteUnboundTaskAsync,

            ["append_shared_note"] = ExecuteUnboundTaskAsync,

            ["read_shared_notes"] = ExecuteUnboundTaskAsync,

            ["ask_human"] = ExecuteAskHumanAsync,

            ["save_memory"] = ExecuteScribeLexiconAsync,

            ["delete_memory"] = ExecuteDeleteLexiconAsync,

            ["search_history"] = ExecuteSearchArchivesAsync,

            // Always registered, never unconditionally advertised. Registration keeps runtime
            // enablement and schema-repair recovery working without rebuilding connection
            // partitions; the handlers recheck the live facts and fail closed on their own.
            [CovenantToolNames.ProposeCovenant] = ExecuteProposeCovenantAsync,

            [CovenantToolNames.RetireCovenant] = ExecuteRetireCovenantAsync,

            ["search_memory"] = ExecuteSearchMemoryAsync,

            ["attach_session_file"] = ExecuteAttachSessionFileAsync,

            ["refresh_session_file"] = ExecuteRefreshSessionFileAsync,
        };

        return handlers;
    }
}

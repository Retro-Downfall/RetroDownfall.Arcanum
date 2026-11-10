using System.Collections.Frozen;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Tool-name recognition and migration diagnostics only; never an invocation alias table.</summary>
public static class ArcanumToolNameCatalog
{
    public static IReadOnlyDictionary<string, string> RemovedNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cast_sending"] = "create_agent_task",
            ["dispatch_sending"] = "create_agent_task",
            ["continue_sending"] = "send_agent_message",
            ["read_file_chunk"] = "read_file",
            ["replace_text_block"] = "edit_file",
            ["scribe_lexicon"] = "save_memory",
            ["delete_lexicon"] = "delete_memory",
            ["read_saga"] = "search_memory",
            ["search_archives"] = "search_history",
            ["adjust_initiative"] = "set_task_priority",
            ["send_commlink_alert"] = "send_notification",
            ["petition_dungeon_master"] = "request_intervention",
            ["run_spell_script"] = "run_script",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "edit_file", "write_file", "list_directory", "search_workspace", "apply_patch",
        "workspace_check", "execute_command", "read_command_output", "ask_human", "save_memory", "delete_memory",
        "search_memory", "search_history", "set_task_priority", "send_notification", "request_intervention",
        "create_agent_task", "send_agent_message", "get_agent_task", "cancel_agent_task", "list_agents",
        "attach_session_file", "refresh_session_file", "propose_covenant", "retire_covenant", "run_script",
        "create_cohort", "admit_cohort_member", "close_cohort", "remove_cohort_member", "post_work_offer",
        "submit_bid", "withdraw_bid", "award_work", "withdraw_work_offer", "append_shared_note", "read_shared_notes",
        "acquire_workspace_lease", "renew_workspace_lease", "release_workspace_lease",
    };

    public static bool TryGetReplacement(string? name, out string? replacement)
    {
        replacement = null;

        return name is not null && RemovedNames.TryGetValue(name, out replacement);
    }

    public static bool IsKnown(string? name) => name is not null && !RemovedNames.ContainsKey(name)
        && (KnownNames.Contains(name) || ArcanumBuiltInToolNames.IsKnown(name));
}

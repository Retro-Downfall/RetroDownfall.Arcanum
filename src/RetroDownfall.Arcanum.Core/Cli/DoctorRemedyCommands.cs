namespace RetroDownfall.Arcanum.Core.Cli;

/// <summary>
/// Every command a diagnostic may tell an operator to run.
///
/// <para>They are constants rather than inline strings for one reason: a remediation that names a
/// command which no longer exists is worse than no remediation at all — it sends someone debugging a
/// broken installation down a dead end. Issue #33 calls this out explicitly (the obsolete
/// <c>config set-key</c> was the motivating example). Collecting them here lets
/// <c>DoctorRemedyCommandContractTests</c> parse every one against the real command tree, so a verb
/// that gets renamed or removed breaks the build instead of shipping as bad advice.</para>
///
/// <para>Placeholders are written in angle brackets (<c>&lt;archive&gt;</c>). The contract test
/// strips them, because it verifies the verb path, not the operand.</para>
/// </summary>
public static class DoctorRemedyCommands
{

    public const string ConfigEdit = "arcanum config edit";

    public const string ConfigShow = "arcanum config show";

    public const string KeyList = "arcanum key list";

    public const string KeySet = "arcanum key set";

    public const string KeyProviderSet = "arcanum key provider set <provider>";

    public const string KeyProviderSetPerplexity =
        "arcanum key provider set perplexity --kind web-research";

    /// <summary>
    /// A Familiar signs in through its own CLI. Arcanum prints the command and stops there — it never
    /// authenticates on the operator's behalf, so these are the only remedies it can offer.
    /// </summary>
    public const string ClaudeCodeSignIn = "claude auth login";

    public const string CodexSignIn = "codex login";

    public const string BackupRestore = "arcanum backup restore <archive>";

    public const string BackupCreateGrimoire = "arcanum backup create --scope grimoire";

    public const string DataPrunePreview = "arcanum data prune --dry-run";

    public const string DataEncryptionStatus = "arcanum data encryption status";

    public const string ModelList = "arcanum model list";

    public const string McpList = "arcanum mcp list";

    public const string OperationList = "arcanum operation list";

    public const string OperationListNeedingRepair =
        "arcanum operation list --state ReconciliationRequired";

    public const string DaemonStatus = "arcanum daemon status";

    public const string Lore = "arcanum lore";

    public const string Serve = "arcanum serve";

    public const string DoctorList = "arcanum doctor list";

    public const string RepairPermissions =
        "arcanum doctor --repair permissions.apply_owner_only --apply";

    public const string RepairManagedDirectories =
        "arcanum doctor --repair paths.create_managed_directories --apply";

    public const string RepairStalePidFile =
        "arcanum doctor --repair runtime.remove_stale_pid --apply";

    /// <summary>
    /// <see cref="KeyProviderSet"/> bound to a concrete provider. A remedy that says
    /// <c>&lt;provider&gt;</c> when the diagnostic already knows which provider failed makes the
    /// operator do work the report could have done.
    /// </summary>
    public static string KeyProviderSetFor(string providerName) =>
        $"arcanum key provider set {providerName}";

    /// <summary>Every command above, for the contract test that parses each against the real tree.</summary>
    public static IReadOnlyList<string> All =>
        [
            ConfigEdit,
            ConfigShow,
            KeyList,
            KeySet,
            KeyProviderSet,
            KeyProviderSetPerplexity,
            BackupRestore,
            BackupCreateGrimoire,
            DataPrunePreview,
            DataEncryptionStatus,
            ModelList,
            McpList,
            OperationList,
            OperationListNeedingRepair,
            DaemonStatus,
            Lore,
            Serve,
            DoctorList,
            RepairPermissions,
            RepairManagedDirectories,
            RepairStalePidFile,
        ];

}

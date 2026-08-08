using RetroDownfall.Arcanum.Core.Cli;

namespace RetroDownfall.Arcanum.Cli.Diagnostics;

/// <summary>
/// Gives the twelve pre-#33 doctor checks the stable id, subsystem, and remediation command that
/// every diagnostic now carries.
///
/// <para>Their <see cref="DoctorCheck.Name"/> and <see cref="DoctorCheck.Status"/> are deliberately
/// left exactly as they were — those two fields are the published <c>--json</c> contract and the
/// thing an operator's <c>jq</c> filter keys on. Enrichment is purely additive, so a consumer that
/// reads only name/status/detail cannot tell this change happened, while a new one can key on the id
/// and act on the remedy.</para>
/// </summary>
internal static class LegacyDoctorChecks
{

    private sealed record Identity(
        string Id,
        DoctorSubsystem Subsystem,
        string? RemedyId = null,
        string? Command = null,
        string? Explanation = null);

    private static readonly IReadOnlyDictionary<string, Identity> ByName =
        new Dictionary<string, Identity>(StringComparer.Ordinal)
        {
            ["Version"] = new("system.version", DoctorSubsystem.System),

            ["Paths"] = new(
                "paths.required",
                DoctorSubsystem.Paths,
                "paths.create_required_state",
                DoctorRemedyCommands.Serve,
                "The Grimoire database and secret store are created the first time the host starts. "
                + "'paths.managed_directories' covers the directories a repair can create."),

            ["Configuration"] = new(
                "configuration.file",
                DoctorSubsystem.Configuration,
                "configuration.repair_file",
                DoctorRemedyCommands.ConfigEdit,
                "Create or correct arcanum.json. The file is optional \u2014 without it Arcanum runs on defaults."),

            ["ProviderCredentials"] = new(
                "credentials.providers",
                DoctorSubsystem.Credentials,
                "credentials.replace_provider",
                DoctorRemedyCommands.KeyProviderSet,
                "Store a current credential for the named provider. Stored values are never displayed."),

            ["MCP"] = new(
                "mcp.global_config",
                DoctorSubsystem.Mcp,
                "mcp.repair_config",
                DoctorRemedyCommands.McpList,
                "Create or correct the global mcp.json; 'arcanum mcp list' reports what actually parses. The file is optional."),

            ["Tokenizer"] = new("system.tokenizer", DoctorSubsystem.System),

            ["ToolChildSandbox"] = new("runtime.tool_child_sandbox", DoctorSubsystem.Runtime),

            ["MasterApiKey"] = new(
                "credentials.master_key",
                DoctorSubsystem.Credentials,
                "credentials.store_master_key",
                DoctorRemedyCommands.KeySet,
                "Store the master API key in the OS credential store. It is mirrored to security.dat when possible."),

            ["FileEncryption"] = new(
                "storage.file_encryption",
                DoctorSubsystem.Storage,
                "storage.repair_encryption",
                DoctorRemedyCommands.DataEncryptionStatus,
                "Inspect encrypted, legacy, and invalid blob counts before migrating or verifying."),

            ["Embeddings"] = new("weave.embeddings", DoctorSubsystem.Weave),

            ["API Health"] = new(
                "host.api_health",
                DoctorSubsystem.Host,
                "host.start",
                DoctorRemedyCommands.Serve,
                "Start the host so the authenticated API surface can be reached."),

            ["DurableOperations"] = new(
                "operations.durable_state",
                DoctorSubsystem.Operations,
                "operations.reconcile",
                DoctorRemedyCommands.OperationListNeedingRepair,
                "List durable work that needs a human, then reconcile it with the host running."),
        };

    /// <summary>Every legacy check id, in the order the panels render.</summary>
    internal static IReadOnlyList<string> Ids => [.. ByName.Values.Select(static identity => identity.Id)];

    internal static IReadOnlyList<DoctorCatalogCheck> Catalog =>
        [
            .. ByName.Select(static entry => new DoctorCatalogCheck(
                entry.Value.Id,
                entry.Key,
                entry.Value.Subsystem,
                RequiresNetwork: false)),
        ];

    /// <summary>
    /// Adds the id, subsystem, outcome, and remedy to a legacy check. A check that is already
    /// enriched, or whose name is not in the table, is returned untouched rather than guessed at.
    /// </summary>
    internal static DoctorCheck Enrich(DoctorCheck check)
    {

        if (check.Id is not null || !ByName.TryGetValue(check.Name, out Identity? identity))
        {

            return check;

        }

        DoctorOutcome outcome = IsOptionalAndAbsent(check)
            ? DoctorOutcome.Skipped
            : FromLegacyStatus(check.Status);

        bool needsRemedy = outcome is DoctorOutcome.Degraded or DoctorOutcome.Unhealthy;

        return check with
        {
            Status = DoctorOutcomes.ToLegacyStatus(outcome),
            Id = identity.Id,
            Subsystem = identity.Subsystem,
            Outcome = outcome,
            Remedies = needsRemedy && identity.Command is not null
                ? [new DoctorRemedy(identity.RemedyId!, null, identity.Command, identity.Explanation!)]
                : [],
        };

    }

    /// <summary>
    /// Whether this check is reporting an optional file that simply is not there.
    ///
    /// <para><c>arcanum.json</c> and the global <c>mcp.json</c> are both optional — a default
    /// installation has neither, and both checks say so in their own detail. Projecting that onto
    /// <see cref="DoctorOutcome.Degraded"/> would make <c>--strict</c> fail every default
    /// installation, which would make the flag useless for the CI gating it exists for.</para>
    /// </summary>
    private static bool IsOptionalAndAbsent(DoctorCheck check) =>
        check.Status == "warn"
        && check.Detail is not null
        && check.Detail.EndsWith("not found (optional)", StringComparison.Ordinal);

    /// <summary>
    /// The inverse of <see cref="DoctorOutcomes.ToLegacyStatus"/>. It cannot recover
    /// <see cref="DoctorOutcome.Unavailable"/> or <see cref="DoctorOutcome.Skipped"/> — both collapse
    /// into the legacy vocabulary — which is exactly why new checks report an outcome directly
    /// instead of routing through a status string.
    /// </summary>
    internal static DoctorOutcome FromLegacyStatus(string status) => status switch
    {
        "fail" => DoctorOutcome.Unhealthy,
        "warn" => DoctorOutcome.Degraded,
        _ => DoctorOutcome.Healthy,
    };

}

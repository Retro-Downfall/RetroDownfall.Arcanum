namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

/// <summary>
/// Runnable examples attached to a canonical command path. Every example is parse-tested against
/// the live tree, so a renamed verb or removed option breaks the build rather than shipping help
/// that no longer works.
///
/// Examples are safe by default: they read rather than mutate wherever a read form exists, they
/// never contain a credential, endpoint, absolute host path, or tool argument payload, and a
/// destructive command's example shows the confirmation-bearing form rather than <c>--yes</c>.
/// A command with no safe example declares that in <see cref="NoSafeExample"/> instead of being
/// silently omitted.
/// </summary>
internal static class CliSurfaceExamples
{

    private const string SampleGuid = "8f14e45f-ceea-467a-9cbd-08c3c4a4c1f5";

    private static readonly Dictionary<string, string[]> Examples = new(StringComparer.Ordinal)
    {
        // Core
        ["run"] =
        [
            "arcanum run \"Explain the Ward model\"",
            "arcanum run -c \"and now summarize that\"",
            "arcanum run -r 8f14e45f \"pick this back up\"",
            "cat error.log | arcanum run \"What failed here?\"",
            "arcanum run --with @notes.md --dry-run \"Draft a release note\"",
            "arcanum run -p --output-format json \"List three risks\"",
        ],
        ["serve"] = ["arcanum serve"],
        ["serve quit"] = ["arcanum serve quit"],
        ["look"] = ["arcanum look"],
        ["center"] = ["arcanum center", "arcanum center -c"],
        ["setup"] = ["arcanum setup"],
        ["help"] = ["arcanum help sessions"],
        ["doctor"] =
        [
            "arcanum doctor",
            "arcanum doctor --include-network",
            "arcanum doctor --repair permissions.apply_owner_only",
        ],
        ["doctor list"] = ["arcanum doctor list"],
        ["doctor explain"] = ["arcanum doctor explain permissions.apply_owner_only"],

        // Completion and help
        ["completion"] = ["arcanum completion zsh"],
        ["completion install"] = ["arcanum completion install zsh"],

        // Context and selection
        ["context current"] = ["arcanum context current"],
        ["context inspect"] = ["arcanum context inspect \"Explain the Ward model\""],
        ["context tools"] = ["arcanum context tools"],
        ["context sources"] = ["arcanum context sources"],
        ["context cost"] = ["arcanum context cost"],
        ["use campaign"] = ["arcanum use campaign Arcanum"],
        ["use workspace"] = ["arcanum use workspace Arcanum"],
        ["use model"] = ["arcanum use model gpt-4o-mini"],
        ["use session"] = [$"arcanum use session {SampleGuid}"],
        ["use clear"] = ["arcanum use clear session"],

        // Campaigns
        ["campaign list"] = ["arcanum campaign list"],
        ["campaign show"] = ["arcanum campaign show Arcanum"],
        ["campaign create"] = ["arcanum campaign create --name Arcanum --path ."],
        ["campaign update"] = ["arcanum campaign update Arcanum --name Arcanum2"],
        ["campaign delete"] = ["arcanum campaign delete Arcanum"],
        ["campaign export"] = ["arcanum campaign export Arcanum --output campaign.json"],
        ["campaign import"] = ["arcanum campaign import Arcanum --file campaign.json"],
        ["campaign spells"] = ["arcanum campaign spells Arcanum"],
        ["campaign prompts"] = ["arcanum campaign prompts Arcanum"],
        ["campaign sessions"] = ["arcanum campaign sessions Arcanum"],
        ["campaign codex get"] = ["arcanum campaign codex get Arcanum"],
        ["campaign codex put"] = ["arcanum campaign codex put Arcanum --file CODEX.md"],
        ["campaign codex delete"] = ["arcanum campaign codex delete Arcanum"],

        // Sessions
        ["session list"] = ["arcanum session list", "arcanum session list --status active"],
        ["session show"] = [$"arcanum session show {SampleGuid}"],
        ["session entries"] = ["arcanum session entries --limit 20"],
        ["session fork"] = ["arcanum session fork --title \"alternative approach\""],
        ["session rename"] = ["arcanum session rename --title \"Ward model review\""],
        ["session archive"] = [$"arcanum session archive {SampleGuid}"],
        ["session export"] = ["arcanum session export --format json"],
        ["session rest"] = ["arcanum session rest"],
        ["session compact"] = ["arcanum session compact"],
        ["session attachments"] = ["arcanum session attachments"],
        ["session delete-entry"] = [$"arcanum session delete-entry {SampleGuid}"],
        ["session pin-entry"] = [$"arcanum session pin-entry {SampleGuid}"],
        ["session unpin-entry"] = [$"arcanum session unpin-entry {SampleGuid}"],
        ["session divine"] = ["arcanum session divine \"ward policy\""],

        // Saga
        ["saga list"] = ["arcanum saga list"],
        ["saga stats"] = ["arcanum saga stats"],
        ["saga divine"] = ["arcanum saga divine \"ward policy\""],
        ["saga delete"] = [$"arcanum saga delete {SampleGuid}"],

        // Memory and Lexicon
        ["memory status"] = ["arcanum memory status"],
        ["memory search"] = ["arcanum memory search \"ward policy\""],
        ["memory sources"] = ["arcanum memory sources"],
        ["memory explain"] = ["arcanum memory explain"],
        ["memory lexicon list"] = ["arcanum memory lexicon list"],
        ["memory lexicon show"] = [$"arcanum memory lexicon show {SampleGuid}"],
        ["memory lexicon search"] = ["arcanum memory lexicon search \"ward policy\""],
        ["memory lexicon delete"] = [$"arcanum memory lexicon delete {SampleGuid}"],

        // The Covenant. `set` shows the --file form because content never travels in an argument, and
        // `retire` shows the confirmation-bearing form rather than --yes.
        ["memory covenant list"] = ["arcanum memory covenant list"],
        ["memory covenant show"] = ["arcanum memory covenant show preference.builds"],
        ["memory covenant set"] =
            ["arcanum memory covenant set preference.builds --file preference.txt --expected-revision 0"],
        ["memory covenant retire"] =
            ["arcanum memory covenant retire preference.builds --expected-revision 1"],

        // Lore
        ["lore list"] = ["arcanum lore list"],
        ["lore get"] = ["arcanum lore get ward.color"],
        ["lore set"] = ["arcanum lore set ward.color amber"],
        ["lore delete"] = ["arcanum lore delete ward.color"],

        // Spells
        ["spell list"] = ["arcanum spell list"],
        ["spell show"] = ["arcanum spell show summarize"],
        ["spell search"] = ["arcanum spell search --query summarize"],
        ["spell create"] = ["arcanum spell create --name summarize --body \"Summarize the input.\""],
        ["spell update"] = ["arcanum spell update summarize --description \"Shorter summaries.\""],
        ["spell delete"] = ["arcanum spell delete summarize"],
        ["spell clone"] = ["arcanum spell clone summarize --new-name summarize-terse"],
        ["spell execute"] = ["arcanum spell execute summarize --input \"a long document\""],
        ["spell cast"] = ["arcanum spell cast summarize"],
        ["spell validate"] = ["arcanum spell validate summarize"],
        ["spell export"] = ["arcanum spell export summarize --output summarize.json"],
        ["spell import"] = ["arcanum spell import --file summarize.json"],
        ["spell versions"] = ["arcanum spell versions summarize"],
        ["spell version create"] = ["arcanum spell version create summarize --version 2 --body \"Summarize tersely.\""],
        ["spell version update"] = ["arcanum spell version update summarize --version 2 --body \"Summarize tersely.\""],
        ["spell version activate"] = ["arcanum spell version activate summarize --version 2"],

        // Prompts
        ["prompt list"] = ["arcanum prompt list"],
        ["prompt show"] = ["arcanum prompt show release-notes"],
        ["prompt versions"] = ["arcanum prompt versions release-notes"],
        ["prompt create"] = ["arcanum prompt create --name release-notes --version 1 --template \"Write notes for {version}.\""],
        ["prompt update"] = ["arcanum prompt update release-notes --template \"Write notes for {version}.\""],
        ["prompt delete"] = ["arcanum prompt delete release-notes"],
        ["prompt render"] = ["arcanum prompt render release-notes --param version=1.2.0"],
        ["prompt test"] = ["arcanum prompt test release-notes"],
        ["prompt execute"] = ["arcanum prompt execute release-notes --input \"the changelog\""],
        ["prompt clone"] = ["arcanum prompt clone release-notes --new-name release-notes-short --new-version 1"],
        ["prompt export"] = ["arcanum prompt export release-notes --output release-notes.json"],
        ["prompt import"] = ["arcanum prompt import --file release-notes.json"],

        // Wards
        ["ward list"] = ["arcanum ward list"],
        ["ward show"] = [$"arcanum ward show {SampleGuid}"],
        ["ward resolve"] = [$"arcanum ward resolve {SampleGuid} --deny --reason \"not this time\""],

        // Trials
        ["trial run"] = ["arcanum trial run --target spell --target-value summarize"],

        // Apprentices
        ["apprentice list"] = ["arcanum apprentice list"],
        ["apprentice show"] = ["arcanum apprentice show nightly-triage"],
        ["apprentice create"] = ["arcanum apprentice create --goal \"Triage new issues\" --name nightly-triage"],
        ["apprentice start"] = ["arcanum apprentice start nightly-triage"],
        ["apprentice pause"] = ["arcanum apprentice pause nightly-triage"],
        ["apprentice resume"] = ["arcanum apprentice resume nightly-triage"],
        ["apprentice cancel"] = ["arcanum apprentice cancel nightly-triage"],
        ["apprentice delete"] = ["arcanum apprentice delete nightly-triage"],
        ["apprentice reweave"] = ["arcanum apprentice reweave nightly-triage --plan @plan.json"],
        ["apprentice intervene"] = ["arcanum apprentice intervene nightly-triage --guidance \"skip the archived repos\""],
        ["apprentice cast"] = ["arcanum apprentice cast nightly-triage --goal \"Summarize one issue\""],

        // Conclave
        ["conclave dispatch"] = ["arcanum conclave dispatch --goal \"Review this diff\""],
        ["conclave status"] = ["arcanum conclave status"],
        ["budget"] = ["arcanum budget"],
        ["conclave continue"] = [$"arcanum conclave continue {SampleGuid} --message \"keep going\""],

        // Models and providers
        ["model list"] = ["arcanum model list"],
        ["model show"] = ["arcanum model show gpt-4o-mini"],
        ["provider list"] = ["arcanum provider list"],
        ["provider show"] = ["arcanum provider show openai"],

        // Workspaces
        ["workspace list"] = ["arcanum workspace list"],
        ["workspace show"] = ["arcanum workspace show Arcanum"],
        ["workspace current"] = ["arcanum workspace current"],
        ["workspace register"] = ["arcanum workspace register --name Arcanum"],
        ["workspace unregister"] = ["arcanum workspace unregister Arcanum"],
        ["workspace tree"] = ["arcanum workspace tree Arcanum"],
        ["workspace info"] = ["arcanum workspace info README.md"],
        ["workspace read"] = ["arcanum workspace read README.md"],
        ["workspace search"] = ["arcanum workspace search \"ward policy\""],
        ["workspace index"] = ["arcanum workspace index Arcanum"],
        ["workspace index-status"] = ["arcanum workspace index-status Arcanum"],
        ["workspace chunks"] = ["arcanum workspace chunks Arcanum --limit 20"],

        // MCP and tools
        ["mcp list"] = ["arcanum mcp list"],
        ["mcp show"] = ["arcanum mcp show filesystem"],
        ["mcp start"] = ["arcanum mcp start filesystem"],
        ["mcp stop"] = ["arcanum mcp stop filesystem"],
        ["mcp restart"] = ["arcanum mcp restart filesystem"],
        ["mcp reload"] = ["arcanum mcp reload"],
        ["mcp trust"] = ["arcanum mcp trust"],
        ["mcp tools"] = ["arcanum mcp tools filesystem"],
        ["mcp invoke"] = ["arcanum mcp invoke read_file --server filesystem"],
        ["tool list"] = ["arcanum tool list"],
        ["tool show"] = ["arcanum tool show workspace_read"],
        ["tool invoke"] = ["arcanum tool invoke workspace_read"],

        // Web workflows
        ["search"] = ["arcanum search \"native aot dotnet 10\""],
        ["browse"] = ["arcanum browse example.com"],
        ["research"] = ["arcanum research \"native aot tradeoffs\" --sources 5"],

        // Files and batches
        ["file upload"] = ["arcanum file upload requests.jsonl"],
        ["file list"] = ["arcanum file list"],
        ["file show"] = ["arcanum file show file-abc123"],
        ["file download"] = ["arcanum file download file-abc123 --output results.jsonl"],
        ["file delete"] = ["arcanum file delete file-abc123"],
        ["batch create"] = ["arcanum batch create requests.jsonl"],
        ["batch list"] = ["arcanum batch list"],
        ["batch show"] = ["arcanum batch show batch-abc123"],
        ["batch wait"] = ["arcanum batch wait batch-abc123"],
        ["batch cancel"] = ["arcanum batch cancel batch-abc123"],
        ["batch reset"] = ["arcanum batch reset batch-abc123"],
        ["batch output"] = ["arcanum batch output batch-abc123 --output results.jsonl"],
        ["batch errors"] = ["arcanum batch errors batch-abc123 --output errors.jsonl"],

        // Attachments
        ["attachment list"] = ["arcanum attachment list"],
        ["attachment show"] = ["arcanum attachment show diagram"],
        ["attachment add"] = ["arcanum attachment add diagram.png"],
        ["attachment versions"] = ["arcanum attachment versions diagram"],
        ["attachment reference"] = ["arcanum attachment reference diagram"],
        ["attachment refresh"] = ["arcanum attachment refresh diagram"],
        ["attachment reveal"] = ["arcanum attachment reveal diagram"],
        ["attachment export"] = ["arcanum attachment export diagram --output diagram.png"],
        ["attachment pin"] = ["arcanum attachment pin diagram"],
        ["attachment unpin"] = ["arcanum attachment unpin diagram"],

        // Operations
        ["operation list"] = ["arcanum operation list"],
        ["operation show"] = [$"arcanum operation show {SampleGuid}"],
        ["operation cancel"] = [$"arcanum operation cancel {SampleGuid}"],
        ["operation retry"] = [$"arcanum operation retry {SampleGuid}"],
        ["operation reconcile"] = ["arcanum operation reconcile"],

        // Backup
        ["backup create"] = ["arcanum backup create --output backup.arcanum"],
        ["backup list"] = ["arcanum backup list"],
        ["backup inspect"] = ["arcanum backup inspect backup.arcanum"],
        ["backup verify"] = ["arcanum backup verify backup.arcanum"],

        // Data lifecycle
        ["data status"] = ["arcanum data status"],
        ["data retention show"] = ["arcanum data retention show"],
        ["data retention set"] = ["arcanum data retention set --sessions 90"],
        ["data encryption status"] = ["arcanum data encryption status"],
        ["data encryption verify"] = ["arcanum data encryption verify"],

        // Daemon
        ["daemon status"] = ["arcanum daemon status"],
        ["daemon jobs"] = ["arcanum daemon jobs"],
        ["daemon initiative"] = ["arcanum daemon initiative --interval 300"],
        ["daemon alert"] = ["arcanum daemon alert \"Nightly run finished\" --title Arcanum"],

        // Configuration and presets
        ["config show"] = ["arcanum config show"],
        ["config get"] = ["arcanum config get Arcanum:DefaultModel"],
        ["config set"] = ["arcanum config set Arcanum:DefaultModel gpt-4o-mini"],
        ["config validate"] = ["arcanum config validate"],
        ["config path"] = ["arcanum config path"],
        ["config edit"] = ["arcanum config edit"],
        ["config open"] = ["arcanum config open"],
        ["preset list"] = ["arcanum preset list"],
        ["preset show"] = ["arcanum preset show local-first"],
        ["preset diff"] = ["arcanum preset diff local-first"],
        ["preset apply"] = ["arcanum preset apply local-first"],
        ["preset reset"] = ["arcanum preset reset"],

        // Keys
        ["key list"] = ["arcanum key list"],
        ["key provider status"] = ["arcanum key provider status openai"],
        ["data factory-reset"] = ["arcanum data factory-reset --all --dry-run"],

        // Watch
        ["watch session"] = ["arcanum watch session --reconnect"],
        ["watch apprentice"] = ["arcanum watch apprentice nightly-triage"],
        ["watch logs"] = ["arcanum watch logs --level Warning"],
        ["watch mcp"] = ["arcanum watch mcp"],
        ["watch daemons"] = ["arcanum watch daemons"],
        ["watch health"] = ["arcanum watch health --interval 10"],

        // Application launch
        ["open center"] = ["arcanum open center"],
        ["open theforge"] = ["arcanum open theforge"],
        ["open compendium"] = ["arcanum open compendium"],
        ["open session"] = [$"arcanum open session {SampleGuid}"],
        ["open campaign"] = ["arcanum open campaign Arcanum"],
        ["open spell"] = ["arcanum open spell summarize"],
        ["open prompt"] = ["arcanum open prompt release-notes"],
        ["open apprentice"] = ["arcanum open apprentice nightly-triage"],
    };

    /// <summary>
    /// Commands that deliberately carry no example, with the reason. Credential-bearing,
    /// irreversibly destructive, and OS-service-lifecycle commands are documented in prose rather
    /// than offered as a line an operator can paste without reading.
    /// </summary>
    private static readonly Dictionary<string, string> NoSafeExample = new(StringComparer.Ordinal)
    {
        ["key show"] = "Prints a secret; an example invites running it with output redirected.",
        ["key set"] = "Writes a credential; a paste-ready form invites putting a real key in shell history.",
        ["key provider set"] = "Writes a provider credential from stdin or a secure prompt.",
        ["key provider delete"] = "Destroys a stored credential.",
        ["data reset-memory"] = "Irreversibly destroys durable memory.",
        ["data prune"] = "Irreversibly deletes durable history.",
        ["data delete-session"] = "Irreversibly deletes a Session and its transcript.",
        ["data delete-attachment"] = "Irreversibly deletes attachment content.",
        ["data encryption rotate-key"] = "Re-encrypts the Grimoire; a mistimed paste is not recoverable from the CLI.",
        ["data encryption migrate"] = "Rewrites the Grimoire's key derivation in place.",
        ["backup restore"] = "Overwrites live local state from an archive.",
        ["backup migrate"] = "Rewrites an archive in place.",
        ["daemon install"] = "Registers an OS service; the correct invocation is platform-specific and privileged.",
        ["daemon uninstall"] = "Removes an OS service registration.",
    };

    public static IReadOnlyList<string> For(string path) =>
        Examples.TryGetValue(path, out string[]? examples)
            ? examples
            : [];

    public static string? ExemptionReason(string path) =>
        NoSafeExample.TryGetValue(path, out string? reason)
            ? reason
            : null;

    public static IReadOnlyCollection<string> Paths => Examples.Keys;

    public static IReadOnlyCollection<string> ExemptPaths => NoSafeExample.Keys;

}

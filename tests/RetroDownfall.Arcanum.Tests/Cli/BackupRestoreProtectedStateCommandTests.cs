using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using System.Text.Json.Serialization.Metadata;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The disclosure a destructive protected-state restore has to write before it asks anything
/// (§10.19.10).
/// </summary>
/// <remarks>
/// Ordered-event assertions rather than "does the output contain it". The whole contract is that an
/// operator has read the nonrevocable-disclosure statement, the receipt-backed possible-attempt count,
/// and where to go and delete externally <em>before</em> the prompt appears — output that arrived after
/// the answer was given would satisfy a contains-check and none of the contract.
///
/// <para>Driven by the wire spelling <c>--protected-state</c> carries rather than by the enum, so the
/// ordering is proved for the value an operator actually types (§10.19.12). The command is still
/// invoked directly rather than through the parser, because what is under test is the sequence of
/// writes, and one ordered log shared by the dispatcher and the prompt is the only way to see it.</para>
/// </remarks>
public sealed class BackupRestoreProtectedStateCommandTests
{

    private const string Archive = "/tmp/protected.arcbackup";

    [Fact]
    public async Task The_disclosure_the_counts_and_every_help_target_precede_the_prompt()
    {

        Harness harness = new(confirm: true);

        int exit = await harness.RestoreAsync("purge-protected-state");

        Assert.Equal((int)CliExitCode.Success, exit);

        int disclosure = harness.IndexOf(CovenantExternalRetentionDisclosure.DestructiveOperationText);

        int counts = harness.IndexOfContaining("at least 9");

        int official = harness.IndexOfContaining(
            "https://privacy.claude.com/en/collections/10672565-data-handling-retention");

        int providersPage = harness.IndexOfContaining(
            CovenantExternalRetentionDisclosure.ConfiguredProvidersPageTarget);

        int operatorGuide = harness.IndexOfContaining(
            CovenantExternalRetentionDisclosure.OperatorGuideTarget);

        int prompt = harness.IndexOf(Harness.PromptEvent);

        Assert.True(disclosure >= 0, harness.Describe());

        Assert.True(counts > disclosure, harness.Describe());

        Assert.True(official > counts, harness.Describe());

        Assert.True(providersPage > counts, harness.Describe());

        Assert.True(operatorGuide > official, harness.Describe());

        Assert.True(operatorGuide > providersPage, harness.Describe());

        Assert.True(prompt > operatorGuide, harness.Describe());

    }

    [Fact]
    public async Task The_shared_destructive_copy_is_written_byte_for_byte()
    {

        Harness harness = new(confirm: true);

        _ = await harness.RestoreAsync("purge-protected-state");

        // Not a paraphrase and not a substring of a longer sentence: one write, exactly the shared
        // constant. Four surfaces have to tell an operator the same true thing, and a local rewording is
        // how they stop doing that.
        Assert.Contains(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            harness.Events);

    }

    /// <summary>
    /// The whole confirmation sequence shares the question's stream. A <c>--output-format json</c>
    /// restore replays whatever the command wrote to stdout ahead of the JSON document, so a
    /// disclosure line on the payload stream is several lines of prose followed by one JSON object —
    /// and `arcanum backup restore ... --output-format json | jq` stops parsing at line one.
    /// </summary>
    [Fact]
    public async Task No_part_of_the_disclosure_sequence_is_written_to_the_payload_stream()
    {

        Harness harness = new(confirm: true);

        int exit = await harness.RestoreAsync("purge-protected-state");

        Assert.Equal((int)CliExitCode.Success, exit);

        // Still written, and still before the question — moving the stream must not lose the record.
        Assert.True(
            harness.IndexOf(CovenantExternalRetentionDisclosure.DestructiveOperationText)
            < harness.IndexOf(Harness.PromptEvent),
            harness.Describe());

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            harness.Payloads);

        Assert.DoesNotContain(
            harness.Payloads,
            static line => line.Contains("at least 9", StringComparison.Ordinal));

        Assert.DoesNotContain(
            harness.Payloads,
            static line => line.Contains("Retention guidance", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Declining_the_protected_state_prompt_starts_no_restore_at_all()
    {

        Harness harness = new(confirm: false);

        int exit = await harness.RestoreAsync("purge-protected-state");

        Assert.Equal((int)CliExitCode.Success, exit);

        // No staging root, no journal, no exclusive owner: the mutating call was never made.
        Assert.Null(harness.Restore.LastRestoreRequest);

        Assert.Contains(
            "Restore cancelled.",
            harness.Events);

        // And the disclosure still preceded the refusal, so the operator declined having read it.
        Assert.True(
            harness.IndexOf(CovenantExternalRetentionDisclosure.DestructiveOperationText)
            < harness.IndexOf(Harness.PromptEvent),
            harness.Describe());

    }

    [Fact]
    public async Task Accepting_marks_the_protected_state_choice_confirmed_separately()
    {

        Harness harness = new(confirm: true);

        _ = await harness.RestoreAsync("restore-protected-state");

        BackupRestoreRequest request =
            Assert.IsType<BackupRestoreRequest>(harness.Restore.LastRestoreRequest);

        Assert.Equal(BackupProtectedStateMode.RestoreProtectedState, request.ProtectedStateMode);

        Assert.True(request.ProtectedStateConfirmed);

        Assert.True(request.Confirmed);

        // Two prompts, not one: replacing the installation and reinstating its protected state are
        // separate destructive answers.
        Assert.Equal(2, harness.Prompt.Calls);

    }

    [Fact]
    public async Task The_default_mode_writes_no_disclosure_and_asks_one_question()
    {

        Harness harness = new(confirm: true);

        _ = await harness.RestoreAsync("reject");

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            harness.Events);

        Assert.Equal(1, harness.Prompt.Calls);

        // The plan is not fetched either: the default authorizes no protected-state effect, so there is
        // no count to precede a decision that is not being made.
        Assert.Null(harness.Restore.LastPlanRequest);

        Assert.False(
            Assert.IsType<BackupRestoreRequest>(harness.Restore.LastRestoreRequest)
                .ProtectedStateConfirmed);

    }

    [Fact]
    public async Task A_plan_blocker_stops_the_command_before_it_writes_or_asks_anything()
    {

        Harness harness = new(confirm: true)
        {

            Blockers =
            [
                new BackupVerifyIssue(
                    BackupRestoreProtectedStatePolicy.CovenantRequiredCode,
                    "This installation does not run the Covenant restore arm."),
            ],

        };

        int exit = await harness.RestoreAsync("purge-protected-state");

        Assert.Equal((int)CliExitCode.GenericError, exit);

        Assert.Equal(0, harness.Prompt.Calls);

        Assert.Null(harness.Restore.LastRestoreRequest);

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            harness.Events);

        Assert.Contains(
            harness.Events,
            static line => line.Contains(
                BackupRestoreProtectedStatePolicy.CovenantRequiredCode,
                StringComparison.Ordinal));

    }

    /// <summary>
    /// The same refusal under <c>--output-format json</c>. The blockers are the whole answer to "why
    /// did this restore refuse", so a consumer has to receive them as data: prose lines on stdout are
    /// both the wrong shape and, on this branch, the only thing written — the command returns before
    /// any document is emitted, so `arcanum backup restore --output-format json | jq` gets a parse
    /// error instead of a reason.
    /// </summary>
    [Fact]
    public async Task A_plan_blocker_under_json_writes_one_typed_document_rather_than_prose()
    {

        Harness harness = new(confirm: true, json: true)
        {

            Blockers =
            [
                new BackupVerifyIssue(
                    BackupRestoreProtectedStatePolicy.CovenantRequiredCode,
                    "This installation does not run the Covenant restore arm."),
            ],

        };

        int exit = await harness.RestoreAsync("purge-protected-state");

        Assert.Equal((int)CliExitCode.GenericError, exit);

        Assert.Empty(harness.Payloads);

        Assert.Contains("<json:BackupRestorePlan>", harness.Events);

    }

    [Fact]
    public async Task An_exact_count_is_reported_as_exact_rather_than_as_a_lower_bound()
    {

        Harness harness = new(confirm: true)
        {

            Exposure = new BackupRestoreDisclosureExposure(
                EverOccurred: true,
                PossibleAttempts: 3,
                CovenantDisclosureCountKind.Exact),

        };

        _ = await harness.RestoreAsync("purge-protected-state");

        Assert.Contains(
            harness.Events,
            static line => line.Contains("exactly 3", StringComparison.Ordinal));

        Assert.DoesNotContain(
            harness.Events,
            static line => line.Contains("at least", StringComparison.Ordinal));

    }

    [Fact]
    public async Task A_destination_that_never_disclosed_anything_says_so_rather_than_printing_zero()
    {

        Harness harness = new(confirm: true)
        {

            Exposure = BackupRestoreDisclosureExposure.None,

        };

        _ = await harness.RestoreAsync("purge-protected-state");

        Assert.Contains(
            harness.Events,
            static line => line.Contains(
                "no nonrevocable disclosure",
                StringComparison.Ordinal));

    }

    [Fact]
    public async Task An_omitted_option_is_the_refusing_default_rather_than_a_missing_answer()
    {

        Harness harness = new(confirm: true);

        _ = await harness.RestoreAsync(protectedState: null);

        Assert.Equal(
            BackupProtectedStateMode.Reject,
            Assert.IsType<BackupRestoreRequest>(harness.Restore.LastRestoreRequest).ProtectedStateMode);

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            harness.Events);

    }

    [Theory]
    [InlineData("purge")]
    [InlineData("restore-protected")]
    [InlineData("")]
    public async Task An_unsupported_value_is_refused_before_the_disclosure_the_plan_or_the_prompt(
        string value)
    {

        Harness harness = new(confirm: true);

        int exit = await harness.RestoreAsync(value);

        Assert.Equal((int)CliExitCode.ConfigurationError, exit);

        // Ahead of everything: no plan, no disclosure, no question, and no mutating call. A value the
        // catalog does not know names no effect, so there is nothing yet for an operator to answer for.
        Assert.Null(harness.Restore.LastPlanRequest);

        Assert.Null(harness.Restore.LastRestoreRequest);

        Assert.Equal(0, harness.Prompt.Calls);

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            harness.Events);

        // The reader itself, because a service that was never called records no passphrase either way.
        Assert.Equal(0, harness.Passphrases.Reads);

        Assert.Contains(
            harness.Events,
            static line => line.Contains("--protected-state", StringComparison.Ordinal));

    }

    /// <summary>
    /// One <see cref="BackupCommands"/> with every dependency recording into a single ordered log.
    /// </summary>
    /// <remarks>
    /// The dispatcher and the prompt share the list on purpose. Two separate logs would have to be
    /// merged by timestamp to be compared, and a merge is exactly where an ordering assertion stops
    /// proving anything.
    /// </remarks>
    private sealed class Harness
    {

        internal const string PromptEvent = "<prompt>";

        private readonly RecordingConfirmationPrompt _prompt;

        private readonly bool _json;

        internal Harness(bool confirm, bool json = false)
        {

            _prompt = new RecordingConfirmationPrompt(Events, confirm);

            _json = json;

        }

        internal List<string> Events { get; } = [];

        /// <summary>
        /// The subset of <see cref="Events"/> that went to stdout. Disclosure is operator-facing
        /// text that precedes a question, so it belongs on the diagnostic stream with the question —
        /// never on the payload stream a <c>--output-format json</c> consumer parses.
        /// </summary>
        internal List<string> Payloads { get; } = [];

        internal FakeRestoreService Restore { get; } = new();

        internal FixedPassphraseReader Passphrases { get; } = new();

        internal BackupVerifyIssue[] Blockers { get; init; } = [];

        internal BackupRestoreDisclosureExposure? Exposure { get; init; } =
            new(EverOccurred: true, PossibleAttempts: 9, CovenantDisclosureCountKind.LowerBound);

        internal RecordingConfirmationPrompt Prompt => _prompt;

        internal async Task<int> RestoreAsync(string? protectedState)
        {

            Restore.Blockers = Blockers;

            Restore.Exposure = Exposure;

            BackupCommands commands = new(
                new ThrowingBackupService(),
                Restore,
                Passphrases,
                _prompt,
                new RecordingDispatcher(Events, Payloads),
                new FixedInvocationContext(_json),
                Options.Create(
                    new ArcanumSettings
                    {

                        Providers =
                        [
                            new ProviderSettings
                            {

                                Name = "Claude Code",

                                Type = AiProviderKind.ClaudeCodeCli,

                            },
                            new ProviderSettings
                            {

                                Name = "House gateway",

                                Type = AiProviderKind.OpenAICompatible,

                                Endpoint = "https://gateway.internal.example/v1",

                            },
                        ],

                    }));

            return await commands.Restore(
                Archive,
                conflictMode: null,
                destinationRoot: null,
                sessionIds: [],
                mappings: [],
                campaignMappings: [],
                protectedState,
                restoreMasterApiKey: false,
                dryRun: false,
                skipSafetyBackup: true,
                passphraseEnvironmentVariable: null,
                passphraseFileDescriptor: null,
                CancellationToken.None);

        }

        internal int IndexOf(string value) => Events.IndexOf(value);

        internal int IndexOfContaining(string value) =>
            Events.FindIndex(line => line.Contains(value, StringComparison.Ordinal));

        internal string Describe() =>
            "Ordered events:\n" + string.Join('\n', Events);

    }

    private sealed class RecordingDispatcher(List<string> events, List<string> payloads)
        : IConsoleDispatcher
    {

        public void WritePayload(string value)
        {

            events.Add(value);

            payloads.Add(value);

        }

        public void WriteDiagnostic(string value) => events.Add(value);

        public void WriteVerbose(string value) => events.Add(value);

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo) =>
            events.Add("<json:" + typeof(T).Name + ">");

        public void WriteJson(System.Text.Json.JsonElement value) => events.Add("<json>");

        public void BeginJsonStream() => events.Add("<json-stream>");

    }

    private sealed class RecordingConfirmationPrompt(List<string> events, bool answer)
        : IConfirmationPrompt
    {

        internal int Calls { get; private set; }

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken = default)
        {

            Calls++;

            events.Add(Harness.PromptEvent);

            return Task.FromResult(answer);

        }

    }

    private sealed class FixedInvocationContext(bool json) : ICliInvocationContext
    {

        public CliInvocationOptions Options { get; } =
            new(json, Plain: true, Yes: false);

    }

    private sealed class FixedPassphraseReader : IBackupPassphraseReader
    {

        internal int Reads { get; private set; }

        public ValueTask<SensitiveBackupPassphrase?> ReadAsync(
            BackupPassphraseReadRequest request,
            CancellationToken cancellationToken)
        {

            Reads++;

            return ValueTask.FromResult<SensitiveBackupPassphrase?>(
                new SensitiveBackupPassphrase("restore secret".ToCharArray()));

        }

    }

    private sealed class ThrowingBackupService : IBackupService
    {

        public Task<BackupPlan> PlanAsync(
            BackupPlanRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A restore plans no backup.");

        public Task<BackupCreateResult> CreateAsync(
            BackupCreateRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A restore creates no backup.");

        public Task<BackupInspectResult> InspectAsync(
            string archivePath,
            ReadOnlyMemory<char>? recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A restore inspects through its own service.");

        public Task<BackupVerifyResult> VerifyAsync(
            string archivePath,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A restore verifies through its own service.");

        public Task<IReadOnlyList<BackupListItem>> ListAsync(
            string? directory,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A restore lists nothing.");

    }

    private sealed class FakeRestoreService : IBackupRestoreService
    {

        internal BackupVerifyIssue[] Blockers { get; set; } = [];

        internal BackupRestoreDisclosureExposure? Exposure { get; set; }

        internal BackupRestoreRequest? LastPlanRequest { get; private set; }

        internal BackupRestoreRequest? LastRestoreRequest { get; private set; }

        public Task<BackupRestorePlan> PlanAsync(
            BackupRestoreRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            LastPlanRequest = request;

            return Task.FromResult(Plan(request));

        }

        public Task<BackupRestoreResult> RestoreAsync(
            BackupRestoreRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {

            LastRestoreRequest = request;

            return Task.FromResult(
                new BackupRestoreResult(
                    BackupRestoreStatus.Completed,
                    request.ArchivePath,
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    request.ConflictMode,
                    "/tmp/arcanum",
                    SafetyBackupPath: null,
                    Plan(request),
                    Manifest: null,
                    Reconciliation: null,
                    [new BackupRestorePhaseRecord(BackupRestorePhase.Commit, "committed")],
                    []));

        }

        public Task<BackupMigrateResult> MigrateAsync(
            BackupMigrateRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This suite migrates nothing.");

        private BackupRestorePlan Plan(BackupRestoreRequest request) =>
            new(
                new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero),
                request.ArchivePath,
                BackupArchiveFormat.CurrentVersion,
                request.ConflictMode,
                "/tmp/arcanum",
                [BackupComponent.GrimoireDatabase],
                Entries: 3,
                RestoredBytes: 2048,
                RequiredBytes: 4096,
                AvailableBytes: 1_000_000,
                "sha256-source",
                "sha256-destination",
                SchemaMigrationRequired: false,
                SelectedSessionIds: [],
                PathMappings: [],
                UnmappedNonportablePaths: [],
                RequiresConfirmation: true,
                SafetyBackupPlanned: false,
                Warnings: [],
                Blockers: Blockers,
                request.ProtectedStateMode,
                Exposure);

    }

}

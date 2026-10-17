using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The Long Rest as the owning documents state it: the configuration key, the sweep route, the recovery that only parks, the operator
/// surface a parked sweep presents, and the Windows lanes that qualify it natively.
/// </summary>
/// <remarks>
/// <para>Each test pairs a claim the documents make with the source that makes it true, or with a phrase of a design that was replaced, so
/// a failure names a document that drifted from the code and not a spelling rule. The failure these prevent is silent: a paragraph that
/// describes recovery resuming a sweep, a pooled provider connection or a missing route stays plausible after the code beside it
/// changes, and an operator who trusts it concludes the wrong remedy.</para>
/// <para>The assertions are narrow on purpose. They name a distinctive fragment, not a sentence, so rewording the surrounding prose does
/// not fail them.</para>
/// </remarks>
public sealed class LongRestDocumentationTests
{
    /// <summary>
    /// Wording from designs the Long Rest went through before it reached its present shape: recovery that ran a sweep, a door for it,
    /// a deferral of its readiness, a pooled provider connection and a feature that performed no discovery. None of it describes the code.
    /// </summary>
    private static readonly string[] SupersededWording =
    [
        "Two-Door",
        "Readiness Deferral",
        "Thin Door",
        "RecoverAdmittedAsync",
        "IAdmittedRecoveryHandler",
        "OrdinaryAdmittedExternalEffect",
        "Resume clean committed sweep work",
        "two-minute pooled lifetime",
        "The pool is reused across",
        "None performs discovery or model work",
        "an acquired Running recovery owner",
        "The registered reconciler still owns latest-revision terminal settlement",
    ];

    private static readonly string[] GovernedDocuments =
    [
        "Arcanum.DESIGN.md",
        "Arcanum.API.md",
        "Arcanum.Command.Reference.md",
        "Compendium.README.md",
        "Arcanum.OATH.md",
    ];

    [Fact]
    public void No_governed_document_describes_a_replaced_long_rest_design()
    {
        List<string> offenders = [];

        foreach (string file in GovernedDocuments)
        {
            string document = ReadDocument(file);

            foreach (string stale in SupersededWording)
            {
                if (document.Contains(stale, StringComparison.Ordinal))
                {
                    offenders.Add($"{file} still says \"{stale}\"");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_registry_tells_an_operator_what_a_parked_sweep_needs_and_not_that_recovery_resumes_it()
    {
        LongRunningOperationRecoveryDescriptor descriptor = LongRunningOperationRecoveryRegistry.Descriptors[LongRunningOperationKinds.LongRestSweep];

        Assert.Contains("never runs a sweep", descriptor.RecoveryIntent, StringComparison.Ordinal);

        Assert.DoesNotContain("Resume clean", descriptor.RecoveryIntent, StringComparison.Ordinal);

        foreach (string code in (string[])
                 [
                     LongRestParkCodes.Interrupted,
                     LongRestParkCodes.MaintenanceDeferred,
                     LongRestParkCodes.ReadmitRequested,
                     LongRestParkCodes.ReadmitExhausted,
                     LongRestParkCodes.AuthorizedRequestRequired,
                     ErrorCodes.LongRest.ProviderRejected,
                     ErrorCodes.LongRest.TransportUnknown,
                 ])
        {
            Assert.Contains(code, descriptor.ManualRepairGuidance, StringComparison.Ordinal);
        }

        // TransportUnknown is the one park a retry cannot clear, and the guidance an operator reads has to say so.
        Assert.Contains("cleared only by cancel", descriptor.ManualRepairGuidance, StringComparison.Ordinal);

        // Every code the worker readmits needs no repair, so every one is named in that list and none is left to read as "fix the cause".
        foreach (string code in LongRestFaultPolicy.ResumeLaterCodes)
        {
            Assert.Contains(code, descriptor.ManualRepairGuidance, StringComparison.Ordinal);
        }

        // A transient provider failure is also an unknown-usage attempt, so the guidance says what a second one of the same unit does.
        Assert.Contains("second such failure of the same unit parks as LongRest.TransportUnknown", descriptor.ManualRepairGuidance, StringComparison.Ordinal);

        // A crash park keeps its code when its failure budget is spent, so "wait" is not true of it: the guidance names that case,
        // the commands that clear it, and what a new request for the scope is answered with, in that order.
        const string spent = "A clean sweep parked as long_rest.interrupted after its fourth readmission in a row without committed progress is not readmitted again";

        int spentAt = descriptor.ManualRepairGuidance.IndexOf(spent, StringComparison.Ordinal);

        Assert.True(spentAt >= 0, "The guidance does not say what a spent failure budget does to a long_rest.interrupted park.");

        string afterSpent = descriptor.ManualRepairGuidance[spentAt..];

        int retryAt = afterSpent.IndexOf("'arcanum operation retry <id>'", StringComparison.Ordinal);

        int cancelAt = afterSpent.IndexOf("'arcanum operation cancel <id>'", StringComparison.Ordinal);

        int parkedAt = afterSpent.IndexOf("LongRest.SweepParked", StringComparison.Ordinal);

        Assert.True(retryAt > 0 && cancelAt > retryAt && parkedAt > cancelAt, "The spent-budget guidance does not name retry, cancel and LongRest.SweepParked in that order.");

        // And it sits beside the "wait" it qualifies, before any other park code is explained.
        Assert.True(
            spentAt > descriptor.ManualRepairGuidance.IndexOf("so wait", StringComparison.Ordinal)
            && spentAt < descriptor.ManualRepairGuidance.IndexOf("Each LongRest.ProviderUnavailable", StringComparison.Ordinal),
            "The spent-budget guidance is not next to the wait it qualifies.");
    }

    [Fact]
    public void The_opt_in_key_is_stated_where_the_features_and_their_degradation_are_listed()
    {
        const string key = "Arcanum:Features:LongRest";

        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains(
            key,
            DocumentSection(design, "### 21.16 The Long Rest: deterministic Saga consolidation", "#### 21.16.1 "),
            StringComparison.Ordinal);

        Assert.Contains(key, DocumentSection(design, "### 21.3 Configuration", "### 21.4 "), StringComparison.Ordinal);

        string degradation = DocumentSection(design, "### 21.4 Graceful degradation matrix", "### 21.5 ");

        Assert.Contains(key, degradation, StringComparison.Ordinal);

        Assert.Contains("Clean Long Rest debt", degradation, StringComparison.Ordinal);

        Assert.Contains("Provider failure during a sweep", degradation, StringComparison.Ordinal);

        // The key is a real setting, and the sentence that said there was no configuration key must not return.
        Assert.Contains(
            "public bool LongRest",
            File.ReadAllText(Path.Combine(
                TestRepositoryPaths.RepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Core",
                "Configuration",
                "PublicConfigurationSettings.cs")),
            StringComparison.Ordinal);

        Assert.Contains("`features.longRest`", ReadDocument("Compendium.README.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sweep_route_is_named_in_the_endpoint_composition_the_source_maps()
    {
        const string route = "/memory/saga/long-rest/sweeps";

        string endpoints = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Api",
            "Tower",
            "LongRestEndpoints.cs"));

        Assert.Contains($"\"{route}\"", endpoints, StringComparison.Ordinal);

        string composition = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "### 4.3 `RetroDownfall.Arcanum.Api`",
            "### 4.4 ");

        Assert.Contains("/api" + route, composition, StringComparison.Ordinal);

        Assert.Contains("RequestSagaLongRestSweep", composition, StringComparison.Ordinal);

        Assert.Contains("RequestSagaLongRestSweep", endpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void The_saga_section_points_at_the_long_rest()
    {
        string saga = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "### 21.9 Saga (long-term associative memory)",
            "### 21.10 ");

        Assert.Contains("§21.16", saga, StringComparison.Ordinal);

        Assert.Contains("Arcanum:Features:LongRest", saga, StringComparison.Ordinal);
    }

    [Fact]
    public void The_design_states_that_recovery_only_parks_and_each_authority_class_readmits_its_own()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string orchestration = DocumentSection(
            design,
            "#### 21.16.3 Durable orchestration and physical authority",
            "#### 21.16.4 ");

        Assert.Contains("Recovery parks every started sweep and never runs one", orchestration, StringComparison.Ordinal);

        Assert.Contains("Parked-Is-Active", orchestration, StringComparison.Ordinal);

        Assert.Contains("ILongRunningOperationCoordinator", orchestration, StringComparison.Ordinal);

        string reconciliation = DocumentSection(design, "#### 10.8.2 Reconciliation", "#### 10.8.3 ");

        Assert.Contains("Long Rest recovery only parks (Park-and-Readmit)", reconciliation, StringComparison.Ordinal);

        Assert.Contains("Atomic Readmission", reconciliation, StringComparison.Ordinal);

        Assert.Contains("Sweep Failure Budget", reconciliation, StringComparison.Ordinal);

        Assert.Contains("One Connection Per Send", reconciliation, StringComparison.Ordinal);

        Assert.Contains("long_rest.checkpoint_invalid", reconciliation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provider_transport_is_documented_as_one_connection_per_send_and_the_source_does_that()
    {
        string factory = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Api",
            "Intelligence",
            "LongRestProviderClientFactory.cs"));

        Assert.Contains("handler.PooledConnectionLifetime = TimeSpan.Zero;", factory, StringComparison.Ordinal);

        Assert.Contains("handler.PooledConnectionIdleTimeout = TimeSpan.Zero;", factory, StringComparison.Ordinal);

        string lane = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "**Closed physical provider lane.**",
            "The process singleton `LongRestConfigurationEpoch`");

        Assert.Contains("PooledConnectionLifetime", lane, StringComparison.Ordinal);

        Assert.Contains("One Connection Per Send", lane, StringComparison.Ordinal);

        Assert.Contains("Provider connections are not reused", ReadDocument("Compendium.README.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_command_reference_keeps_the_long_rest_notes_at_the_end_of_the_operation_section()
    {
        string reference = ReadDocument("Arcanum.Command.Reference.md");

        Assert.DoesNotContain("\n### Long Rest sweep operation notes", reference, StringComparison.Ordinal);

        int operation = reference.IndexOf("\n### `arcanum operation`\n", StringComparison.Ordinal);

        int table = reference.IndexOf("| Terminal error code | Meaning | What to do |", StringComparison.Ordinal);

        int notes = reference.IndexOf("\n#### Long Rest sweep operation notes\n", StringComparison.Ordinal);

        int backup = reference.IndexOf("\n### `arcanum backup`\n", StringComparison.Ordinal);

        Assert.True(operation >= 0 && table > operation, "The terminal-code table is not filed under the operation section.");

        Assert.True(notes > table, "The Long Rest notes must follow the generic operation material, not precede it.");

        Assert.True(backup > notes, "The Long Rest notes must end the operation section.");

        string operationSection = reference[operation..backup];

        Assert.DoesNotContain("\n### ", operationSection[4..], StringComparison.Ordinal);

        string notesText = reference[notes..backup];

        Assert.Contains("`arcanum operation reconcile` parks", notesText, StringComparison.Ordinal);

        Assert.Contains("never runs one", notesText, StringComparison.Ordinal);

        Assert.Contains("`arcanum operation retry <id>` resets a sweep's failure budget", notesText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_terminal_code_table_names_every_code_a_parked_sweep_can_carry_to_an_operator()
    {
        string reference = ReadDocument("Arcanum.Command.Reference.md");

        int start = reference.IndexOf("| Terminal error code | Meaning | What to do |", StringComparison.Ordinal);

        int end = reference.IndexOf("\n\n", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "The terminal-code table could not be located.");

        string[] rows = reference[start..end].Split('\n').Where(static line => line.StartsWith("| ", StringComparison.Ordinal)).ToArray();

        List<string> missing = [];

        // Every code the fault policy knows is either a park an operator can meet, named in the table, or one of the informational
        // markers a sweep ends Abandoned with, or a refusal of a request that is never a sweep's terminal code.
        HashSet<string> notParkCodes = new(StringComparer.Ordinal)
        {
            LongRestParkCodes.Cancelled,
            LongRestParkCodes.NotStarted,
            LongRestParkCodes.StaleConfiguration,
            ErrorCodes.LongRest.SweepParked,
            ErrorCodes.LongRest.InterruptedTransport,
        };

        foreach ((string code, LongRestFaultClass fault) in LongRestFaultPolicy.KnownCodes)
        {
            if (fault == LongRestFaultClass.RestartGeneration || notParkCodes.Contains(code))
            {
                continue;
            }

            if (!rows.Any(row => RowNames(row, code)))
            {
                missing.Add(code);
            }
        }

        Assert.Empty(missing);

        // The two codes this contract gained with Park-and-Readmit, and the one an undecodable checkpoint reports.
        Assert.Contains(rows, static row => RowNames(row, LongRestParkCodes.CheckpointInvalid));

        Assert.Contains(rows, static row => RowNames(row, LongRestParkCodes.ReadmitExhausted));

        Assert.Contains(rows, static row => RowNames(row, "Any other `LongRest.*` code"));
    }

    [Fact]
    public void The_oath_records_the_discovery_and_sweep_slice_as_landed_and_the_epic_as_open()
    {
        string oath = ReadDocument("Arcanum.OATH.md");

        Assert.Contains("| **#91** | XL, landed |", oath, StringComparison.Ordinal);

        Assert.Contains("| **#93** | XL, landed |", oath, StringComparison.Ordinal);

        Assert.Contains("remain open. |", oath, StringComparison.Ordinal);

        string roadmap = DocumentSection(oath, "### 20.4 The Long Rest (#75, Epic)", "### 20.5 ");

        Assert.Contains("**Delivered.**", roadmap, StringComparison.Ordinal);

        Assert.DoesNotContain("reconciled at startup and at the end of every sweep", roadmap, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_section_8_36_names_the_park_remedies_and_what_a_busy_sweep_means()
    {
        string section = DocumentSection(
            ReadDocument("Arcanum.API.md"),
            "### 8.36 The Long Rest (`/api/memory/saga/long-rest`)",
            "*End of API reference.*");

        foreach (string expected in (string[])
                 [
                     "ReconciliationRequired",
                     "arcanum operation retry",
                     "arcanum operation cancel",
                     "LongRest.SweepParked",
                     "LongRest.TransportUnknown",
                     "LongRest.OperationBusy",
                     "long_rest.readmit_exhausted",
                     "long_rest.authorized_request_required",
                     "long_rest.stale_configuration",
                 ])
        {
            Assert.Contains(expected, section, StringComparison.Ordinal);
        }

        // Every resumable code is listed, because a reader who finds a sweep parked under one that is missing concludes it is refused.
        foreach ((string code, LongRestFaultClass fault) in LongRestFaultPolicy.KnownCodes)
        {
            if (fault == LongRestFaultClass.ResumeLater)
            {
                Assert.Contains(code, section, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_windows_lanes_that_qualify_the_long_rest_natively_are_documented_and_exist()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string workflow = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), ".github", "workflows", "ci.yml"));

        foreach (string artifact in (string[]) ["windows-long-rest-win-x64", "windows-long-rest-win-arm64"])
        {
            Assert.Contains(artifact, design, StringComparison.Ordinal);

            Assert.Contains($"name: {artifact}", workflow, StringComparison.Ordinal);
        }

        Assert.Contains("Test Long Rest", design, StringComparison.Ordinal);

        Assert.Contains("name: Test Long Rest", workflow, StringComparison.Ordinal);

        Assert.Contains("Assert the SDK is native to", design, StringComparison.Ordinal);

        Assert.Contains("ARCANUM_EXPECTED_WINDOWS_RID", design, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_guide_names_the_one_long_running_work_departure()
    {
        string engineering = ReadDocument("Arcanum.Engineering.md");

        Assert.Contains("the one departure is the Long Rest sweep", engineering, StringComparison.Ordinal);
    }

    /// <summary>Whether the first cell of a table row names <paramref name="code"/>.</summary>
    private static bool RowNames(string row, string code)
    {
        string[] cells = row.Split('|', StringSplitOptions.None);

        return cells.Length > 2 && cells[1].Contains(code, StringComparison.Ordinal);
    }

    private static string ReadDocument(string fileName) =>
        File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string DocumentSection(
        string document,
        string startHeading,
        string endHeading)
    {
        int start = document.IndexOf(startHeading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Missing documentation heading: {startHeading}");

        int end = document.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);

        Assert.True(end > start, $"Missing documentation heading after {startHeading}: {endHeading}");

        return document[start..end];
    }
}

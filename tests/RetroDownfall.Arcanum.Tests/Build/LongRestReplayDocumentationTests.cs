using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The rules that decide when a Long Rest sweep, park or completed sweep stays current, as the owning documents state them: a completed
/// sweep replays while it still describes the installation and is replaced, never refused, when it does not; Covenant availability
/// publications end nothing; and a transient provider failure spends the work unit's unknown-usage allowance.
/// </summary>
/// <remarks>
/// Each assertion names a distinctive fragment of a claim the behavior tests in <c>LongRestMaintenanceTests.CurrencyDrift</c> and
/// <c>LongRestMaintenanceTests.UnknownUsage</c> prove, so a document that drifts from them fails here with the document's name.
/// </remarks>
public sealed class LongRestReplayDocumentationTests
{
    private static readonly string[] GovernedDocuments =
    [
        "Arcanum.DESIGN.md",
        "Arcanum.API.md",
        "Arcanum.Command.Reference.md",
        "Compendium.README.md",
        "Arcanum.OATH.md",
    ];

    /// <summary>
    /// Sentences that narrate how the Long Rest used to be, or promise a calibration still to come. A governed document describes the
    /// Long Rest as it is; the one pending limitation is stated once, as a limitation, in the graceful-degradation section.
    /// </summary>
    private static readonly string[] NarratedHistory =
    [
        "initial values awaiting live calibration",
        "are calibrated against the real local models during live qualification",
        "is an initial value that is calibrated live",
        "as a candidate score has always been",
        "was never queried and is gone",
        "no longer lives out the operation's retention",
        "adopts the successor",
        "the metadata anti-join that was always there",
        "the completion verify that now precedes",
        "exactly as it always was",
        "the one every earlier recipe used",
        "The earlier alternatives",
        "policyVersion` (currently `1`)",
        "capability generations the request fence",
    ];

    /// <summary>
    /// Sentences that narrate how the hosted-producer checker, its CI lanes or their measurements came to stand where they stand, or that
    /// describe an intermediate state of the work: a root that was still missing its admission, a lane nobody had run, a row raised or
    /// lowered by a change. A governed document states the checker, the lanes and the tripwire as the delivered code has them; a
    /// measurement is dated and belongs to the tree it was taken on.
    /// </summary>
    private static readonly string[] NarratedCheckerHistory =
    [
        "Startup recovery also links revocation",
        "recovery kinds without that per-unit binding",
        "the Long Rest recovery handler mints and carries its own sweep lease",
        "neither root admits Work anywhere",
        "both roots also exceed the 8192-state cap",
        "consequences of the missing admission of those two roots",
        "measured on the shipped tree at three root workers",
        "byte-identical before and after",
        "and the open Serve, scoped-lease and keyed-construction gates",
        "The base admitted such a call",
        "the former rule accepted a group on any lease receiver",
        "across frames it did not",
        "The Effect now carries the origin of the lease",
        "rests on the Authority Carrier Ban of the admitted entry census until that lands",
        "abstractness is the guard until the checker resolves lifetime",
        "enforces the unchanged line, branch, and security thresholds",
        "excluded by this split",
        "keep the existing 1,740-second per-worker deadline",
        "The seven unrelated job names remain unchanged",
        "The primary production lane did not fit",
        "at the start of this work",
        "predate this work",
        "no CI run of the four lanes exists yet",
        "a prediction to check against the first run",
        "as the second one was",
        "Before this split",
        "measured once more at the end of the review fixes",
        "added with this work",
        "refused the 6,463 cases",
        "still stale against the discovery",
        "was seen at 3,440, 1,720 and 3,110",
        "The two Long Rest rows were raised by the change that earned the room",
        "the delivery that grew them raises their rows",
        "the same one `HOSTED_SITE_WORK_FRONTIER_MISSING` and no other diagnostic",
        "the shapes the work reshaped",
        "the group shapes the work replaced",
        "The gate stays after the asset landed",
        "The actual hosted traversal limit remains unresolved",
        "has been seen at",
        "Ceilings only move down",
        "The lane was dormant",
        "They were previously",
        "The shared state cap is unchanged",
        "This changes neither value/ref provenance demand",
        "proof limits are unchanged",
        "The audit that backs this rule",
        "the first regeneration",
        "the checker the one that rendered the old manifest",
        "measured on the delivered tree",
        "Measured on the delivered tree",
    ];

    /// <summary>
    /// The words that introduce such a sentence, refused wherever the checker, the testing contract and the CI jobs are described. Each
    /// region below is meant to be a statement about the delivered code. A closed list cannot prove a region free of narration: it keeps the
    /// sentences that were removed, and the wording that introduced them, from returning.
    /// </summary>
    private static readonly string[] NarratingWords =
    [
        " no longer ",
        "this work ",
        "this work,",
        "this work.",
        "this work)",
        "this split",
        "delivery tree",
        "delivery head",
        "at delivery",
        "at the start of",
        "until that lands",
        " was dormant",
        "were previously",
        "has been seen",
        "have been seen",
        "changes neither",
        " is unchanged",
        " are unchanged",
        "remain unchanged",
        "remains unchanged",
        "the unchanged ",
        "not yet run",
        "not yet confirmed",
        "first failed",
        "now comes from",
    ];

    [Fact]
    public void No_governed_document_narrates_the_long_rests_history_or_a_calibration_still_to_come()
    {
        List<string> offenders = [];

        foreach (string file in GovernedDocuments)
        {
            string document = ReadDocument(file);

            foreach (string phrase in NarratedHistory)
            {
                if (document.Contains(phrase, StringComparison.Ordinal))
                {
                    offenders.Add($"{file} still says \"{phrase}\"");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_governed_document_narrates_how_the_checker_its_lanes_or_its_measurements_came_to_be()
    {
        List<string> offenders = [];

        foreach (string file in GovernedDocuments)
        {
            string document = ReadDocument(file);

            foreach (string phrase in NarratedCheckerHistory)
            {
                if (document.Contains(phrase, StringComparison.Ordinal))
                {
                    offenders.Add($"{file} still says \"{phrase}\"");
                }
            }
        }

        string design = ReadDocument("Arcanum.DESIGN.md");

        (string Name, string Text)[] regions =
        [
            (
                "the hosted-producer checker",
                DocumentSection(
                    design,
                    "**Authored hosted-producer inventory (24 services).**",
                    "Closed-period ledger cleanup preserves")),
            (
                "the testing contract (13.1)",
                DocumentSection(
                    design,
                    "### 13.1 Commands and coverage authority",
                    "### 13.2 Test data, ownership, and parallel collections")),
            (
                "the CI jobs (13.8)",
                DocumentSection(
                    design,
                    "### 13.8 Exclusions, runtime budget, and CI",
                    "## 14. Extension guidelines")),
        ];

        foreach ((string name, string text) in regions)
        {
            foreach (string word in NarratingWords)
            {
                if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"Arcanum.DESIGN.md, {name}, still says \"{word.Trim()}\"");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_ci_section_lists_exactly_the_jobs_ci_yml_defines()
    {
        string workflow = File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        string[] jobNames =
        [
            .. workflow
                .Split('\n')
                .Where(static line => line.StartsWith("    name: ", StringComparison.Ordinal))
                .Select(static line => line["    name: ".Length..].Trim()),
        ];

        // Steps carry a name too, at a deeper indent; a job's name sits directly under its id.
        Assert.Equal(13, jobNames.Length);

        string design = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "### 13.1 Commands and coverage authority",
            "### 13.2 Test data, ownership, and parallel collections");

        Assert.Contains("`ci.yml` defines thirteen jobs:", design, StringComparison.Ordinal);

        foreach (string name in jobNames)
        {
            Assert.Contains($"`{name}`", design, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_replay_rule_and_the_covenant_publication_rule_are_stated_where_a_reader_is_told_a_configuration_edit_starts_a_new_generation()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains("**Completed replay.**", design, StringComparison.Ordinal);

        Assert.Contains("**Capability currency.**", design, StringComparison.Ordinal);

        Assert.Contains("LongRestCapabilityState.Of(availability)", design, StringComparison.Ordinal);

        Assert.Contains("falls through to admission and creates a new generation", design, StringComparison.Ordinal);

        string api = DocumentSection(
            ReadDocument("Arcanum.API.md"),
            "### 8.36 The Long Rest (`/api/memory/saga/long-rest`)",
            "*End of API reference.*");

        Assert.Contains("**Completed replay.**", api, StringComparison.Ordinal);

        Assert.Contains("**Covenant publications.**", api, StringComparison.Ordinal);

        Assert.Contains("is not refused: it admits a new generation", api, StringComparison.Ordinal);

        string reference = ReadDocument("Arcanum.Command.Reference.md");

        Assert.Contains("A completed sweep replays only while it still describes the installation", reference, StringComparison.Ordinal);

        Assert.Contains("Covenant availability republications end neither a sweep nor a park", reference, StringComparison.Ordinal);

        string compendium = ReadDocument("Compendium.README.md");

        Assert.Contains("a completed sweep stops replaying", compendium, StringComparison.Ordinal);

        Assert.Contains("Covenant availability republications", compendium, StringComparison.Ordinal);
    }

    [Fact]
    public void The_comparison_rules_of_a_retained_fence_are_stated_and_the_restart_limitation_they_ended_is_not()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains("`LongRestFenceCurrency.Describes`", design, StringComparison.Ordinal);

        Assert.Contains("`LongRestFenceCurrency.CanResume`", design, StringComparison.Ordinal);

        Assert.Contains("the highest sequence the table has ever issued", design, StringComparison.Ordinal);

        Assert.Contains("a setting changed and changed back is the same setting", design, StringComparison.Ordinal);

        Assert.Contains("A change ends a sweep only if a check observes it", design, StringComparison.Ordinal);

        Assert.Contains("a request that raced the completion of its replacement joins that replacement", design, StringComparison.Ordinal);

        Assert.Contains("is retired by the next Session turn that carries live authority and replaced by a new generation", design, StringComparison.Ordinal);

        string api = ReadDocument("Arcanum.API.md");

        Assert.Contains("A setting changed and changed back is the same configuration value", api, StringComparison.Ordinal);

        Assert.Contains("unless its captured generation can no longer resume", api, StringComparison.Ordinal);

        Assert.Contains("the same admission, authority and Covenant capability state", api, StringComparison.Ordinal);

        string reference = ReadDocument("Arcanum.Command.Reference.md");

        Assert.Contains("A setting changed and then changed back is the same setting", reference, StringComparison.Ordinal);

        Assert.Contains("can no longer be readmitted, so the next Session turn that needs it retires it", reference, StringComparison.Ordinal);

        Assert.Contains("leaves a completed sweep replaying", ReadDocument("Compendium.README.md"), StringComparison.Ordinal);

        foreach (string file in GovernedDocuments)
        {
            string document = ReadDocument(file);

            Assert.DoesNotContain("until the host restarts (a new boot readmits across all of them)", document, StringComparison.Ordinal);

            Assert.DoesNotContain("because only a new boot readmits across such a change", document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_document_that_describes_a_transient_provider_failure_says_it_spends_the_unknown_usage_allowance()
    {
        string reference = ReadDocument("Arcanum.Command.Reference.md");

        string row = Assert.Single(
            reference.Split('\n'),
            static line => line.StartsWith("| `LongRest.ProviderUnavailable` / `LongRest.ProviderUnreachable`", StringComparison.Ordinal));

        Assert.Contains("one of the two unknown-usage attempts a work unit is allowed", row, StringComparison.Ordinal);

        Assert.Contains("`LongRest.TransportUnknown` whatever readmissions remain", row, StringComparison.Ordinal);

        Assert.Contains("`ProviderUnreachable` sent nothing and is not counted", row, StringComparison.Ordinal);

        Assert.Contains("is one of the work unit's two unknown-usage attempts", reference, StringComparison.Ordinal);

        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains("each `ProviderUnavailable` failure (and each `ProviderRejected` one) is also one of a work unit's two unknown-usage attempts", design, StringComparison.Ordinal);

        Assert.Contains("a transient `LongRest.ProviderUnavailable` failure counts as one", design, StringComparison.Ordinal);

        string compendium = ReadDocument("Compendium.README.md");

        Assert.Contains("each `LongRest.ProviderUnavailable` failure is also one of the two unknown-usage attempts", compendium, StringComparison.Ordinal);

        Assert.Contains(
            "each `LongRest.ProviderUnavailable` failure and each `LongRest.ProviderRejected` one is one unknown-usage attempt",
            ReadDocument("Arcanum.API.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_operator_rows_state_what_resets_the_budget_what_a_protected_sweep_carries_and_what_the_doctor_counts()
    {
        string reference = ReadDocument("Arcanum.Command.Reference.md");

        string exhausted = Assert.Single(
            reference.Split('\n'),
            static line => line.StartsWith("| `long_rest.readmit_exhausted`", StringComparison.Ordinal));

        Assert.Contains("without an accepted adjudication unit or an advanced discovery cursor", exhausted, StringComparison.Ordinal);

        string authorized = Assert.Single(
            reference.Split('\n'),
            static line => line.StartsWith("| `long_rest.authorized_request_required`", StringComparison.Ordinal));

        Assert.Contains("for every resumable fault", authorized, StringComparison.Ordinal);

        Assert.DoesNotContain("for every event that parks a clean one", authorized, StringComparison.Ordinal);

        Assert.Contains("the doctor counts every `Abandoned` operation", reference, StringComparison.Ordinal);

        Assert.Contains(
            "`2` for one a protected sweep wrote",
            ReadDocument("Arcanum.API.md"),
            StringComparison.Ordinal);

        Assert.Contains(
            "Retrying a sweep that a newer sweep has replaced leaves a `ReconciliationRequired` operation that nothing readmits",
            ReadDocument("Arcanum.API.md"),
            StringComparison.Ordinal);

        Assert.Contains("scoped read-only agent recall", ReadDocument("ArcanumOATH.Human.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_advertised_model_is_named_wherever_the_chat_model_is_resolved()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains("and the first model any configured provider advertises when neither is set", design, StringComparison.Ordinal);

        Assert.Contains("otherwise `DefaultModel`, otherwise the first advertised model", design, StringComparison.Ordinal);
    }

    [Fact]
    public void The_attachment_source_routes_name_the_campaign_they_ask_and_the_mode_they_honour()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains("put the same question to a Campaign chosen the same way (`AttachmentSourceSanctum`)", design, StringComparison.Ordinal);

        Assert.Contains("link that lies inside a Campaign's root and leaves it is still that Campaign's read", design, StringComparison.Ordinal);

        Assert.Contains("a source that no claim names is unrestricted", design, StringComparison.Ordinal);

        Assert.Contains("`ICampaignRepository.GetBySpelledPathAsync`", design, StringComparison.Ordinal);

        Assert.Contains("however the root is reached", design, StringComparison.Ordinal);

        Assert.Contains("`attachment_reference`, `attachment_refresh`, or `refresh_session_file`", design, StringComparison.Ordinal);

        Assert.Contains("and they take no Sanctum decision: a poll that recorded a breach for every refused source would fill the bounded breach history", design, StringComparison.Ordinal);

        Assert.DoesNotContain("The Campaign it asks is the Session's stored `CampaignId` alone", design, StringComparison.Ordinal);

        Assert.DoesNotContain("it never reads the Campaign's mode", design, StringComparison.Ordinal);

        string api = ReadDocument("Arcanum.API.md");

        Assert.Contains("The governing Campaign is the stricter of the Session's Campaigns", api, StringComparison.Ordinal);

        Assert.Contains("neither mode lets through a Sanctum that cannot be read or a breach that cannot be recorded", api, StringComparison.Ordinal);

        Assert.Contains("Its Sanctum decision is the live reference's", api, StringComparison.Ordinal);

        Assert.Contains("the root that contains the directory it stands in with its links resolved", api, StringComparison.Ordinal);

        Assert.Contains("with its breach recorded under the surface `attachment_refresh`", api, StringComparison.Ordinal);

        Assert.Contains("takes no Sanctum decision (DESIGN §11.15)", api, StringComparison.Ordinal);
    }

    private static string ReadDocument(string fileName) =>
        File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string DocumentSection(string document, string startHeading, string endHeading)
    {
        int start = document.IndexOf(startHeading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Missing documentation heading: {startHeading}");

        int end = document.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);

        Assert.True(end > start, $"Missing documentation heading after {startHeading}: {endHeading}");

        return document[start..end];
    }
}

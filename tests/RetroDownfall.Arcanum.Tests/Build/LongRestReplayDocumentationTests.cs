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
    public void The_attachment_reference_route_names_the_campaign_it_asks()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains("The Campaign it asks is the Session's stored `CampaignId` alone", design, StringComparison.Ordinal);
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

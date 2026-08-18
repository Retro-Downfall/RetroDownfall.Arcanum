using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Every model-directed child launched through <c>CappedChildProcessRunner</c> must be told which
/// variables the operator declared as Arcanum's secrets.
/// </summary>
/// <remarks>
/// The <c>ARCANUM_</c> prefix scrub only covers the derived default names. A provider key the operator
/// legitimately pointed at <c>MY_OPENAI_KEY</c> — or the HTTPS certificate password, the Comm Link
/// webhook URL, the web-research key, the A2A outbound credential — carries no prefix, so without the
/// declared-name list it is inherited by every child the model can direct. The list exists and the
/// scrubber applies it correctly; the failure this suite prevents is the argument going missing at a
/// call site, which no behaviour test of the scrubber can see.
///
/// <para>An inventory assertion over production source rather than a behaviour test, because the
/// defect is a call site that stopped passing an optional argument, not a wrong result. The
/// <c>WorkspaceCheck</c> and <c>McpChild</c> profiles are deliberately absent: both rebuild the child
/// environment from scratch rather than scrubbing an inherited one, so there is nothing for the list
/// to remove. <c>FamiliarProcessRunner</c> is likewise absent because it never routes through
/// <c>RunAsync</c> — it scrubs the same names inline from <c>request.DeniedEnvironmentVariables</c>,
/// which <c>FamiliarSecretEnvironmentNames.Collect</c> builds.</para>
///
/// <para>Scoped to every authored production source, not to the Infrastructure project alone. The
/// runner is Infrastructure's, but the callers are not: <c>run_spell_script</c> lives in the Api
/// project, and an inventory that only looked where the runner lives declared the wiring complete
/// while the spell-script child still inherited every operator-declared credential.</para>
/// </remarks>
public sealed class ChildProcessSecretScrubCallSiteTests
{

    private const string RunnerCall = "CappedChildProcessRunner.RunAsync";

    private const string DeclaredNamesArgument = "operatorDeclaredSecretEnvironmentVariables";

    private const string CollectCall = "FamiliarSecretEnvironmentNames.Collect";

    /// <summary>
    /// The one call site that legitimately does not build the list itself: the tool is constructed
    /// per turn by <c>WizardIntelligenceProvider</c> and receives the names as a constructor
    /// argument, which <see cref="The_spell_script_composition_site_sources_the_names_from_configuration"/>
    /// pins to the configured source.
    /// </summary>
    private const string NamesReceivedByConstructor = "ArcanumSpellScriptTool.cs";

    [Theory]
    [InlineData("ChildProcessEnvironmentProfile.ToolExec")]
    [InlineData("ChildProcessEnvironmentProfile.SpellScript")]
    public void Every_scrubbed_profile_call_site_passes_the_operator_declared_secret_names(string profile)
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => source.Names(RunnerCall) && source.Names(profile))
                .Where(static source => !source.Names(DeclaredNamesArgument))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            $"A {profile} child inherits every operator-declared secret variable unless the call site "
            + $"passes {DeclaredNamesArgument}, and the prefix scrub does not cover names the operator "
            + "chose. Thread FamiliarSecretEnvironmentNames.Collect(settings) through: "
            + string.Join(", ", offenders));

    }

    /// <summary>
    /// The scrub is only as good as the list, so the list has to be the configured one rather than a
    /// literal a call site invented.
    /// </summary>
    [Fact]
    public void Those_call_sites_source_the_names_from_configuration()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names(RunnerCall) && source.Names(DeclaredNamesArgument))
                .Where(static source => !source.Is(NamesReceivedByConstructor))
                .Where(static source => !source.Names(CollectCall))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "The declared-secret names are configuration, not a constant: every name in "
            + "Arcanum:Providers:*:CredentialEnvironmentVariable and its four siblings has to reach the "
            + "child scrub. Build the list with FamiliarSecretEnvironmentNames.Collect in: "
            + string.Join(", ", offenders));

    }

    /// <summary>
    /// The spell-script tool takes the names as a constructor argument, so its scrub is only as good
    /// as what the composition site hands it.
    /// </summary>
    /// <remarks>
    /// Without this, the previous assertion's exemption would be a hole: a composition site could
    /// construct the tool with no names at all, or with a hand-written literal, and every file
    /// involved would still satisfy the other two checks.
    /// </remarks>
    [Fact]
    public void The_spell_script_composition_site_sources_the_names_from_configuration()
    {

        List<ProductionSource> compositionSites =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names("new ArcanumSpellScriptTool(")),
        ];

        Assert.NotEmpty(compositionSites);

        List<string> offenders =
        [
            .. compositionSites
                .Where(static source => !source.Names(CollectCall))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "A spell-script child is scrubbed of the operator's declared secrets only when the site "
            + "that constructs the tool builds that list with " + CollectCall + ": "
            + string.Join(", ", offenders));

    }

}

using RetroDownfall.Arcanum.Infrastructure.Familiars;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// Resolution is the one place a Familiar's configured command becomes a path, and the runner now
/// spawns nothing else. What it picks therefore has to be a file the OS can actually start — a
/// resolution the spawn rejects is worse than no resolution at all, because the probe reports the
/// CLI as installed and every turn then fails with an error the operator cannot act on.
/// </summary>
public sealed class FamiliarExecutableResolverTests
{

    /// <summary>
    /// <c>npm i -g @anthropic-ai/claude-code</c> writes three files into the npm prefix on Windows:
    /// <c>claude.cmd</c>, <c>claude.ps1</c>, and an extensionless <c>claude</c> that is an sh script.
    /// Only the first is startable through <c>CreateProcess</c> with <c>UseShellExecute = false</c>,
    /// so the PATHEXT variants have to be tried before the bare name — otherwise the sh shim wins
    /// resolution inside that same directory and the Familiar can never run.
    /// </summary>
    [Fact]
    public void On_windows_the_pathext_variants_are_tried_before_the_extensionless_shim()
    {

        string[] candidates =
        [
            .. FamiliarExecutableResolver.CandidateNames(
                "claude",
                windows: true,
                pathExt: ".COM;.EXE;.BAT;.CMD"),
        ];

        Assert.Equal(["claude.COM", "claude.EXE", "claude.BAT", "claude.CMD", "claude"], candidates);

    }

    [Fact]
    public void An_absent_pathext_still_prefers_the_extensions_windows_would_use()
    {

        string[] candidates =
        [
            .. FamiliarExecutableResolver.CandidateNames("codex", windows: true, pathExt: null),
        ];

        Assert.Equal("codex", candidates[^1]);

        Assert.Contains("codex.CMD", candidates);

        Assert.True(
            Array.IndexOf(candidates, "codex.CMD") < Array.IndexOf(candidates, "codex"),
            "A .CMD install must outrank the extensionless shim.");

    }

    /// <summary>Off Windows there is no PATHEXT, and the command is the file name.</summary>
    [Fact]
    public void Off_windows_the_command_is_the_only_candidate()
    {

        string[] candidates =
        [
            .. FamiliarExecutableResolver.CandidateNames("claude", windows: false, pathExt: ".EXE"),
        ];

        Assert.Equal(["claude"], candidates);

    }

    /// <summary>An operator override that names a path is taken literally, never searched.</summary>
    [Fact]
    public void An_operator_override_containing_a_separator_resolves_to_that_exact_file()
    {

        using StubFamiliarCli stub = StubFamiliarCli.Create([]);

        Assert.True(FamiliarExecutableResolver.TryResolve(stub.FileName, out string? resolved));

        Assert.Equal(Path.GetFullPath(stub.FileName), resolved);

    }

    [Fact]
    public void A_command_nothing_on_disk_answers_is_reported_unresolved()
    {

        Assert.False(
            FamiliarExecutableResolver.TryResolve(StubFamiliarCli.MissingExecutablePath(), out string? resolved));

        Assert.Null(resolved);

    }

}

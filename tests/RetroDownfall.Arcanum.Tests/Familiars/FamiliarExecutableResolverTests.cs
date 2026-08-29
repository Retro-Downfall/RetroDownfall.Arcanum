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

        // A path this test builds itself, rather than StubFamiliarCli.FileName. The stub's FileName
        // is only path-shaped on Unix: on Windows it is the bare name `powershell.exe` with the .ps1
        // in its arguments, because a script is not directly startable there. So the stub fed this
        // test the PATH branch on Windows and the separator branch it exists to pin never ran.
        string directory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-familiar-override-" + Guid.NewGuid().ToString("N"));

        _ = Directory.CreateDirectory(directory);

        try
        {

            // Existence is the whole of the separator branch's test — it takes the path literally
            // and never consults PATH or PATHEXT — so the file's contents and mode do not matter.
            string overridePath = Path.Combine(directory, "claude");

            File.WriteAllText(overridePath, string.Empty);

            Assert.True(FamiliarExecutableResolver.TryResolve(overridePath, out string? resolved));

            Assert.Equal(Path.GetFullPath(overridePath), resolved);

        }
        finally
        {

            try
            {

                Directory.Delete(directory, recursive: true);

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {

                // A leftover temp directory is not worth failing a test over, and on Windows a
                // scanner holding the file we just wrote is the ordinary way this refuses. The same
                // guard is on StubFamiliarCli.Dispose.

            }

        }

    }

    [Fact]
    public void A_command_nothing_on_disk_answers_is_reported_unresolved()
    {

        Assert.False(
            FamiliarExecutableResolver.TryResolve(StubFamiliarCli.MissingExecutablePath(), out string? resolved));

        Assert.Null(resolved);

    }

}

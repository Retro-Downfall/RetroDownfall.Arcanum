using System.Diagnostics;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Spell_parent_pivot_during_hydration_never_discloses_external_content_to_provider(bool explicitPath, bool warmCache)
    {
        await CreateSpellAsync("contained", "Contained", dependencies: null, body: "safe spell body");

        string directory = Path.Combine(_workspace.Root, "contained");

        string spellPath = Path.Combine(directory, "SPELL.md");

        if (warmCache)
        {
            Assert.NotNull(await SpellScanner.LoadFullAsync(spellPath, _workspace.Root, CancellationToken.None, 262144));
        }

        string external = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(external);

        await File.WriteAllTextAsync(Path.Combine(external, "SPELL.md"), "---\nname: Contained\n---\nEXTERNAL BODY SECRET");

        await File.WriteAllTextAsync(Path.Combine(external, "SPELL.json"), "{\"name\":\"Contained\",\"description\":\"EXTERNAL SIDECAR SECRET\"}");

        ScriptingChatClient chat = new();

        chat.EnqueueText("safe answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        int bodyOpens = 0;

        int sidecarReads = 0;

        bool pivoted = false;

        void PivotParent()
        {
            Directory.Move(directory, directory + "-original");

            if (OperatingSystem.IsWindows())
            {
                // A junction exercises the real reparse-point escape without requiring symlink privilege.
                using System.Diagnostics.Process junction = System.Diagnostics.Process.Start(new ProcessStartInfo(
                    Path.Combine(System.Environment.SystemDirectory, "cmd.exe"),
                    $"/d /c mklink /J \"{directory}\" \"{external}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                })!;

                if (!junction.WaitForExit(30000))
                {
                    junction.Kill(entireProcessTree: true);

                    Assert.Fail("The directory-junction setup did not finish.");
                }

                Assert.True(junction.ExitCode == 0, junction.StandardError.ReadToEnd());
            }
            else
            {
                Directory.CreateSymbolicLink(directory, external);
            }

            pivoted = true;
        }

        SecureFileReader.AfterRegularFileOpenedForTests = path =>
        {
            // Named routing first reads metadata; explicit path routing goes straight to hydration.
            if (path == spellPath && ++bodyOpens == (explicitPath ? 1 : 2)
                && !OperatingSystem.IsWindows())
            {
                PivotParent();
            }
        };

        int containmentChecks = 0;

        if (OperatingSystem.IsWindows())
        {
            // Windows can prevent renaming a directory once a child handle is open. Pivot at the
            // hydration containment check instead: after explicit-path validation or named metadata.
            WorkspacePathPolicy.ContainmentCheckObserverForTests = path =>
            {
                if (!pivoted && path == spellPath
                    && (explicitPath ? ++containmentChecks == 2 : bodyOpens > 0))
                {
                    PivotParent();
                }
            };
        }

        SecureFileReader.AfterOpenForTests = path =>
        {
            if (pivoted && path.EndsWith("SPELL.json", StringComparison.Ordinal))
            {
                sidecarReads++;
            }
        };

        try
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                BaseRequest() with
                {
                    Prompt = "cast",
                    WorkingDirectory = _workspace.Root,
                    OverrideSpellPath = explicitPath ? spellPath : null,
                    OverrideSpellName = explicitPath ? null : "Contained",
                    SkipSpellRouting = false,
                },
                InvocationContexts.AttendedSession(),
                CancellationToken.None);

            Assert.True(pivoted);

            FileSystemInfo? target = Directory.ResolveLinkTarget(directory, returnFinalTarget: true);

            Assert.NotNull(target);

            Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(external, out FileHandleIdentity expectedTarget));

            Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(target.FullName, out FileHandleIdentity resolvedTarget));

            Assert.Equal(expectedTarget, resolvedTarget);

            Assert.False(WorkspacePathPolicy.IsPathUnderWorkspace(_workspace.Root, target.FullName));

            if (explicitPath)
            {
                Assert.True(result.IsFailure);

                Assert.Equal("Validation.SpellOverride", result.Error.Code);

                Assert.Empty(chat.AllBufferedCalls);
            }
            else
            {
                Assert.True(result.IsSuccess);

                Assert.NotEmpty(chat.AllBufferedCalls);
            }

            Assert.Equal(0, sidecarReads);

            Assert.All(chat.AllBufferedCalls.SelectMany(call => call), message =>
            {
                Assert.DoesNotContain("EXTERNAL BODY SECRET", message.Text ?? string.Empty, StringComparison.Ordinal);

                Assert.DoesNotContain("EXTERNAL SIDECAR SECRET", message.Text ?? string.Empty, StringComparison.Ordinal);
            });
        }
        finally
        {
            SecureFileReader.AfterRegularFileOpenedForTests = null;

            WorkspacePathPolicy.ContainmentCheckObserverForTests = null;

            SecureFileReader.AfterOpenForTests = null;

            if (pivoted)
            {
                Directory.Delete(directory);
            }

            Directory.Delete(external, recursive: true);
        }
    }
}

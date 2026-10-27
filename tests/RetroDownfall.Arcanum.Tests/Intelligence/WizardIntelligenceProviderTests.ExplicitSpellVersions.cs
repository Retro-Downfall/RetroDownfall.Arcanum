using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData("oversize", false)]
    [InlineData("oversize", true)]
    [InlineData("frontmatter", false)]
    [InlineData("frontmatter", true)]
    [InlineData("valid", false)]
    [InlineData("valid", true)]
    public async Task Explicit_spell_version_scanner_refusal_never_runs_a_spell_less_turn(string kind, bool streaming)
    {
        await CreateSpellAsync("versioned", "Versioned", dependencies: null, body: "ACTIVE_MUST_NOT_REPLACE_VERSION");

        string path = Path.Combine(_workspace.Root, "versioned", "SPELL.v2.md");

        string description = kind == "frontmatter" ? "---PRIVATE_FRONTMATTER" : "explicit version";

        string body = kind == "oversize" ? new string('x', 2 * 1024 * 1024) : "VALID_VERSION_BODY";

        await File.WriteAllTextAsync(path, $"---\nname: Versioned\ndescription: {description}\n---\n{body}");

        bool valid = kind == "valid";

        Assert.Equal(valid, await SpellScanner.LoadFullAsync(path, _workspace.Root, CancellationToken.None, ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes()) is not null);

        ScriptingChatClient chat = new();

        if (streaming)
        {
            chat.EnqueueStreamTokens("controlled answer");
        }
        else
        {
            chat.EnqueueText("controlled answer");
        }

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        PingRequest request = BaseRequest() with
        {
            Prompt = "cast the explicit version",
            WorkingDirectory = _workspace.Root,
            OverrideSpellPath = path,
            SkipSpellRouting = false,
            DisableMcpTools = true,
            DisableAllTools = true,
        };

        if (streaming)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            if (valid)
            {
                Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);

                Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);
            }
            else
            {
                IntelligenceEvent refused = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Error);

                Assert.Equal("Validation.SpellOverride", refused.Data);

                Assert.DoesNotContain(path, refused.Message);

                Assert.DoesNotContain("PRIVATE_FRONTMATTER", refused.Message);

                Assert.DoesNotContain(frames, static frame => frame.Type is IntelligenceEventType.Token or IntelligenceEventType.Result);
            }
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.Equal(valid, result.IsSuccess);

            if (!valid)
            {
                Assert.Equal("Validation.SpellOverride", result.Error.Code);

                Assert.DoesNotContain(path, result.Error.Message);

                Assert.DoesNotContain("PRIVATE_FRONTMATTER", result.Error.Message);
            }
        }

        Assert.Equal(valid && !streaming ? 1 : 0, chat.BufferedCallCount);

        Assert.Equal(valid && streaming ? 1 : 0, chat.AllStreamingCalls.Count);

        if (valid)
        {
            Assert.Contains(chat.AllBufferedCalls.Concat(chat.AllStreamingCalls).SelectMany(static messages => messages),
                static message => message.Text?.Contains("VALID_VERSION_BODY", StringComparison.Ordinal) == true);
        }
    }
}

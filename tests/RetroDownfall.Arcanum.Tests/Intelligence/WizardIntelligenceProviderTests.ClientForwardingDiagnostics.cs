using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deliberate_client_tool_forwarding_is_debug_count_only_across_turns(bool streaming)
    {
        const string privateName = "PRIVATE_TOOL_IDENTIFIER";

        const string privatePrompt = "PRIVATE_PROMPT_SHOULD_NOT_APPEAR_IN_DIAGNOSTIC";

        var configuration = DefaultSettings();

        configuration.Features.ClientTools = true;

        ScriptingChatClient chat = new();

        FakeMcpConnectionManager mcp = new();

        TestCapturingLogger<WizardIntelligenceProvider> logger = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, mcp: mcp, logger: logger);

        for (int turn = 0; turn < 3; turn++)
        {
            if (streaming)
            {
                chat.EnqueueStreamTokens("controlled answer");
            }
            else
            {
                chat.EnqueueText("controlled answer");
            }

            var request = BaseRequest() with
            {
                Prompt = privatePrompt,
                SkipSpellRouting = true,
                DisableMcpTools = true,
                ForwardClientTools = true,
                ToolPolicy = ToolPolicy.AllTools,
                ClientTools = [new OpenAiToolDefinition("function", new OpenAiFunctionDefinition(privateName))],
            };

            if (streaming)
            {
                List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

                Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);
            }
            else
            {
                var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

                Assert.True(result.IsSuccess, result.Error.Message);
            }

            Assert.Contains(privateName, ToolNames(chat.LastChatOptions));
        }

        TestLogEntry[] forwarding = logger.Entries.Where(static entry =>
            entry.Message.Contains("client-supplied tools", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("client-supplied tool definitions", StringComparison.OrdinalIgnoreCase)).ToArray();

        Assert.Equal(3, forwarding.Length);

        Assert.All(forwarding, entry =>
        {
            Assert.Equal(LogLevel.Debug, entry.Level);

            Assert.Null(entry.Exception);

            Assert.DoesNotContain(privateName, entry.Message);

            Assert.DoesNotContain(privatePrompt, entry.Message);

            Assert.Contains(entry.Properties!, static pair => pair.Key == "ToolCount" && pair.Value is int count && count == 1);
        });

        Assert.Equal(0, mcp.GetAvailableToolsCallCount);

        Assert.Equal(streaming ? 0 : 3, chat.BufferedCallCount);

        Assert.Equal(streaming ? 3 : 0, chat.StreamingCallCount);
    }
}

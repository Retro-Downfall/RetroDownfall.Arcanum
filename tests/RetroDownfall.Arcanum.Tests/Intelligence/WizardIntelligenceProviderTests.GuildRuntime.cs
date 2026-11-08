using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    private sealed class UnknownGuildInvocation : IGuildInvocationBinding;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_wizard_entry_refuses_unknown_binding_without_ambient(bool streaming)
    {
        ScriptingChatClient chat = new();

        var wizard = CreateWizard(chat);

        var invocation = ArcanumInvocationContext.ForGuild(new UnknownGuildInvocation());

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (var frame in wizard.StreamPromptAsync(BaseRequest(), invocation, default)) frames.Add(frame);

            Assert.Contains(frames, frame => frame.Type == IntelligenceEventType.Error && frame.Data == ErrorCodes.Guild.AuthorityUnavailable);
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(BaseRequest(), invocation, default);

            Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, result.Error.Code);
        }

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);
    }

    internal async Task<(IGuildLocalInferenceRuntime Runtime, Func<int> Calls, Func<IReadOnlyList<ChatMessage>> Messages,
        Func<ChatOptions?> Options)> CreateGuildRuntimeAsync(BudgetReservationService daily,
            ArcanumSettings? settings = null, IChatClientFactory? factory = null, IGuildLocalChildToolDispatcher? guildChildren = null)
    {
        await InitializeAsync();

        ScriptingChatClient chat = new();

        chat.EnqueueResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, "qualified answer"))
        {
            Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3, TotalTokenCount = 10 },
        });

        var wizard = CreateWizard(chat, settings, factory: factory, budgetReservationService: daily, guildChildren: guildChildren);

        return (Assert.IsAssignableFrom<IGuildLocalInferenceRuntime>(wizard), () => chat.BufferedCallCount,
            () => chat.LastBufferedMessages, () => chat.LastChatOptions);
    }
}

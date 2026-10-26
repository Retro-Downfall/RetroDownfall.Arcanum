using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class ModelCallExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TapestryChat_RefusesDirectProviderWithoutSourceValidator(bool streaming)
    {
        var provider = CacheProvider();

        var executor = CreateAccountedExecutor(provider);

        ScriptingChatClient chat = new("answer");

        using IDisposable observation = TapestryBuildContext.Enter(new(
            new(new(TapestryScopeKind.Workspace, "/repo"), 1, 0, new(1, "source", false)),
            provider.Name, "gpt-5", "local", "embedding"));

        var context = new ModelCallContext(provider, "gpt-5", 0, 0);

        if (streaming)
        {
            var updates = new List<ModelCallUpdate>();

            await foreach (var update in executor.ExecuteStreamingAsync(chat, [new(ChatRole.User, "source")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context)) updates.Add(update);

            Assert.Contains(updates, update => update is ModelCallFailureUpdate);
        }
        else
        {
            var result = await executor.ExecuteBufferedAsync(chat, [new(ChatRole.User, "source")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context);

            Assert.False(result.IsSuccess);
        }

        Assert.Equal(0, chat.CallCount);
    }
}

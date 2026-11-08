using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Guild;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class ModelCallExecutorTests
{
    private sealed class UnrecognizedGuildBinding : IGuildInvocationBinding;

    [Fact]
    public async Task Guild_physical_provider_entry_refuses_unknown_binding_even_without_ambient()
    {
        var provider = CacheProvider();

        var executor = CreateAccountedExecutor(provider);

        ScriptingChatClient chat = new("must not send");

        var result = await executor.ExecuteBufferedAsync(chat, [new(ChatRole.User, "unbound")], new(),
            UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default,
            new ModelCallContext(provider, "gpt-5", 0, 0) { GuildBinding = new UnrecognizedGuildBinding() });

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, result.Error.Code);

        Assert.Equal(0, chat.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_physical_provider_entry_requires_explicit_host_binding(bool streaming)
    {
        await using var fixture = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        var execution = await fixture.RootAsync();

        var provider = CacheProvider();

        var executor = CreateAccountedExecutor(provider);

        ScriptingChatClient chat = new("must not send");

        using var invocation = GuildMemoryInvocationAmbient.Begin(new(fixture.Authority, execution));

        ModelCallContext context = new(provider, "gpt-5", 0, 0);

        if (streaming)
        {
            List<ModelCallUpdate> frames = [];

            await foreach (var frame in executor.ExecuteStreamingAsync(chat, [new(ChatRole.User, "unbound")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context)) frames.Add(frame);

            Assert.Contains(frames, frame => frame is ModelCallFailureUpdate failure && failure.Error.Code == ErrorCodes.Guild.AuthorityUnavailable);
        }
        else
        {
            var result = await executor.ExecuteBufferedAsync(chat, [new(ChatRole.User, "unbound")], new(),
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context);

            Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, result.Error.Code);
        }

        Assert.Equal(0, chat.CallCount);
    }
}

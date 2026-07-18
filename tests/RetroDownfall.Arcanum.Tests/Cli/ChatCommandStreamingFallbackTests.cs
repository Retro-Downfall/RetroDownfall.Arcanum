using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.UX;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ChatCommandStreamingFallbackTests
{

    [Fact]
    public void ShouldUseLiveLayout_false_when_noninteractive()
    {

        TestConsole console = new TestConsole().Width(120).Height(40);

        FakeCliEnvironment env = new(interactive: false, colorEnabled: true);

        Assert.False(ChatCommand.ShouldUseLiveLayout(env, console));

    }

    [Fact]
    public void ShouldUseLiveLayout_false_when_narrow_width()
    {

        TestConsole console = new TestConsole().Width(80).Height(40);

        FakeCliEnvironment env = new(interactive: true, colorEnabled: true);

        Assert.False(ChatCommand.ShouldUseLiveLayout(env, console));

    }

    [Fact]
    public void ShouldUseLiveLayout_requires_color_enabled()
    {

        TestConsole console = new TestConsole().Width(120).Height(40);

        FakeCliEnvironment env = new(interactive: true, colorEnabled: false);

        Assert.False(ChatCommand.ShouldUseLiveLayout(env, console));

        FakeCliEnvironment colorOn = new(interactive: true, colorEnabled: true);

        Assert.True(ChatCommand.ShouldUseLiveLayout(colorOn, console));

    }

    private sealed class FakeCliEnvironment : ICliEnvironment
    {

        public FakeCliEnvironment(bool interactive, bool colorEnabled)
        {

            IsInteractive = interactive;

            ColorEnabled = colorEnabled;

            ShouldShowManaBar = interactive;

        }

        public bool IsInteractive { get; }

        public bool ColorEnabled { get; }

        public bool ShouldShowManaBar { get; }

    }

}

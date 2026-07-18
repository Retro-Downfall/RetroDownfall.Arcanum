using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Mcp;
using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ChatLayoutRendererTests
{

    [Fact]
    public void Build_returns_layout_with_header_body_sidebar_sections()
    {

        Layout root = ChatLayoutRenderer.Build(CreateContext());

        Assert.NotNull(root.GetLayout("header"));

        Assert.NotNull(root.GetLayout("main"));

        Assert.NotNull(root.GetLayout("body"));

        Assert.NotNull(root.GetLayout("sidebar"));

        Assert.NotNull(root.GetLayout("assistant"));

        Assert.NotNull(root.GetLayout("diagnostics"));

        Assert.NotNull(root.GetLayout("mcp"));

    }

    [Fact]
    public void Body_shows_assistant_streaming_text_when_generating()
    {

        TestConsole console = new TestConsole().Width(160).Height(50);

        Layout root = ChatLayoutRenderer.Build(CreateContext(
            assistantText: "Hello from the stream",
            generating: true));

        console.Write(root);

        Assert.Contains("Hello from the stream", console.Output, StringComparison.Ordinal);

        // Empty + generating uses the thinking placeholder (panel headers truncate under layout width).
        TestConsole thinkingConsole = new TestConsole().Width(160).Height(50);

        thinkingConsole.Write(ChatLayoutRenderer.Build(CreateContext(
            assistantText: string.Empty,
            generating: true)));

        Assert.Contains("thinking", thinkingConsole.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Body_shows_capped_tool_diagnostics_separate_from_assistant_text()
    {

        List<ToolDiagnosticLine> diagnostics = new();

        for (int i = 0; i < 10; i++)
        {

            diagnostics.Add(ToolDiagnosticLine.Create(
                $"tool_{i}",
                ToolDiagnosticOutcome.Succeeded,
                $"preview-{i}"));

        }

        TestConsole console = new TestConsole().Width(120).Height(40);

        Layout root = ChatLayoutRenderer.Build(CreateContext(
            assistantText: "ASSISTANT_ONLY_TEXT",
            diagnostics: diagnostics));

        console.Write(root);

        Assert.Contains("ASSISTANT_ONLY_TEXT", console.Output, StringComparison.Ordinal);

        Assert.Contains("Tool activity", console.Output, StringComparison.OrdinalIgnoreCase);

        // Cap is 6 live lines — earliest previews should be dropped.
        Assert.DoesNotContain("preview-0", console.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("preview-3", console.Output, StringComparison.Ordinal);

        Assert.Contains("preview-9", console.Output, StringComparison.Ordinal);

        Assert.Contains("tool_9", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void McpReadoutPanel_truncates_long_server_lists()
    {

        List<McpServerInfo> servers = new();

        for (int i = 0; i < 15; i++)
        {

            servers.Add(CreateServer($"server-{i}"));

        }

        TestConsole console = new();

        console.Write(McpReadoutPanel.Render(servers, CreateTheme()));

        Assert.Contains("server-0", console.Output, StringComparison.Ordinal);

        Assert.Contains("server-9", console.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("server-10", console.Output, StringComparison.Ordinal);

        Assert.Contains("+5 more", console.Output, StringComparison.Ordinal);

    }

    private static ChatLayoutContext CreateContext(
        string assistantText = "",
        bool generating = false,
        IReadOnlyList<ToolDiagnosticLine>? diagnostics = null) =>
        new(
            HeaderMarkup: "header",
            AssistantText: assistantText,
            LiveDiagnostics: diagnostics ?? [],
            TranscriptTail: [],
            McpServers: [],
            Model: "test-model",
            ManaSummary: null,
            ServerStatus: ServeLaunchStatus.AlreadyRunning,
            Theme: CreateTheme(),
            Generating: generating);

    private static ConfiguredThemePalette CreateTheme()
    {

        ThemeSemanticColors colors = new();

        return new ConfiguredThemePalette(colors, colors);

    }

    private static McpServerInfo CreateServer(string name) =>
        new(
            name,
            WorkingDirectory: null,
            McpServerTransport.Stdio,
            AlwaysOn: false,
            Command: "echo",
            Arguments: [],
            Url: null,
            McpServerState.Running,
            ErrorMessage: null,
            Tools: ["tool_a"],
            LastConnectedAt: null);

}

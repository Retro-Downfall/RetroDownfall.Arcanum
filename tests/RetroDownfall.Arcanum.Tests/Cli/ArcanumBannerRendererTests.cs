using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumBannerRendererTests
{

    [Theory]
    [InlineData(ServeLaunchStatus.AlreadyRunning, "running")]
    [InlineData(ServeLaunchStatus.Started, "auto-started")]
    [InlineData(ServeLaunchStatus.AuthFailed, "auth failed")]
    [InlineData(ServeLaunchStatus.LaunchDisabled, "auto-start disabled")]
    [InlineData(ServeLaunchStatus.Failed, "unreachable")]
    public void Render_includes_ARCANUM_and_status_text(ServeLaunchStatus status, string expectedStatus)
    {

        TestConsole console = new();

        console.Write(ArcanumBannerRenderer.Render(CreateContext(status)));

        Assert.Contains("ARCANUM", console.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(expectedStatus, console.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Render_Failed_TlsFailure_shows_tls_guidance()
    {

        TestConsole console = new();

        console.Write(ArcanumBannerRenderer.Render(CreateContext(
            ServeLaunchStatus.Failed,
            HealthProbeState.TlsFailure)));

        Assert.Contains("TLS", console.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("doctor", console.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Render_shows_mcp_running_counts()
    {

        TestConsole console = new();

        console.Write(ArcanumBannerRenderer.Render(CreateContext(
            ServeLaunchStatus.AlreadyRunning,
            mcpRunning: 2,
            mcpTotal: 3)));

        Assert.Contains("2/3 running", console.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Render_shows_mcp_unavailable()
    {

        TestConsole console = new();

        console.Write(ArcanumBannerRenderer.Render(CreateContext(
            ServeLaunchStatus.AlreadyRunning,
            mcpUnavailable: true)));

        Assert.Contains("unavailable", console.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Render_includes_sword_through_title()
    {
        TestConsole console = new();

        console.Write(ArcanumBannerRenderer.Render(CreateContext(ServeLaunchStatus.AlreadyRunning)));

        Assert.Contains('═', console.Output);
        Assert.Contains('>', console.Output);
        Assert.Contains('╪', console.Output);
    }

    private static BannerContext CreateContext(
        ServeLaunchStatus status,
        HealthProbeState health = HealthProbeState.Healthy,
        int mcpRunning = 0,
        int mcpTotal = 0,
        bool mcpUnavailable = false)
    {

        ThemeSemanticColors colors = new();

        ConfiguredThemePalette theme = new(colors, colors);

        return new BannerContext(
            status,
            health,
            "http://localhost:5001/",
            "test-model",
            CampaignId: null,
            Unattended: false,
            ToolsDisabled: false,
            InferenceOverrides: [],
            mcpRunning,
            mcpTotal,
            mcpUnavailable,
            Version: "0.0.0-test",
            theme);

    }

}

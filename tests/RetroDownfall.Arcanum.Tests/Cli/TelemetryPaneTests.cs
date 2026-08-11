using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using Xunit;

namespace RetroDownfall.Arcanum.Cli.Tests;

public class TelemetryPaneTests
{
    [Fact]
    public void Pane_IsNonFocusable()
    {
        TelemetryPane pane = new();
        Assert.False(pane.CanFocus);
    }

    [Fact]
    public void Pane_RendersRequiredContextSourcesAndProviderBilledTotal()
    {
        TelemetryPane pane = new();

        ContextTokenBreakdown breakdown = Breakdown(
            providerReportedInputTokens: 1_337,
            droppedAttachmentChunks: 2,
            droppedAttachmentTokens: 41);

        pane.UpdateContext(breakdown);

        Assert.Contains("Chat history", pane.Text, StringComparison.Ordinal);

        Assert.Contains("100", pane.Text, StringComparison.Ordinal);

        Assert.Contains("Attachments", pane.Text, StringComparison.Ordinal);

        Assert.Contains("200", pane.Text, StringComparison.Ordinal);

        Assert.Contains("Refreshed", pane.Text, StringComparison.Ordinal);

        Assert.Contains("50", pane.Text, StringComparison.Ordinal);

        Assert.Contains("Attachment RAG", pane.Text, StringComparison.Ordinal);

        Assert.Contains("75", pane.Text, StringComparison.Ordinal);

        Assert.Contains("Workspace RAG", pane.Text, StringComparison.Ordinal);

        Assert.Contains("125", pane.Text, StringComparison.Ordinal);

        Assert.Contains("1,337 billed", pane.Text, StringComparison.Ordinal);

        Assert.Contains("RAG dropped 2 / 41 tokens", pane.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pane_UsesEstimatedInputUntilProviderBillingArrives()
    {
        TelemetryPane pane = new();

        pane.UpdateContext(Breakdown());

        Assert.Contains("550 estimated", pane.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("billed", pane.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void State_ReportsBackgroundAttachmentIndexingInFooter()
    {
        CommandCenterState state = new(new SessionLogBuffer())
        {
            SessionAttachments =
            [
                Attachment(SessionAttachmentIndexStatus.Pending),
                Attachment(SessionAttachmentIndexStatus.Pending),
                Attachment(SessionAttachmentIndexStatus.Failed),
            ],
        };

        Assert.StartsWith(
            "Indexing attachments: 2 pending · 1 failed",
            state.FooterHints,
            StringComparison.Ordinal);

        state.SessionAttachments =
        [
            Attachment(SessionAttachmentIndexStatus.Indexed),
            Attachment(SessionAttachmentIndexStatus.Indexed),
        ];

        Assert.StartsWith(
            "Attachment indexing complete: 2 indexed",
            state.FooterHints,
            StringComparison.Ordinal);

        state.SessionAttachments =
        [
            Attachment(SessionAttachmentIndexStatus.Failed),
        ];

        Assert.StartsWith(
            "Attachment indexing failed: 1",
            state.FooterHints,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Window_ShowsAndHidesContextTelemetryPane()
    {
        CommandCenterState state = new(new SessionLogBuffer())
        {
            LastContextBreakdown = Breakdown(),
        };

        CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 40);

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        Assert.True(window.ContextTelemetryFrame.Visible);

        Assert.Contains("Chat history", window.ContextTelemetryBody.Text.ToString(), StringComparison.Ordinal);

        state.ShowTelemetryPane = false;

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        Assert.False(window.ContextTelemetryFrame.Visible);
    }

    private static ContextTokenBreakdown Breakdown(
        long? providerReportedInputTokens = null,
        int droppedAttachmentChunks = 0,
        int droppedAttachmentTokens = 0) =>
        new()
        {
            Provider = "provider",

            Model = "model",

            Profile = new ResolvedModelTokenizationProfile
            {
                ProfileId = "test",

                Type = ModelTokenizationProfileType.ExactLocalTokenizer,

                TokenizerId = "test",

                SafetyMarginPercent = 0,

                PerMessageOverheadTokens = 0,

                PerToolOverheadTokens = 0,

                ProviderFramingTokens = 0,

                StopTokenOverheadTokens = 0,

                UnknownImageReserveTokens = 0,

                Confidence = 1,
            },

            Components =
            [
                Component(ContextTokenSource.History, 100),

                Component(ContextTokenSource.ExplicitAttachments, 200),

                Component(ContextTokenSource.RefreshedFiles, 50),

                Component(ContextTokenSource.AttachmentRag, 75),

                Component(ContextTokenSource.WorkspaceRag, 125),
            ],

            InputTokens = 550,

            ReservedTokens = 100,

            TotalTokens = 650,

            OverallClassification = TokenEstimateClassification.Exact,

            SafetyMarginTokens = 0,

            ProviderReportedInputTokens = providerReportedInputTokens,

            ProviderReportedInputValid = providerReportedInputTokens is not null,

            DroppedAttachmentRagChunks = droppedAttachmentChunks,

            DroppedAttachmentRagTokens = droppedAttachmentTokens,
        };

    private static ContextTokenComponent Component(ContextTokenSource source, int tokens) =>
        new(
            source,
            new TokenEstimate(
                tokens,
                TokenEstimateClassification.Exact,
                "test"));

    private static SessionAttachmentDto Attachment(SessionAttachmentIndexStatus status) =>
        new(
            Guid.NewGuid(),
            "notes",
            "notes.txt",
            1,
            "notes.txt",
            "text/plain",
            10,
            SessionAttachmentKind.Text,
            "sha256",
            DateTimeOffset.UtcNow,
            IndexingStatus: status);
}

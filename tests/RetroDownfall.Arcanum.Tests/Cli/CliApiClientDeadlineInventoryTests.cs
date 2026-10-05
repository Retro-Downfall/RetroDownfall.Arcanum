using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Pins which CLI API calls carry the short-call response-headers deadline and which are exempt.
/// </summary>
/// <remarks>
/// The deadline belongs to the short-call client (<c>ArcanumApiRequest</c>) and a call is exempt by
/// being issued on the unbounded streaming client (<c>ArcanumApi</c>). The default for a new call is
/// the short-call client, so a long-running call added without thinking about it would fail as
/// <c>Connection.Timeout</c> (exit 3) after the deadline while the host kept working. Every member
/// that picks a client therefore has to be named here, in exactly one of the two lists, so adding or
/// moving a call is a deliberate edit that has to say which kind it is.
/// </remarks>
public sealed class CliApiClientDeadlineInventoryTests
{
    private static readonly Regex MemberDeclaration = new(
        @"^    (?:public|internal|private|protected)[^\r\n;=]*?\b(?<name>\w+)\s*(?:<[^>\r\n]*>)?\s*\(",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The routing helpers and the deadline rule take the client name from their caller, so the caller
    /// is the choice and the helper itself is not a call.
    /// </summary>
    private static readonly string[] NotCalls =
    [
        "SendRequestAsync",
        "SendJsonAsync",
        "ResponseHeadersDeadlineFor",
    ];

    /// <summary>
    /// Exempt from the headers deadline: their answer is the work itself, and none of that work has an
    /// Arcanum-owned expected duration.
    /// </summary>
    private static readonly string[] Unbounded =
    [
        // Streams: the response is the work, read until the host finishes.
        "AskStreamAsync",
        "ExecutePromptAsync",
        "ExecuteSpellAsync",
        "ResearchWebAsync",
        "RunTrialAsync",
        "StreamApprenticeChronicleAsync",
        "WatchSseAsync",

        // Transfers: the headers arrive only once the whole body has moved, and that takes as long
        // as the file is large.
        "DownloadFileAsync",
        "DownloadSessionAttachmentAsync",
        "UploadFileAsync",
        "UploadSessionAttachmentAsync",

        // A model turn, a tool execution, a model summary, a remote agent settling, a durable
        // operation sweep and the durable data applies answer when their work is done.
        "ApplyDataPruneAsync",
        "AskAsync",
        "CompactSessionAsync",
        "ContinueSendingAsync",
        "DispatchSendingAsync",
        "FactoryResetDataAsync",
        "InvokeDiagnosticMcpToolAsync",
        "InvokeToolAsync",
        "ReconcileOperationsAsync",
        "ResetDataMemoryAsync",
    ];

    /// <summary>
    /// Carry the deadline: the handler reads or writes local state, queues the work and answers 202, or
    /// makes one bounded lookup, so a host that has not answered after the deadline is hung.
    /// </summary>
    private const string BoundedShortCalls = """
        ActivateSpellVersionAsync
        AdjustDaemonJobInitiativeAsync
        ArchiveSessionAsync
        BrowseWebAsync
        CancelOperationAsync
        CastApprenticeAsync
        CastSpellAsync
        ChangeMcpServerStateAsync
        ClonePromptAsync
        CloneSpellAsync
        CorrectLexiconAsync
        CorrectSagaMemoryAsync
        CreateApprenticeAsync
        CreateBatchAsync
        CreateCampaignAsync
        CreatePromptAsync
        CreateSessionAttachmentReferenceAsync
        CreateSessionContextPinAsync
        CreateSpellAsync
        CreateSpellVersionAsync
        DeleteDataAttachmentAsync
        DeleteDataSessionAsync
        DeleteFileAsync
        DeleteLoreAsync
        DeleteReturningNoContentAsync
        DeleteSessionContextPinAsync
        DivineSessionsAsync
        ExportCampaignAsync
        ExportPromptAsync
        ExportSessionAsync
        ExportSpellAsync
        ForkSessionAsync
        GetApiAsync
        GetApprenticeAsync
        GetApprenticesAsync
        GetBatchAsync
        GetBudgetAsync
        GetCampaignAsync
        GetCampaignCodexAsync
        GetCampaignPromptsAsync
        GetCampaignSessionsAsync
        GetCampaignSpellsAsync
        GetCampaignsPageAsync
        GetConclaveStatusAsync
        GetConfigurationAsync
        GetDaemonJobsAsync
        GetDataRetentionSettingsAsync
        GetDataRetentionStatusAsync
        GetFamiliarProbeAsync
        GetFileAsync
        GetHealthWatchReportAsync
        GetLoreAsync
        GetMcpServerAsync
        GetMcpServersAsync
        GetModelsAsync
        GetOperationAsync
        GetOperationsAsync
        GetPromptAsync
        GetPromptVersionsByNameAsync
        GetPromptsAsync
        GetProvidersAsync
        GetSessionAnalyticsAsync
        GetSessionAsync
        GetSessionAttachmentsAsync
        GetSessionContextPinsAsync
        GetSessionEntriesAsync
        GetSpellAsync
        GetSpellCatalogPageAsync
        GetSpellVersionsAsync
        GetSpellsAsync
        GetWardAsync
        GetWardsAsync
        GetWorkspaceArsenalAsync
        GetWorkspaceAsync
        GetWorkspaceChunksAsync
        GetWorkspaceFileInfoAsync
        GetWorkspaceIndexStatusAsync
        GetWorkspacesAsync
        ImportCampaignAsync
        ImportPromptAsync
        ImportSpellAsync
        IndexWorkspaceAsync
        IntervereApprenticeAsync
        ListBatchesAsync
        ListFilesAsync
        ListLoreAsync
        ListWorkspaceFilesAsync
        PerceivePatternAsync
        PinLexiconAsync
        PinSagaMemoryAsync
        PlanDataMemoryResetAsync
        PlanDataPruneAsync
        PlanFactoryResetDataAsync
        PostApprenticeLifecycleAsync
        PostBatchMutationAsync
        PostCovenantAsync
        PostReviewAsync
        PreviewContextAsync
        PutCampaignCodexAsync
        QuerySessionsAsync
        QuitServerAsync
        ReadWorkspaceFileAsync
        RefreshSessionAttachmentAsync
        RegisterWorkspaceAsync
        ReinstateLexiconAsync
        ReinstateSagaMemoryAsync
        ReloadMcpAsync
        RenderPromptAsync
        ResolveWardAsync
        RestAsync
        RetireLexiconAsync
        RetireSagaMemoryAsync
        RetryOperationAsync
        ReweaveApprenticeAsync
        SagaDivineAsync
        SagaListAsync
        SearchMemoryAsync
        SearchSpellsAsync
        SearchWebAsync
        SearchWorkspaceAsync
        SendCommLinkAlertAsync
        SendErasureAsync
        SendSessionEntryMutationAsync
        SetCovenantAsync
        ShowLexiconAsync
        ShowSagaMemoryAsync
        SubmitHumanResponseAsync
        SynchronizePatternAsync
        TestPromptAsync
        TrustMcpWorkspaceAsync
        UnpinLexiconAsync
        UnpinSagaMemoryAsync
        UpdateCampaignAsync
        UpdateConfigurationAsync
        UpdateDataRetentionRuleAsync
        UpdatePromptAsync
        UpdateSessionAsync
        UpdateSpellAsync
        UpdateSpellVersionAsync
        UpsertLoreAsync
        ValidateConfigurationAsync
        ValidateSpellAsync
        """;

    [Fact]
    public void Every_call_that_picks_an_http_client_is_classified_as_unbounded_or_bounded()
    {
        (SortedSet<string> unbounded, SortedSet<string> bounded) = DiscoverClientChoices();

        string[] bothWays = [.. unbounded.Intersect(bounded)];

        Assert.True(
            bothWays.Length == 0,
            "These names choose the unbounded client in one overload and the short-call client in "
            + $"another, which an inventory by name cannot tell apart: {string.Join(", ", bothWays)}");

        Assert.Equal(
            Unbounded.Order(StringComparer.Ordinal),
            unbounded);

        Assert.Equal(
            ExpectedBounded(),
            bounded);
    }

    [Fact]
    public void The_inventory_names_no_call_twice_and_no_call_in_both_lists()
    {
        string[] bounded = ExpectedBounded();

        Assert.Equal(Unbounded.Length, Unbounded.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(bounded.Length, bounded.Distinct(StringComparer.Ordinal).Count());

        Assert.Empty(Unbounded.Intersect(bounded));
    }

    private static string[] ExpectedBounded() =>
        [
            .. BoundedShortCalls
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Order(StringComparer.Ordinal),
        ];

    /// <summary>
    /// Every member of the CLI's two API clients that picks an <c>HttpClient</c>, split by whether it
    /// picks the unbounded streaming client or leaves the short-call default in place.
    /// </summary>
    private static (SortedSet<string> Unbounded, SortedSet<string> Bounded) DiscoverClientChoices()
    {
        SortedSet<string> unbounded = new(StringComparer.Ordinal);

        SortedSet<string> bounded = new(StringComparer.Ordinal);

        ProductionSource[] clients =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.RelativePath.StartsWith(
                        "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient",
                        StringComparison.Ordinal)
                    || source.IsExactOwner("src/RetroDownfall.Arcanum.Cli/Services/FileBatchApiClient.cs")),
        ];

        Assert.True(clients.Length >= 2, "The API client sources were not found.");

        foreach (ProductionSource source in clients)
        {
            MatchCollection members = MemberDeclaration.Matches(source.Text);

            for (int index = 0; index < members.Count; index++)
            {
                Match member = members[index];

                int end = index + 1 < members.Count
                    ? members[index + 1].Index
                    : source.Text.Length;

                string body = source.Text[member.Index..end];

                string name = member.Groups["name"].Value;

                bool choosesClient = body.Contains("SendRequestAsync(", StringComparison.Ordinal)
                    || body.Contains("SendJsonAsync(", StringComparison.Ordinal)
                    || body.Contains("CreateClient(", StringComparison.Ordinal)
                    || body.Contains("StreamingHttpClientName", StringComparison.Ordinal)
                    || body.Contains("RequestHttpClientName", StringComparison.Ordinal);

                if (!choosesClient || NotCalls.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                _ = body.Contains("StreamingHttpClientName", StringComparison.Ordinal)
                    ? unbounded.Add(name)
                    : bounded.Add(name);
            }
        }

        return (unbounded, bounded);
    }
}

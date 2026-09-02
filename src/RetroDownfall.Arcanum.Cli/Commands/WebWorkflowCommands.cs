using System.Text;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class WebWorkflowCommands(
    ArcanumApiClient apiClient,
    IConsoleDispatcher dispatcher,
    ICliResourceCatalog resources)
{

    public async Task<int> Search(
        string query,
        int count,
        string? freshness,
        string[] includeDomains,
        string[] excludeDomains,
        string? save,
        string? attachToSession,
        CancellationToken cancellationToken)
    {

        SessionSelection attachment = await ResolveSessionAsync(
            attachToSession,
            cancellationToken).ConfigureAwait(false);

        if (!attachment.Success)
        {

            return attachment.Cancelled ? 0 : 1;

        }

        Result<WebSearchWorkflowResult> response = await apiClient
            .SearchWebAsync(
                new WebSearchWorkflowRequest
                {

                    Query = query,

                    ResultCount = count,

                    Freshness = freshness,

                    IncludeDomains = includeDomains,

                    ExcludeDomains = excludeDomains,

                    AttachToSessionId = attachment.Id,

                },
                cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {

            return WriteError(response.Error);

        }

        string markdown = FormatAnswer(
            response.Value.Answer,
            response.Value.Citations);

        if (!await SaveAsync(save, markdown, cancellationToken)
            .ConfigureAwait(false))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                response.Value,
                ArcanumJsonContext.Default.WebSearchWorkflowResult);

        }
        else
        {

            dispatcher.WritePayload(markdown);

        }

        WriteAttachmentDiagnostic(
            response.Value.AttachmentId,
            response.Value.AttachmentError);

        return 0;

    }

    public async Task<int> Browse(
        string url,
        string renderMode,
        string? save,
        string? attachToSession,
        CancellationToken cancellationToken)
    {

        SessionSelection attachment = await ResolveSessionAsync(
            attachToSession,
            cancellationToken).ConfigureAwait(false);

        if (!attachment.Success)
        {

            return attachment.Cancelled ? 0 : 1;

        }

        Result<WebBrowseWorkflowResult> response = await apiClient
            .BrowseWebAsync(
                new WebBrowseWorkflowRequest
                {

                    Url = url,

                    RenderMode = renderMode,

                    AttachToSessionId = attachment.Id,

                },
                cancellationToken)
            .ConfigureAwait(false);

        if (response.IsFailure)
        {

            return WriteError(response.Error);

        }

        if (!await SaveAsync(
                save,
                response.Value.Markdown,
                cancellationToken)
            .ConfigureAwait(false))
        {

            return 1;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                response.Value,
                ArcanumJsonContext.Default.WebBrowseWorkflowResult);

        }
        else
        {

            dispatcher.WritePayload(response.Value.Markdown);

        }

        WriteAttachmentDiagnostic(
            response.Value.AttachmentId,
            response.Value.AttachmentError);

        return 0;

    }

    public async Task<int> Research(
        string question,
        int? sourceTarget,
        string? model,
        int tokenBudget,
        decimal? costBudget,
        string? continueSession,
        string? outputFormat,
        string? save,
        string? attachToSession,
        string? workingDirectory,
        Guid? campaignId,
        IReadOnlyList<AttachedFileDto>? attachedFiles,
        IReadOnlyList<ScryingFocusDto>? scryingFoci,
        InferenceFlagBinder.Parsed? inferenceFlags,
        bool unattendedMode,
        CancellationToken cancellationToken)
    {

        SessionSelection continuation = await ResolveSessionAsync(
            continueSession,
            cancellationToken).ConfigureAwait(false);

        if (!continuation.Success)
        {

            return continuation.Cancelled ? 0 : 1;

        }

        SessionSelection attachment = await ResolveSessionAsync(
            attachToSession,
            cancellationToken).ConfigureAwait(false);

        if (!attachment.Success)
        {

            return attachment.Cancelled ? 0 : 1;

        }

        string format = CliInvocationContext.Current.Json
            ? "json"
            : (outputFormat ?? "terminal").Trim().ToLowerInvariant();

        if (format is not ("terminal" or "markdown" or "json"))
        {

            dispatcher.WriteDiagnostic(
                "--format must be terminal, markdown, or json.");

            return (int)CliExitCode.ConfigurationError;

        }

        WebResearchWorkflowResult? result = null;

        await foreach (WebResearchStreamFrame frame in apiClient
            .ResearchWebAsync(
                new WebResearchWorkflowRequest
                {

                    Question = question,

                    SourceTarget = sourceTarget,

                    Model = model,

                    TokenBudget = tokenBudget,

                    CostBudgetUsd = costBudget,

                    ContinueSessionId = continuation.Id,

                    AttachToSessionId = attachment.Id,

                    WorkingDirectory = workingDirectory ?? string.Empty,

                    CampaignId = campaignId,

                    AttachedFiles = attachedFiles is null

                        ? null

                        : [.. attachedFiles],

                    ScryingFoci = scryingFoci is null

                        ? null

                        : [.. scryingFoci],

                    Temperature = inferenceFlags?.Temperature,

                    TopP = inferenceFlags?.TopP,

                    Stop = inferenceFlags?.Stop,

                    Seed = inferenceFlags?.Seed,

                    ResponseFormat = inferenceFlags?.ResponseFormat,

                    PresencePenalty = inferenceFlags?.PresencePenalty,

                    FrequencyPenalty = inferenceFlags?.FrequencyPenalty,

                    UnattendedMode = unattendedMode,

                },
                cancellationToken)
            .ConfigureAwait(false))
        {

            switch (frame.Type)
            {

                case WebResearchStreamFrameType.Limits:

                case WebResearchStreamFrameType.Progress:

                    if (!string.IsNullOrWhiteSpace(frame.Message))
                    {

                        dispatcher.WriteDiagnostic(frame.Message);

                    }

                    break;

                case WebResearchStreamFrameType.Result:

                    result = frame.Result;

                    break;

                case WebResearchStreamFrameType.Error:

                    // A transport failure here is the same failure `arcanum ask` reports, so it gets
                    // the same next step. A provider or policy error keeps its own copy untouched.
                    dispatcher.WriteDiagnostic(
                        $"{frame.Code ?? ErrorCodes.WebResearch.ProviderUnavailable}: "
                        + CliStreamTransportHint.Append(frame.Code, frame.Message ?? "Research failed."));

                    return 1;

                default:

                    dispatcher.WriteDiagnostic(
                        "The API returned an unknown research progress frame.");

                    return 1;

            }

        }

        if (result is null)
        {

            dispatcher.WriteDiagnostic(
                CliStreamTransportHint.Append(ArcanumApiClient.StreamEmptyResultMessage));

            return 1;

        }

        string markdown = FormatAnswer(result.Answer, result.Citations);

        if (!await SaveAsync(save, markdown, cancellationToken)
            .ConfigureAwait(false))
        {

            return 1;

        }

        if (format == "json")
        {

            dispatcher.WriteJson(
                result,
                ArcanumJsonContext.Default.WebResearchWorkflowResult);

        }
        else if (format == "markdown")
        {

            dispatcher.WritePayload(markdown);

        }
        else
        {

            dispatcher.WritePayload(FormatTerminal(result));

        }

        // Research streams, so a failed attachment already arrived as its own `attachment_failed`
        // progress frame and was written to stderr by the frame loop above. There is no
        // `AttachmentError` on the research result to pass here, and printing one would double the
        // diagnostic.
        WriteAttachmentDiagnostic(result.AttachmentId, attachmentError: null);

        return 0;

    }

    private async Task<SessionSelection> ResolveSessionAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(identifier))
        {

            return new SessionSelection(true, false, null);

        }

        if (Guid.TryParse(identifier, out Guid id))
        {

            return new SessionSelection(true, false, id);

        }

        ResourceSelectionResult<SessionSummaryDto> selection =
            await resources
                .SelectSessionAsync(identifier, cancellationToken)
                .ConfigureAwait(false);

        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {

            return new SessionSelection(false, true, null);

        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {

            dispatcher.WriteDiagnostic(
                selection.Error ?? "Session selection failed.");

            return default;

        }

        return new SessionSelection(
            true,
            false,
            selection.Value!.Id);

    }

    private async Task<bool> SaveAsync(
        string? path,
        string content,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(path))
        {

            return true;

        }

        try
        {

            string fullPath = Path.GetFullPath(path);

            string? directory = Path.GetDirectoryName(fullPath);

            if (string.IsNullOrWhiteSpace(directory)
                || !Directory.Exists(directory))
            {

                dispatcher.WriteDiagnostic(
                    "The --save destination directory does not exist.");

                return false;

            }

            string temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {

                await File.WriteAllTextAsync(
                    temporaryPath,
                    content,
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);

                File.Move(temporaryPath, fullPath, overwrite: true);

            }
            finally
            {

                if (File.Exists(temporaryPath))
                {

                    File.Delete(temporaryPath);

                }

            }

            dispatcher.WriteDiagnostic($"Saved {fullPath}");

            return true;

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            dispatcher.WriteDiagnostic(
                $"Could not save the result: {exception.Message}");

            return false;

        }

    }

    private static string FormatAnswer(
        string answer,
        IReadOnlyList<WebWorkflowCitation> citations)
    {

        if (citations.Count == 0)
        {

            return answer;

        }

        return answer.TrimEnd()
            + Environment.NewLine
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                citations.Select(
                    static citation =>
                        $"[{citation.Index}]: {citation.Url}"
                        + (string.IsNullOrWhiteSpace(citation.Title)
                            ? string.Empty
                            : $" \"{citation.Title}\"")));

    }

    private static string FormatTerminal(
        WebResearchWorkflowResult result)
    {

        if (result.Citations.Length == 0)
        {

            return result.Answer;

        }

        return result.Answer.TrimEnd()
            + Environment.NewLine
            + Environment.NewLine
            + "Sources:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                result.Citations.Select(
                    static citation =>
                        $"  [{citation.Index}] {citation.Title ?? citation.Url} — {citation.Url}"));

    }

    private int WriteError(Error error)
    {

        dispatcher.WriteDiagnostic($"{error.Code}: {error.Message}");

        return 1;

    }

    /// <summary>
    /// Reports what became of a requested <c>--attach-to-session</c>: the stored id, or the reason it was
    /// not stored.
    /// </summary>
    /// <remarks>
    /// Search and browse return the answer with a non-fatal <c>attachmentError</c> rather than failing the
    /// whole workflow, because the provider call is billed before the attachment is attempted. That trade
    /// only holds if the operator hears about it — an unreported failure is indistinguishable from an
    /// attachment that happened — so the error goes to stderr, leaving stdout to the answer and the exit
    /// code at success. Both fields are never set at once: an id means it was stored, an error means it
    /// was not.
    /// </remarks>
    private void WriteAttachmentDiagnostic(Guid? attachmentId, string? attachmentError)
    {

        if (attachmentId is Guid id)
        {

            dispatcher.WriteDiagnostic(
                $"Attached result to session as {id:D}.");

        }

        if (!string.IsNullOrWhiteSpace(attachmentError))
        {

            dispatcher.WriteDiagnostic(
                $"The result could not be attached to the session: {attachmentError}");

        }

    }

    private readonly record struct SessionSelection(
        bool Success,
        bool Cancelled,
        Guid? Id);

}

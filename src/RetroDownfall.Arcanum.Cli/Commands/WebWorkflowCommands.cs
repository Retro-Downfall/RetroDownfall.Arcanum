using System.Text;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.TheForge;

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

        WriteAttachmentDiagnostic(response.Value.AttachmentId);

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

        WriteAttachmentDiagnostic(response.Value.AttachmentId);

        return 0;

    }

    public async Task<int> Research(
        string question,
        int maxSources,
        int maxHops,
        string? model,
        int tokenBudget,
        decimal? costBudget,
        string? continueSession,
        string? outputFormat,
        string? save,
        string? attachToSession,
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

            return 1;

        }

        WebResearchWorkflowResult? result = null;

        await foreach (WebResearchStreamFrame frame in apiClient
            .ResearchWebAsync(
                new WebResearchWorkflowRequest
                {

                    Question = question,

                    MaxSources = maxSources,

                    MaxHops = maxHops,

                    Model = model,

                    TokenBudget = tokenBudget,

                    CostBudgetUsd = costBudget,

                    ContinueSessionId = continuation.Id,

                    AttachToSessionId = attachment.Id,

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

                    dispatcher.WriteDiagnostic(
                        $"{frame.Code ?? ErrorCodes.WebResearch.ProviderUnavailable}: {frame.Message ?? "Research failed."}");

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
                ArcanumApiClient.StreamEmptyResultMessage);

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

        WriteAttachmentDiagnostic(result.AttachmentId);

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

    private void WriteAttachmentDiagnostic(Guid? attachmentId)
    {

        if (attachmentId is Guid id)
        {

            dispatcher.WriteDiagnostic(
                $"Attached result to session as {id:D}.");

        }

    }

    private readonly record struct SessionSelection(
        bool Success,
        bool Cancelled,
        Guid? Id);

}

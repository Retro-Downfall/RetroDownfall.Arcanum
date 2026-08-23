using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Session-state warnings go to the diagnostic stream, never to the payload stream. <c>ask</c> saves
/// and clears the bound session with the default <c>quiet: false</c>, so a warning written to stdout
/// would be folded into the answer text under <c>--json</c> or a redirected <c>run</c>.
/// </summary>
public sealed class CliSessionManager(
    IConsoleDispatcher console,
    ILogger<CliSessionManager>? logger = null,
    ICliContextStore? contextStore = null,
    IArcanumClientMutationBoundary? mutationBoundary = null)
{
    private int _corruptionWarned;

    private string SessionFilePath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-session.txt");

    public Guid? GetLastSessionId(bool quiet = false)
    {
        Guid? contextSession = contextStore?.Load().SessionId;

        if (contextSession is not null
            || contextStore is not null && File.Exists(contextStore.FilePath))
        {
            return contextSession;
        }

        try
        {
            if (!File.Exists(SessionFilePath))
            {
                return null;
            }

            string text = File.ReadAllText(SessionFilePath).Trim();

            if (text.Length == 0)
            {
                return null;
            }

            if (Guid.TryParse(text, out Guid id))
            {
                return id;
            }

            WarnOnceSessionCorruption(quiet);

            return null;
        }
        catch (IOException ex)
        {
            WarnSessionIo(ex, quiet);

            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            WarnSessionIo(ex, quiet);

            return null;
        }
    }

    public async Task<ArcanumClientMutationResult<CliContextDocument>>
        SaveSessionIdAsync(
            Guid id,
            Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
            bool quiet = false,
            CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(revalidateAsync);

        return await MutateContextAsync(
                async (context, token) =>
                {

                    Result<bool> revalidated = await revalidateAsync(id, token)
                        .ConfigureAwait(false);

                    if (revalidated.IsFailure)
                    {

                        return Result<CliContextDocument>.Failure(
                            revalidated.Error);

                    }

                    return revalidated.Value
                        ? Result<CliContextDocument>.Success(
                            context with { SessionId = id })
                        : Result<CliContextDocument>.Failure(
                            new Error(
                                ErrorCodes.Session.NotFound,
                                $"Session {id:D} is no longer available on the current host."));

                },
                quiet,
                cancellationToken)
            .ConfigureAwait(false);

    }

    public async Task<ArcanumClientMutationResult<CliContextDocument>>
        ClearSessionAsync(
            bool quiet = false,
            CancellationToken cancellationToken = default)
    {

        return await MutateContextAsync(
                (context, _) => Task.FromResult(
                    Result<CliContextDocument>.Success(
                        context with { SessionId = null })),
                quiet,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<ArcanumClientMutationResult<CliContextDocument>>
        MutateContextAsync(
            Func<
                CliContextDocument,
                CancellationToken,
                Task<Result<CliContextDocument>>> prepareAsync,
            bool quiet,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(prepareAsync);

        if (contextStore is null
            || contextStore is not ICliContextExclusiveWriter contextWriter
            || mutationBoundary is null)
        {

            ArcanumClientMutationResult<CliContextDocument> unavailable =
                ArcanumClientMutationResult<CliContextDocument>.Unsafe(
                    new Error(
                        ErrorCodes.Data.ControlPathUnavailable,
                        "The authoritative CLI session context is unavailable."));

            WarnSessionMutation(unavailable.Error, quiet);

            return unavailable;

        }

        try
        {

            ArcanumClientMutationResult<Result<CliContextDocument>> admitted =
                await mutationBoundary
                    .RunAsync(
                        async token =>
                        {

                            Result<CliContextDocument> prepared =
                                await prepareAsync(contextStore.Load(), token)
                                    .ConfigureAwait(false);

                            if (prepared.IsSuccess)
                            {

                                contextWriter.SaveUnderExclusive(
                                    prepared.Value);

                            }

                            return prepared;

                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            ArcanumClientMutationResult<CliContextDocument> result =
                Flatten(admitted);

            if (!result.IsCompleted)
            {

                WarnSessionMutation(result.Error, quiet);

            }

            return result;

        }
        catch (IOException ex)
        {

            WarnSessionIo(ex, quiet);

            return UnsafeIoResult();

        }
        catch (UnauthorizedAccessException ex)
        {

            WarnSessionIo(ex, quiet);

            return UnsafeIoResult();

        }

    }

    private static ArcanumClientMutationResult<CliContextDocument> Flatten(
        ArcanumClientMutationResult<Result<CliContextDocument>> admitted)
    {

        if (!admitted.IsCompleted)
        {

            return admitted.Disposition
                is ArcanumClientMutationDisposition.Blocked
                    ? ArcanumClientMutationResult<CliContextDocument>.Blocked(
                        admitted.Error)
                    : ArcanumClientMutationResult<CliContextDocument>.Unsafe(
                        admitted.Error);

        }

        return admitted.Value.IsSuccess
            ? ArcanumClientMutationResult<CliContextDocument>.Completed(
                admitted.Value.Value)
            : ArcanumClientMutationResult<CliContextDocument>.Unsafe(
                admitted.Value.Error);

    }

    private static ArcanumClientMutationResult<CliContextDocument>
        UnsafeIoResult() =>
        ArcanumClientMutationResult<CliContextDocument>.Unsafe(
            new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The authoritative CLI session context could not be changed safely."));

    private void WarnSessionIo(Exception ex, bool quiet)
    {
        if (quiet)
        {
            logger?.LogDebug(
                "Could not save/load CLI session state (quiet); exception type {ExceptionType}.",
                ex.GetType().FullName);
            return;
        }

        console.WriteDiagnostic("Warning: Could not save/load session state.");
    }

    private void WarnSessionMutation(Error error, bool quiet)
    {

        if (quiet)
        {

            logger?.LogDebug(
                "CLI session mutation was refused with code {ErrorCode}.",
                error.Code);

            return;

        }

        console.WriteDiagnostic("Warning: " + error.Message);

    }

    private void WarnOnceSessionCorruption(bool quiet)
    {
        if (Interlocked.Exchange(ref _corruptionWarned, 1) != 0)
        {
            return;
        }

        if (quiet)
        {
            logger?.LogDebug(
                "cli-session.txt does not contain a valid session id. Quiet path; no Spectre output.");
            return;
        }

        console.WriteDiagnostic(
            "Warning: cli-session.txt does not contain a valid session id. Select or resume a session to establish authoritative CLI context.");
    }
}

internal static class SessionMutationRevalidator
{

    internal static async Task<Result<bool>> RevalidateAsync(
        ArcanumApiClient apiClient,
        Guid sessionId,
        SessionDetailDto? expected,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(apiClient);

        Result<SessionDetailDto> result = await apiClient
            .GetSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return Result<bool>.Failure(result.Error);

        }

        SessionDetailDto current = result.Value;

        bool sameStableIdentity = current.Id == sessionId
            && (expected is null
                || current.CampaignId == expected.CampaignId
                    && current.CreatedAt == expected.CreatedAt
                    && current.ForkedFromSessionId
                        == expected.ForkedFromSessionId);

        if (!sameStableIdentity)
        {

            return Result<bool>.Failure(
                new Error(
                    ErrorCodes.Session.NotFound,
                    $"Session {sessionId:D} changed identity before it could be persisted. Retry the operation."));

        }

        if (string.Equals(
            current.Status,
            "Archived",
            StringComparison.OrdinalIgnoreCase))
        {

            return Result<bool>.Failure(
                new Error(
                    ErrorCodes.Session.Archived,
                    $"Session {sessionId:D} is archived and cannot become the active CLI session."));

        }

        return Result<bool>.Success(true);

    }

}

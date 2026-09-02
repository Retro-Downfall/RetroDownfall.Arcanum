using System.Globalization;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Tower;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class WatchCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    IConsoleDispatcher dispatcher,
    TimeProvider timeProvider,
    ICliResourceCatalog? resourceCatalog = null)
{

    private const string GapWarning =
        "Potential event gap: the stream disconnected and events may have been missed. No replay guarantee is available.";

    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);

    public async Task<int> Session(
        string? identifier,
        string? since,
        WatchCommandOptions options,
        CancellationToken cancellationToken = default)
    {

        PrepareJsonStream();

        ResourceResolution resolution = await ResolveSessionAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        Guid? cursor = null;

        if (!string.IsNullOrWhiteSpace(since))
        {

            if (!Guid.TryParse(since, out Guid parsedCursor))
            {

                dispatcher.WriteDiagnostic("--since must be a valid Session Entry GUID.");

                return (int)CliExitCode.ConfigurationError;

            }

            cursor = parsedCursor;

        }

        string Path() => BuildPath(
            $"api/sessions/{resolution.Id:D}/stream",
            ("since", cursor?.ToString("D")));

        void AdvanceCursor(JsonElement payload)
        {

            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out Guid entryId))
            {

                cursor = entryId;

            }

        }

        return await RunSseAsync(
            Path,
            WatchSource.Session,
            options,
            AdvanceCursor,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<int> Apprentice(
        string? identifier,
        WatchCommandOptions options,
        CancellationToken cancellationToken = default)
    {

        PrepareJsonStream();

        ResourceResolution resolution = await ResolveApprenticeAsync(
            identifier,
            cancellationToken).ConfigureAwait(false);

        if (!resolution.Success)
        {

            return resolution.Cancelled ? 0 : 1;

        }

        return await RunSseAsync(
            () => $"api/apprentices/{resolution.Id:D}/chronicle",
            WatchSource.Apprentice,
            options,
            null,
            cancellationToken).ConfigureAwait(false);

    }

    public Task<int> Logs(
        string? level,
        string? category,
        string? search,
        WatchCommandOptions options,
        CancellationToken cancellationToken = default)
    {

        PrepareJsonStream();

        return RunSseAsync(
            () => BuildPath(
                "api/events/logs",
                ("level", level),
                ("category", category),
                ("search", search)),
            WatchSource.Logs,
            options,
            null,
            cancellationToken);

    }

    public Task<int> Mcp(
        WatchCommandOptions options,
        CancellationToken cancellationToken = default)
    {

        PrepareJsonStream();

        return RunSseAsync(
            static () => "api/events/mcp",
            WatchSource.Mcp,
            options,
            null,
            cancellationToken);

    }

    public Task<int> Daemons(
        WatchCommandOptions options,
        CancellationToken cancellationToken = default)
    {

        PrepareJsonStream();

        return RunSseAsync(
            static () => "api/events/daemon",
            WatchSource.Daemons,
            options,
            null,
            cancellationToken);

    }

    public Task<int> Health(
        int intervalSeconds,
        WatchCommandOptions options,
        CancellationToken cancellationToken = default)
    {

        PrepareJsonStream();

        if (intervalSeconds <= 0)
        {

            dispatcher.WriteDiagnostic("--interval must be a positive number of seconds.");

            return Task.FromResult((int)CliExitCode.ConfigurationError);

        }

        return WithCancellationAsync(
            token => RunHealthAsync(
                TimeSpan.FromSeconds(intervalSeconds),
                options,
                token),
            cancellationToken);

    }

    internal static TimeSpan ReconnectDelay(int attempt)
    {

        int exponent = Math.Clamp(attempt - 1, 0, 5);

        double seconds = Math.Pow(2, exponent);

        return TimeSpan.FromSeconds(
            Math.Min(seconds, MaximumReconnectDelay.TotalSeconds));

    }

    private Task<int> RunSseAsync(
        Func<string> path,
        WatchSource source,
        WatchCommandOptions options,
        Action<JsonElement>? observe,
        CancellationToken cancellationToken)
    {

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.BeginJsonStream();

        }

        return WithCancellationAsync(
            token => RunSseCoreAsync(
                path,
                source,
                options,
                observe,
                token),
            cancellationToken);

    }

    private async Task<int> RunSseCoreAsync(
        Func<string> path,
        WatchSource source,
        WatchCommandOptions options,
        Action<JsonElement>? observe,
        CancellationToken cancellationToken)
    {

        int reconnectAttempt = 0;

        while (true)
        {

            string disconnectDiagnostic =
                "The stream disconnected before a [DONE] marker was received.";

            bool retryableDisconnect = false;

            await foreach (WatchSseFrame frame in apiClient
                .WatchSseAsync(path(), cancellationToken)
                .ConfigureAwait(false))
            {

                switch (frame.Type)
                {

                    case WatchSseFrameType.Data when frame.Data is { } payload:
                    {

                        observe?.Invoke(payload);

                        WatchEventView view = WatchEventView.Create(
                            source,
                            payload,
                            timeProvider.GetUtcNow());

                        if (view.Matches(options))
                        {

                            WriteEvent(payload, view);

                        }

                        continue;

                    }

                    case WatchSseFrameType.Heartbeat:
                    {

                        if (!string.IsNullOrWhiteSpace(frame.Diagnostic))
                        {

                            dispatcher.WriteDiagnostic(frame.Diagnostic);

                        }

                        continue;

                    }

                    case WatchSseFrameType.Done:
                    {

                        return 0;

                    }

                    case WatchSseFrameType.Error:
                    {

                        if (frame.Recoverable
                            && frame.Error is { } recoverable)
                        {

                            dispatcher.WriteDiagnostic(
                                $"{recoverable.Code}: {recoverable.Message}");

                            continue;

                        }

                        disconnectDiagnostic = frame.Error is { } error
                            ? $"{error.Code}: {error.Message}"
                            : frame.Diagnostic
                                ?? "The stream failed.";

                        retryableDisconnect = frame.Retryable;

                        break;

                    }

                    case WatchSseFrameType.UnexpectedEof:
                    {

                        disconnectDiagnostic = frame.Diagnostic
                            ?? disconnectDiagnostic;

                        retryableDisconnect = true;

                        break;

                    }

                    default:
                    {

                        continue;

                    }

                }

                break;

            }

            dispatcher.WriteDiagnostic(disconnectDiagnostic);

            if (!options.Reconnect || !retryableDisconnect)
            {

                return 1;

            }

            dispatcher.WriteDiagnostic(GapWarning);

            reconnectAttempt++;

            TimeSpan delay = ReconnectDelay(reconnectAttempt);

            dispatcher.WriteDiagnostic(
                $"Reconnecting in {delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds (attempt {reconnectAttempt.ToString(CultureInfo.InvariantCulture)}).");

            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task<int> RunHealthAsync(
        TimeSpan interval,
        WatchCommandOptions options,
        CancellationToken cancellationToken)
    {

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.BeginJsonStream();

        }

        int reconnectAttempt = 0;

        while (true)
        {

            HealthWatchResult observation = await apiClient
                .GetHealthWatchReportAsync(cancellationToken)
                .ConfigureAwait(false);

            Result<HealthReportDto> result = observation.Report;

            if (result.IsFailure)
            {

                dispatcher.WriteDiagnostic(
                    $"{result.Error.Code}: {result.Error.Message}");

                if (!options.Reconnect || !observation.Retryable)
                {

                    return 1;

                }

                dispatcher.WriteDiagnostic(
                    "Potential health-observation gap: a poll failed and intermediate states may have been missed. No replay guarantee is available.");

                reconnectAttempt++;

                TimeSpan retryDelay = ReconnectDelay(reconnectAttempt);

                dispatcher.WriteDiagnostic(
                    $"Retrying health in {retryDelay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds (attempt {reconnectAttempt.ToString(CultureInfo.InvariantCulture)}).");

                await Task.Delay(
                    retryDelay,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);

                continue;

            }

            reconnectAttempt = 0;

            DateTimeOffset observedAt = timeProvider.GetUtcNow();

            HealthReportDto report = result.Value;

            string eventType = report.Status.ToString();

            bool matchesType = options.EventTypes.Length == 0
                || options.EventTypes.Any(
                    value => string.Equals(
                        value?.Trim(),
                        eventType,
                        StringComparison.OrdinalIgnoreCase));

            if (matchesType && options.ToolNames.Length == 0)
            {

                WriteHealth(observedAt, report);

            }

            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task<int> WithCancellationAsync(
        Func<CancellationToken, Task<int>> action,
        CancellationToken cancellationToken)
    {

        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {

            eventArgs.Cancel = true;

            try
            {

                linked.Cancel();

            }
            catch (ObjectDisposedException)
            {

            }

        }

        Console.CancelKeyPress += Cancel;

        try
        {

            return await action(linked.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {

            return (int)CliExitCode.Cancelled;

        }
        finally
        {

            Console.CancelKeyPress -= Cancel;

        }

    }

    private void WriteEvent(JsonElement payload, WatchEventView view)
    {

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(payload);

            return;

        }

        string label = Markup.Escape(
            $"[{view.TimestampText()}] {view.EventType}");

        string message = Markup.Escape(view.Message);

        string line = view.IsFailure
            ? themePalette.ErrorLabelMarkup(label, message)
            : view.IsActive
                ? themePalette.HighlightLabelMarkup(label, message)
                : themePalette.MutedLabelMarkup(label, message);

        AnsiConsole.MarkupLine(line);

    }

    private void WriteHealth(
        DateTimeOffset observedAt,
        HealthReportDto report)
    {

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                new HealthWatchSnapshot(
                    observedAt,
                    report.Status,
                    report.Components),
                CliJsonContext.Default.HealthWatchSnapshot);

            return;

        }

        string detail = string.Join(
            ", ",
            report.Components.Select(
                component => string.IsNullOrWhiteSpace(component.Detail)
                    ? $"{component.Name}={component.Status}"
                    : $"{component.Name}={component.Status} ({component.Detail})"));

        string timestamp = observedAt
            .ToUniversalTime()
            .ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);

        string label = Markup.Escape(
            $"[{timestamp}] {report.Status}");

        string message = Markup.Escape(detail);

        string line = report.Status == HealthStatus.Unhealthy
            ? themePalette.ErrorLabelMarkup(label, message)
            : report.Status == HealthStatus.Healthy
                ? themePalette.HighlightLabelMarkup(label, message)
                : themePalette.MutedLabelMarkup(label, message);

        AnsiConsole.MarkupLine(line);

    }

    private async Task<ResourceResolution> ResolveSessionAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (Guid.TryParse(identifier, out Guid id))
        {

            return new ResourceResolution(true, false, id);

        }

        if (resourceCatalog is null)
        {

            dispatcher.WriteDiagnostic(
                "<SESSION> must be a valid GUID.");

            return default;

        }

        ResourceSelectionResult<SessionSummaryDto> result = await resourceCatalog
            .SelectSessionAsync(identifier, cancellationToken)
            .ConfigureAwait(false);

        return ResolveSelection(result, static value => value.Id);

    }

    private async Task<ResourceResolution> ResolveApprenticeAsync(
        string? identifier,
        CancellationToken cancellationToken)
    {

        if (Guid.TryParse(identifier, out Guid id))
        {

            return new ResourceResolution(true, false, id);

        }

        if (resourceCatalog is null)
        {

            dispatcher.WriteDiagnostic(
                "<APPRENTICE> must be a valid GUID.");

            return default;

        }

        ResourceSelectionResult<ApprenticeSummaryDto> result = await resourceCatalog
            .SelectApprenticeAsync(identifier, cancellationToken)
            .ConfigureAwait(false);

        return ResolveSelection(result, static value => value.Id);

    }

    private ResourceResolution ResolveSelection<T>(
        ResourceSelectionResult<T> result,
        Func<T, Guid> id)
        where T : class
    {

        if (result.Status == ResourceSelectionStatus.Selected
            && result.Value is { } value)
        {

            return new ResourceResolution(true, false, id(value));

        }

        if (result.Status == ResourceSelectionStatus.Cancelled)
        {

            return new ResourceResolution(false, true, default);

        }

        dispatcher.WriteDiagnostic(
            result.Error ?? "Resource selection failed.");

        return default;

    }

    private static string BuildPath(
        string path,
        params (string Name, string? Value)[] parameters)
    {

        string query = string.Join(
            "&",
            parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
                .Select(
                    parameter =>
                        $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value!)}"));

        return query.Length == 0
            ? path
            : $"{path}?{query}";

    }

    private readonly record struct ResourceResolution(
        bool Success,
        bool Cancelled,
        Guid Id);

    private void PrepareJsonStream()
    {

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.BeginJsonStream();

        }

    }

}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;


namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private async Task<McpToolsCallResultWire> ExecuteCommandAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_allowHostProcessTools)
        {
            return ToolError(Core.Security.HostProcessToolPolicy.DeniedMessage);
        }

        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {
            return gate;
        }

        ExecuteCommandParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.ExecuteCommandParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "execute_command argument deserialization failed.");

            return ToolError("Invalid arguments for execute_command.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Command))
        {
            return ToolError("execute_command requires 'command'.");
        }

        string commandFileName = args.Command.Trim();

        IReadOnlyList<string> tokenizedArgs = ResolveCommandArgumentTokens(args.ArgumentList, args.Arguments);

        string root = _workspaceRoot!;

        string workingDir;

        if (string.IsNullOrWhiteSpace(args.WorkingDirectory))
        {
            workingDir = root;
        }
        else
        {
            if (Path.IsPathRooted(args.WorkingDirectory))
            {
                return ToolError(
                    "execute_command: workingDirectory must be relative to the workspace root; absolute paths are not allowed.");
            }

            if (!TryResolveSandboxedPath(args.WorkingDirectory.Trim(), out string? resolvedCwd, out McpToolsCallResultWire? cwdErr))
            {
                return cwdErr!;
            }

            workingDir = resolvedCwd;

            if (!Directory.Exists(workingDir))
            {
                return ToolError(
                    $"execute_command: workingDirectory does not exist or is not a directory: '{args.WorkingDirectory}'.");
            }
        }

        ProcessStartInfo psi = new()
        {
            FileName = commandFileName,

            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            WorkingDirectory = workingDir,
        };

        foreach (string token in tokenizedArgs)
        {
            psi.ArgumentList.Add(token);
        }

        long effectiveToolOutputBytes =
            ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
                _settings.ToolOutputCapBytes,
                _maxJsonRpcLineBytes);

        const long retrievalMetadataReserveBytes = 4096L;

        long totalOutputCapBytes = Math.Max(
            2048L,
            effectiveToolOutputBytes - retrievalMetadataReserveBytes);

        await using AsyncServiceScope resourceScope = _scopeFactory.CreateAsyncScope();

        ISanctumGuard sanctumGuard = resourceScope.ServiceProvider.GetRequiredService<ISanctumGuard>();

        IProcessResourceLimiter resourceLimiter = resourceScope.ServiceProvider.GetRequiredService<IProcessResourceLimiter>();

        IOptionsMonitor<ArcanumSettings> optionsMonitor =
            resourceScope.ServiceProvider.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

        ResourceLimits resourceLimits = await sanctumGuard
            .GetEffectiveResourceLimitsForWorkspaceAsync(_workspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        SanctumChildProcessBoundary? boundary = await sanctumGuard
            .GetChildProcessBoundaryForWorkspaceAsync(_workspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        bool allowUnsandboxed = optionsMonitor.CurrentValue.Security.AllowUnsandboxedToolChildren;

        ChildProcessSandboxRequest sandboxRequest = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspaceRoot!,
            boundary?.AllowedPaths,
            allowUnsandboxed,
            windowsPathBoundaryRequired: boundary?.PathBoundaryRequired == true);

        string outputSpillDirectory;

        try
        {

            outputSpillDirectory = _commandOutputArtifacts.SpillDirectory;

        }
        catch (Exception ex)
            when (ex is IOException
                  or UnauthorizedAccessException)
        {

            _logger?.LogError(
                "execute_command: private complete-output storage allocation failed ({ExceptionType}).",
                ex.GetType().Name);

            return ToolError(
                "execute_command: private complete-output storage could not be allocated, so the process was not started.");

        }

        CappedChildProcessRunResult runResult = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes,
            Timeout.InfiniteTimeSpan,
            resourceLimits,
            resourceLimiter,
            cancellationToken,
            sandboxRequest,
            _logger,
            outputSpillDirectory: outputSpillDirectory)
            .ConfigureAwait(false);

        if (runResult.Outcome != CappedChildProcessOutcome.Completed)
        {

            _commandOutputArtifacts.Discard(
                runResult.Stdout,
                runResult.Stderr);

        }

        switch (runResult.Outcome)
        {
            case CappedChildProcessOutcome.FilesystemSandboxUnavailable:

            case CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum:

                return ToolError(
                    runResult.FilesystemSandboxDenialMessage
                    ?? ChildProcessSandboxMessages.SandboxUnavailable);

            case CappedChildProcessOutcome.ResourceLimitApplyFailed:

                _logger?.LogError(
                    "execute_command: failed to apply Sanctum resource limits: {Error}",
                    runResult.ResourceLimitApplyError);

                return ToolError(
                    "execute_command: the invocation was blocked because OS-level resource limits could not be applied.");

            case CappedChildProcessOutcome.ResourceLimitExceeded:

                string denialMessage = await ResourceLimitDenialFormatter.RecordAndDescribeAsync(
                    sanctumGuard,
                    _workspaceRoot,
                    "execute_command",
                    resourceLimits,
                    runResult.ExceededResource,
                    cancellationToken).ConfigureAwait(false);

                return ToolError(denialMessage);

            case CappedChildProcessOutcome.IoErrorOnStart:

                _logger?.LogError(runResult.FaultException, "execute_command: I/O error starting process.");

                return ToolError("execute_command: failed to start the process.");

            case CappedChildProcessOutcome.AccessDeniedOnStart:

                _logger?.LogError(runResult.FaultException, "execute_command: access denied starting process.");

                return ToolError("execute_command: access denied starting the process.");

            case CappedChildProcessOutcome.CanceledBeforeStart:

                return ToolError("execute_command: canceled before start completed.");

            case CappedChildProcessOutcome.FailedToStart:

                _logger?.LogError(runResult.FaultException, "execute_command: could not start process.");

                return ToolError("execute_command: failed to start the process.");

            case CappedChildProcessOutcome.TimedOut:

                return ToolError("execute_command: canceled by an external process deadline.");

            case CappedChildProcessOutcome.Canceled when cancellationToken.IsCancellationRequested:

                return ToolError("execute_command: canceled.");

            case CappedChildProcessOutcome.Canceled:

                return ToolError("execute_command: canceled.");

            case CappedChildProcessOutcome.IoErrorReadingOutput:

                _logger?.LogError(runResult.FaultException, "execute_command: I/O error reading process output.");

                return ToolError("execute_command: failed to read process output.");

            case CappedChildProcessOutcome.AccessDeniedReadingOutput:

                _logger?.LogError(runResult.FaultException, "execute_command: access denied reading process output.");

                return ToolError("execute_command: access denied reading process output.");

            case CappedChildProcessOutcome.CanceledWhileReadingOutput:

                return ToolError("execute_command: canceled while reading output.");

            case CappedChildProcessOutcome.OutputPreservationFailed:

                _logger?.LogError(
                    "execute_command: complete output preservation failed ({ExceptionType}).",
                    runResult.FaultException?.GetType().Name ?? "unknown");

                Exception? preservationFailure =
                    runResult.FaultException?.GetBaseException();

                return preservationFailure is CommandOutputSpillLimitException spillLimit
                    ? ToolError("execute_command: " + spillLimit.Message)
                    : ToolError(
                        "execute_command: complete output could not be preserved, so the process tree was terminated instead of silently discarding diagnostics.");

            case CappedChildProcessOutcome.Completed:

                break;

            default:

                return ToolError("execute_command: failed to start the process.");

        }

        long perStreamCapBytes = runResult.PerStreamCapBytes;

        CommandOutputArtifactRegistration? completeOutput = null;

        if (runResult.Stdout.Truncated || runResult.Stderr.Truncated)
        {

            try
            {

                completeOutput = _commandOutputArtifacts.Register(
                    runResult.Stdout,
                    runResult.Stderr);

            }
            catch (Exception ex)
                when (ex is IOException
                      or UnauthorizedAccessException
                      or InvalidOperationException
                      or ObjectDisposedException)
            {

                _commandOutputArtifacts.Discard(
                    runResult.Stdout,
                    runResult.Stderr);

                _logger?.LogError(
                    "execute_command: complete output retention failed ({ExceptionType}).",
                    ex.GetType().Name);

                return ToolError(
                    "execute_command: complete output could not be retained, so the invocation failed without silently discarding diagnostics.");

            }

        }

        StringBuilder text = new();

        text.AppendLine("--- stdout ---");

        text.AppendLine(runResult.Stdout.Text);

        if (runResult.Stdout.Truncated)
        {
            text.AppendLine($"[preview ended after {perStreamCapBytes} bytes; complete stdout is available below]");
        }

        text.AppendLine("--- stderr ---");

        text.AppendLine(runResult.Stderr.Text);

        if (runResult.Stderr.Truncated)
        {
            text.AppendLine($"[preview ended after {perStreamCapBytes} bytes; complete stderr is available below]");
        }

        text.Append("--- exit code ---\n");

        text.Append(runResult.ExitCode);

        if (completeOutput is not null)
        {

            text.AppendLine();

            text.AppendLine("--- complete output handle ---");

            text.AppendLine(completeOutput.Value.Handle);

            text.AppendLine("--- complete output streams ---");

            text.AppendLine(string.Join(", ", completeOutput.Value.AvailableStreams));

            text.Append(
                "Use read_command_output with this handle, a listed stream, offset 0, then each returned nextOffset. The handle expires when this connection closes.");

        }

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = text.ToString() },
            ],
            IsError = false,
        };
    }

    private async Task<McpToolsCallResultWire> ExecuteReadCommandOutputAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        if (!_allowHostProcessTools)
        {

            return ToolError(
                Core.Security.HostProcessToolPolicy.DeniedMessage);

        }

        McpToolsCallResultWire? gate = TryRequireWorkspaceRoot();

        if (gate is not null)
        {

            return gate;

        }

        ReadCommandOutputParams? args;

        try
        {

            args = JsonSerializer.Deserialize(
                arguments,
                _json.ReadCommandOutputParams);

        }
        catch (JsonException ex)
        {

            _logger?.LogError(
                ex,
                "read_command_output argument deserialization failed.");

            return ToolError(
                "Invalid arguments for read_command_output.");

        }

        if (args is null
            || string.IsNullOrWhiteSpace(args.Handle)
            || !Guid.TryParseExact(
                args.Handle,
                "N",
                out _))
        {

            return ToolError(
                "read_command_output requires the opaque 'handle' returned by execute_command.");

        }

        string streamName;

        if (string.Equals(
                args.Stream,
                "stdout",
                StringComparison.OrdinalIgnoreCase))
        {

            streamName = "stdout";

        }
        else if (string.Equals(
                     args.Stream,
                     "stderr",
                     StringComparison.OrdinalIgnoreCase))
        {

            streamName = "stderr";

        }
        else
        {

            return ToolError(
                "read_command_output stream must be 'stdout' or 'stderr'.");

        }

        int maximumPageBytes = GetMaxCommandOutputPageBytes();

        int pageBytes = args.MaxBytes
            ?? Math.Min(
                64 * 1024,
                maximumPageBytes);

        if (pageBytes < 4 || pageBytes > maximumPageBytes)
        {

            return ToolError(
                $"read_command_output maxBytes must be between 4 and {maximumPageBytes}; the upper bound preserves the existing JSON-RPC line boundary. Continue with additional pages for any remaining output.");

        }

        try
        {

            CommandOutputArtifactPage page = await _commandOutputArtifacts
                .ReadPageAsync(
                    args.Handle,
                    streamName,
                    args.Offset,
                    pageBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            CommandOutputPageResultWire wire = new()
            {

                Handle = args.Handle,

                Stream = streamName,

                Text = page.Text,

                Offset = page.Offset,

                NextOffset = page.NextOffset,

                TotalBytes = page.TotalBytes,

                Complete = page.NextOffset is null,

            };

            string text = JsonSerializer.Serialize(
                wire,
                _json.CommandOutputPageResultWire);

            return new McpToolsCallResultWire
            {

                Content =
                [
                    new McpToolContentTextWire
                    {

                        Text = text,

                    },
                ],

                IsError = false,

            };

        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {

            return ToolError(
                "read_command_output: canceled.");

        }
        catch (Exception ex)
            when (ex is InvalidDataException
                  or ArgumentOutOfRangeException
                  or KeyNotFoundException)
        {

            _logger?.LogWarning(
                "read_command_output could not read the requested page: {ExceptionType}",
                ex.GetType().Name);

            return ToolError(
                "read_command_output: " + ex.Message);

        }
        catch (Exception ex)
            when (ex is IOException
                  or UnauthorizedAccessException
                  or ObjectDisposedException)
        {

            _logger?.LogWarning(
                "read_command_output artifact access failed: {ExceptionType}",
                ex.GetType().Name);

            return ToolError(
                "read_command_output: the private artifact could not be read.");

        }

    }

    private int GetMaxCommandOutputPageBytes()
    {

        const int envelopeReserveBytes = 4096;

        const int worstCaseNestedJsonExpansion = 8;

        int payloadBoundary = Math.Max(
            32,
            _maxJsonRpcLineBytes - envelopeReserveBytes);

        long inProcessOutputBoundary =
            ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
                _settings.ToolOutputCapBytes,
                _maxJsonRpcLineBytes);

        int nestedJsonBoundary = (int)Math.Max(
            32L,
            inProcessOutputBoundary - 1024L);

        return Math.Max(
            4,
            Math.Min(
                payloadBoundary,
                nestedJsonBoundary)
            / worstCaseNestedJsonExpansion);

    }

    private static IReadOnlyList<string> ResolveCommandArgumentTokens(string[]? argumentList, string? argumentsString)
    {
        if (argumentList is { Length: > 0 })
        {
            List<string> direct = new(argumentList.Length);

            foreach (string token in argumentList)
            {
                if (token is null)
                {
                    continue;
                }

                direct.Add(token);
            }

            return direct;
        }

        if (string.IsNullOrEmpty(argumentsString))
        {
            return Array.Empty<string>();
        }

        return TokenizeArgumentsString(argumentsString);
    }

    private static IReadOnlyList<string> TokenizeArgumentsString(string line)
    {
        List<string> tokens = [];

        StringBuilder current = new();

        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;

                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());

                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

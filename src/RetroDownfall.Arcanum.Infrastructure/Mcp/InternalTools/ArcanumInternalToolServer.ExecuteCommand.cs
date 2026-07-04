using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        long totalOutputCapBytes = ArcanumSettingClamps.ToolOutputCapBytes(_settings.ToolOutputCapBytes);

        await using AsyncServiceScope resourceScope = _scopeFactory.CreateAsyncScope();

        ISanctumGuard sanctumGuard = resourceScope.ServiceProvider.GetRequiredService<ISanctumGuard>();

        IProcessResourceLimiter resourceLimiter = resourceScope.ServiceProvider.GetRequiredService<IProcessResourceLimiter>();

        ResourceLimits resourceLimits = await sanctumGuard
            .GetEffectiveResourceLimitsForWorkspaceAsync(_workspaceRoot, cancellationToken)
            .ConfigureAwait(false);

        CappedChildProcessRunResult runResult = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes,
            _executeCommandTimeout,
            resourceLimits,
            resourceLimiter,
            cancellationToken).ConfigureAwait(false);

        switch (runResult.Outcome)
        {
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

                return ToolError(
                    $"execute_command: the command timed out after {_executeCommandTimeoutSeconds} seconds.");

            case CappedChildProcessOutcome.Canceled when cancellationToken.IsCancellationRequested:

                return ToolError("execute_command: canceled.");

            case CappedChildProcessOutcome.Canceled:

                return ToolError("execute_command: canceled or timed out.");

            case CappedChildProcessOutcome.IoErrorReadingOutput:

                _logger?.LogError(runResult.FaultException, "execute_command: I/O error reading process output.");

                return ToolError("execute_command: failed to read process output.");

            case CappedChildProcessOutcome.AccessDeniedReadingOutput:

                _logger?.LogError(runResult.FaultException, "execute_command: access denied reading process output.");

                return ToolError("execute_command: access denied reading process output.");

            case CappedChildProcessOutcome.CanceledWhileReadingOutput:

                return ToolError("execute_command: canceled while reading output.");

            case CappedChildProcessOutcome.Completed:

                break;

            default:

                return ToolError("execute_command: failed to start the process.");

        }

        long perStreamCapBytes = runResult.PerStreamCapBytes;

        StringBuilder text = new();

        text.AppendLine("--- stdout ---");

        text.AppendLine(runResult.Stdout.Text);

        if (runResult.Stdout.Truncated)
        {
            text.AppendLine($"[truncated: exceeded {perStreamCapBytes} bytes]");
        }

        text.AppendLine("--- stderr ---");

        text.AppendLine(runResult.Stderr.Text);

        if (runResult.Stderr.Truncated)
        {
            text.AppendLine($"[truncated: exceeded {perStreamCapBytes} bytes]");
        }

        text.Append("--- exit code ---\n");

        text.Append(runResult.ExitCode);

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = text.ToString() },
            ],
            IsError = false,
        };
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

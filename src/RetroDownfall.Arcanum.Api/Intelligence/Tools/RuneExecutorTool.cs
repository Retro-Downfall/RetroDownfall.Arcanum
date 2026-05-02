using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

public sealed class RuneExecutorTool : AIFunction
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """

        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "Executable name, e.g. python or dotnet."
            },
            "arguments": {
              "type": "string",
              "description": "Full argument string passed to the executable."
            },
            "spellDirectory": {
              "type": "string",
              "description": "An optional relative path to a specific spell directory to execute the command within. Defaults to workspace root."
            }
          },
          "required": ["command", "arguments"],
          "additionalProperties": false
        }

        """);

    private readonly string? _workspaceRoot;

    private readonly string? _workspaceConfigurationError;

    public RuneExecutorTool(string workingDirectory)
    {
        if (!ToolHelpers.TryNormalizeWorkspace(workingDirectory, out string? root, out string? err))
        {
            _workspaceRoot = null;

            _workspaceConfigurationError = err;

            return;
        }

        _workspaceRoot = root;

        _workspaceConfigurationError = null;
    }

    public override string Name => "invoke_rune";

    public override string Description =>

        "Executes a CLI command (e.g., python, bash, dotnet) in the workspace and returns the console output. Use this to run scripts, size trades, or build code. Optional spellDirectory sets the process working directory to a path relative to the workspace (for encapsulated spell folders).";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_workspaceConfigurationError is not null)
        {
            return _workspaceConfigurationError;
        }

        if (_workspaceRoot is null)
        {
            return "The workspace is not configured for command execution on this request.";
        }

        if (!ToolHelpers.TryGetRequiredStringArgument(arguments, "command", out string? command, out string? cmdError))
        {
            return cmdError;
        }

        if (!ToolHelpers.TryGetRequiredStringArgument(arguments, "arguments", out string? argumentsLine, out string? argsError))
        {
            return argsError;
        }

        string workingDir = _workspaceRoot;

        if (ToolHelpers.TryGetOptionalStringArgument(arguments, "spellDirectory", out string? spellDirectory))
        {
            if (Path.IsPathRooted(spellDirectory))
            {
                return

                    "The spellDirectory must be relative to the workspace (absolute paths are not allowed). Please use a path like `spells/kalshi-trade` under the project directory.";
            }

            string combined;

            try
            {
                combined = Path.GetFullPath(Path.Combine(_workspaceRoot, spellDirectory));
            }
            catch (Exception)
            {
                return "That spell directory path could not be resolved. Please simplify the path and try again.";
            }

            if (!ToolHelpers.IsPathUnderWorkspace(_workspaceRoot, combined))
            {
                return

                    "That spellDirectory would leave the workspace sandbox, so the command was not run. Please stay within the project directory.";
            }

            if (!Directory.Exists(combined))
            {
                return

                    $"There is no directory at `{spellDirectory}` in the workspace (after resolving paths). Use a relative path to an existing spell folder.";
            }

            workingDir = combined;
        }

        using var timeoutCts = new CancellationTokenSource(RunTimeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        CancellationToken linkedToken = linked.Token;

        using (Process process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = argumentsLine,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            process.EnableRaisingEvents = true;

            try
            {
                if (!process.Start())
                {
                    return $"Could not start `{command}`. The executable may be missing or not on PATH.";
                }
            }
            catch (Exception ex)
            {
                return $"Failed to start the process: {ex.Message}";
            }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linkedToken);

            Task<string> stderrTask = process.StandardError.ReadToEndAsync(linkedToken);

            try
            {
                await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKillEntireTree(process);

                return

                    "The command exceeded the 30 second time limit and was stopped so the system stays responsive. Try a quicker command, smaller input, or ask the operator to run it manually.";
            }

            string stdout;

            string stderr;

            try
            {
                stdout = await stdoutTask.ConfigureAwait(false);

                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKillEntireTree(process);

                return

                    "The command exceeded the 30 second time limit while reading output and was stopped.";
            }

            var sb = new StringBuilder(stdout.Length + stderr.Length + 128);

            sb.AppendLine("=== stdout ===");

            sb.AppendLine(stdout);

            sb.AppendLine("=== stderr ===");

            sb.AppendLine(stderr);

            sb.Append("=== exit code ===");

            sb.AppendLine();

            sb.Append(process.ExitCode);

            return sb.ToString();
        }
    }

    private static void TryKillEntireTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}

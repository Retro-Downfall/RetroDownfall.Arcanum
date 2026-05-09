using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

public sealed class ArcanumSpellScriptTool : AIFunction
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """

        {
          "type": "object",
          "properties": {
            "script_name": { "type": "string", "description": "File name only of a script under the spell scripts/ folder (e.g. analyze.py)." },
            "arguments": { "type": "string", "description": "Optional extra arguments for the script, space-separated; use quotes for tokens containing spaces." }
          },
          "required": ["script_name"],
          "additionalProperties": false
        }

        """);

    private readonly string? _scriptsRootFull;

    private readonly TimeSpan _executeTimeout;

    private readonly int _executeTimeoutSeconds;

    public ArcanumSpellScriptTool(string scriptsDirectoryPath, TimeSpan executeTimeout, int executeTimeoutSecondsForDisplay)
    {
        _executeTimeout = executeTimeout;

        _executeTimeoutSeconds = executeTimeoutSecondsForDisplay;

        try
        {
            _scriptsRootFull = Path.GetFullPath(scriptsDirectoryPath);
        }
        catch
        {
            _scriptsRootFull = null;
        }
    }

    public override string Name => "run_spell_script";

    public override string Description =>
        $"Runs a script file only from the active spell's scripts/ directory (no path traversal). "
        + $"Stdout and stderr are captured; execution uses UseShellExecute=false. "
        + $"Hard timeout {_executeTimeoutSeconds}s with full process tree termination on timeout or cancel.";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_scriptsRootFull is null || _scriptsRootFull.Length == 0)
        {
            return "run_spell_script: scripts directory path could not be resolved.";
        }

        if (!TryGetStringArgument(arguments, "script_name", out string? scriptName) || string.IsNullOrWhiteSpace(scriptName))
        {
            return "run_spell_script: 'script_name' is required and must be a non-empty string.";
        }

        scriptName = scriptName.Trim();

        if (!string.Equals(Path.GetFileName(scriptName), scriptName, StringComparison.Ordinal))
        {
            return "run_spell_script: 'script_name' must be a bare file name (no directories or path separators).";
        }

        string candidate = Path.GetFullPath(Path.Combine(_scriptsRootFull, scriptName));

        if (!IsPathUnderRoot(_scriptsRootFull, candidate))
        {
            return "run_spell_script: resolved path would leave the spell scripts directory; request rejected.";
        }

        if (!File.Exists(candidate))
        {
            return $"run_spell_script: script not found: '{scriptName}'.";
        }

        _ = TryGetStringArgument(arguments, "arguments", out string? extraArgs);

        ProcessStartInfo psi = BuildProcessStartInfo(_scriptsRootFull, candidate, extraArgs);

        using Process process = new();

        process.StartInfo = psi;

        using CancellationTokenSource timeoutCts = new(_executeTimeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        CancellationToken waitToken = linked.Token;

        try
        {
            if (!process.Start())
            {
                return "run_spell_script: failed to start the process.";
            }
        }
        catch (IOException)
        {
            return "run_spell_script: failed to start the script process.";
        }
        catch (UnauthorizedAccessException)
        {
            return "run_spell_script: failed to start the script process.";
        }
        catch (OperationCanceledException)
        {
            return "run_spell_script: canceled before start completed.";
        }
        catch (InvalidOperationException)
        {
            return "run_spell_script: failed to start the script process.";
        }
        catch (Win32Exception)
        {
            return "run_spell_script: failed to start the script process.";
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(waitToken);

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(waitToken);

        try
        {
            await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessEntireTree(process);

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return $"run_spell_script: the command timed out after {_executeTimeoutSeconds} seconds.";
            }

            return "run_spell_script: canceled.";
        }

        string stdout;

        string stderr;

        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);

            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (IOException)
        {
            return "run_spell_script: failed to read process output.";
        }
        catch (UnauthorizedAccessException)
        {
            return "run_spell_script: failed to read process output.";
        }
        catch (OperationCanceledException)
        {
            return "run_spell_script: canceled while reading output.";
        }

        var text = new StringBuilder();

        text.AppendLine("--- stdout ---");

        text.AppendLine(stdout);

        text.AppendLine("--- stderr ---");

        text.AppendLine(stderr);

        text.Append("--- exit code ---\n");

        text.Append(process.ExitCode);

        return text.ToString();
    }

    private static ProcessStartInfo BuildProcessStartInfo(string scriptsRootFull, string scriptFullPath, string? argumentsLine)
    {
        string ext = Path.GetExtension(scriptFullPath);

        ProcessStartInfo psi = new()
        {
            UseShellExecute = false,

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            CreateNoWindow = true,

            StandardOutputEncoding = Utf8NoBom,

            StandardErrorEncoding = Utf8NoBom,

            WorkingDirectory = scriptsRootFull,
        };

        if (string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = OperatingSystem.IsWindows() ? "python" : "python3";

            psi.ArgumentList.Add(scriptFullPath);

            AppendTokenizedArguments(psi.ArgumentList, argumentsLine);
        }
        else if (string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "node";

            psi.ArgumentList.Add(scriptFullPath);

            AppendTokenizedArguments(psi.ArgumentList, argumentsLine);
        }
        else if (string.Equals(ext, ".sh", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "bash";

            psi.ArgumentList.Add(scriptFullPath);

            AppendTokenizedArguments(psi.ArgumentList, argumentsLine);
        }
        else if (string.Equals(ext, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "pwsh";

            psi.ArgumentList.Add("-File");

            psi.ArgumentList.Add(scriptFullPath);

            AppendTokenizedArguments(psi.ArgumentList, argumentsLine);
        }
        else
        {
            psi.FileName = scriptFullPath;

            AppendTokenizedArguments(psi.ArgumentList, argumentsLine);
        }

        return psi;
    }

    private static void AppendTokenizedArguments(ICollection<string> argumentList, string? argumentsLine)
    {
        if (string.IsNullOrWhiteSpace(argumentsLine))
        {
            return;
        }

        foreach (string token in TokenizeArguments(argumentsLine))
        {
            argumentList.Add(token);
        }
    }

    private static IEnumerable<string> TokenizeArguments(string line)
    {
        var current = new StringBuilder();

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
                    yield return current.ToString();

                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool TryGetStringArgument(AIFunctionArguments arguments, string key, out string? value)
    {
        value = null;

        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            if (!string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            value = CoerceToString(pair.Value);

            return true;
        }

        return false;
    }

    private static string? CoerceToString(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;

            case string s:
                return s;

            case JsonElement je when je.ValueKind == JsonValueKind.String:
                return je.GetString();

            case JsonElement je when je.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return je.ToString();

            default:
                return raw.ToString();
        }
    }

    private static bool IsPathUnderRoot(string rootFullPath, string candidateFullPath)
    {
        char sep = Path.DirectorySeparatorChar;

        string normalizedRoot = rootFullPath.TrimEnd(sep);

        string prefix = normalizedRoot + sep;

        StringComparison cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidateFullPath.Equals(normalizedRoot, cmp) || candidateFullPath.StartsWith(prefix, cmp);
    }

    private static void TryKillProcessEntireTree(Process process)
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
        catch (NotSupportedException)
        {
        }
    }
}

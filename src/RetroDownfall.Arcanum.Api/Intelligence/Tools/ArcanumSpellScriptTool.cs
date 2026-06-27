using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

[ExcludeFromCodeCoverage] // Reason: executes external spell scripts via AIFunction; covered via ArcanumSpellScriptToolMultiRootTests and spell integration paths.
public sealed class ArcanumSpellScriptTool : AIFunction
{
    public const string ToolName = "run_spell_script";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """

        {
          "type": "object",
          "properties": {
            "script_name": { "type": "string", "description": "File name only of a script under the active spell or resonant dependency scripts/ folder (e.g. analyze.py)." },
            "arguments": { "type": "string", "description": "Optional extra arguments for the script, space-separated; use quotes for tokens containing spaces." }
          },
          "required": ["script_name"],
          "additionalProperties": false
        }

        """);

    private readonly IReadOnlyList<string> _scriptsRootsFull;

    private readonly TimeSpan _executeTimeout;

    private readonly int _executeTimeoutSeconds;

    private readonly long _toolOutputCapBytes;

    private readonly ILogger? _logger;

    public ArcanumSpellScriptTool(
        IReadOnlyList<string> scriptsDirectoryPaths,
        TimeSpan executeTimeout,
        int executeTimeoutSecondsForDisplay,
        long toolOutputCapBytes = 1L * 1024L * 1024L,
        ILogger? logger = null)
    {
        _executeTimeout = executeTimeout;

        _executeTimeoutSeconds = executeTimeoutSecondsForDisplay;

        _toolOutputCapBytes = toolOutputCapBytes < 2048L ? 2048L : toolOutputCapBytes;

        _logger = logger;

        var roots = new List<string>(scriptsDirectoryPaths.Count);

        foreach (string path in scriptsDirectoryPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                string full = Path.GetFullPath(path);

                if (full.Length > 0)
                {
                    roots.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
            }
        }

        _scriptsRootsFull = roots;
    }

    public ArcanumSpellScriptTool(
        string scriptsDirectoryPath,
        TimeSpan executeTimeout,
        int executeTimeoutSecondsForDisplay,
        long toolOutputCapBytes = 1L * 1024L * 1024L,
        ILogger? logger = null)
        : this([scriptsDirectoryPath], executeTimeout, executeTimeoutSecondsForDisplay, toolOutputCapBytes, logger)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        $"Runs a script file only from the active spell and its resonant dependency scripts/ directories (no path traversal). "
        + $"Stdout and stderr are captured; execution uses UseShellExecute=false. "
        + $"Hard timeout {_executeTimeoutSeconds}s with full process tree termination on timeout or cancel.";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_scriptsRootsFull.Count == 0)
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

        List<(string Root, string Candidate)> matches = FindScriptMatches(scriptName);

        if (matches.Count == 0)
        {
            return $"run_spell_script: script not found: '{scriptName}'.";
        }

        if (matches.Count > 1)
        {
            string rootsList = string.Join(", ", matches.Select(static m => m.Root));

            _logger?.LogWarning(
                "run_spell_script ambiguous script {Script} across roots {Roots}",
                scriptName,
                rootsList);

            return $"run_spell_script: ambiguous script '{scriptName}'; it exists in multiple resonant spells ({rootsList}). Rename the script to a unique file name.";
        }

        (string scriptsRootFull, string candidate) = matches[0];

        if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(scriptsRootFull, candidate, out _))
        {
            return "run_spell_script: resolved path leaves the spell scripts directory; request rejected.";
        }

        _ = TryGetStringArgument(arguments, "arguments", out string? extraArgs);

        ProcessStartInfo psi = BuildProcessStartInfo(scriptsRootFull, candidate, extraArgs);

        using Process process = new();

        process.StartInfo = psi;

        using CancellationTokenSource timeoutCts = new(_executeTimeout);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        CancellationToken waitToken = linked.Token;

        try
        {
            if (!File.Exists(candidate))
            {
                return $"run_spell_script: script not found: '{scriptName}'.";
            }

            if (!ToolHelpers.RevalidatePathBeforeIo(scriptsRootFull, candidate))
            {
                return "run_spell_script: resolved path leaves the spell scripts directory; request rejected.";
            }

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

        CancellationTokenRegistration killRegistration = waitToken.Register(
            static state => TryKillProcessEntireTree((Process)state!),
            process);

        long perStreamCap = _toolOutputCapBytes / 2L;

        if (perStreamCap < 1024L)
        {
            perStreamCap = 1024L;
        }

        try
        {
            Task<CappedOutput> stdoutTask = ReadStreamCappedAsync(process.StandardOutput, perStreamCap, waitToken);

            Task<CappedOutput> stderrTask = ReadStreamCappedAsync(process.StandardError, perStreamCap, waitToken);

            try
            {
                await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessEntireTree(process);

                await ObserveStreamReadTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);

                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return $"run_spell_script: the command timed out after {_executeTimeoutSeconds} seconds.";
                }

                return "run_spell_script: canceled.";
            }

            CappedOutput stdout;

            CappedOutput stderr;

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

            text.AppendLine(stdout.Text);

            if (stdout.Truncated)
            {
                text.AppendLine($"[truncated: exceeded {perStreamCap} bytes]");
            }

            text.AppendLine("--- stderr ---");

            text.AppendLine(stderr.Text);

            if (stderr.Truncated)
            {
                text.AppendLine($"[truncated: exceeded {perStreamCap} bytes]");
            }

            text.Append("--- exit code ---\n");

            text.Append(process.ExitCode);

            return text.ToString();
        }
        finally
        {
            await killRegistration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private List<(string Root, string Candidate)> FindScriptMatches(string scriptName)
    {
        var matches = new List<(string Root, string Candidate)>();

        foreach (string scriptsRootFull in _scriptsRootsFull)
        {
            string candidate;

            try
            {
                candidate = Path.GetFullPath(Path.Combine(scriptsRootFull, scriptName));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            if (!IsPathUnderRoot(scriptsRootFull, candidate))
            {
                continue;
            }

            if (!File.Exists(candidate))
            {
                continue;
            }

            if (!ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(scriptsRootFull, candidate, out _))
            {
                continue;
            }

            matches.Add((scriptsRootFull, candidate));
        }

        return matches;
    }

    private static async Task ObserveStreamReadTasksAsync(
        Task<CappedOutput> stdoutTask,
        Task<CappedOutput> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private readonly record struct CappedOutput(string Text, bool Truncated);

    private static async Task<CappedOutput> ReadStreamCappedAsync(
        StreamReader reader,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new();

        char[] buffer = new char[4096];

        long approximateBytes = 0L;

        bool truncated = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {
                break;
            }

            long encodedSize = Encoding.UTF8.GetByteCount(buffer, 0, read);

            if (approximateBytes + encodedSize > maxBytes)
            {
                long remaining = maxBytes - approximateBytes;

                if (remaining > 0)
                {
                    int safeChars = ChooseSafeCharCount(buffer, read, remaining);

                    builder.Append(buffer, 0, safeChars);
                }

                truncated = true;

                break;
            }

            builder.Append(buffer, 0, read);

            approximateBytes += encodedSize;
        }

        return new CappedOutput(builder.ToString(), truncated);
    }

    private static int ChooseSafeCharCount(char[] buffer, int charCount, long remainingBytes)
    {
        long running = 0L;

        for (int i = 0; i < charCount; i++)
        {
            int charByteSize = Encoding.UTF8.GetByteCount(buffer, i, 1);

            if (running + charByteSize > remainingBytes)
            {
                return i;
            }

            running += charByteSize;
        }

        return charCount;
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
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
            }
        }
    }
}

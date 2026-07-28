using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

[ExcludeFromCodeCoverage] // Reason: executes external spell scripts via AIFunction; covered via ArcanumSpellScriptToolMultiRootTests and spell integration paths.
public sealed class ArcanumSpellScriptTool : AIFunction
{
    public const string ToolName =
        ArcanumBuiltInToolNames.RunSpellScript;

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

    /// <summary>
    /// The normalized full <c>scripts/</c> roots this tool will resolve against (the active spell plus
    /// any Arcane Resonance dependencies). Exposed so the Sanctum preflight can validate every candidate
    /// root, not just the active spell's.
    /// </summary>
    internal IReadOnlyList<string> ScriptRoots => _scriptsRootsFull;

    private readonly TimeSpan _executeTimeout;

    private readonly int _executeTimeoutSeconds;

    private readonly long _toolOutputCapBytes;

    private readonly ILogger? _logger;

    /// <summary>
    /// Null when this tool is constructed without Sanctum/DI context (e.g. direct unit-test
    /// construction). In that case OS-level resource-limit enforcement is skipped for this tool
    /// instance rather than applying an unconfigured default (see <see cref="RetroDownfall.Arcanum.Infrastructure.ProcessExecution.CappedChildProcessRunner"/>).
    /// </summary>
    private readonly ISanctumGuard? _sanctumGuard;

    private readonly IProcessResourceLimiter? _resourceLimiter;

    private readonly string? _campaignWorkspaceRoot;

    private readonly bool _allowUnsandboxedToolChildren;

    public ArcanumSpellScriptTool(
        IReadOnlyList<string> scriptsDirectoryPaths,
        TimeSpan executeTimeout,
        int executeTimeoutSecondsForDisplay,
        long toolOutputCapBytes = 1L * 1024L * 1024L,
        ILogger? logger = null,
        ISanctumGuard? sanctumGuard = null,
        IProcessResourceLimiter? resourceLimiter = null,
        string? campaignWorkspaceRoot = null,
        bool allowUnsandboxedToolChildren = false)
    {
        _executeTimeout = executeTimeout;

        _executeTimeoutSeconds = executeTimeoutSecondsForDisplay;

        _toolOutputCapBytes = toolOutputCapBytes < 2048L ? 2048L : toolOutputCapBytes;

        _logger = logger;

        _sanctumGuard = sanctumGuard;

        _resourceLimiter = resourceLimiter;

        _campaignWorkspaceRoot = campaignWorkspaceRoot;

        _allowUnsandboxedToolChildren = allowUnsandboxedToolChildren;

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
        ILogger? logger = null,
        ISanctumGuard? sanctumGuard = null,
        IProcessResourceLimiter? resourceLimiter = null,
        string? campaignWorkspaceRoot = null,
        bool allowUnsandboxedToolChildren = false)
        : this(
            [scriptsDirectoryPath],
            executeTimeout,
            executeTimeoutSecondsForDisplay,
            toolOutputCapBytes,
            logger,
            sanctumGuard,
            resourceLimiter,
            campaignWorkspaceRoot,
            allowUnsandboxedToolChildren)
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
        if (!HostProcessToolPolicy.AreAllowed(ArcanumEnvironment.ResolveEdition(ArcanumEdition.Local)))
        {
            return HostProcessToolPolicy.DeniedMessage;
        }

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

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(scriptsRootFull, candidate, out _))
        {
            return "run_spell_script: resolved path leaves the spell scripts directory; request rejected.";
        }

        // W3.5: allow-list the documented interpreter-wrapped script types. Without this, an
        // unknown extension (e.g. a shipped .exe or extensionless binary in an imported spell's
        // scripts/ dir) fell through to being launched directly as a process image.
        string scriptExtension = Path.GetExtension(candidate);

        if (!IsAllowedScriptExtension(scriptExtension))
        {
            return $"run_spell_script: unsupported script type '{scriptExtension}'. Allowed extensions: .py, .js, .sh, .ps1.";
        }

        _ = TryGetStringArgument(arguments, "arguments", out string? extraArgs);

        ProcessStartInfo psi = BuildProcessStartInfo(scriptsRootFull, candidate, extraArgs);

        try
        {

            string? resolvedScript = File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName;

            if (!string.IsNullOrEmpty(resolvedScript))
            {

                candidate = Path.GetFullPath(resolvedScript);

                // Rebuild so argv carries the realpath Seatbelt/Landlock will match.
                psi = BuildProcessStartInfo(
                    Path.GetDirectoryName(candidate) is string parent
                        ? parent
                        : scriptsRootFull,
                    candidate,
                    extraArgs);

                // Keep working directory on the resolved scripts root when possible.
                string? resolvedRoot = Directory.ResolveLinkTarget(scriptsRootFull, returnFinalTarget: true)?.FullName
                                       ?? scriptsRootFull;

                psi.WorkingDirectory = Path.GetFullPath(resolvedRoot);

            }

        }
        catch (Exception)
        {

        }

        try
        {

            if (!File.Exists(candidate))
            {

                return $"run_spell_script: script not found: '{scriptName}'.";

            }

            if (!WorkspacePathPolicy.RevalidatePathBeforeIo(scriptsRootFull, candidate))
            {

                return "run_spell_script: resolved path leaves the spell scripts directory; request rejected.";

            }

        }
        catch (OperationCanceledException)
        {

            return "run_spell_script: canceled before start completed.";

        }

        ResourceLimits? resourceLimits = _sanctumGuard is null
            ? null
            : await _sanctumGuard
                .GetEffectiveResourceLimitsForWorkspaceAsync(_campaignWorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);

        SanctumChildProcessBoundary? boundary = _sanctumGuard is null
            ? null
            : await _sanctumGuard
                .GetChildProcessBoundaryForWorkspaceAsync(_campaignWorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);

        ChildProcessSandboxRequest sandboxRequest = ChildProcessSandboxRoots.ForSpellScript(
            _scriptsRootsFull,
            _campaignWorkspaceRoot,
            boundary?.AllowedPaths,
            _allowUnsandboxedToolChildren,
            windowsPathBoundaryRequired: boundary?.PathBoundaryRequired == true);

        CappedChildProcessRunResult runResult = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            _toolOutputCapBytes,
            _executeTimeout,
            resourceLimits,
            _resourceLimiter,
            cancellationToken,
            sandboxRequest,
            _logger).ConfigureAwait(false);

        switch (runResult.Outcome)
        {

            case CappedChildProcessOutcome.FilesystemSandboxUnavailable:

            case CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum:

                return runResult.FilesystemSandboxDenialMessage
                       ?? ChildProcessSandboxMessages.SandboxUnavailable;

            case CappedChildProcessOutcome.ResourceLimitApplyFailed:

                _logger?.LogError(
                    "run_spell_script: failed to apply Sanctum resource limits: {Error}",
                    runResult.ResourceLimitApplyError);

                return "run_spell_script: the invocation was blocked because OS-level resource limits could not be applied.";

            case CappedChildProcessOutcome.ResourceLimitExceeded when _sanctumGuard is not null:

                return await ResourceLimitDenialFormatter.RecordAndDescribeAsync(
                    _sanctumGuard,
                    _campaignWorkspaceRoot,
                    ToolName,
                    resourceLimits ?? new ResourceLimits(),
                    runResult.ExceededResource,
                    cancellationToken).ConfigureAwait(false);

            case CappedChildProcessOutcome.ResourceLimitExceeded:

                return "run_spell_script: execution blocked after exceeding an OS-enforced resource limit.";

            case CappedChildProcessOutcome.IoErrorOnStart:

            case CappedChildProcessOutcome.AccessDeniedOnStart:

            case CappedChildProcessOutcome.FailedToStart:

                return "run_spell_script: failed to start the script process.";

            case CappedChildProcessOutcome.CanceledBeforeStart:

                return "run_spell_script: canceled before start completed.";

            case CappedChildProcessOutcome.TimedOut:

                return $"run_spell_script: the command timed out after {_executeTimeoutSeconds} seconds.";

            case CappedChildProcessOutcome.Canceled:

                return "run_spell_script: canceled.";

            case CappedChildProcessOutcome.IoErrorReadingOutput:

            case CappedChildProcessOutcome.AccessDeniedReadingOutput:

                return "run_spell_script: failed to read process output.";

            case CappedChildProcessOutcome.CanceledWhileReadingOutput:

                return "run_spell_script: canceled while reading output.";

            case CappedChildProcessOutcome.Completed:

                break;

            default:

                return "run_spell_script: failed to start the script process.";

        }

        long perStreamCap = runResult.PerStreamCapBytes;

        var text = new StringBuilder();

        text.AppendLine("--- stdout ---");

        text.AppendLine(runResult.Stdout.Text);

        if (runResult.Stdout.Truncated)
        {

            text.AppendLine($"[truncated: exceeded {perStreamCap} bytes]");

        }

        text.AppendLine("--- stderr ---");

        text.AppendLine(runResult.Stderr.Text);

        if (runResult.Stderr.Truncated)
        {

            text.AppendLine($"[truncated: exceeded {perStreamCap} bytes]");

        }

        text.Append("--- exit code ---\n");

        text.Append(runResult.ExitCode);

        return text.ToString();
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

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(scriptsRootFull, candidate, out _))
            {
                continue;
            }

            matches.Add((scriptsRootFull, candidate));
        }

        return matches;
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
            psi.FileName = OperatingSystem.IsWindows() ? "python" : "/usr/bin/env";

            if (!OperatingSystem.IsWindows())
            {

                psi.ArgumentList.Add("python3");

            }

            psi.ArgumentList.Add(scriptFullPath);

            AppendTokenizedArguments(psi.ArgumentList, argumentsLine);
        }
        else if (string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = OperatingSystem.IsWindows() ? "node" : "/usr/bin/env";

            if (!OperatingSystem.IsWindows())
            {

                psi.ArgumentList.Add("node");

            }

            psi.ArgumentList.Add(scriptFullPath);

            AppendTokenizedArguments(psi.ArgumentList, argumentsLine);
        }
        else if (string.Equals(ext, ".sh", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = OperatingSystem.IsWindows() ? "bash" : "/bin/bash";

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
            // W3.5: defense-in-depth — callers allow-list the extension first, so an unknown type
            // must never reach here and be launched directly as a process image.
            throw new InvalidOperationException(
                $"run_spell_script: unsupported script extension '{ext}'.");
        }

        return psi;
    }

    private static readonly HashSet<string> AllowedScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py",
        ".js",
        ".sh",
        ".ps1",
    };

    private static bool IsAllowedScriptExtension(string extension) =>
        AllowedScriptExtensions.Contains(extension);

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
}

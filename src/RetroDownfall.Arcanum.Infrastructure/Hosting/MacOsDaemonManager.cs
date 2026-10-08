using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Xml;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: launchd interop, macOS-only
public sealed class MacOsDaemonManager : IDaemonManager
{
    internal const string LaunchdLabel = "com.retrodownfall.arcanum";
    internal const string NotLoadedMessage = "Daemon is not currently loaded";
    private static readonly string DefaultPlistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        "com.retrodownfall.arcanum.plist");
    private readonly IDaemonProcessRunner _runner;

    private readonly string _plistPath;

    public MacOsDaemonManager()
        : this(DaemonProcessRunner.Default, DefaultPlistPath)
    {
    }

    internal MacOsDaemonManager(IDaemonProcessRunner runner, string plistPath)
    {
        _runner = runner;
        _plistPath = plistPath;
    }

    public bool RequiresServiceAccount => false;

    public async Task<Result> InstallAsync(DaemonInstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (DaemonInstallRequestPolicy.RefuseServiceAccount(request, "A launchd user agent") is { } refused)
        {
            return Result.Failure(refused);
        }

        Result<string> uidResult = await TryResolveUidAsync(cancellationToken).ConfigureAwait(false);
        if (uidResult.IsFailure)
        {
            return Result.Failure(uidResult.Error);
        }

        string uid = uidResult.Value;
        string plistXml = BuildLaunchAgentPlistXml();
        Result writeResult = await WritePlistAtomicallyAsync(plistXml, cancellationToken).ConfigureAwait(false);
        if (writeResult.IsFailure)
        {
            return writeResult;
        }

        string guiDomain = string.Create(CultureInfo.InvariantCulture, $"gui/{uid}");

        // A label that is already loaded makes bootstrap fail with an opaque I/O error, so a reinstall
        // boots it out first. A label that is not loaded is the ordinary first install.
        Result bootedOut = await BootoutAsync(guiDomain, cancellationToken).ConfigureAwait(false);
        if (bootedOut.IsFailure)
        {
            return bootedOut;
        }

        DaemonProcessOutcome bootstrapOutcome = await _runner.RunAsync(
            "/bin/launchctl",
            ["bootstrap", guiDomain, _plistPath],
            cancellationToken).ConfigureAwait(false);
        if (bootstrapOutcome.FatalError is { } fatalBootstrap)
        {
            return Result.Failure(fatalBootstrap);
        }

        if (bootstrapOutcome.ExitCode != 0)
        {
            return Result.Failure(
                ToolError(
                    "DaemonBootstrap",
                    "launchctl bootstrap failed.",
                    bootstrapOutcome.StdErr,
                    bootstrapOutcome.ExitCode));
        }

        return Result.Success();
    }

    public async Task<Result> UninstallAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_plistPath))
        {
            return Result.Success();
        }
        Result<string> uidResult = await TryResolveUidAsync(cancellationToken).ConfigureAwait(false);
        if (uidResult.IsFailure)
        {
            return Result.Failure(uidResult.Error);
        }

        string uid = uidResult.Value;
        string guiDomain = string.Create(CultureInfo.InvariantCulture, $"gui/{uid}");
        // An agent that is not loaded (never bootstrapped, or already unloaded by a logout or a manual
        // bootout) leaves nothing to stop, so the plist is still removed rather than kept forever.
        Result bootedOut = await BootoutAsync(guiDomain, cancellationToken).ConfigureAwait(false);
        if (bootedOut.IsFailure)
        {
            return bootedOut;
        }

        try
        {
            if (File.Exists(_plistPath))
            {
                File.Delete(_plistPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new Error("DaemonPlistDelete", ex.Message));
        }

        return Result.Success();
    }

    public async Task<Result<string>> GetStatusAsync(CancellationToken cancellationToken)
    {
        DaemonProcessOutcome listOutcome = await _runner.RunAsync(
            "/bin/launchctl",
            ["list", LaunchdLabel],
            cancellationToken).ConfigureAwait(false);
        if (listOutcome.FatalError is { } fatal)
        {
            return Result<string>.Failure(fatal);
        }

        if (listOutcome.ExitCode != 0)
        {
            if (IndicatesPermissionDenied(listOutcome.StdErr))
            {
                return Result<string>.Failure(
                    ToolError(
                        "DaemonLaunchctlList",
                        "launchctl list failed.",
                        listOutcome.StdErr,
                        listOutcome.ExitCode));
            }

            return Result<string>.Success(NotLoadedMessage);
        }

        if (string.IsNullOrWhiteSpace(listOutcome.StdOut))
        {
            return Result<string>.Success(NotLoadedMessage);
        }

        string[] lines = listOutcome.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? pidToken;

        // Given a label, launchctl prints the job as a dictionary ("Label" = "...";, "PID" = 1334;),
        // whose PID entry is absent while the loaded job is not running. Only the label-less listing
        // prints the tab-separated PID, Status and Label table, which older releases also used here.
        if (lines.Length > 0 && lines[0].StartsWith('{'))
        {
            if (!lines.Contains($"\"Label\" = \"{LaunchdLabel}\";", StringComparer.Ordinal))
            {
                return Result<string>.Failure(
                    new Error("DaemonStatusParse", "Could not parse launchctl list output."));
            }

            const string PidEntryPrefix = "\"PID\" = ";

            string? pidEntry = lines.FirstOrDefault(
                static l => l.StartsWith(PidEntryPrefix, StringComparison.Ordinal) && l.EndsWith(';'));

            pidToken = pidEntry?[PidEntryPrefix.Length..^1];
        }
        else
        {
            string line = lines.FirstOrDefault(l => l.Contains(LaunchdLabel, StringComparison.Ordinal)) ?? string.Empty;
            if (string.IsNullOrEmpty(line))
            {
                return Result<string>.Success(NotLoadedMessage);
            }

            string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !string.Equals(parts[2].Trim(), LaunchdLabel, StringComparison.Ordinal))
            {
                return Result<string>.Failure(
                    new Error("DaemonStatusParse", "Could not parse launchctl list output."));
            }

            pidToken = parts[0].Trim();
        }

        if (pidToken is null || pidToken == "-" || !int.TryParse(pidToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
        {
            return Result<string>.Success(NotLoadedMessage);
        }

        return Result<string>.Success(string.Create(CultureInfo.InvariantCulture, $"Daemon is running (PID {pid})."));
    }

    /// <summary>
    /// Boots the agent out of the user's GUI domain. Success covers both a loaded agent that was unloaded and
    /// one that was not loaded to begin with, and a "not loaded" answer is confirmed against the agent list before it
    /// is believed; any other failure keeps its launchctl diagnostics.
    /// </summary>
    private async Task<Result> BootoutAsync(string guiDomain, CancellationToken cancellationToken)
    {
        DaemonProcessOutcome bootoutOutcome = await _runner.RunAsync(
            "/bin/launchctl",
            ["bootout", guiDomain, _plistPath],
            cancellationToken).ConfigureAwait(false);
        if (bootoutOutcome.FatalError is { } fatalBootout)
        {
            return Result.Failure(fatalBootout);
        }

        if (bootoutOutcome.ExitCode == 0)
        {
            return Result.Success();
        }

        if (!IndicatesNotLoaded(bootoutOutcome.ExitCode, bootoutOutcome.StdErr))
        {
            return Result.Failure(
                ToolError(
                    "DaemonBootout",
                    "launchctl bootout failed.",
                    bootoutOutcome.StdErr,
                    bootoutOutcome.ExitCode));
        }

        // The not-loaded answers are matched by exit code and text that differ between macOS releases, and exit 5
        // (EIO) is also what launchctl says when a loaded agent could not be unloaded, so a "not loaded" answer is
        // confirmed against the agent list before the plist is allowed to go. Otherwise an uninstall would delete the
        // plist and leave the agent loaded.
        DaemonProcessOutcome stillLoaded = await _runner.RunAsync(
            "/bin/launchctl",
            ["list", LaunchdLabel],
            cancellationToken).ConfigureAwait(false);
        if (stillLoaded.FatalError is { } fatalList)
        {
            return Result.Failure(fatalList);
        }

        if (stillLoaded.ExitCode == 0)
        {
            return Result.Failure(
                ToolError(
                    "DaemonBootout",
                    "launchctl bootout reported the agent as not loaded, but it is still loaded.",
                    bootoutOutcome.StdErr,
                    bootoutOutcome.ExitCode));
        }

        return Result.Success();
    }

    /// <summary>
    /// launchctl reports an agent that is not loaded as ESRCH (3), EIO (5), 36 or 113 ("Could not find specified
    /// service"), depending on the macOS release; the stderr text is matched too for releases that exit 1.
    /// </summary>
    private static bool IndicatesNotLoaded(int exitCode, string stderr)
    {
        if (exitCode is 3 or 5 or 36 or 113)
        {
            return true;
        }

        return stderr.Contains("No such process", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Could not find specified service", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Could not find service", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndicatesPermissionDenied(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return false;
        }

        return stderr.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Result<string>> TryResolveUidAsync(CancellationToken cancellationToken)
    {
        DaemonProcessOutcome idOutcome = await _runner.RunAsync(
            "/usr/bin/id",
            ["-u"],
            cancellationToken).ConfigureAwait(false);
        if (idOutcome.FatalError is { } fatal)
        {
            return Result<string>.Failure(fatal);
        }

        if (idOutcome.ExitCode != 0)
        {
            return Result<string>.Failure(
                ToolError("DaemonUidResolution", "id -u failed.", idOutcome.StdErr, idOutcome.ExitCode));
        }

        string trimmed = idOutcome.StdOut.Trim();
        if (string.IsNullOrEmpty(trimmed) || !uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return Result<string>.Failure(new Error("DaemonUidResolution", "id -u returned invalid UID output."));
        }

        return Result<string>.Success(trimmed);
    }

    private async Task<Result> WritePlistAtomicallyAsync(string plistXml, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_plistPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Result.Failure(new Error("DaemonPlistPath", "Invalid LaunchAgents plist path."));
        }
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".{LaunchdLabel}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, plistXml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _plistPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteQuiet(tempPath);
            return Result.Failure(new Error("DaemonPlistWrite", ex.Message));
        }

        return Result.Success();
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string BuildLaunchAgentPlistXml()
    {
        string processPath = Environment.ProcessPath ?? string.Empty;
        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
            CloseOutput = false,
        };
        using (XmlWriter writer = XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument(false);
            writer.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            writer.WriteStartElement("plist");
            writer.WriteAttributeString("version", "1.0");
            writer.WriteStartElement("dict");
            writer.WriteElementString("key", "Label");
            writer.WriteElementString("string", LaunchdLabel);
            writer.WriteElementString("key", "ProgramArguments");
            writer.WriteStartElement("array");
            writer.WriteElementString("string", processPath);
            writer.WriteElementString("string", "serve");
            writer.WriteEndElement();
            writer.WriteElementString("key", "RunAtLoad");
            writer.WriteStartElement("true");
            writer.WriteEndElement();
            writer.WriteElementString("key", "KeepAlive");
            writer.WriteStartElement("true");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Error ToolError(string code, string message, string stderr, int exitCode)
    {
        string trimmed = stderr.Trim();
        string suffix = string.IsNullOrEmpty(trimmed) ? $"Exit code {exitCode}." : trimmed;
        return new Error(code, $"{message} {suffix}".Trim());
    }
}

using System.ComponentModel;
using System.Diagnostics;
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
    private static readonly string PlistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        "com.retrodownfall.arcanum.plist");
    public async Task<Result> InstallAsync(CancellationToken cancellationToken)
    {
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
        DaemonProcessOutcome bootstrapOutcome = await RunProcessAsync(
            "/bin/launchctl",
            ["bootstrap", guiDomain, PlistPath],
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
        if (!File.Exists(PlistPath))
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
        DaemonProcessOutcome bootoutOutcome = await RunProcessAsync(
            "/bin/launchctl",
            ["bootout", guiDomain, PlistPath],
            cancellationToken).ConfigureAwait(false);
        if (bootoutOutcome.FatalError is { } fatalBootout)
        {
            return Result.Failure(fatalBootout);
        }

        if (bootoutOutcome.ExitCode != 0)
        {
            return Result.Failure(
                ToolError(
                    "DaemonBootout",
                    "launchctl bootout failed.",
                    bootoutOutcome.StdErr,
                    bootoutOutcome.ExitCode));
        }

        try
        {
            if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
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
        DaemonProcessOutcome listOutcome = await RunProcessAsync(
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

        string line = listOutcome.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Contains(LaunchdLabel, StringComparison.Ordinal)) ?? string.Empty;
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

        string pidToken = parts[0].Trim();
        if (pidToken == "-" || !int.TryParse(pidToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
        {
            return Result<string>.Success(NotLoadedMessage);
        }

        return Result<string>.Success(string.Create(CultureInfo.InvariantCulture, $"Daemon is running (PID {pid})."));
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

    private static async Task<Result<string>> TryResolveUidAsync(CancellationToken cancellationToken)
    {
        DaemonProcessOutcome idOutcome = await RunProcessAsync(
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

    private static async Task<Result> WritePlistAtomicallyAsync(string plistXml, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(PlistPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Result.Failure(new Error("DaemonPlistPath", "Invalid LaunchAgents plist path."));
        }
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".{LaunchdLabel}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, plistXml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, PlistPath, overwrite: true);
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

    /// <summary>
    /// Runs a launchd helper binary. A binary that cannot be started at all is reported as
    /// <see cref="DaemonProcessOutcome.FatalError"/> rather than thrown, so every caller stays inside
    /// the <see cref="Result"/> contract.
    /// </summary>
    internal static async Task<DaemonProcessOutcome> RunProcessAsync(
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process();
        process.StartInfo = startInfo;
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return new DaemonProcessOutcome(-1, string.Empty, string.Empty, StartError(fileName, ex));
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return new DaemonProcessOutcome(process.ExitCode, stdout, stderr, null);
    }

    private static Error StartError(string fileName, Exception ex)
    {
        return new Error("DaemonProcessStart", $"Could not start '{fileName}'. {ex.Message}");
    }
}

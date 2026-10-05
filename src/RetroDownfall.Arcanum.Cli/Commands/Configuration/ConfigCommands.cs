using System.Diagnostics;

using System.Text.Json;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Infrastructure.Security;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;

internal sealed class ConfigCommands(
    IConfigurationCommandService configurationService,
    IConsoleDispatcher console,
    CompendiumLauncher compendiumLauncher,
    ICliInvocationContext invocationContext)
{
    /// <summary>Opens the temporary configuration document for the operator; replaceable by tests.</summary>
    internal Func<string, CancellationToken, Task<Result>> RunEditorAsync { get; set; } = ConfigEditor.RunAsync;

    /// <summary>Removes the temporary document after the edit; replaceable by tests.</summary>
    internal Action<string> DeleteTemporaryFile { get; set; } = File.Delete;

    public int Path()
    {
        console.WritePayload(configurationService.ConfigurationPath);

        return (int)CliExitCode.Success;
    }

    public async Task<int> Show(CancellationToken cancellationToken)
    {
        Result<ConfigurationCommandSnapshot> read = await configurationService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            return Failure(read.Error);
        }

        ConfigurationCommandSnapshot snapshot = read.Value;

        ArcanumSettings redacted = ConfigurationRedactor.Redact(snapshot.EffectiveSettings());

        console.WritePayload(
            JsonSerializer.Serialize(
                redacted,
                ConfigurationJsonContext.Default.ArcanumSettings));

        DescribeAccess(snapshot);

        return (int)CliExitCode.Success;
    }

    public async Task<int> Get(
        string key,
        CancellationToken cancellationToken)
    {
        Result<ConfigurationCommandSnapshot> read = await configurationService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            return Failure(read.Error);
        }

        ArcanumSettings effective = read.Value.EffectiveSettings();

        if (!ConfigurationPathAccessor.Exists(effective, key))
        {
            console.WriteDiagnostic($"Unknown configuration key '{key}'.");

            return (int)CliExitCode.ConfigurationError;
        }

        console.WritePayload(
            ConfigurationPathAccessor.GetDisplayValue(effective, key));

        DescribeAccess(read.Value, key);

        return (int)CliExitCode.Success;
    }

    public async Task<int> Set(
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        Result<ConfigurationCommandSnapshot> read = await configurationService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            return Failure(read.Error);
        }

        ConfigurationCommandSnapshot snapshot = read.Value;

        string? pathError = ConfigurationPathAccessor.GetPathResolutionError(
            snapshot.Settings,
            key);

        if (pathError is not null)
        {
            console.WriteDiagnostic(pathError);

            return (int)CliExitCode.ConfigurationError;
        }

        string? resolvedValue = value;

        if (ConfigurationPathAccessor.IsSensitive(key))
        {
            if (!string.IsNullOrEmpty(value))
            {
                console.WriteDiagnostic(
                    "Sensitive configuration values must not be passed as command-line arguments. "
                    + "Omit <value> and enter it through redirected stdin or the hidden prompt.");

                return (int)CliExitCode.ConfigurationError;
            }

            SensitiveValueRead sensitive = await ReadSensitiveValueAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!sensitive.IsAvailable)
            {
                console.WriteDiagnostic(SensitiveValueInput.UnavailableDiagnostic);

                return (int)CliExitCode.ConfigurationError;
            }

            resolvedValue = sensitive.Value;
        }

        if (resolvedValue is null)
        {
            console.WriteDiagnostic("A configuration value is required.");

            return (int)CliExitCode.ConfigurationError;
        }

        ConfigurationPathUpdate update = ConfigurationPathAccessor.Set(
            snapshot.Settings,
            key,
            resolvedValue);

        if (!update.IsSuccess)
        {
            console.WriteDiagnostic(update.Error!);

            return (int)CliExitCode.ConfigurationError;
        }

        Result write = await configurationService
            .WriteAsync(snapshot, update.Settings!, cancellationToken)
            .ConfigureAwait(false);

        if (write.IsFailure)
        {
            return Failure(write.Error);
        }

        console.WritePayload(
            $"{key} = {ConfigurationPathAccessor.GetDisplayValue(snapshot.EffectiveSettings(update.Settings), key)}");

        DescribeAccess(snapshot);

        return (int)CliExitCode.Success;
    }

    public async Task<int> Validate(CancellationToken cancellationToken)
    {
        Result<ConfigurationCommandSnapshot> read = await configurationService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            return Failure(read.Error);
        }

        Result validation = await configurationService
            .ValidateAsync(read.Value, read.Value.Settings, cancellationToken)
            .ConfigureAwait(false);

        if (validation.IsFailure)
        {
            return Failure(validation.Error);
        }

        console.WritePayload("Configuration is valid.");

        DescribeAccess(read.Value);

        return (int)CliExitCode.Success;
    }

    public async Task<int> Edit(CancellationToken cancellationToken)
    {
        Result<ConfigurationCommandSnapshot> read = await configurationService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            return Failure(read.Error);
        }

        ConfigurationCommandSnapshot snapshot = read.Value;

        string tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"arcanum-edit-{Guid.NewGuid():N}.json");

        try
        {
            ArcanumConfigurationFile editable = new()
            {
                Arcanum = ConfigurationRedactor.Redact(snapshot.Settings),
            };

            await WriteSecureTemporaryFileAsync(
                    tempPath,
                    editable,
                    cancellationToken)
                .ConfigureAwait(false);

            Result editor = await RunEditorAsync(tempPath, cancellationToken)
                .ConfigureAwait(false);

            if (editor.IsFailure)
            {
                return Failure(editor.Error);
            }

            byte[] bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken)
                .ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(bytes);

            Result tree = new ConfigurationValidator()
                .ValidateConfigurationFileJson(document.RootElement);

            if (tree.IsFailure)
            {
                return Failure(tree.Error);
            }

            ArcanumConfigurationFile? edited = JsonSerializer.Deserialize(
                bytes,
                ConfigurationJsonContext.Default.ArcanumConfigurationFile);

            if (edited is null)
            {
                console.WriteDiagnostic("The editor returned an empty configuration document.");

                return (int)CliExitCode.ConfigurationError;
            }

            ConfigurationPathUpdate prepared = PrepareEditedSettings(
                snapshot,
                edited.Arcanum);

            if (!prepared.IsSuccess)
            {
                console.WriteDiagnostic(prepared.Error!);

                return (int)CliExitCode.ConfigurationError;
            }

            Result write = await configurationService
                .WriteAsync(snapshot, prepared.Settings!, cancellationToken)
                .ConfigureAwait(false);

            if (write.IsFailure)
            {
                return Failure(write.Error);
            }

            console.WritePayload("Configuration validated and applied atomically.");

            DescribeAccess(snapshot);

            return (int)CliExitCode.Success;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            console.WriteDiagnostic($"Could not edit configuration: {exception.Message}");

            return (int)CliExitCode.ConfigurationError;
        }
        finally
        {
            try
            {
                DeleteTemporaryFile(tempPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort, and never a reason to change the outcome: the edit has already been
                // applied or refused, and the editor may still hold the temporary file briefly.
            }
        }
    }

    public int Open()
    {
        CompendiumLaunchResult result = compendiumLauncher.TryLaunch();

        if (result.Launched)
        {
            console.WritePayload(result.Message);

            return (int)CliExitCode.Success;
        }

        console.WriteDiagnostic(result.Message);

        console.WriteDiagnostic(
            $"Configuration path: {result.ConfigPath}{Environment.NewLine}"
            + "Fallback command: arcanum config edit");

        return (int)CliExitCode.GenericError;
    }

    internal static ConfigurationPathUpdate PrepareEditedSettings(
        ConfigurationCommandSnapshot snapshot,
        ArcanumSettings edited)
    {
        if (snapshot.AccessMode == ConfigurationAccessMode.HostApi)
        {
            return ConfigurationPathUpdate.Success(edited);
        }

        ArcanumSettings merged = ConfigurationRedactor.MergeRedactedSecrets(
            edited,
            snapshot.Settings);

        Result residual = ConfigurationRedactor.ValidateNoResidualMask(merged);

        return residual.IsSuccess
            ? ConfigurationPathUpdate.Success(merged)
            : ConfigurationPathUpdate.Failure(edited, residual.Error.Message);
    }

    internal static async Task WriteSecureTemporaryFileAsync(
        string path,
        ArcanumConfigurationFile configuration,
        CancellationToken cancellationToken)
    {
        FileStreamOptions options = new()
        {
            Access = FileAccess.Write,

            Mode = FileMode.CreateNew,

            Options = FileOptions.Asynchronous,

            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (FileStream stream = new(path, options))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    configuration,
                    ConfigurationJsonContext.Default.ArcanumConfigurationFile,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        SecureFilePermissions.ApplyOwnerOnlyFile(path);
    }

    private Task<SensitiveValueRead> ReadSensitiveValueAsync(
        CancellationToken cancellationToken) =>
        SensitiveValueInput.ReadAsync(
            SystemSensitiveValueConsole.Instance,
            invocationContext.Options,
            "Sensitive value:",
            cancellationToken);

    private int Failure(Error error)
    {
        console.WriteDiagnostic(error.Message);

        if (error.Details is { Count: > 0 })
        {
            foreach (ConfigurationValidationError detail in error.Details)
            {
                console.WriteDiagnostic($"{detail.Pointer}: {detail.Detail}");
            }
        }

        return error.Code.StartsWith("Connection.", StringComparison.Ordinal)
            ? (int)CliExitCode.NetworkError
            : (int)CliExitCode.ConfigurationError;
    }

    private void DescribeAccess(
        ConfigurationCommandSnapshot snapshot,
        string? key = null)
    {
        if (snapshot.AccessMode == ConfigurationAccessMode.LocalBootstrap)
        {
            console.WriteDiagnostic(
                "Host API unavailable; used documented local configuration bootstrap mode.");
        }

        foreach (string environmentOverride in snapshot.EnvironmentOverrides)
        {
            if (key is null
                || environmentOverride.StartsWith(
                    $"{key} ",
                    StringComparison.OrdinalIgnoreCase))
            {
                console.WriteDiagnostic($"Environment override: {environmentOverride}");
            }
        }
    }
}

internal static class ConfigEditor
{
    public static Task<Result> RunAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable("VISUAL");

        configured = string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable("EDITOR")
            : configured;

        return RunAsync(configured, path, cancellationToken);
    }

    internal static async Task<Result> RunAsync(
        string? configured,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo startInfo = CreateStartInfo(configured, path);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The configured editor did not start.");

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelling the wait does not end the editor, which would go on running against a
                // temporary file the caller is about to delete.
                await TerminateAsync(process).ConfigureAwait(false);

                throw;
            }

            return process.ExitCode == 0
                ? Result.Success()
                : Result.Failure(
                    new Error(
                        "Configuration.EditorFailed",
                        $"The configured editor exited with code {process.ExitCode}; no changes were applied."));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            return Result.Failure(
                new Error(
                    "Configuration.EditorFailed",
                    $"Could not start the configured editor: {exception.Message}"));
        }
    }

    /// <summary>
    /// Ends the editor and its children and waits a bounded time for it to go, on its own clock because
    /// the caller's token is the one that was just cancelled.
    /// </summary>
    private static Task TerminateAsync(Process process) =>
        TerminateAsync(
            () => process.HasExited,
            () => process.Kill(entireProcessTree: true),
            process.WaitForExitAsync,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// <see cref="TerminateAsync(Process)"/> over the three things it needs from a process, so the
    /// failures a real kill can raise can be raised in a test.
    /// </summary>
    /// <param name="hasExited">Whether the editor is already gone.</param>
    /// <param name="killTree">Ends the editor and its children.</param>
    /// <param name="waitForExit">Completes when the editor has exited.</param>
    /// <param name="grace">How long to wait for it to go after the kill.</param>
    internal static async Task TerminateAsync(
        Func<bool> hasExited,
        Action killTree,
        Func<CancellationToken, Task> waitForExit,
        TimeSpan grace)
    {
        try
        {
            if (!hasExited())
            {
                killTree();
            }

            using CancellationTokenSource graceSource = new(grace);

            await waitForExit(graceSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OperationCanceledException
            // Killing a whole process tree raises this when part of the tree cannot be signalled (a child
            // already gone, or one this user does not own). The editor is being abandoned either way, and
            // letting it through would replace the cancellation in flight with a generic failure.
            or AggregateException)
        {
            // Already gone, not ours to signal, partly signalled, or slow to die: the caller is unwinding
            // either way.
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string? configured, string path)
    {
        ProcessStartInfo startInfo = new()
        {
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(configured))
        {
            IReadOnlyList<string> configuredParts = SplitCommand(configured);

            if (configuredParts.Count == 0)
            {
                throw new ArgumentException("The configured editor command is empty.", nameof(configured));
            }

            startInfo.FileName = configuredParts[0];

            foreach (string argument in configuredParts.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(path);

            return startInfo;
        }

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "notepad.exe";

            startInfo.ArgumentList.Add(path);

            return startInfo;
        }

        if (OperatingSystem.IsMacOS())
        {
            startInfo.FileName = "open";

            startInfo.ArgumentList.Add("-W");

            startInfo.ArgumentList.Add("-t");

            startInfo.ArgumentList.Add(path);

            return startInfo;
        }

        startInfo.FileName = "vi";

        startInfo.ArgumentList.Add(path);

        return startInfo;
    }

    private static IReadOnlyList<string> SplitCommand(string command)
    {
        List<string> parts = [];

        System.Text.StringBuilder current = new();

        char quote = '\0';

        for (int index = 0; index < command.Length; index++)
        {
            char character = command[index];

            if (quote == '\0' && (character == '\'' || character == '"'))
            {
                quote = character;

                continue;
            }

            if (quote != '\0' && character == quote)
            {
                quote = '\0';

                continue;
            }

            if (character == '\\'
                && index + 1 < command.Length
                && command[index + 1] == quote)
            {
                current.Append(command[++index]);

                continue;
            }

            if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());

                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (quote != '\0')
        {
            throw new ArgumentException("The configured editor command contains an unmatched quote.", nameof(command));
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}

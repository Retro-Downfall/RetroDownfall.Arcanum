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

            Result editor = await ConfigEditor.RunAsync(tempPath, cancellationToken)
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

                File.Delete(tempPath);

            }
            catch (IOException)
            {

                // Best effort: the editor may still hold the temporary file briefly.

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

    public static async Task<Result> RunAsync(
        string path,
        CancellationToken cancellationToken)
    {

        string? configured = Environment.GetEnvironmentVariable("VISUAL");

        configured = string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable("EDITOR")
            : configured;

        try
        {

            ProcessStartInfo startInfo = CreateStartInfo(configured, path);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The configured editor did not start.");

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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

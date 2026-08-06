using System.Net.Http;

using System.Text;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Configuration.Presets;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

public enum CliExitCode
{

    Success = 0,

    GenericError = 1,

    ConfigurationError = 2,

    NetworkError = 3,

    Cancelled = 130,

}

public readonly record struct CliInvocationOptions(
    bool Json,
    bool Plain,
    bool Yes,
    bool NoContext = false);

public interface ICliInvocationContext
{

    CliInvocationOptions Options { get; }

}

public interface IConsoleDispatcher
{

    void WritePayload(string value);

    void WriteDiagnostic(string value);

    void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo);

    void WriteJson(JsonElement value);

    void BeginJsonStream();

}

public interface IConfirmationPrompt
{

    Task<bool> PromptForConfirmationAsync(
        string question,
        CancellationToken cancellationToken);

}

public sealed record CliTextPayload(
    string Output,
    int ExitCode);

public sealed record CliErrorPayload(
    string Error,
    int ExitCode);

public sealed record SessionShowPayload(
    Guid Id,
    Guid? CampaignId,
    string? Title,
    string Status,
    int EntryCount,
    int AttachmentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Summary,
    long TotalTokensUsed,
    decimal TotalCostUsd,
    Guid? ForkedFromSessionId);

public sealed record CliFailure(
    CliExitCode ExitCode,
    string SafeMessage);

public sealed record FileDownloadPayload(
    string Id,
    string Path,
    long Bytes);

public sealed record BatchArtifactPayload(
    string BatchId,
    string Kind,
    string FileId,
    string Path,
    long Bytes);

public sealed record HealthWatchSnapshot(
    DateTimeOffset Timestamp,
    RetroDownfall.Arcanum.Api.Models.HealthStatus Status,
    RetroDownfall.Arcanum.Api.Models.HealthComponentDto[] Components);

public sealed record ConfigurationPresetListItemPayload(
    string Id,
    int Version,
    string DisplayName,
    string Purpose,
    string EffectiveState,
    bool IsActive);

public sealed record ConfigurationPresetListPayload(
    ConfigurationPresetListItemPayload[] Presets,
    ConfigurationPresetCompletionSummary? CompletionSummary);

public sealed record ConfigurationPresetShowPayload(
    ConfigurationPresetDefinition Preset,
    string EffectiveState,
    bool IsActive,
    ConfigurationPresetGlossaryEntry[] Glossary);

/// <summary>
/// One Arcanum-owned credential identity. Carries presence/status and fixed recovery guidance only —
/// never the credential value, and never a value-derived hint (§8.1, issue #47).
/// </summary>
public sealed record CredentialInventoryEntryPayload(
    string Id,
    string Kind,
    string DisplayName,
    string Storage,
    string Status,
    string Source,
    string? EnvironmentVariable,
    string Recovery);

public sealed record CredentialInventoryPayload(
    CredentialInventoryEntryPayload[] Credentials);

internal sealed class CliInvocationContext : ICliInvocationContext
{

    private static readonly AsyncLocal<InvocationState?> AmbientState = new();

    public CliInvocationOptions Options =>
        AmbientState.Value?.Options ?? default;

    internal static CliInvocationOptions Current =>
        AmbientState.Value?.Options ?? default;

    internal static bool StructuredPayloadWritten =>
        AmbientState.Value?.StructuredPayloadWritten ?? false;

    internal static bool JsonStreamStarted =>
        AmbientState.Value?.JsonStreamStarted ?? false;

    internal static IDisposable Push(CliInvocationOptions options)
    {

        InvocationState? previous = AmbientState.Value;

        AmbientState.Value = new InvocationState(options);

        return new RestoreScope(previous);

    }

    internal static void MarkStructuredPayloadWritten()
    {

        if (AmbientState.Value is { } state)
        {

            state.StructuredPayloadWritten = true;

        }

    }

    internal static void AttachJsonOutput(DeferredJsonTextWriter output)
    {

        if (AmbientState.Value is { } state)
        {

            state.JsonOutput = output;

        }

    }

    internal static void BeginJsonStream()
    {

        if (AmbientState.Value is { } state)
        {

            state.StructuredPayloadWritten = true;

            state.JsonStreamStarted = true;

            state.JsonOutput?.BeginStreaming();

        }

    }

    private sealed class InvocationState(CliInvocationOptions options)
    {

        public CliInvocationOptions Options { get; } = options;

        public bool StructuredPayloadWritten { get; set; }

        public bool JsonStreamStarted { get; set; }

        public DeferredJsonTextWriter? JsonOutput { get; set; }

    }

    private sealed class RestoreScope(InvocationState? previous) : IDisposable
    {

        private bool _disposed;

        public void Dispose()
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            AmbientState.Value = previous;

        }

    }

}

internal sealed class ConsoleDispatcher : IConsoleDispatcher
{

    private readonly TextWriter? _standardOutput;

    private readonly TextWriter? _standardError;

    private readonly CliInvocationOptions? _fixedOptions;

    private readonly ICliInvocationContext? _invocationContext;

    public ConsoleDispatcher(ICliInvocationContext invocationContext)
    {

        _invocationContext = invocationContext;

    }

    internal ConsoleDispatcher(
        TextWriter standardOutput,
        TextWriter standardError,
        CliInvocationOptions options)
    {

        _standardOutput = standardOutput;

        _standardError = standardError;

        _fixedOptions = options;

    }

    public void WritePayload(string value) =>
        WriteLine(StandardOutput, value);

    public void WriteDiagnostic(string value) =>
        WriteLine(StandardError, value);

    /// <summary>
    /// Serializes through explicit source-generated metadata only. Serialization runs to completion
    /// in memory before a single line is written, so a failure can never emit a partial document; a
    /// failure degrades to the fixed <see cref="CliErrorPayload"/> envelope rather than falling back
    /// to reflection-based serialization, which would not survive Native AOT (issue #49).
    /// </summary>
    public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
    {

        string json;

        try
        {

            json = JsonSerializer.Serialize(value, typeInfo);

        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException)
        {

            WriteSerializationFailure();

            return;

        }

        WriteLine(StandardOutput, json);

        CliInvocationContext.MarkStructuredPayloadWritten();

    }

    public void WriteJson(JsonElement value)
    {

        string json;

        try
        {

            json = JsonSerializer.Serialize(value, CliJsonContext.Default.JsonElement);

        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException)
        {

            WriteSerializationFailure();

            return;

        }

        WriteLine(StandardOutput, json);

        CliInvocationContext.MarkStructuredPayloadWritten();

    }

    private void WriteSerializationFailure()
    {

        WriteLine(
            StandardOutput,
            JsonSerializer.Serialize(
                new CliErrorPayload(
                    "The command result could not be serialized.",
                    (int)CliExitCode.GenericError),
                CliJsonContext.Default.CliErrorPayload));

        WriteLine(StandardError, "The command result could not be serialized.");

        CliInvocationContext.MarkStructuredPayloadWritten();

    }

    public void BeginJsonStream()
    {

        CliInvocationContext.BeginJsonStream();

    }

    private CliInvocationOptions Options =>
        _fixedOptions ?? _invocationContext?.Options ?? default;

    private TextWriter StandardOutput =>
        _standardOutput ?? Console.Out;

    private TextWriter StandardError =>
        _standardError ?? Console.Error;

    private void WriteLine(TextWriter writer, string value)
    {

        writer.WriteLine(Options.Plain ? StripAnsi(value) : value);

    }

    internal static string StripAnsi(string value)
    {

        int escapeIndex = value.IndexOf('\u001b');

        if (escapeIndex < 0)
        {

            return value;

        }

        char[] buffer = new char[value.Length];

        int written = 0;

        for (int index = 0; index < value.Length;)
        {

            if (value[index] != '\u001b')
            {

                buffer[written++] = value[index++];

                continue;

            }

            index++;

            if (index >= value.Length)
            {

                break;

            }

            if (value[index] == '[')
            {

                index++;

                while (index < value.Length)
                {

                    char current = value[index++];

                    if (current is >= '@' and <= '~')
                    {

                        break;

                    }

                }

                continue;

            }

            if (value[index] is ']' or 'P' or 'X' or '^' or '_')
            {

                index++;

                while (index < value.Length)
                {

                    if (value[index] is '\a' or '\u009c')
                    {

                        index++;

                        break;

                    }

                    if (value[index] == '\u001b'
                        && index + 1 < value.Length
                        && value[index + 1] == '\\')
                    {

                        index += 2;

                        break;

                    }

                    index++;

                }

                continue;

            }

            index++;

        }

        return new string(buffer, 0, written);

    }

}

internal sealed class DeferredJsonTextWriter(TextWriter destination) : TextWriter
{

    private readonly StringWriter _buffer = new();

    private readonly object _gate = new();

    private bool _streaming;

    public override Encoding Encoding => destination.Encoding;

    public bool IsStreaming
    {

        get
        {

            lock (_gate)
            {

                return _streaming;

            }

        }

    }

    public string BufferedOutput
    {

        get
        {

            lock (_gate)
            {

                return _buffer.ToString();

            }

        }

    }

    public void BeginStreaming()
    {

        lock (_gate)
        {

            if (_streaming)
            {

                return;

            }

            destination.Write(_buffer.ToString());

            destination.Flush();

            _streaming = true;

        }

    }

    public override void Write(char value)
    {

        lock (_gate)
        {

            ActiveWriter.Write(value);

            FlushDestinationWhenStreaming();

        }

    }

    public override void Write(string? value)
    {

        lock (_gate)
        {

            ActiveWriter.Write(value);

            FlushDestinationWhenStreaming();

        }

    }

    public override void WriteLine(string? value)
    {

        lock (_gate)
        {

            ActiveWriter.WriteLine(value);

            FlushDestinationWhenStreaming();

        }

    }

    public override void Flush()
    {

        lock (_gate)
        {

            ActiveWriter.Flush();

        }

    }

    protected override void Dispose(bool disposing)
    {

        if (disposing)
        {

            _buffer.Dispose();

        }

        base.Dispose(disposing);

    }

    private TextWriter ActiveWriter => _streaming ? destination : _buffer;

    private void FlushDestinationWhenStreaming()
    {

        if (_streaming)
        {

            destination.Flush();

        }

    }

}

internal sealed class ConfirmationPrompt : IConfirmationPrompt
{

    private readonly IConsoleDispatcher _dispatcher;

    private readonly ICliInvocationContext? _invocationContext;

    private readonly CliInvocationOptions? _fixedOptions;

    private readonly TextReader _input;

    private readonly Func<bool> _isOutputRedirected;

    public ConfirmationPrompt(
        IConsoleDispatcher dispatcher,
        ICliInvocationContext invocationContext)
        : this(
            dispatcher,
            invocationContext,
            fixedOptions: null,
            Console.In,
            static () => Console.IsOutputRedirected)
    {

    }

    internal ConfirmationPrompt(
        IConsoleDispatcher dispatcher,
        CliInvocationOptions options,
        TextReader input,
        Func<bool> isOutputRedirected)
        : this(
            dispatcher,
            invocationContext: null,
            options,
            input,
            isOutputRedirected)
    {

    }

    private ConfirmationPrompt(
        IConsoleDispatcher dispatcher,
        ICliInvocationContext? invocationContext,
        CliInvocationOptions? fixedOptions,
        TextReader input,
        Func<bool> isOutputRedirected)
    {

        _dispatcher = dispatcher;

        _invocationContext = invocationContext;

        _fixedOptions = fixedOptions;

        _input = input;

        _isOutputRedirected = isOutputRedirected;

    }

    public async Task<bool> PromptForConfirmationAsync(
        string question,
        CancellationToken cancellationToken)
    {

        CliInvocationOptions options =
            _fixedOptions ?? _invocationContext?.Options ?? default;

        if (options.Yes)
        {

            return true;

        }

        if (_isOutputRedirected())
        {

            throw new NonInteractiveConfirmationException();

        }

        _dispatcher.WriteDiagnostic($"{question} [y/N]");

        string? response = await _input
            .ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(
            response?.Trim(),
            "y",
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                response?.Trim(),
                "yes",
                StringComparison.OrdinalIgnoreCase);

    }

}

public sealed class NonInteractiveConfirmationException()
    : InvalidOperationException(
        "Confirmation requires an interactive console. Re-run with --yes to approve.");

internal static class CliFailureMapper
{

    public static CliFailure Map(Exception exception) =>
        exception switch
        {
            HttpRequestException => new CliFailure(
                CliExitCode.NetworkError,
                "A network operation failed."),
            NonInteractiveConfirmationException => new CliFailure(
                CliExitCode.ConfigurationError,
                "Confirmation is required. Re-run interactively or pass --yes."),
            BackupPassphraseInputException passphrase => new CliFailure(
                CliExitCode.ConfigurationError,
                passphrase.Message),
            OperationCanceledException => new CliFailure(
                CliExitCode.Cancelled,
                "The operation was cancelled."),
            _ => new CliFailure(
                CliExitCode.GenericError,
                "An unexpected CLI error occurred."),
        };

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(CliTextPayload))]
[JsonSerializable(typeof(CliErrorPayload))]
[JsonSerializable(typeof(CliContextStatusPayload))]
[JsonSerializable(typeof(CliContextMutationResult))]
[JsonSerializable(typeof(SessionShowPayload))]
[JsonSerializable(typeof(FileDownloadPayload))]
[JsonSerializable(typeof(BatchArtifactPayload))]

[JsonSerializable(typeof(BackupPlan))]

[JsonSerializable(typeof(BackupCreateResult))]

[JsonSerializable(typeof(BackupInspectResult))]

[JsonSerializable(typeof(BackupVerifyResult))]

[JsonSerializable(typeof(BackupListItem[]))]

[JsonSerializable(typeof(BackupRestorePlan))]

[JsonSerializable(typeof(BackupRestoreResult))]

[JsonSerializable(typeof(BackupMigrateResult))]

[JsonSerializable(typeof(HealthWatchSnapshot))]

[JsonSerializable(typeof(ConfigurationPresetListPayload))]

[JsonSerializable(typeof(ConfigurationPresetShowPayload))]

[JsonSerializable(typeof(ConfigurationPresetPlan))]

[JsonSerializable(typeof(ConfigurationPresetApplyResult))]

[JsonSerializable(typeof(ConfigurationPresetResetResult))]

[JsonSerializable(typeof(CredentialInventoryEntryPayload))]

[JsonSerializable(typeof(CredentialInventoryPayload))]

[JsonSerializable(typeof(SetupPlanPayload))]

[JsonSerializable(typeof(SetupResultPayload))]

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

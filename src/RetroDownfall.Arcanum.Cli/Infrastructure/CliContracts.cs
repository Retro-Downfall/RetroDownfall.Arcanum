using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
    bool Yes);

public interface ICliInvocationContext
{

    CliInvocationOptions Options { get; }

}

public interface IConsoleDispatcher
{

    void WritePayload(string value);

    void WriteDiagnostic(string value);

    void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo);

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

public sealed record CliFailure(
    CliExitCode ExitCode,
    string SafeMessage);

internal sealed class CliInvocationContext : ICliInvocationContext
{

    private static readonly AsyncLocal<InvocationState?> AmbientState = new();

    public CliInvocationOptions Options =>
        AmbientState.Value?.Options ?? default;

    internal static CliInvocationOptions Current =>
        AmbientState.Value?.Options ?? default;

    internal static bool StructuredPayloadWritten =>
        AmbientState.Value?.StructuredPayloadWritten ?? false;

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

    private sealed class InvocationState(CliInvocationOptions options)
    {

        public CliInvocationOptions Options { get; } = options;

        public bool StructuredPayloadWritten { get; set; }

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

    public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
    {

        string json = JsonSerializer.Serialize(value, typeInfo);

        WriteLine(StandardOutput, json);

        CliInvocationContext.MarkStructuredPayloadWritten();

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
internal sealed partial class CliJsonContext : JsonSerializerContext;

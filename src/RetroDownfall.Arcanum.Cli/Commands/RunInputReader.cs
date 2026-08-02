using System.Text;

using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal interface IRunInputReader
{

    Task<RunInputReadResult> ReadAsync(
        string? positionalInstruction,
        CancellationToken cancellationToken,
        bool hasExplicitFileContext = false);

}

internal sealed record RunInputReadResult(
    bool IsSuccess,
    string Instruction,
    string? PipedContent,
    long PipedUtf8Bytes,
    bool InputRedirected,
    bool Prompted,
    bool IsOversize,
    string[] Diagnostics,
    string? Error);

internal sealed class RunInputReader : IRunInputReader
{

    internal const int MaxRedirectedInputBytes = 10 * 1024 * 1024;

    private const int ReadBufferChars = 4096;

    private readonly CliStandardInput _standardInput;

    private readonly IConsoleDispatcher _dispatcher;

    private readonly Func<bool> _inputRedirected;

    public RunInputReader(
        CliStandardInput standardInput,
        IConsoleDispatcher dispatcher)
        : this(
            standardInput,
            dispatcher,
            static () => Console.IsInputRedirected)
    {

    }

    internal RunInputReader(
        CliStandardInput standardInput,
        IConsoleDispatcher dispatcher,
        Func<bool> inputRedirected)
    {

        _standardInput = standardInput;

        _dispatcher = dispatcher;

        _inputRedirected = inputRedirected;

    }

    public async Task<RunInputReadResult> ReadAsync(
        string? positionalInstruction,
        CancellationToken cancellationToken,
        bool hasExplicitFileContext = false)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string instruction = positionalInstruction ?? string.Empty;

        TextReader activeInput = Console.In;

        bool injectedInput = !ReferenceEquals(
            activeInput,
            _standardInput.ConfiguredReader);

        bool redirected = injectedInput || _inputRedirected();

        if (!redirected)
        {

            if (!string.IsNullOrWhiteSpace(instruction)
                || hasExplicitFileContext)
            {

                return Success(
                    instruction,
                    pipedContent: null,
                    pipedUtf8Bytes: 0,
                    inputRedirected: false,
                    prompted: false,
                    diagnostics: []);

            }

            _dispatcher.WriteDiagnostic("Prompt:");

            string? entered = await activeInput
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            if (entered is null)
            {

                return Success(
                    string.Empty,
                    pipedContent: null,
                    pipedUtf8Bytes: 0,
                    inputRedirected: false,
                    prompted: true,
                    diagnostics:
                    [
                        "No interactive prompt was entered.",
                    ]);

            }

            return Success(
                entered,
                pipedContent: null,
                pipedUtf8Bytes: 0,
                inputRedirected: false,
                prompted: true,
                diagnostics: []);

        }

        try
        {

            RedirectedInputRead read = await ReadRedirectedAsync(
                activeInput,
                cancellationToken).ConfigureAwait(false);

            if (read.IsOversize)
            {

                return new RunInputReadResult(
                    false,
                    instruction,
                    null,
                    read.Utf8Bytes,
                    true,
                    false,
                    true,
                    [],
                    $"Redirected standard input exceeds the {MaxRedirectedInputBytes}-byte UTF-8 limit.");

            }

            if (read.Content.Length == 0)
            {

                return Success(
                    instruction,
                    pipedContent: null,
                    pipedUtf8Bytes: 0,
                    inputRedirected: true,
                    prompted: false,
                    diagnostics:
                    [
                        "Redirected standard input was empty.",
                    ]);

            }

            return Success(
                instruction,
                read.Content,
                read.Utf8Bytes,
                inputRedirected: true,
                prompted: false,
                diagnostics: []);

        }
        catch (Exception exception) when (
            exception is IOException
                or ObjectDisposedException
                or UnauthorizedAccessException)
        {

            string message =
                $"Could not read redirected standard input: {exception.Message}";

            return new RunInputReadResult(
                false,
                instruction,
                null,
                0,
                true,
                false,
                false,
                [],
                message);

        }

    }

    private static RunInputReadResult Success(
        string instruction,
        string? pipedContent,
        long pipedUtf8Bytes,
        bool inputRedirected,
        bool prompted,
        string[] diagnostics) =>
        new(
            true,
            instruction,
            pipedContent,
            pipedUtf8Bytes,
            inputRedirected,
            prompted,
            false,
            diagnostics,
            null);

    private static async Task<RedirectedInputRead> ReadRedirectedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {

        StringBuilder content = new();

        char[] buffer = new char[ReadBufferChars];

        long utf8Bytes = 0;

        bool pendingHighSurrogate = false;

        while (true)
        {

            int read = await reader
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {

                break;

            }

            utf8Bytes += CountUtf8Bytes(
                buffer.AsSpan(0, read),
                ref pendingHighSurrogate);

            if (utf8Bytes > MaxRedirectedInputBytes)
            {

                return new RedirectedInputRead(
                    string.Empty,
                    utf8Bytes,
                    true);

            }

            content.Append(buffer, 0, read);

        }

        if (pendingHighSurrogate)
        {

            utf8Bytes += 3;

        }

        return utf8Bytes > MaxRedirectedInputBytes
            ? new RedirectedInputRead(string.Empty, utf8Bytes, true)
            : new RedirectedInputRead(content.ToString(), utf8Bytes, false);

    }

    private static int CountUtf8Bytes(
        ReadOnlySpan<char> characters,
        ref bool pendingHighSurrogate)
    {

        int bytes = 0;

        foreach (char character in characters)
        {

            if (pendingHighSurrogate)
            {

                if (char.IsLowSurrogate(character))
                {

                    bytes += 4;

                    pendingHighSurrogate = false;

                    continue;

                }

                bytes += 3;

                pendingHighSurrogate = false;

            }

            if (character <= 0x7F)
            {

                bytes++;

            }
            else if (character <= 0x7FF)
            {

                bytes += 2;

            }
            else if (char.IsHighSurrogate(character))
            {

                pendingHighSurrogate = true;

            }
            else
            {

                bytes += 3;

            }

        }

        return bytes;

    }

    private sealed record RedirectedInputRead(
        string Content,
        long Utf8Bytes,
        bool IsOversize);

}

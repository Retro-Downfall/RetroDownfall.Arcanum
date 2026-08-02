using System.Text;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class RunInputReaderTests
{

    [Fact]

    public async Task ReadAsync_keeps_positional_instruction_separate_from_injected_redirected_input()
    {

        TextReader originalInput = Console.In;

        StringReader configuredInput = new(string.Empty);

        StringReader redirectedInput = new("piped context");

        StringWriter diagnostics = new();

        try
        {

            Console.SetIn(redirectedInput);

            RunInputReader reader = CreateReader(
                configuredInput,
                diagnostics,
                inputRedirected: false);

            RunInputReadResult result = await reader.ReadAsync(
                "explain this",
                CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal("explain this", result.Instruction);

            Assert.Equal("piped context", result.PipedContent);

            Assert.Equal(Encoding.UTF8.GetByteCount("piped context"), result.PipedUtf8Bytes);

            Assert.True(result.InputRedirected);

            Assert.False(result.Prompted);

            Assert.False(result.IsOversize);

            Assert.Null(result.Error);

        }
        finally
        {

            Console.SetIn(originalInput);

        }

    }

    [Fact]

    public async Task ReadAsync_accepts_exact_ten_mebibyte_utf8_boundary_and_rejects_one_byte_more()
    {

        TextReader originalInput = Console.In;

        StringReader configuredInput = new(string.Empty);

        string exact = new('\u00e9', RunInputReader.MaxRedirectedInputBytes / 2);

        try
        {

            Console.SetIn(new StringReader(exact));

            RunInputReader exactReader = CreateReader(
                configuredInput,
                new StringWriter(),
                inputRedirected: false);

            RunInputReadResult accepted = await exactReader.ReadAsync(
                "summarize",
                CancellationToken.None);

            Assert.True(accepted.IsSuccess);

            Assert.Equal(RunInputReader.MaxRedirectedInputBytes, accepted.PipedUtf8Bytes);

            Assert.Equal(exact, accepted.PipedContent);

            Console.SetIn(new StringReader(exact + "x"));

            RunInputReader oversizedReader = CreateReader(
                configuredInput,
                new StringWriter(),
                inputRedirected: false);

            RunInputReadResult rejected = await oversizedReader.ReadAsync(
                "summarize",
                CancellationToken.None);

            Assert.False(rejected.IsSuccess);

            Assert.True(rejected.IsOversize);

            Assert.Null(rejected.PipedContent);

            Assert.Contains(
                RunInputReader.MaxRedirectedInputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                rejected.Error,
                StringComparison.Ordinal);

        }
        finally
        {

            Console.SetIn(originalInput);

        }

    }

    [Fact]

    public async Task ReadAsync_prompts_once_and_reads_one_line_for_true_tty_without_instruction()
    {

        TextReader originalInput = Console.In;

        StringReader interactiveInput = new("typed prompt\nignored second line\n");

        StringWriter diagnostics = new();

        try
        {

            Console.SetIn(interactiveInput);

            RunInputReader reader = CreateReader(
                Console.In,
                diagnostics,
                inputRedirected: false);

            RunInputReadResult result = await reader.ReadAsync(
                null,
                CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal("typed prompt", result.Instruction);

            Assert.Null(result.PipedContent);

            Assert.False(result.InputRedirected);

            Assert.True(result.Prompted);

            Assert.Equal("ignored second line", await interactiveInput.ReadLineAsync());

            Assert.Contains("Prompt", diagnostics.ToString(), StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            Console.SetIn(originalInput);

        }

    }

    [Fact]

    public async Task ReadAsync_skips_prompt_for_true_tty_when_explicit_file_context_exists()
    {

        TextReader originalInput = Console.In;

        StringReader interactiveInput = new("must remain unread\n");

        StringWriter diagnostics = new();

        try
        {

            Console.SetIn(interactiveInput);

            RunInputReader reader = CreateReader(
                Console.In,
                diagnostics,
                inputRedirected: false);

            RunInputReadResult result = await reader.ReadAsync(
                null,
                CancellationToken.None,
                hasExplicitFileContext: true);

            Assert.True(result.IsSuccess);

            Assert.Equal(string.Empty, result.Instruction);

            Assert.Null(result.PipedContent);

            Assert.False(result.InputRedirected);

            Assert.False(result.Prompted);

            Assert.Equal("must remain unread", await interactiveInput.ReadLineAsync());

            Assert.Equal(string.Empty, diagnostics.ToString());

        }
        finally
        {

            Console.SetIn(originalInput);

        }

    }

    [Fact]

    public async Task ReadAsync_returns_success_and_warning_for_empty_redirected_input()
    {

        TextReader originalInput = Console.In;

        StringReader configuredInput = new(string.Empty);

        try
        {

            Console.SetIn(new StringReader(string.Empty));

            RunInputReader reader = CreateReader(
                configuredInput,
                new StringWriter(),
                inputRedirected: false);

            RunInputReadResult result = await reader.ReadAsync(
                null,
                CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Equal(string.Empty, result.Instruction);

            Assert.Null(result.PipedContent);

            Assert.True(result.InputRedirected);

            Assert.Contains(
                result.Diagnostics,
                value => value.Contains("empty", StringComparison.OrdinalIgnoreCase));

        }
        finally
        {

            Console.SetIn(originalInput);

        }

    }

    [Fact]

    public async Task ReadAsync_fails_instead_of_dropping_redirected_input_when_it_cannot_be_read()
    {

        TextReader originalInput = Console.In;

        StringReader configuredInput = new(string.Empty);

        try
        {

            Console.SetIn(new ThrowingTextReader());

            RunInputReader reader = CreateReader(
                configuredInput,
                new StringWriter(),
                inputRedirected: false);

            RunInputReadResult result = await reader.ReadAsync(
                "continue safely",
                CancellationToken.None);

            Assert.False(result.IsSuccess);

            Assert.Equal("continue safely", result.Instruction);

            Assert.Null(result.PipedContent);

            Assert.Contains(
                "Could not read redirected standard input",
                result.Error,
                StringComparison.Ordinal);

            Assert.Empty(result.Diagnostics);

        }
        finally
        {

            Console.SetIn(originalInput);

        }

    }

    private static RunInputReader CreateReader(
        TextReader configuredInput,
        TextWriter diagnostics,
        bool inputRedirected)
    {

        ConsoleDispatcher dispatcher = new(
            TextWriter.Null,
            diagnostics,
            default);

        return new RunInputReader(
            new CliStandardInput(configuredInput),
            dispatcher,
            () => inputRedirected);

    }

    private sealed class ThrowingTextReader : TextReader
    {

        public override int Read(
            char[] buffer,
            int index,
            int count) =>
            throw new IOException("simulated read failure");

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("simulated read failure"));

    }

}

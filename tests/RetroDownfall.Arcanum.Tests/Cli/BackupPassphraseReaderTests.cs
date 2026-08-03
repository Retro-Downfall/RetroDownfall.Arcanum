using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class BackupPassphraseReaderTests
{

    private const int ExpectedMaximumPassphraseUnits = 1024 * 1024;

    [Fact]
    public async Task ReadAsync_rejects_multiple_explicit_sources_before_reading_either()
    {

        RecordingPrompt prompt = new(["prompt secret".ToCharArray()]);

        RecordingFileDescriptorReader fileDescriptors = new();

        BackupPassphraseReader reader = CreateReader(
            prompt,
            fileDescriptors,
            _ => "environment secret");

        BackupPassphraseReadRequest request = new(
            "ARCANUM_BACKUP_PASSPHRASE",
            0,
            BackupPassphraseReadPurpose.CreateArchive);

        BackupPassphraseInputException exception = await Assert.ThrowsAsync<BackupPassphraseInputException>(
            () => reader.ReadAsync(request, CancellationToken.None).AsTask());

        Assert.Contains("one passphrase source", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("environment secret", exception.Message, StringComparison.Ordinal);

        Assert.Empty(prompt.Prompts);

        Assert.Empty(fileDescriptors.ReadDescriptors);

    }

    [Fact]
    public async Task ReadAsync_resolves_an_environment_variable_reference_without_prompting()
    {

        List<string> requestedVariables = [];

        RecordingPrompt prompt = new([]);

        BackupPassphraseReader reader = CreateReader(
            prompt,
            new RecordingFileDescriptorReader(),
            name =>
            {

                requestedVariables.Add(name);

                return "environment secret";

            });

        BackupPassphraseReadRequest request = new(
            "ARCANUM_BACKUP_PASSPHRASE",
            null,
            BackupPassphraseReadPurpose.OpenArchive);

        using SensitiveBackupPassphrase passphrase = Assert.IsType<SensitiveBackupPassphrase>(
            await reader.ReadAsync(request, CancellationToken.None));

        Assert.Equal(["ARCANUM_BACKUP_PASSPHRASE"], requestedVariables);

        Assert.Equal("environment secret", new string(passphrase.Value.Span));

        Assert.Empty(prompt.Prompts);

    }

    [Fact]
    public async Task ReadAsync_accepts_file_descriptor_zero_as_an_explicit_source()
    {

        RecordingFileDescriptorReader fileDescriptors = new();

        fileDescriptors.Results.Enqueue("descriptor secret".ToCharArray());

        RecordingPrompt prompt = new([]);

        BackupPassphraseReader reader = CreateReader(
            prompt,
            fileDescriptors,
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            0,
            BackupPassphraseReadPurpose.OpenArchive);

        using SensitiveBackupPassphrase passphrase = Assert.IsType<SensitiveBackupPassphrase>(
            await reader.ReadAsync(request, CancellationToken.None));

        Assert.Equal([0], fileDescriptors.ReadDescriptors);

        Assert.Equal("descriptor secret", new string(passphrase.Value.Span));

        Assert.Empty(prompt.Prompts);

    }

    [Fact]
    public async Task ReadAsync_prompts_twice_for_create_and_zeroes_confirmation()
    {

        char[] entered = "interactive secret".ToCharArray();

        char[] confirmation = "interactive secret".ToCharArray();

        RecordingPrompt prompt = new([entered, confirmation]);

        BackupPassphraseReader reader = CreateReader(
            prompt,
            new RecordingFileDescriptorReader(),
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            null,
            BackupPassphraseReadPurpose.CreateArchive);

        SensitiveBackupPassphrase passphrase = Assert.IsType<SensitiveBackupPassphrase>(
            await reader.ReadAsync(request, CancellationToken.None));

        Assert.Equal(
            ["Backup passphrase: ", "Confirm backup passphrase: "],
            prompt.Prompts);

        Assert.Equal("interactive secret", new string(passphrase.Value.Span));

        Assert.All(confirmation, character => Assert.Equal('\0', character));

        passphrase.Dispose();

        Assert.All(entered, character => Assert.Equal('\0', character));

    }

    [Fact]
    public async Task ReadAsync_prompts_once_when_opening_an_existing_archive()
    {

        RecordingPrompt prompt = new(["existing secret".ToCharArray()]);

        BackupPassphraseReader reader = CreateReader(
            prompt,
            new RecordingFileDescriptorReader(),
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            null,
            BackupPassphraseReadPurpose.OpenArchive);

        using SensitiveBackupPassphrase passphrase = Assert.IsType<SensitiveBackupPassphrase>(
            await reader.ReadAsync(request, CancellationToken.None));

        Assert.Equal(["Backup passphrase: "], prompt.Prompts);

        Assert.Equal("existing secret", new string(passphrase.Value.Span));

    }

    [Fact]
    public async Task ReadAsync_can_inspect_outer_metadata_without_prompting()
    {

        RecordingPrompt prompt = new(["unused secret".ToCharArray()]);

        BackupPassphraseReader reader = CreateReader(
            prompt,
            new RecordingFileDescriptorReader(),
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            null,
            BackupPassphraseReadPurpose.InspectOuterMetadata);

        SensitiveBackupPassphrase? passphrase = await reader.ReadAsync(
            request,
            CancellationToken.None);

        Assert.Null(passphrase);

        Assert.Empty(prompt.Prompts);

    }

    [Fact]
    public async Task ReadAsync_zeroes_both_entries_when_confirmation_does_not_match()
    {

        char[] entered = "first secret".ToCharArray();

        char[] confirmation = "different secret".ToCharArray();

        RecordingPrompt prompt = new([entered, confirmation]);

        BackupPassphraseReader reader = CreateReader(
            prompt,
            new RecordingFileDescriptorReader(),
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            null,
            BackupPassphraseReadPurpose.CreateArchive);

        BackupPassphraseInputException exception = await Assert.ThrowsAsync<BackupPassphraseInputException>(
            () => reader.ReadAsync(request, CancellationToken.None).AsTask());

        Assert.Equal("Backup passphrases did not match.", exception.Message);

        Assert.DoesNotContain("first secret", exception.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("different secret", exception.Message, StringComparison.Ordinal);

        Assert.All(entered, character => Assert.Equal('\0', character));

        Assert.All(confirmation, character => Assert.Equal('\0', character));

    }

    [Theory]
    [InlineData("x")]
    [InlineData(" ")]
    public async Task ReadAsync_has_no_length_or_complexity_rule_beyond_nonempty(string value)
    {

        BackupPassphraseReader reader = CreateReader(
            new RecordingPrompt([]),
            new RecordingFileDescriptorReader(),
            _ => value);

        BackupPassphraseReadRequest request = new(
            "ARCANUM_BACKUP_PASSPHRASE",
            null,
            BackupPassphraseReadPurpose.CreateArchive);

        using SensitiveBackupPassphrase passphrase = Assert.IsType<SensitiveBackupPassphrase>(
            await reader.ReadAsync(request, CancellationToken.None));

        Assert.Equal(value, new string(passphrase.Value.Span));

    }

    [Fact]
    public async Task ReadAsync_rejects_an_empty_passphrase_and_zeroes_the_buffer()
    {

        char[] entered = [];

        BackupPassphraseReader reader = CreateReader(
            new RecordingPrompt([entered]),
            new RecordingFileDescriptorReader(),
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            null,
            BackupPassphraseReadPurpose.OpenArchive);

        BackupPassphraseInputException exception = await Assert.ThrowsAsync<BackupPassphraseInputException>(
            () => reader.ReadAsync(request, CancellationToken.None).AsTask());

        Assert.Equal("Backup passphrase cannot be empty.", exception.Message);

        Assert.Empty(entered);

    }

    [Fact]
    public async Task Disposing_the_result_zeroes_its_owned_character_buffer()
    {

        char[] entered = "owned secret".ToCharArray();

        BackupPassphraseReader reader = CreateReader(
            new RecordingPrompt([entered]),
            new RecordingFileDescriptorReader(),
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            null,
            BackupPassphraseReadPurpose.OpenArchive);

        SensitiveBackupPassphrase passphrase = Assert.IsType<SensitiveBackupPassphrase>(
            await reader.ReadAsync(request, CancellationToken.None));

        passphrase.Dispose();

        Assert.All(entered, character => Assert.Equal('\0', character));

        Assert.Throws<ObjectDisposedException>(() => _ = passphrase.Value);

    }

    [Fact]
    public async Task ReadAsync_rejects_and_zeroes_an_oversized_prompt_buffer()
    {

        char[] entered = Enumerable
            .Repeat('s', ExpectedMaximumPassphraseUnits + 1)
            .ToArray();

        BackupPassphraseReader reader = CreateReader(
            new RecordingPrompt([entered]),
            new RecordingFileDescriptorReader(),
            _ => null);

        BackupPassphraseReadRequest request = new(
            null,
            null,
            BackupPassphraseReadPurpose.OpenArchive);

        SensitiveBackupPassphrase? unboundedResult = null;

        BackupPassphraseInputException? error = null;

        try
        {

            unboundedResult = await reader.ReadAsync(
                request,
                CancellationToken.None);

        }
        catch (BackupPassphraseInputException exception)
        {

            error = exception;

        }
        finally
        {

            unboundedResult?.Dispose();

        }

        Assert.NotNull(error);

        Assert.Equal(
            "Backup passphrase input exceeds the 1 MiB safety limit.",
            error.Message);

        Assert.DoesNotContain(entered, static character => character != '\0');

    }

    [Fact]
    public async Task File_descriptor_reader_reads_one_utf8_line_without_closing_the_callers_descriptor()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string path = Path.GetTempFileName();

        try
        {

            await File.WriteAllTextAsync(
                path,
                "descriptor secret\nignored second line");

            await using FileStream source = File.OpenRead(path);

            int fileDescriptor = checked(
                (int)source.SafeFileHandle.DangerousGetHandle());

            FileDescriptorBackupPassphraseReader reader = new();

            char[] value = Assert.IsType<char[]>(
                await reader.ReadAsync(fileDescriptor, CancellationToken.None));

            try
            {

                Assert.Equal("descriptor secret", new string(value));

                Assert.False(source.SafeFileHandle.IsClosed);

            }
            finally
            {

                Array.Clear(value);

            }

        }
        finally
        {

            File.Delete(path);

        }

    }

    [Fact]
    public async Task File_descriptor_reader_rejects_an_oversized_utf8_line_without_closing_the_callers_descriptor()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        const string marker = "private-fd-marker";

        string path = Path.GetTempFileName();

        byte[] oversized = Enumerable
            .Repeat((byte)'x', ExpectedMaximumPassphraseUnits + 2)
            .ToArray();

        Encoding.UTF8.GetBytes(marker).CopyTo(oversized, 0);

        oversized[^1] = (byte)'\n';

        try
        {

            await File.WriteAllBytesAsync(path, oversized);

            await using FileStream source = File.OpenRead(path);

            int fileDescriptor = checked(
                (int)source.SafeFileHandle.DangerousGetHandle());

            FileDescriptorBackupPassphraseReader reader = new();

            char[]? unboundedResult = null;

            BackupPassphraseInputException? error = null;

            try
            {

                unboundedResult = await reader.ReadAsync(
                    fileDescriptor,
                    CancellationToken.None);

            }
            catch (BackupPassphraseInputException exception)
            {

                error = exception;

            }
            finally
            {

                if (unboundedResult is not null)
                {

                    Array.Clear(unboundedResult);

                }

            }

            Assert.NotNull(error);

            Assert.Equal(
                "Backup passphrase input exceeds the 1 MiB safety limit.",
                error.Message);

            Assert.DoesNotContain(marker, error.Message, StringComparison.Ordinal);

            Assert.False(source.SafeFileHandle.IsClosed);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(oversized);

            File.Delete(path);

        }

    }

    [Fact]
    public async Task Console_prompt_intercepts_every_key_and_never_echoes_the_secret()
    {

        ScriptedPassphraseConsole console = new("hidden secret");

        ConsoleBackupPassphrasePrompt prompt = new(console);

        char[] value = await prompt.ReadHiddenAsync(
            "Backup passphrase: ",
            CancellationToken.None);

        try
        {

            Assert.Equal("hidden secret", new string(value));

            Assert.DoesNotContain("hidden secret", console.Output, StringComparison.Ordinal);

            Assert.Equal(
                "Backup passphrase: " + global::System.Environment.NewLine,
                console.Output);

            Assert.All(console.Intercepts, intercept => Assert.True(intercept));

        }
        finally
        {

            Array.Clear(value);

        }

    }

    [Fact]
    public async Task Console_prompt_rejects_oversized_input_without_echoing_it()
    {

        const string marker = "private-interactive-marker";

        OversizedPassphraseConsole console = new(
            marker,
            ExpectedMaximumPassphraseUnits + 1);

        ConsoleBackupPassphrasePrompt prompt = new(console);

        char[]? unboundedResult = null;

        BackupPassphraseInputException? error = null;

        try
        {

            unboundedResult = await prompt.ReadHiddenAsync(
                "Backup passphrase: ",
                CancellationToken.None);

        }
        catch (BackupPassphraseInputException exception)
        {

            error = exception;

        }
        finally
        {

            if (unboundedResult is not null)
            {

                Array.Clear(unboundedResult);

            }

        }

        Assert.NotNull(error);

        Assert.Equal(
            "Backup passphrase input exceeds the 1 MiB safety limit.",
            error.Message);

        Assert.DoesNotContain(marker, error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(marker, console.Output, StringComparison.Ordinal);

        Assert.Equal(
            "Backup passphrase: " + global::System.Environment.NewLine,
            console.Output);

        Assert.Equal(ExpectedMaximumPassphraseUnits + 1, console.Reads);

    }

    private static BackupPassphraseReader CreateReader(
        IBackupPassphrasePrompt prompt,
        IBackupPassphraseFileDescriptorReader fileDescriptors,
        Func<string, string?> environmentVariableReader) =>
        new(prompt, fileDescriptors, environmentVariableReader);

    private sealed class RecordingPrompt(IEnumerable<char[]> results) : IBackupPassphrasePrompt
    {

        public Queue<char[]> Results { get; } = new(results);

        public List<string> Prompts { get; } = [];

        public ValueTask<char[]> ReadHiddenAsync(
            string prompt,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Prompts.Add(prompt);

            return ValueTask.FromResult(Results.Dequeue());

        }

    }

    private sealed class RecordingFileDescriptorReader : IBackupPassphraseFileDescriptorReader
    {

        public Queue<char[]?> Results { get; } = new();

        public List<int> ReadDescriptors { get; } = [];

        public ValueTask<char[]?> ReadAsync(
            int fileDescriptor,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            ReadDescriptors.Add(fileDescriptor);

            return ValueTask.FromResult(Results.Dequeue());

        }

    }

    private sealed class ScriptedPassphraseConsole : IBackupPassphraseConsole
    {

        private readonly Queue<ConsoleKeyInfo> _keys;

        private readonly StringWriter _error = new();

        public ScriptedPassphraseConsole(string secret)
        {

            _keys = new Queue<ConsoleKeyInfo>(
                secret.Select(character => new ConsoleKeyInfo(
                    character,
                    ConsoleKey.A,
                    shift: false,
                    alt: false,
                    control: false)));

            _keys.Enqueue(new ConsoleKeyInfo(
                '\r',
                ConsoleKey.Enter,
                shift: false,
                alt: false,
                control: false));

        }

        public bool IsInputRedirected => false;

        public TextWriter Error => _error;

        public List<bool> Intercepts { get; } = [];

        public string Output => _error.ToString();

        public ConsoleKeyInfo ReadKey(bool intercept)
        {

            Intercepts.Add(intercept);

            return _keys.Dequeue();

        }

    }

    private sealed class OversizedPassphraseConsole(
        string marker,
        int characterCount) : IBackupPassphraseConsole
    {

        private readonly StringWriter _error = new();

        public bool IsInputRedirected => false;

        public TextWriter Error => _error;

        public int Reads { get; private set; }

        public string Output => _error.ToString();

        public ConsoleKeyInfo ReadKey(bool intercept)
        {

            Assert.True(intercept);

            if (Reads >= characterCount)
            {

                Reads++;

                return new ConsoleKeyInfo(
                    '\r',
                    ConsoleKey.Enter,
                    shift: false,
                    alt: false,
                    control: false);

            }

            char character = Reads < marker.Length
                ? marker[Reads]
                : 'x';

            Reads++;

            return new ConsoleKeyInfo(
                character,
                ConsoleKey.A,
                shift: false,
                alt: false,
                control: false);

        }

    }

}

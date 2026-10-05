using System.ComponentModel;

using System.Runtime.InteropServices;

using System.Security.Cryptography;

using System.Text;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Cli.Services;

internal enum BackupPassphraseReadPurpose
{
    CreateArchive = 0,

    OpenArchive = 1,

    InspectOuterMetadata = 2,
}

internal readonly record struct BackupPassphraseReadRequest(
    string? EnvironmentVariableName,
    int? FileDescriptor,
    BackupPassphraseReadPurpose Purpose);

internal interface IBackupPassphraseReader
{
    ValueTask<SensitiveBackupPassphrase?> ReadAsync(
        BackupPassphraseReadRequest request,
        CancellationToken cancellationToken);
}

internal interface IBackupPassphrasePrompt
{
    ValueTask<char[]> ReadHiddenAsync(
        string prompt,
        CancellationToken cancellationToken);
}

internal interface IBackupPassphraseFileDescriptorReader
{
    ValueTask<char[]?> ReadAsync(
        int fileDescriptor,
        CancellationToken cancellationToken);
}

internal interface IBackupPassphraseConsole
{
    bool IsInputRedirected { get; }

    TextWriter Error { get; }

    /// <summary>Whether a keystroke can be taken without blocking.</summary>
    bool KeyAvailable { get; }

    /// <summary>
    /// When <see langword="true"/>, Ctrl+C is delivered as an ordinary key instead of raising the
    /// terminal interrupt.
    /// </summary>
    bool TreatControlCAsInput { get; set; }

    ConsoleKeyInfo ReadKey(bool intercept);
}

internal sealed class BackupPassphraseInputException : Exception
{
    public BackupPassphraseInputException(string message)
        : base(message)
    {
    }

    public BackupPassphraseInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Resource-safety ceilings only. Passphrases retain arbitrary content and complexity up to
/// 1 MiB of decoded interactive characters or 1 MiB of inherited-descriptor UTF-8 input. The one
/// content rule, the creation floor in <see cref="BackupPassphrasePolicy"/>, is applied by the reader
/// to a passphrase being chosen for a new archive and to nothing else.
/// </summary>
internal static class BackupPassphraseLimits
{
    internal const int MaximumCharacters = 1024 * 1024;

    internal const int MaximumUtf8Bytes = 1024 * 1024;

    internal const string ExceededMessage =
        "Backup passphrase input exceeds the 1 MiB safety limit.";

    internal static BackupPassphraseInputException Exceeded() =>
        new(ExceededMessage);
}

internal sealed class SensitiveBackupPassphrase : IDisposable
{
    private char[]? _characters;

    internal SensitiveBackupPassphrase(char[] characters)
    {
        ArgumentNullException.ThrowIfNull(characters);

        _characters = characters;
    }

    public ReadOnlyMemory<char> Value =>
        _characters is null
            ? throw new ObjectDisposedException(nameof(SensitiveBackupPassphrase))
            : _characters;

    public void Dispose()
    {
        char[]? characters = Interlocked.Exchange(ref _characters, null);

        if (characters is not null)
        {
            BackupPassphraseMemory.Clear(characters);
        }
    }
}

internal sealed class BackupPassphraseReader : IBackupPassphraseReader
{
    internal const string PassphrasePrompt = "Backup passphrase: ";

    internal const string ConfirmationPrompt = "Confirm backup passphrase: ";

    private readonly IBackupPassphrasePrompt _prompt;

    private readonly IBackupPassphraseFileDescriptorReader _fileDescriptors;

    private readonly Func<string, string?> _environmentVariableReader;

    public BackupPassphraseReader()
        : this(
            new ConsoleBackupPassphrasePrompt(),
            new FileDescriptorBackupPassphraseReader(),
            Environment.GetEnvironmentVariable)
    {
    }

    internal BackupPassphraseReader(
        IBackupPassphrasePrompt prompt,
        IBackupPassphraseFileDescriptorReader fileDescriptors,
        Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        ArgumentNullException.ThrowIfNull(fileDescriptors);

        ArgumentNullException.ThrowIfNull(environmentVariableReader);

        _prompt = prompt;

        _fileDescriptors = fileDescriptors;

        _environmentVariableReader = environmentVariableReader;
    }

    public async ValueTask<SensitiveBackupPassphrase?> ReadAsync(
        BackupPassphraseReadRequest request,
        CancellationToken cancellationToken)
    {
        bool hasEnvironmentSource = request.EnvironmentVariableName is not null;

        bool hasFileDescriptorSource = request.FileDescriptor.HasValue;

        if (hasEnvironmentSource && hasFileDescriptorSource)
        {
            throw new BackupPassphraseInputException(
                "Specify exactly one passphrase source: --passphrase-env or --passphrase-fd.");
        }

        char[]? characters = null;

        try
        {
            if (hasEnvironmentSource)
            {
                characters = ReadEnvironmentVariable(request.EnvironmentVariableName!);
            }
            else if (hasFileDescriptorSource)
            {
                characters = await _fileDescriptors
                    .ReadAsync(request.FileDescriptor!.Value, cancellationToken)
                    .ConfigureAwait(false);

                if (characters is null)
                {
                    throw new BackupPassphraseInputException(
                        "The passphrase file descriptor did not contain a value.");
                }
            }
            else if (request.Purpose == BackupPassphraseReadPurpose.InspectOuterMetadata)
            {
                return null;
            }
            else
            {
                characters = await _prompt
                    .ReadHiddenAsync(PassphrasePrompt, cancellationToken)
                    .ConfigureAwait(false);
            }

            EnsureValid(characters);

            // Creation only, and before the confirmation prompt so the operator is not asked to retype
            // a passphrase that was never going to be accepted. Opening an archive takes whatever
            // passphrase it was written under, however short.
            if (request.Purpose == BackupPassphraseReadPurpose.CreateArchive
                && !BackupPassphrasePolicy.MeetsCreateMinimum(characters))
            {
                throw new BackupPassphraseInputException(
                    BackupPassphrasePolicy.CreateMinimumMessage);
            }

            if (!hasEnvironmentSource
                && !hasFileDescriptorSource
                && request.Purpose == BackupPassphraseReadPurpose.CreateArchive)
            {
                char[] confirmation = await _prompt
                    .ReadHiddenAsync(ConfirmationPrompt, cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    EnsureValid(confirmation);

                    if (!BackupPassphraseMemory.FixedTimeEquals(characters, confirmation))
                    {
                        throw new BackupPassphraseInputException(
                            "Backup passphrases did not match.");
                    }
                }
                finally
                {
                    BackupPassphraseMemory.Clear(confirmation);
                }
            }

            SensitiveBackupPassphrase result = new(characters);

            characters = null;

            return result;
        }
        finally
        {
            if (characters is not null)
            {
                BackupPassphraseMemory.Clear(characters);
            }
        }
    }

    private char[] ReadEnvironmentVariable(string variableName)
    {
        string? value;

        try
        {
            value = _environmentVariableReader(variableName);
        }
        catch (ArgumentException exception)
        {
            throw new BackupPassphraseInputException(
                "The passphrase environment-variable reference is invalid.",
                exception);
        }

        if (value is null)
        {
            throw new BackupPassphraseInputException(
                "The referenced passphrase environment variable is not set.");
        }

        if (value.Length > BackupPassphraseLimits.MaximumCharacters)
        {
            throw BackupPassphraseLimits.Exceeded();
        }

        return value.ToCharArray();
    }

    private static void EnsureValid(char[] characters)
    {
        if (characters.Length == 0)
        {
            throw new BackupPassphraseInputException(
                "Backup passphrase cannot be empty.");
        }

        if (characters.Length > BackupPassphraseLimits.MaximumCharacters)
        {
            throw BackupPassphraseLimits.Exceeded();
        }
    }
}

internal sealed class ConsoleBackupPassphrasePrompt : IBackupPassphrasePrompt
{
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly IBackupPassphraseConsole _console;

    public ConsoleBackupPassphrasePrompt()
        : this(new SystemBackupPassphraseConsole())
    {
    }

    internal ConsoleBackupPassphrasePrompt(IBackupPassphraseConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);

        _console = console;
    }

    public ValueTask<char[]> ReadHiddenAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        if (_console.IsInputRedirected)
        {
            throw new BackupPassphraseInputException(
                "Interactive passphrase input requires a terminal. Use --passphrase-env or --passphrase-fd.");
        }

        TextWriter error = _console.Error;

        error.Write(prompt);

        error.Flush();

        char[] characters = new char[32];

        int length = 0;

        bool lineEnded = false;

        // Ctrl+C reaches this loop as a key only while the terminal is told to deliver it as one;
        // otherwise it raises the interrupt and the branch below can never run. The previous mode is
        // restored on every exit so the terminal is handed back exactly as it was found.
        bool previousControlCAsInput = TryReadControlCAsInput();

        bool controlCCaptured = TrySetControlCAsInput(true);

        try
        {
            while (true)
            {
                // Wait by polling rather than parking in ReadKey, so a token that is cancelled while
                // the operator is not typing is observed within one poll interval.
                while (!_console.KeyAvailable)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Thread.Sleep(KeyPollInterval);
                }

                cancellationToken.ThrowIfCancellationRequested();

                ConsoleKeyInfo key = _console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    error.WriteLine();

                    error.Flush();

                    lineEnded = true;

                    return ValueTask.FromResult(
                        characters.AsSpan(0, length).ToArray());
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        length--;

                        characters[length] = '\0';
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.C
                    && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    error.WriteLine();

                    error.Flush();

                    lineEnded = true;

                    throw new OperationCanceledException(
                        "Passphrase entry was cancelled.",
                        cancellationToken);
                }

                if (key.KeyChar == '\0')
                {
                    continue;
                }

                if (length >= BackupPassphraseLimits.MaximumCharacters)
                {
                    throw BackupPassphraseLimits.Exceeded();
                }

                if (length == characters.Length)
                {
                    characters = GrowAndClear(characters);
                }

                characters[length] = key.KeyChar;

                length++;
            }
        }
        finally
        {
            if (controlCCaptured)
            {
                _ = TrySetControlCAsInput(previousControlCAsInput);
            }

            if (!lineEnded)
            {
                error.WriteLine();

                error.Flush();
            }

            BackupPassphraseMemory.Clear(characters);
        }
    }

    private bool TryReadControlCAsInput()
    {
        try
        {
            return _console.TreatControlCAsInput;
        }
        catch (Exception exception) when (
            exception is IOException or PlatformNotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool TrySetControlCAsInput(bool value)
    {
        try
        {
            _console.TreatControlCAsInput = value;

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or PlatformNotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static char[] GrowAndClear(char[] characters)
    {
        int expandedLength = Math.Min(
            checked(characters.Length * 2),
            BackupPassphraseLimits.MaximumCharacters);

        char[] expanded = new char[expandedLength];

        characters.CopyTo(expanded, 0);

        BackupPassphraseMemory.Clear(characters);

        return expanded;
    }
}

internal sealed class SystemBackupPassphraseConsole : IBackupPassphraseConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;

    public TextWriter Error => Console.Error;

    public bool KeyAvailable => Console.KeyAvailable;

    public bool TreatControlCAsInput
    {
        get => Console.TreatControlCAsInput;

        set => Console.TreatControlCAsInput = value;
    }

    public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);
}

internal sealed class FileDescriptorBackupPassphraseReader
    : IBackupPassphraseFileDescriptorReader
{
    private const int InitialBufferSize = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async ValueTask<char[]?> ReadAsync(
        int fileDescriptor,
        CancellationToken cancellationToken)
    {
        if (fileDescriptor < 0)
        {
            throw new BackupPassphraseInputException(
                "The passphrase file descriptor must be zero or greater.");
        }

        try
        {
            using SafeFileHandle handle = DuplicateOrBorrowHandle(fileDescriptor);

            await using FileStream stream = new(
                handle,
                FileAccess.Read,
                bufferSize: 1,
                isAsync: false);

            return await ReadFirstLineAsync(stream, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BackupPassphraseInputException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            throw new BackupPassphraseInputException(
                "The passphrase could not be read from the file descriptor.",
                exception);
        }
    }

    private static async ValueTask<char[]?> ReadFirstLineAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] encoded = new byte[InitialBufferSize];

        byte[] singleByte = new byte[1];

        int length = 0;

        bool receivedInput = false;

        try
        {
            while (true)
            {
                int read = await stream
                    .ReadAsync(singleByte.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                receivedInput = true;

                if (singleByte[0] == (byte)'\n')
                {
                    break;
                }

                if (length >= BackupPassphraseLimits.MaximumUtf8Bytes)
                {
                    throw BackupPassphraseLimits.Exceeded();
                }

                if (length == encoded.Length)
                {
                    encoded = GrowAndClear(encoded);
                }

                encoded[length] = singleByte[0];

                length++;
            }

            if (!receivedInput)
            {
                return null;
            }

            if (length > 0 && encoded[length - 1] == (byte)'\r')
            {
                length--;
            }

            try
            {
                return StrictUtf8.GetChars(encoded, 0, length);
            }
            catch (DecoderFallbackException exception)
            {
                throw new BackupPassphraseInputException(
                    "The passphrase file descriptor must contain UTF-8 text.",
                    exception);
            }
        }
        finally
        {
            BackupPassphraseMemory.Clear(singleByte);

            BackupPassphraseMemory.Clear(encoded);
        }
    }

    private static byte[] GrowAndClear(byte[] encoded)
    {
        int expandedLength = Math.Min(
            checked(encoded.Length * 2),
            BackupPassphraseLimits.MaximumUtf8Bytes);

        byte[] expanded = new byte[expandedLength];

        encoded.CopyTo(expanded, 0);

        BackupPassphraseMemory.Clear(encoded);

        return expanded;
    }

    private static SafeFileHandle DuplicateOrBorrowHandle(int fileDescriptor)
    {
        if (OperatingSystem.IsWindows())
        {
            nint nativeHandle = GetOperatingSystemHandle(fileDescriptor);

            if (nativeHandle == -1)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "The file descriptor is not open.");
            }

            return new SafeFileHandle(nativeHandle, ownsHandle: false);
        }

        int duplicate = DuplicateFileDescriptor(fileDescriptor);

        if (duplicate < 0)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The file descriptor is not open.");
        }

        return new SafeFileHandle((nint)duplicate, ownsHandle: true);
    }

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int DuplicateFileDescriptor(int fileDescriptor);

    [DllImport("msvcrt.dll", EntryPoint = "_get_osfhandle", SetLastError = true)]
    private static extern nint GetOperatingSystemHandle(int fileDescriptor);
}

internal static class BackupPassphraseMemory
{
    public static void Clear(char[] characters) =>
        CryptographicOperations.ZeroMemory(
            MemoryMarshal.AsBytes(characters.AsSpan()));

    public static void Clear(byte[] bytes) =>
        CryptographicOperations.ZeroMemory(bytes);

    public static bool FixedTimeEquals(char[] left, char[] right) =>
        CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(left.AsSpan()),
            MemoryMarshal.AsBytes(right.AsSpan()));
}

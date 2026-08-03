using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal readonly record struct CommandOutputArtifactRegistration(
    string Handle,
    IReadOnlyList<string> AvailableStreams);

internal readonly record struct CommandOutputArtifactPage(
    string Text,
    long Offset,
    long? NextOffset,
    long TotalBytes);

internal sealed class CommandOutputArtifactStore : IAsyncDisposable
{

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ConcurrentDictionary<string, CommandOutputArtifact> _artifacts =
        new(StringComparer.Ordinal);

    private readonly object _rootGate = new();

    private string? _rootPath;

    private volatile bool _disposed;

    internal string SpillDirectory
    {

        get
        {

            lock (_rootGate)
            {

                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_rootPath is not null)
                {

                    return _rootPath;

                }

                DirectoryInfo directory = Directory.CreateTempSubdirectory(
                    "arcanum-command-output-");

                if (!OperatingSystem.IsWindows())
                {

                    File.SetUnixFileMode(
                        directory.FullName,
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute);

                }

                _rootPath = directory.FullName;

                return _rootPath;

            }

        }

    }

    internal string? RootPathForTests
    {

        get
        {

            lock (_rootGate)
            {

                return _rootPath;

            }

        }

    }

    internal CommandOutputArtifactRegistration Register(
        CappedStreamOutput stdout,
        CappedStreamOutput stderr)
    {

        string root = SpillDirectory;

        string? stdoutPath = ValidateCompleteOutputPath(
            root,
            stdout);

        string? stderrPath = ValidateCompleteOutputPath(
            root,
            stderr);

        if (stdoutPath is null && stderrPath is null)
        {

            throw new InvalidOperationException(
                "No complete command output was available to register.");

        }

        string handle = Guid.NewGuid().ToString("N");

        FileStream? stdoutStream = null;

        FileStream? stderrStream = null;

        try
        {

            stdoutStream = OpenRetainedArtifact(stdoutPath);

            stderrStream = OpenRetainedArtifact(stderrPath);

        }
        catch
        {

            stdoutStream?.Dispose();

            stderrStream?.Dispose();

            throw;

        }

        CommandOutputArtifact artifact = new(
            stdoutStream,
            stderrStream);

        lock (_rootGate)
        {

            if (_disposed)
            {

                artifact.Dispose();

                throw new ObjectDisposedException(
                    nameof(CommandOutputArtifactStore));

            }

            if (!_artifacts.TryAdd(handle, artifact))
            {

                artifact.Dispose();

                throw new IOException(
                    "Could not allocate a command-output artifact handle.");

            }

        }

        List<string> streams = [];

        if (stdoutPath is not null)
        {

            streams.Add("stdout");

        }

        if (stderrPath is not null)
        {

            streams.Add("stderr");

        }

        return new CommandOutputArtifactRegistration(
            handle,
            streams);

    }

    internal async Task<CommandOutputArtifactPage> ReadPageAsync(
        string handle,
        string streamName,
        long offset,
        int maxBytes,
        CancellationToken cancellationToken)
    {

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_artifacts.TryGetValue(handle, out CommandOutputArtifact? artifact))
        {

            throw new KeyNotFoundException(
                "The command-output handle is unknown or has expired with its connection.");

        }

        await artifact.ReadGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {

        FileStream? retainedStream = streamName switch
        {
            "stdout" => artifact.Stdout,
            "stderr" => artifact.Stderr,
            _ => throw new ArgumentOutOfRangeException(
                nameof(streamName),
                "Stream must be 'stdout' or 'stderr'."),
        };

        if (retainedStream is null)
        {

            throw new KeyNotFoundException(
                $"The '{streamName}' stream has already been fully read or fit in the execute_command response and has no paged artifact.");

        }

        long totalBytes = RandomAccess.GetLength(
            retainedStream.SafeFileHandle);

        if (offset < 0L || offset > totalBytes)
        {

            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Offset must be between 0 and {totalBytes} bytes.");

        }

        if (maxBytes < 4)
        {

            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                "maxBytes must be at least 4 so one UTF-8 scalar always fits.");

        }

        if (offset == totalBytes)
        {

            CommandOutputArtifactPage emptyPage = new(
                string.Empty,
                offset,
                NextOffset: null,
                totalBytes);

            ReleaseCompletedStream(handle, artifact, streamName);

            return emptyPage;

        }

        int requestedBytes = (int)Math.Min(
            maxBytes,
            totalBytes - offset);

        int bytesToRead = (int)Math.Min(
            requestedBytes + 1L,
            totalBytes - offset);

        byte[] rented = ArrayPool<byte>.Shared.Rent(bytesToRead);

        try
        {

            int bytesRead = 0;

            while (bytesRead < bytesToRead)
            {

                int read = await RandomAccess.ReadAsync(
                        retainedStream.SafeFileHandle,
                        rented.AsMemory(bytesRead, bytesToRead - bytesRead),
                        offset + bytesRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                {

                    break;

                }

                bytesRead += read;

            }

            if (bytesRead <= 0)
            {

                throw new IOException(
                    "The command-output artifact ended before its recorded length.");

            }

            if (IsUtf8ContinuationByte(rented[0]))
            {

                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    "Offset must be a page boundary returned by read_command_output.");

            }

            int pageBytes = Math.Min(
                requestedBytes,
                bytesRead);

            bool hasMore = offset + pageBytes < totalBytes;

            if (hasMore)
            {

                while (pageBytes > 0
                       && IsUtf8ContinuationByte(rented[pageBytes]))
                {

                    pageBytes--;

                }

            }

            if (pageBytes <= 0)
            {

                throw new InvalidDataException(
                    "The requested page could not contain a complete UTF-8 scalar.");

            }

            string text = StrictUtf8.GetString(
                rented,
                0,
                pageBytes);

            long next = offset + pageBytes;

            CommandOutputArtifactPage page = new(
                text,
                offset,
                next < totalBytes ? next : null,
                totalBytes);

            if (page.NextOffset is null)
            {

                ReleaseCompletedStream(handle, artifact, streamName);

            }

            return page;

        }
        finally
        {

            ArrayPool<byte>.Shared.Return(
                rented,
                clearArray: true);

        }

        }
        finally
        {

            artifact.ReadGate.Release();

        }

    }

    internal void Discard(
        CappedStreamOutput stdout,
        CappedStreamOutput stderr)
    {

        TryDelete(stdout.CompleteOutputPath);

        TryDelete(stderr.CompleteOutputPath);

    }

    public ValueTask DisposeAsync()
    {

        string? root;

        lock (_rootGate)
        {

            if (_disposed)
            {

                return ValueTask.CompletedTask;

            }

            _disposed = true;

            root = _rootPath;

        }

        CommandOutputArtifact[] artifacts =
            _artifacts.Values.ToArray();

        _artifacts.Clear();

        foreach (CommandOutputArtifact artifact in artifacts)
        {

            artifact.Dispose();

        }

        if (root is not null)
        {

            try
            {

                Directory.Delete(
                    root,
                    recursive: true);

            }
            catch (DirectoryNotFoundException)
            {

            }
            catch (IOException)
            {

            }
            catch (UnauthorizedAccessException)
            {

            }

        }

        return ValueTask.CompletedTask;

    }

    private static string? ValidateCompleteOutputPath(
        string root,
        CappedStreamOutput output)
    {

        if (!output.Truncated || output.CompleteOutputPath is null)
        {

            return null;

        }

        string fullPath = Path.GetFullPath(
            output.CompleteOutputPath);

        string? parent = Path.GetDirectoryName(fullPath);

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(parent ?? string.Empty),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !File.Exists(fullPath))
        {

            throw new IOException(
                "The complete command-output artifact was not created in the owned connection directory.");

        }

        return fullPath;

    }

    private void ReleaseCompletedStream(
        string handle,
        CommandOutputArtifact artifact,
        string streamName)
    {

        if (!artifact.Release(streamName))
        {

            return;

        }

        _ = _artifacts.TryRemove(handle, out _);

    }

    private static FileStream? OpenRetainedArtifact(string? path)
    {

        if (path is null)
        {

            return null;

        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1,
            FileOptions.Asynchronous
            | FileOptions.RandomAccess
            | FileOptions.DeleteOnClose);

    }

    private static bool IsUtf8ContinuationByte(byte value) =>
        (value & 0b1100_0000) == 0b1000_0000;

    private static void TryDelete(string? path)
    {

        if (path is null)
        {

            return;

        }

        try
        {

            File.Delete(path);

        }
        catch (IOException)
        {

        }
        catch (UnauthorizedAccessException)
        {

        }

    }

    private sealed class CommandOutputArtifact(
        FileStream? stdout,
        FileStream? stderr) : IDisposable
    {

        internal SemaphoreSlim ReadGate { get; } = new(1, 1);

        internal FileStream? Stdout { get; private set; } = stdout;

        internal FileStream? Stderr { get; private set; } = stderr;

        internal bool Release(string streamName)
        {

            FileStream? stream;

            if (streamName == "stdout")
            {

                stream = Stdout;

                Stdout = null;

            }
            else
            {

                stream = Stderr;

                Stderr = null;

            }

            stream?.Dispose();

            return Stdout is null && Stderr is null;

        }

        public void Dispose()
        {

            Stdout?.Dispose();

            Stderr?.Dispose();

        }

    }

}

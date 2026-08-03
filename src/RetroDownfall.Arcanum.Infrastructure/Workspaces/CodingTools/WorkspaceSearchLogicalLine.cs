using System.Buffers;

using System.IO.MemoryMappedFiles;

using System.Runtime.InteropServices;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal interface IWorkspaceSearchLineSpillObserver
{

    void OnCreated(string path);

}

internal delegate TResult WorkspaceSearchLogicalLineReader<TResult>(
    ReadOnlySpan<char> line);

internal sealed class WorkspaceSearchLineSpillException(
    string message,
    Exception innerException) : IOException(message, innerException);

/// <summary>
/// Holds one decoded logical line in a fixed-size pooled buffer and spills only that line to an
/// owner-only temporary UTF-16 file when it outgrows the buffer. A mapped read view preserves exact
/// <see cref="System.Text.RegularExpressions.Regex"/> semantics without a full-line managed string.
/// </summary>
internal sealed unsafe class WorkspaceSearchLogicalLine : IDisposable
{

    internal const int InMemoryCharacterLimit = 256 * 1024;

    private readonly IWorkspaceSearchLineSpillObserver? _spillObserver;

    private char[]? _buffer;

    private FileStream? _spillWriter;

    private OwnedTemporaryFile? _spillArtifact;

    private int _length;

    internal WorkspaceSearchLogicalLine(
        IWorkspaceSearchLineSpillObserver? spillObserver)
    {

        _spillObserver = spillObserver;

        _buffer = ArrayPool<char>.Shared.Rent(InMemoryCharacterLimit);

    }

    internal int Length => _length;

    internal void Append(ReadOnlySpan<char> characters)
    {

        if (characters.IsEmpty)
        {

            return;

        }

        int resultingLength;

        try
        {

            resultingLength = checked(_length + characters.Length);

        }
        catch (OverflowException exception)
        {

            throw new WorkspaceSearchLineSpillException(
                "A logical line exceeded the search coordinate representation.",
                exception);

        }

        try
        {

            if (_spillArtifact is null
                && resultingLength <= InMemoryCharacterLimit)
            {

                characters.CopyTo(_buffer.AsSpan(_length));

                _length = resultingLength;

                return;

            }

            EnsureSpillWriter();

            _spillWriter!.Write(MemoryMarshal.AsBytes(characters));

            _length = resultingLength;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {

            throw new WorkspaceSearchLineSpillException(
                "The owner-only workspace-search line spill could not be written.",
                exception);

        }

    }

    internal TResult Read<TResult>(
        WorkspaceSearchLogicalLineReader<TResult> reader)
    {

        ArgumentNullException.ThrowIfNull(reader);

        if (_spillArtifact is null)
        {

            return reader(_buffer.AsSpan(0, _length));

        }

        try
        {

            CompleteSpillWrite();

            using MemoryMappedFile mapped = MemoryMappedFile.CreateFromFile(
                _spillArtifact.Path,
                FileMode.Open,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read);

            using MemoryMappedViewAccessor view = mapped.CreateViewAccessor(
                offset: 0,
                size: 0,
                MemoryMappedFileAccess.Read);

            byte* pointer = null;

            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

            try
            {

                byte* start = pointer + checked((nint)view.PointerOffset);

                ReadOnlySpan<char> line = new((char*)start, _length);

                return reader(line);

            }
            finally
            {

                view.SafeMemoryMappedViewHandle.ReleasePointer();

            }

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {

            throw new WorkspaceSearchLineSpillException(
                "The owner-only workspace-search line spill could not be read.",
                exception);

        }

    }

    internal void Reset()
    {

        CleanupSpill();

        _length = 0;

    }

    public void Dispose()
    {

        CleanupSpill();

        char[]? buffer = Interlocked.Exchange(ref _buffer, null);

        if (buffer is not null)
        {

            ArrayPool<char>.Shared.Return(buffer);

        }

    }

    private void EnsureSpillWriter()
    {

        if (_spillWriter is not null)
        {

            return;

        }

        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-workspace-search-line-{Guid.NewGuid():N}.tmp");

        OwnedTemporaryFile artifact = OwnedTemporaryFile.Create(
            path,
            out FileStream writer);

        _spillArtifact = artifact;

        _spillWriter = writer;

        _spillObserver?.OnCreated(path);

        if (_length > 0)
        {

            writer.Write(
                MemoryMarshal.AsBytes(
                    _buffer.AsSpan(0, _length)));

        }

    }

    private void CompleteSpillWrite()
    {

        FileStream? writer = Interlocked.Exchange(
            ref _spillWriter,
            null);

        if (writer is null)
        {

            return;

        }

        writer.Flush(flushToDisk: false);

        writer.Dispose();

    }

    private void CleanupSpill()
    {

        try
        {

            _spillWriter?.Dispose();

        }
        finally
        {

            _spillWriter = null;

            OwnedTemporaryFile? artifact = Interlocked.Exchange(
                ref _spillArtifact,
                null);

            if (artifact is not null)
            {

                _ = artifact.TryDelete();

            }

        }

    }

}

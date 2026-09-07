using System.Buffers;

using System.Runtime.CompilerServices;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal sealed record BatchJsonlRecordReadResult(

    long PhysicalLine,

    BatchJsonlRequestLine? Request,

    string? Error);

/// <summary>
/// Reads physical JSONL records without creating one managed <see cref="string"/> per line.
/// Small records stay in a pooled buffer and larger records spill to an owner-only temporary file.
/// The retained per-record byte boundary protects the unavoidable request-DTO materialization, not
/// the total number of records in a batch.
/// </summary>
internal static class BatchJsonlRecordReader
{

    internal const int InMemoryByteLimit = 256 * 1024;

    internal const long MaxRecordBytes = 64L * 1024L * 1024L;

    private const int ReadBufferBytes = 64 * 1024;

    /// <summary>
    /// Read-only lookahead keeps only one logical first byte. A bounded raw I/O buffer may
    /// contain unread source bytes; only ReadRecordAsync materializes or spills a record.
    /// The caller owns the source stream and must serialize access to this cursor.
    /// </summary>
    internal sealed class Cursor : IDisposable
    {
        private readonly Stream _source;

        private readonly BatchJsonlRecordBuffer _record;

        private byte[]? _readBuffer;

        private int _cursor;

        private int _available;

        private int _firstByte = -1;

        private long _leadingWhitespaceBytes;

        private long _physicalLine;

        private bool _endOfSource;

        internal Cursor(
            Stream source,
            string? temporaryDirectory = null,
            Action<string>? spillCreated = null,
            long maxRecordBytes = MaxRecordBytes)
        {
            ArgumentNullException.ThrowIfNull(source);

            ArgumentOutOfRangeException.ThrowIfLessThan(maxRecordBytes, 1);

            _source = source;

            _record = new BatchJsonlRecordBuffer(maxRecordBytes, temporaryDirectory, spillCreated);

            _readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        }

        internal async ValueTask<bool> HasNextRecordAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_readBuffer is null, this);

            cancellationToken.ThrowIfCancellationRequested();

            if (_firstByte >= 0)
            {
                return true;
            }

            while (await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                byte value = _readBuffer![_cursor++];

                if (value == (byte)'\n')
                {
                    _physicalLine++;

                    _leadingWhitespaceBytes = 0;
                }
                else if (value is (byte)' ' or (byte)'\t' or (byte)'\r')
                {
                    _leadingWhitespaceBytes = checked(_leadingWhitespaceBytes + 1);
                }
                else
                {
                    _firstByte = value;

                    return true;
                }
            }

            return false;
        }

        internal async ValueTask<BatchJsonlRecordReadResult> ReadRecordAsync(
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_readBuffer is null, this);

            cancellationToken.ThrowIfCancellationRequested();

            if (_firstByte < 0)
            {
                throw new InvalidOperationException("Read-only lookahead must establish the next batch record first.");
            }

            try
            {
                _record.BeginWithWhitespace(_leadingWhitespaceBytes);

                byte[] first = [(byte)_firstByte];

                _firstByte = -1;

                _leadingWhitespaceBytes = 0;

                await _record.AppendAsync(first, cancellationToken).ConfigureAwait(false);

                while (await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    int newlineOffset = _readBuffer.AsSpan(_cursor, _available - _cursor)
                        .IndexOf((byte)'\n');

                    int count = newlineOffset >= 0 ? newlineOffset : _available - _cursor;

                    await _record.AppendAsync(
                            _readBuffer.AsMemory(_cursor, count),
                            cancellationToken)
                        .ConfigureAwait(false);

                    _cursor += count;

                    if (newlineOffset >= 0)
                    {
                        _cursor++;

                        break;
                    }
                }

                _physicalLine++;

                return (await _record.CompleteAsync(_physicalLine, cancellationToken)
                    .ConfigureAwait(false))!;
            }
            finally
            {
                // The page still owns its effect frontier here; no spill may outlive it.
                _record.Reset();
            }
        }

        private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_cursor < _available)
            {
                return true;
            }

            if (_endOfSource)
            {
                return false;
            }

            _available = await _source.ReadAsync(
                    _readBuffer.AsMemory(0, ReadBufferBytes),
                    cancellationToken)
                .ConfigureAwait(false);

            _cursor = 0;

            _endOfSource = _available == 0;

            return !_endOfSource;
        }

        public void Dispose()
        {
            _record.Dispose();

            byte[]? buffer = Interlocked.Exchange(ref _readBuffer, null);

            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    internal static async IAsyncEnumerable<BatchJsonlRecordReadResult> ReadAsync(
        Stream source,
        string? temporaryDirectory,
        Action<string>? spillCreated,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        long maxRecordBytes = MaxRecordBytes)
    {
        using Cursor reader = new(source, temporaryDirectory, spillCreated, maxRecordBytes);

        while (await reader.HasNextRecordAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return await reader.ReadRecordAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class BatchJsonlRecordBuffer(

        long maxRecordBytes,

        string? temporaryDirectory,

        Action<string>? spillCreated) : IDisposable

    {

        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(InMemoryByteLimit);

        private OwnedTemporaryFile? _spillArtifact;

        private FileStream? _spillStream;

        private long _physicalBytes;

        private int _bufferedBytes;

        private bool _hasNonWhitespace;

        private bool _tooLarge;

        private bool _spillUnavailable;

        internal void BeginWithWhitespace(long count)
        {
            _physicalBytes = count;

            // JSON whitespace is insignificant, but its exact byte count still owns the limit.
            // Keep one space when present so a later BOM cannot become a legal initial preamble.
            if (count > 0)
            {
                _buffer![0] = (byte)' ';

                _bufferedBytes = 1;
            }
        }

        internal async ValueTask AppendAsync(

            ReadOnlyMemory<byte> bytes,

            CancellationToken cancellationToken)

        {

            if (bytes.IsEmpty)

            {

                return;

            }

            _physicalBytes = checked(_physicalBytes + bytes.Length);

            if (!_hasNonWhitespace)

            {

                _hasNonWhitespace = ContainsNonWhitespace(bytes.Span);

            }

            if (_tooLarge || _spillUnavailable)

            {

                return;

            }

            if (_physicalBytes > maxRecordBytes)

            {

                _tooLarge = true;

                CleanupSpill();

                return;

            }

            try

            {

                if (_spillStream is null

                    && _bufferedBytes + bytes.Length <= InMemoryByteLimit)

                {

                    bytes.Span.CopyTo(_buffer.AsSpan(_bufferedBytes));

                    _bufferedBytes += bytes.Length;

                    return;

                }

                await EnsureSpillAsync(cancellationToken).ConfigureAwait(false);

                await _spillStream!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            }
            catch (Exception exception) when (

                exception is IOException

                    or UnauthorizedAccessException

                    or NotSupportedException)

            {

                _spillUnavailable = true;

                CleanupSpill();

            }

        }

        internal async ValueTask<BatchJsonlRecordReadResult?> CompleteAsync(

            long physicalLine,

            CancellationToken cancellationToken)

        {

            try

            {

                if (!_hasNonWhitespace)

                {

                    return null;

                }

                if (_tooLarge)

                {

                    return new BatchJsonlRecordReadResult(

                        physicalLine,

                        Request: null,

                        $"Batch JSONL physical resource protection reached on physical line {physicalLine}: measured {_physicalBytes} UTF-8 bytes; the per-record request materialization limit is {maxRecordBytes} bytes. The oversized record was not allocated or sent to a provider. This line will be checkpointed as an error and processing can continue with physical line {physicalLine + 1}. Split this one provider request into smaller requests.");

                }

                if (_spillUnavailable)

                {

                    return new BatchJsonlRecordReadResult(

                        physicalLine,

                        Request: null,

                        $"Batch JSONL physical resource protection failed on physical line {physicalLine}: the owner-only record spill was unavailable after measuring {_physicalBytes} UTF-8 bytes. The record was not allocated or sent to a provider. This line will be checkpointed as an error and processing can continue with physical line {physicalLine + 1}. Restore temporary-disk capacity and permissions before retrying this line.");

                }

                BatchJsonlRequestLine? request;

                if (_spillStream is null)

                {

                    request = JsonSerializer.Deserialize(

                        TrimUtf8Preamble(_buffer.AsSpan(0, _bufferedBytes)),

                        ArcanumJsonContext.Default.BatchJsonlRequestLine);

                }
                else

                {

                    await _spillStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    _spillStream.Position = 0;

                    request = await JsonSerializer.DeserializeAsync(

                            _spillStream,

                            ArcanumJsonContext.Default.BatchJsonlRequestLine,

                            cancellationToken)

                        .ConfigureAwait(false);

                }

                return new BatchJsonlRecordReadResult(

                    physicalLine,

                    request,

                    Error: null);

            }
            catch (JsonException exception)

            {

                return new BatchJsonlRecordReadResult(

                    physicalLine,

                    Request: null,

                    $"Batch JSONL protocol invariant failed on physical line {physicalLine}: the record is not strict UTF-8 JSON matching the batch request shape ({exception.Message}). This line will be checkpointed as an error and processing can continue with physical line {physicalLine + 1}.");

            }
            catch (Exception exception) when (

                exception is IOException

                    or UnauthorizedAccessException

                    or NotSupportedException)

            {

                return new BatchJsonlRecordReadResult(

                    physicalLine,

                    Request: null,

                    $"Batch JSONL physical resource protection failed on physical line {physicalLine}: the owner-only record spill could not be read after measuring {_physicalBytes} UTF-8 bytes. The record was not sent to a provider. This line will be checkpointed as an error and processing can continue with physical line {physicalLine + 1}. Restore temporary-disk capacity and permissions before retrying this line.");

            }
            finally

            {

                Reset();

            }

        }

        public void Dispose()

        {

            CleanupSpill();

            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);

            if (buffer is not null)

            {

                ArrayPool<byte>.Shared.Return(buffer);

            }

        }

        private async ValueTask EnsureSpillAsync(

            CancellationToken cancellationToken)

        {

            if (_spillStream is not null)

            {

                return;

            }

            string directory = temporaryDirectory ?? Path.GetTempPath();

            string path = Path.Combine(

                directory,

                $"arcanum-batch-jsonl-record-{Guid.NewGuid():N}.tmp");

            OwnedTemporaryFile artifact = OwnedTemporaryFile.Create(

                path,

                out FileStream stream,

                FileAccess.ReadWrite);

            _spillArtifact = artifact;

            _spillStream = stream;

            spillCreated?.Invoke(path);

            if (_bufferedBytes > 0)

            {

                await stream.WriteAsync(

                        _buffer.AsMemory(0, _bufferedBytes),

                        cancellationToken)

                    .ConfigureAwait(false);

            }

        }

        internal void Reset()

        {

            CleanupSpill();

            _physicalBytes = 0;

            _bufferedBytes = 0;

            _hasNonWhitespace = false;

            _tooLarge = false;

            _spillUnavailable = false;

        }

        private void CleanupSpill()

        {

            try

            {

                _spillStream?.Dispose();

            }
            finally

            {

                _spillStream = null;

                OwnedTemporaryFile? artifact = Interlocked.Exchange(

                    ref _spillArtifact,

                    null);

                if (artifact is not null)

                {

                    _ = artifact.TryDelete();

                }

            }

        }

        /// <summary>
        /// Matches the BOM handling that <see cref="JsonSerializer.DeserializeAsync"/> already
        /// applies on the spill path, so an identical record parses the same way whether or not it
        /// fit in <see cref="InMemoryByteLimit"/>.
        /// </summary>
        private static ReadOnlySpan<byte> TrimUtf8Preamble(ReadOnlySpan<byte> bytes) =>

            bytes.StartsWith(Encoding.UTF8.Preamble)

                ? bytes[Encoding.UTF8.Preamble.Length..]

                : bytes;

        private static bool ContainsNonWhitespace(ReadOnlySpan<byte> bytes)

        {

            foreach (byte value in bytes)

            {

                if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r'))

                {

                    return true;

                }

            }

            return false;

        }

    }

}

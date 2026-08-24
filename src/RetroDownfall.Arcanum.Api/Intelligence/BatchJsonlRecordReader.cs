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

    internal static async IAsyncEnumerable<BatchJsonlRecordReadResult> ReadAsync(

        Stream source,

        string? temporaryDirectory,

        Action<string>? spillCreated,

        [EnumeratorCancellation] CancellationToken cancellationToken,

        long maxRecordBytes = MaxRecordBytes)

    {

        ArgumentNullException.ThrowIfNull(source);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecordBytes, 1);

        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);

        using BatchJsonlRecordBuffer record = new(

            maxRecordBytes,

            temporaryDirectory,

            spillCreated);

        long physicalLine = 0;

        try

        {

            while (true)

            {

                int bytesRead = await source.ReadAsync(

                        readBuffer.AsMemory(0, ReadBufferBytes),

                        cancellationToken)

                    .ConfigureAwait(false);

                if (bytesRead == 0)

                {

                    break;

                }

                int cursor = 0;

                while (cursor < bytesRead)

                {

                    int newlineOffset = readBuffer.AsSpan(cursor, bytesRead - cursor)

                        .IndexOf((byte)'\n');

                    if (newlineOffset < 0)

                    {

                        await record.AppendAsync(

                                readBuffer.AsMemory(cursor, bytesRead - cursor),

                                cancellationToken)

                            .ConfigureAwait(false);

                        break;

                    }

                    await record.AppendAsync(

                            readBuffer.AsMemory(cursor, newlineOffset),

                            cancellationToken)

                        .ConfigureAwait(false);

                    physicalLine++;

                    BatchJsonlRecordReadResult? completed = await record.CompleteAsync(

                            physicalLine,

                            cancellationToken)

                        .ConfigureAwait(false);

                    if (completed is not null)

                    {

                        yield return completed;

                    }

                    cursor += newlineOffset + 1;

                }

            }

            if (record.HasPhysicalBytes)

            {

                physicalLine++;

                BatchJsonlRecordReadResult? completed = await record.CompleteAsync(

                        physicalLine,

                        cancellationToken)

                    .ConfigureAwait(false);

                if (completed is not null)

                {

                    yield return completed;

                }

            }

        }
        finally

        {

            ArrayPool<byte>.Shared.Return(readBuffer);

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

        internal bool HasPhysicalBytes => _physicalBytes > 0;

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

        private void Reset()

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

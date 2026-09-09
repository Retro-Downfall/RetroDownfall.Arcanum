using System.Reflection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class BatchJsonlWriterOwnershipTests
{
    [Fact]
    public async Task CreateAsync_DisposesEveryAcquiredWriter_WhenTextWriterConstructionFails()
    {
        TrackingEncryptedBlobWriter output = new(canWrite: true);

        TrackingEncryptedBlobWriter error = new(canWrite: false);

        SequenceEncryptedBlobStore store = new(output, error);

        Task construction = InvokeCreateAsync(store);

        await Assert.ThrowsAsync<ArgumentException>(() => construction);

        Assert.Equal(1, output.DisposeCount);

        Assert.Equal(1, error.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryOwnedWriterExactlyOnce_WhenCalledAgain()
    {
        TrackingEncryptedBlobWriter output = new(canWrite: true);

        TrackingEncryptedBlobWriter error = new(canWrite: true);

        SequenceEncryptedBlobStore store = new(output, error);

        IAsyncDisposable writers = await CreateCarrierAsync(store);

        await writers.DisposeAsync();

        await writers.DisposeAsync();

        Assert.Equal(1, output.DisposeCount);

        Assert.Equal(1, error.DisposeCount);
    }

    [Fact]
    public async Task CreateAsync_DisposesEveryAcquiredWriter_WhenCleanupAlsoFails()
    {
        TrackingEncryptedBlobWriter output = new(canWrite: true);

        TrackingEncryptedBlobWriter error = new(canWrite: false, throwOnDispose: true);

        SequenceEncryptedBlobStore store = new(output, error);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => InvokeCreateAsync(store));

        Assert.Contains(failure.InnerExceptions, static exception => exception is ArgumentException);

        Assert.Contains(
            failure.InnerExceptions,
            static exception => exception is InvalidOperationException { Message: "Injected writer disposal failure." });

        Assert.Equal(1, output.DisposeCount);

        Assert.Equal(1, error.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_AttemptsEveryOwnedWriter_WhenOneDisposalFails()
    {
        TrackingEncryptedBlobWriter output = new(canWrite: true, throwOnDispose: true);

        TrackingEncryptedBlobWriter error = new(canWrite: true);

        SequenceEncryptedBlobStore store = new(output, error);

        IAsyncDisposable writers = await CreateCarrierAsync(store);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writers.DisposeAsync().AsTask());

        Assert.Equal("Injected writer disposal failure.", failure.Message);

        Assert.Equal(1, output.DisposeCount);

        Assert.Equal(1, error.DisposeCount);

        await writers.DisposeAsync();

        Assert.Equal(1, output.DisposeCount);

        Assert.Equal(1, error.DisposeCount);
    }

    private static async Task<IAsyncDisposable> CreateCarrierAsync(IEncryptedBlobStore blobStore)
    {
        Task construction = InvokeCreateAsync(blobStore);

        await construction;

        object result = construction.GetType().GetProperty("Result")?.GetValue(construction)
            ?? throw new InvalidOperationException("Batch JSONL writer factory returned no carrier.");

        return Assert.IsAssignableFrom<IAsyncDisposable>(result);
    }

    private static Task InvokeCreateAsync(IEncryptedBlobStore blobStore)
    {
        Type writerSetType = typeof(BatchProcessingService).GetNestedType(
            "BatchJsonlWriters",
            BindingFlags.NonPublic) ?? throw new InvalidOperationException("Batch JSONL writer carrier was not found.");

        MethodInfo create = writerSetType.GetMethod(
            "CreateAsync",
            BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException("Batch JSONL writer factory was not found.");

        return (Task)(create.Invoke(
            obj: null,
            [blobStore, "output.stage", "error.stage", Guid.NewGuid(), CancellationToken.None])
            ?? throw new InvalidOperationException("Batch JSONL writer factory returned no task."));
    }

    private sealed class SequenceEncryptedBlobStore(params EncryptedBlobWriter[] writers) : IEncryptedBlobStore
    {
        private int _nextWriter;

        public Task<EncryptedBlobWriter> CreateWriterAsync(
            string destinationPath,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int index = Interlocked.Increment(ref _nextWriter) - 1;

            return Task.FromResult(writers[index]);
        }

        public Task<EncryptedBlobDescriptor> WriteAsync(
            string destinationPath,
            Stream plaintext,
            EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default,
            long? plaintextLength = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            string path,
            EncryptedBlobPurpose purpose,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EncryptedBlobDescriptor> InspectAsync(
            string path,
            EncryptedBlobPurpose purpose,
            bool verifyAllChunks,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool HasEnvelope(string path) => throw new NotSupportedException();
    }

    private sealed class TrackingEncryptedBlobWriter(
        bool canWrite,
        bool throwOnDispose = false) : EncryptedBlobWriter
    {
        private readonly MemoryStream _inner = new();

        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => canWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;

            set => throw new NotSupportedException();
        }

        public override Task<EncryptedBlobDescriptor> CompleteAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!canWrite)
            {
                throw new NotSupportedException();
            }

            _inner.Write(buffer, offset, count);
        }

        public override ValueTask DisposeAsync()
        {
            _ = Interlocked.Increment(ref _disposeCount);

            _inner.Dispose();

            GC.SuppressFinalize(this);

            return throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException("Injected writer disposal failure."))
                : ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _ = Interlocked.Increment(ref _disposeCount);

                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

using System.Collections.Concurrent;

using System.Data.Common;

using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Hosting;

using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.AI;

using Microsoft.EntityFrameworkCore;

using Microsoft.EntityFrameworkCore.Diagnostics;

using Microsoft.EntityFrameworkCore.Infrastructure;

using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Events;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Logging;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Logging;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Api;

internal sealed class ProfileFileEncryptionSecretStore(ISecretStore seeded, OsKeychainSecretStore durable) : ISecretStore
{
    public Task<string?> GetApiKeyAsync() => seeded.GetApiKeyAsync();

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => seeded.GetApiKeyReadResultAsync();

    public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() => seeded.PeekApiKeyReadResultAsync();

    public Task SaveApiKeyAsync(string apiKey) => seeded.SaveApiKeyAsync(apiKey);

    public Task<string?> GetGrimoireEncryptionSecretAsync() => seeded.GetGrimoireEncryptionSecretAsync();

    public Task<SecretStoreReadResult> GetGrimoireEncryptionSecretReadResultAsync() => seeded.GetGrimoireEncryptionSecretReadResultAsync();

    public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => seeded.SaveGrimoireEncryptionSecretAsync(encryptionSecret);

    public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() => durable.GetFileEncryptionSecretReadResultAsync();

    public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync() => durable.PeekFileEncryptionSecretReadResultAsync();

    public Task SaveFileEncryptionSecretAsync(string encryptionSecret) => durable.SaveFileEncryptionSecretAsync(encryptionSecret);
}

internal sealed class PausingEfOpenInterceptor : DbConnectionInterceptor
{
    internal DbContext? TargetContext { get; set; }

    internal DbContext? Context { get; private set; }

    internal DbConnection? Connection { get; private set; }

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (TargetContext is not null && ReferenceEquals(TargetContext, eventData.Context))
        {
            Context = eventData.Context;

            Connection = connection;

            await Checkpoint.PauseAsync(cancellationToken);
        }

        return result;
    }
}

internal sealed class PausingOrdinaryConnectionFactoryTestSeam : IGrimoireOrdinaryConnectionFactoryTestSeam
{
    private readonly IGrimoireOrdinaryConnectionFactoryTestSeam _inner = new NoOpGrimoireOrdinaryConnectionFactoryTestSeam();

    private readonly AsyncLocal<bool> _selected = new();

    private readonly ConcurrentQueue<SqliteConnection> _clearedConnections = new();

    private int _selectedConstructions;

    internal int SelectedConstructions => Volatile.Read(ref _selectedConstructions);

    internal IReadOnlyList<SqliteConnection> ClearedConnections => _clearedConnections.ToArray();

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal IDisposable SelectCurrentExecution()
    {
        bool previous = _selected.Value;

        _selected.Value = true;

        return new Selection(_selected, previous);
    }

    public void BeforeProviderConstruction()
    {
        _inner.BeforeProviderConstruction();

        if (_selected.Value)
        {
            Interlocked.Increment(ref _selectedConstructions);
        }
    }

    public async ValueTask BeforeNativeOpenAsync(CancellationToken cancellationToken)
    {
        await _inner.BeforeNativeOpenAsync(cancellationToken);

        if (_selected.Value)
        {
            await Checkpoint.PauseAsync(cancellationToken);
        }
    }

    public void AfterExactPoolClear(SqliteConnection connection)
    {
        _inner.AfterExactPoolClear(connection);

        if (_selected.Value)
        {
            _clearedConnections.Enqueue(connection);
        }
    }

    private sealed class Selection(AsyncLocal<bool> selected, bool previous) : IDisposable
    {
        public void Dispose() => selected.Value = previous;
    }
}

internal sealed class ObservingCovenantConnectionDrain(CovenantConnectionDrain inner) : ICovenantConnectionDrain
{
    private readonly ConcurrentQueue<DrainEnrolmentObservation> _enrolments = new();

    private readonly ConcurrentQueue<PoolClearObservation> _poolClears = new();

    internal IReadOnlyList<DrainEnrolmentObservation> Enrolments => _enrolments.ToArray();

    internal IReadOnlyList<PoolClearObservation> PoolClears => _poolClears.ToArray();

    internal MaintenanceStageObservation Draining { get; } = new();

    public IDisposable Register(SqliteConnection connection) => Observe(connection, inner.Register(connection));

    public IDisposable Register(SqliteConnection connection, ICovenantPhysicalCloseObserver observer) =>
        Observe(connection, inner.Register(connection, observer));

    private IDisposable Observe(SqliteConnection connection, IDisposable registration)
    {
        DrainEnrolmentObservation observation = new(connection, registration);

        _enrolments.Enqueue(observation);

        return observation;
    }

    public Result ClearExactPoolAfterClose(SqliteConnection connection)
    {
        System.Data.ConnectionState state = connection.State;

        Result result = inner.ClearExactPoolAfterClose(connection);

        _poolClears.Enqueue(new PoolClearObservation(connection, state, result));

        return result;
    }

    public async Task<Result> DrainAsync(CancellationToken cancellationToken)
    {
        Draining.Enter();

        try
        {
            return await inner.DrainAsync(cancellationToken);
        }
        finally
        {
            Draining.Complete();
        }
    }
}

internal sealed record PoolClearObservation(SqliteConnection Connection, System.Data.ConnectionState StateBefore, Result Result);

internal sealed class DrainEnrolmentObservation(SqliteConnection connection, IDisposable registration) : IDisposable
{
    private int _disposals;

    internal SqliteConnection Connection { get; } = connection;

    internal int Disposals => Volatile.Read(ref _disposals);

    public void Dispose()
    {
        registration.Dispose();

        Interlocked.Increment(ref _disposals);
    }
}

internal sealed class ControlledWeaveService(WeaveService inner) : IWeaveService
{
    private readonly TaskCompletionSource _secondCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _calls;

    private int _completed;

    private int _cancellations;

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal int Calls => Volatile.Read(ref _calls);

    internal int Completed => Volatile.Read(ref _completed);

    internal int Cancellations => Volatile.Read(ref _cancellations);

    internal Task WaitUntilSecondCallAsync() => _secondCall.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public bool IsAvailable => inner.IsAvailable;

    public async Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        Result<Embedding<float>[]> result = await EmbedBatchAsync([text], cancellationToken);

        return result.IsSuccess ? Result<Embedding<float>>.Success(result.Value[0]) : Result<Embedding<float>>.Failure(result.Error);
    }

    public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _calls);

        if (call == 2)
        {
            _secondCall.TrySetResult();
        }

        try
        {
            if (call == 1)
            {
                await Checkpoint.PauseAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            Embedding<float>[] embeddings = texts.Select(_ =>
            {
                float[] vector = new float[64];

                vector[0] = 1;

                return new Embedding<float>(vector);
            }).ToArray();

            Interlocked.Increment(ref _completed);

            return Result<Embedding<float>[]>.Success(embeddings);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancellations);

            throw;
        }
    }

    public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
        inner.ChunkAsync(text, cancellationToken);
}

// Only the real indexing worker receives this factory; inspection/request scopes remain separate.
internal sealed class ObservingWorkerScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
{
    private readonly ConcurrentQueue<WorkerScopeObservation> _scopes = new();

    private readonly TaskCompletionSource<WorkerScopeObservation> _firstScope = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<WorkerScopeObservation> Scopes => _scopes.ToArray();

    internal async Task WaitUntilInitialScopeDisposedAsync()
    {
        WorkerScopeObservation first = await _firstScope.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await first.WaitUntilDisposedAsync();
    }

    public IServiceScope CreateScope()
    {
        IServiceScope scope = inner.CreateScope();

        WorkerScopeObservation observation = new();

        _scopes.Enqueue(observation);

        _firstScope.TrySetResult(observation);

        return new ObservedScope(scope, observation);
    }

    private sealed class ObservedScope(IServiceScope innerScope, WorkerScopeObservation observation) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => innerScope.ServiceProvider;

        public void Dispose()
        {
            innerScope.Dispose();

            observation.Disposed();
        }

        public async ValueTask DisposeAsync()
        {
            if (innerScope is IAsyncDisposable asynchronous)
            {
                await asynchronous.DisposeAsync();
            }
            else
            {
                innerScope.Dispose();
            }

            observation.Disposed();
        }
    }
}

internal sealed class WorkerScopeObservation
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposals;

    internal int Disposals => Volatile.Read(ref _disposals);

    internal Task WaitUntilDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal void Disposed()
    {
        Interlocked.Increment(ref _disposals);

        _disposed.TrySetResult();
    }
}

internal sealed record MaintenanceHostLog(string Category, Microsoft.Extensions.Logging.LogLevel Level, string Message, string? Template, Exception? Exception);

internal sealed class MaintenanceHostLogCapture : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly ConcurrentQueue<MaintenanceHostLog> _entries = new();

    internal IReadOnlyList<MaintenanceHostLog> Entries => _entries.ToArray();

    // These exact framework warnings describe the existing temporary-profile key storage and
    // TestServer's absent body-size feature. Retain them; permit no application warning or error.
    internal IReadOnlyList<MaintenanceHostLog> Unexpected => Entries.Where(entry =>
        entry.Level != Microsoft.Extensions.Logging.LogLevel.Warning
        || entry.Exception is not null
        || !((entry.Category == "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager"
                && entry.Template == "No XML encryptor configured. Key {KeyId:B} may be persisted to storage in unencrypted form.")
            || (entry.Category == "Microsoft.AspNetCore.Routing.EndpointRoutingMiddleware"
                && entry.Template == "A request body size limit could not be applied. This server does not support the IHttpMaxRequestBodySizeFeature."))).ToArray();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<MaintenanceHostLog> entries) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
            {
                string? template = (state as IEnumerable<KeyValuePair<string, object?>>)?
                    .FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;

                entries.Enqueue(new(category, level, formatter(state, exception), template, exception));
            }
        }
    }
}

internal sealed class ObservingIndexingLogger(Microsoft.Extensions.Logging.ILoggerFactory factory) : Microsoft.Extensions.Logging.ILogger<SessionAttachmentIndexingService>
{
    private readonly TestCapturingLogger<SessionAttachmentIndexingService> _inner = new();

    private readonly Microsoft.Extensions.Logging.ILogger _forward = factory.CreateLogger(typeof(SessionAttachmentIndexingService).FullName!);

    private readonly TaskCompletionSource _deferred = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Guid _expected;

    internal IReadOnlyCollection<TestLogEntry> Entries => _inner.Entries;

    internal void ExpectDeferred(Guid attachmentId) => _expected = attachmentId;

    internal Task WaitUntilDeferredAsync() => _deferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _inner.Log(logLevel, eventId, state, exception, formatter);

        _forward.Log(logLevel, eventId, state, exception, formatter);

        string message = formatter(state, exception);

        if (message.Contains(_expected.ToString(), StringComparison.OrdinalIgnoreCase)
            && message.Contains("indexing deferred", StringComparison.Ordinal))
        {
            _deferred.TrySetResult();
        }
    }
}

internal sealed class ControlledChatClientFactory : IChatClientFactory
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _calls;

    private int _completed;

    private int _disposals;

    private int _bufferedCalls;

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal int Calls => Volatile.Read(ref _calls);

    internal int Completed => Volatile.Read(ref _completed);

    internal int Disposals => Volatile.Read(ref _disposals);

    internal int BufferedCalls => Volatile.Read(ref _bufferedCalls);

    internal Task WaitUntilDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
        ResolveClientAsync(new ProviderSettings { Name = "test", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1", Models = ["mistral:latest"] }, targetModel ?? "mistral:latest", cancellationToken);

    public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken) =>
        Task.FromResult(new ChatClientLease(new ControlledChatClient(this), provider, resolvedModel, null));

    private sealed class ControlledChatClient(ControlledChatClientFactory owner) : IChatClient
    {
        private int _disposed;

        private bool _streaming;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Interlocked.Increment(ref owner._bufferedCalls);

            string content = options?.ResponseFormat is ChatResponseFormatJson
                ? System.Text.Json.JsonSerializer.Serialize(new LexiconEntityExtractionResponse([]),
                    RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.LexiconEntityExtractionResponse)
                : "Controlled conversation";

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, content)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _streaming = true;

            Interlocked.Increment(ref owner._calls);

            yield return new ChatResponseUpdate(ChatRole.Assistant, "controlled-billable-prefix");

            await owner.Checkpoint.PauseAsync(cancellationToken);

            yield return new ChatResponseUpdate(ChatRole.Assistant, "controlled-billable-complete");

            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = 7, OutputTokenCount = 2 })])
            {
                FinishReason = ChatFinishReason.Stop,
            };

            Interlocked.Increment(ref owner._completed);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _streaming)
            {
                Interlocked.Increment(ref owner._disposals);

                owner._disposed.TrySetResult();
            }
        }
    }
}

internal sealed class ControlledEncryptedBlobStore(EncryptedBlobStore inner) : IEncryptedBlobStore
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _wrappedReads;

    private long _bytesRead;

    internal string? DownloadPath { get; set; }

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal int WrappedReads => Volatile.Read(ref _wrappedReads);

    internal long BytesRead => Interlocked.Read(ref _bytesRead);

    internal Task WaitUntilDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public Task<EncryptedBlobDescriptor> WriteAsync(string destinationPath, Stream plaintext, EncryptedBlobPurpose purpose,
        ReadOnlyMemory<byte> authenticatedMetadata = default, long? plaintextLength = null, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(destinationPath, plaintext, purpose, authenticatedMetadata, plaintextLength, cancellationToken);

    public async Task<Stream> OpenReadAsync(string path, EncryptedBlobPurpose purpose, CancellationToken cancellationToken = default)
    {
        Stream stream = await inner.OpenReadAsync(path, purpose, cancellationToken);

        if (!string.Equals(Path.GetFullPath(path), DownloadPath, StringComparison.Ordinal))
        {
            return stream;
        }

        Interlocked.Increment(ref _wrappedReads);

        return new ObservedReadStream(stream, this);
    }

    public Task<EncryptedBlobWriter> CreateWriterAsync(string destinationPath, EncryptedBlobPurpose purpose,
        ReadOnlyMemory<byte> authenticatedMetadata = default, CancellationToken cancellationToken = default) =>
        inner.CreateWriterAsync(destinationPath, purpose, authenticatedMetadata, cancellationToken);

    public Task<EncryptedBlobDescriptor> InspectAsync(string path, EncryptedBlobPurpose purpose, bool verifyAllChunks, CancellationToken cancellationToken = default) =>
        inner.InspectAsync(path, purpose, verifyAllChunks, cancellationToken);

    public bool HasEnvelope(string path) => inner.HasEnvelope(path);

    private sealed class ObservedReadStream(Stream inner, ControlledEncryptedBlobStore observer) : Stream
    {
        private bool _prefixRead;

        private int _disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixRead)
            {
                await observer.Checkpoint.PauseAsync(cancellationToken);
            }

            int read = await inner.ReadAsync(_prefixRead ? buffer : buffer[..Math.Min(8, buffer.Length)], cancellationToken);

            if (read > 0)
            {
                _prefixRead = true;

                Interlocked.Add(ref observer._bytesRead, read);
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The controlled download reader requires asynchronous reads.");

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();

                observer._disposed.TrySetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await inner.DisposeAsync();

                observer._disposed.TrySetResult();
            }

            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class ControlledFrameProducer<T>(T frame, CancellationToken shutdown)
{
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal Task WaitUntilCancelledAsync() => _cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal Task WaitUntilDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(() => _cancelled.TrySetResult());

        try
        {
            yield return frame;

            await Checkpoint.PauseAsync(shutdown);

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _disposed.TrySetResult();
        }
    }
}

internal sealed class ControlledEventBus(InMemoryEventBus inner, CancellationToken shutdown) : IEventBus
{
    internal ControlledFrameProducer<DaemonEvent> Daemon { get; } = new(
        new DaemonEvent(DateTimeOffset.UnixEpoch, Guid.Parse("25700000-0000-0000-0000-000000000001"),
            "controlled-daemon", "controlled-spell", DaemonEventType.Started), shutdown);

    internal ControlledFrameProducer<McpServerEvent> Mcp { get; } = new(
        new McpServerEvent(DateTimeOffset.UnixEpoch) { ServerName = "controlled-mcp", State = McpServerState.Running }, shutdown);

    public void Publish<T>(T @event) where T : notnull => inner.Publish(@event);

    public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull
    {
        if (typeof(T) == typeof(DaemonEvent))
        {
            return (IAsyncEnumerable<T>)(object)Daemon.ReadAsync(cancellationToken);
        }

        if (typeof(T) == typeof(McpServerEvent))
        {
            return (IAsyncEnumerable<T>)(object)Mcp.ReadAsync(cancellationToken);
        }

        return inner.Subscribe<T>(cancellationToken);
    }
}

internal sealed class ControlledLogQueryService(CancellationToken shutdown) : ILogQueryService
{
    internal ControlledFrameProducer<LogEntry> Frames { get; } = new(
        new LogEntry(257, DateTimeOffset.UnixEpoch, LogLevel.Information, "MaintenanceTests", "controlled-log", null, null, null, []), shutdown);

    public Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken ct) =>
        Task.FromResult(new LogQueryResult([], null, false));

    public IAsyncEnumerable<LogEntry> StreamAsync(LogQueryRequest? request, CancellationToken ct) => Frames.ReadAsync(ct);
}

internal sealed class MaintenanceCheckpoint
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task WaitUntilReachedAsync() => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal async ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        _reached.TrySetResult();

        await _released.Task.WaitAsync(cancellationToken);
    }

    internal void Release() => _released.TrySetResult();
}

internal sealed class MaintenanceScopeSentinel : IDisposable, IAsyncDisposable
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task WaitUntilDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Dispose() => _disposed.TrySetResult();

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }
}

// Installed only through the factory's endpoint hook: append-only startup middleware was
// proven unreachable behind the real endpoint middleware by the focused placement RED.
internal sealed class PostAdmissionEndpointProbe(Func<string> path)
{
    internal PostAdmissionEndpointProbe(string path) : this(() => path)
    {
    }

    internal string Path => path();

    private int _invocations;

    private int _executing;

    internal const string Header = "X-Arcanum-Maintenance-Test";

    internal string RequestId { get; } = Guid.NewGuid().ToString("N");

    internal ArcanumDbContext? Context { get; private set; }

    internal GrimoireRequestAdmissionScope? Admission { get; private set; }

    internal IGrimoireRequestLease? ObservedLease { get; private set; }

    internal int Invocations => Volatile.Read(ref _invocations);

    internal MaintenanceScopeSentinel? Scope { get; private set; }

    internal System.Data.ConnectionState? InitialConnectionState { get; private set; }

    internal DbContextId? ContextLeaseId { get; private set; }

    internal bool IsExecuting => Volatile.Read(ref _executing) != 0;

    internal bool IsSelectedContext(DbContext? context) => IsExecuting
        && context is not null
        && ReferenceEquals(Context, context)
        && ContextLeaseId == context.ContextId;

    internal async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == Path && context.Request.Headers[Header] == RequestId)
        {
            Admission = context.RequestServices.GetRequiredService<GrimoireRequestAdmissionScope>();

            ObservedLease = Admission.Lease;

            Interlocked.Increment(ref _invocations);

            Scope = context.RequestServices.GetRequiredService<MaintenanceScopeSentinel>();

            if (Path == "/api/grimoire/stats")
            {
                Context = context.RequestServices.GetRequiredService<ArcanumDbContext>();

                ContextLeaseId = Context.ContextId;

                await Context.Database.CloseConnectionAsync();

                InitialConnectionState = Context.Database.GetDbConnection().State;
            }

            Volatile.Write(ref _executing, 1);

            try
            {
                await next(context);
            }
            finally
            {
                Volatile.Write(ref _executing, 0);
            }

            return;
        }

        await next(context);
    }
}

internal sealed class PausingStatsConnectionInterceptor(PostAdmissionEndpointProbe probe) : DbConnectionInterceptor
{
    private int _callbacks;

    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal int Callbacks => Volatile.Read(ref _callbacks);

    internal ArcanumDbContext? Context { get; private set; }

    internal DbConnection? Connection { get; private set; }

    internal System.Data.ConnectionState? ConnectionStateAtPause { get; private set; }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (probe.IsSelectedContext(eventData.Context))
        {
            Interlocked.Increment(ref _callbacks);

            Context = (ArcanumDbContext)eventData.Context!;

            Connection = connection;

            ConnectionStateAtPause = connection.State;

            await Checkpoint.PauseAsync(cancellationToken);
        }
    }
}

internal readonly record struct OrdinaryMutationCounters(
    long IndexAttempts,
    long Billing,
    long Failures,
    long Watermarks);

internal sealed class OrdinaryMutationCounterInterceptor : DbCommandInterceptor
{

    private long _indexAttempts;

    private long _billing;

    private long _failures;

    private long _watermarks;

    internal OrdinaryMutationCounters Snapshot => new(
        Interlocked.Read(ref _indexAttempts),
        Interlocked.Read(ref _billing),
        Interlocked.Read(ref _failures),
        Interlocked.Read(ref _watermarks));

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {

        Observe(command);

        return result;

    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {

        Observe(command);

        return ValueTask.FromResult(result);

    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {

        Observe(command);

        return result;

    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {

        Observe(command);

        return ValueTask.FromResult(result);

    }

    private void Observe(DbCommand command)
    {

        string sql = command.CommandText;

        if (!sql.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            && !sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
            && !sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase))
        {

            return;

        }

        if (sql.Contains("session_attachment_index_state", StringComparison.OrdinalIgnoreCase))
        {

            Interlocked.Increment(ref _indexAttempts);

        }

        if (sql.Contains("BillableOperations", StringComparison.OrdinalIgnoreCase))
        {

            Interlocked.Increment(ref _billing);

        }

        if (sql.Contains("FailureCode", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("LastFailure", StringComparison.OrdinalIgnoreCase))
        {

            Interlocked.Increment(ref _failures);

        }

        if (sql.Contains("UnseenServantWatermarks", StringComparison.OrdinalIgnoreCase))
        {

            Interlocked.Increment(ref _watermarks);

        }

    }

}

// Wiring control only. Production stats executes raw SqliteCommand and is observed above.
internal sealed class PausingFactoryCommandInterceptor : DbCommandInterceptor
{
    internal MaintenanceCheckpoint Checkpoint { get; } = new();

    internal System.Data.ConnectionState? ConnectionStateAtPause { get; private set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("COUNT(*) FROM \"Sessions\"", StringComparison.Ordinal)
            && command.CommandText.Contains("COUNT(*) FROM \"Entries\"", StringComparison.Ordinal)
            && command.CommandText.Contains("COUNT(*) FROM \"Campaigns\"", StringComparison.Ordinal))
        {
            ConnectionStateAtPause = command.Connection?.State;

            await Checkpoint.PauseAsync(cancellationToken);
        }

        return result;
    }
}

internal sealed class GrimoireLeaseObservation<TKind>(
    TKind kind,
    long generation,
    bool acquired,
    CancellationToken maintenanceRevocation)
{
    private int _disposals;

    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TKind Kind { get; } = kind;

    internal long Generation { get; } = generation;

    internal bool Acquired { get; } = acquired;

    internal IAsyncDisposable? Lease { get; set; }

    internal CancellationToken MaintenanceRevocation { get; } = maintenanceRevocation;

    internal int Disposals => Volatile.Read(ref _disposals);

    internal Task WaitUntilDisposedAsync() => _disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal void Disposed()
    {
        Interlocked.Increment(ref _disposals);

        _disposed.TrySetResult();
    }
}

internal sealed class GrimoireMaintenanceAdmissionObserver(
    GrimoireConnectionAdmissionGate inner) : IGrimoireConnectionAdmissionGate
{
    private readonly ConcurrentDictionary<IGrimoireRequestLease, IGrimoireRequestLease> _requests = new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentDictionary<IGrimoireWorkLease, IGrimoireWorkLease> _work = new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentDictionary<IGrimoireClosingOwner, IGrimoireClosingOwner> _owners = new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentDictionary<IGrimoireClosingOwner, Lazy<IGrimoireClosingOwner>> _ownerWrappers = new(ReferenceEqualityComparer.Instance);

    private readonly ConcurrentQueue<GrimoireLeaseObservation<GrimoireRequestKind>> _requestAttempts = new();

    private readonly ConcurrentQueue<GrimoireLeaseObservation<GrimoireWorkKind>> _workAttempts = new();

    private readonly ConcurrentQueue<GrimoireEffectObservation> _effects = new();

    private readonly ConcurrentQueue<GrimoireTicketObservation> _tickets = new();

    private readonly ConcurrentQueue<GrimoireOwnerObservation> _closingOwners = new();

    private readonly ConcurrentQueue<GrimoireOwnerObservation> _closedLeases = new();

    internal IReadOnlyList<GrimoireLeaseObservation<GrimoireRequestKind>> RequestAttempts => _requestAttempts.ToArray();

    internal IReadOnlyList<GrimoireLeaseObservation<GrimoireWorkKind>> WorkAttempts => _workAttempts.ToArray();

    internal MaintenanceStageObservation StageOne { get; } = new();

    internal MaintenanceStageObservation StageTwo { get; } = new();

    internal IReadOnlyList<GrimoireEffectObservation> Effects => _effects.ToArray();

    internal IReadOnlyList<GrimoireTicketObservation> Tickets => _tickets.ToArray();

    internal IReadOnlyList<GrimoireOwnerObservation> ClosingOwners => _closingOwners.ToArray();

    internal IReadOnlyList<GrimoireOwnerObservation> ClosedLeases => _closedLeases.ToArray();

    internal IGrimoireExclusiveClosedLease? LastClosedLease { get; private set; }

    internal IGrimoireMaintenanceIoLane? LastMaintenanceLane { get; private set; }

    public long CurrentGeneration => inner.CurrentGeneration;

    internal IGrimoireRequestLease InnerRequest(IGrimoireRequestLease lease) =>
        _requests.TryGetValue(lease, out IGrimoireRequestLease? actual) ? actual : lease;

    internal IGrimoireWorkLease InnerWork(IGrimoireWorkLease lease) =>
        _work.TryGetValue(lease, out IGrimoireWorkLease? actual) ? actual : lease;

    private IGrimoireClosingOwner InnerOwner(IGrimoireClosingOwner owner) =>
        _owners.TryGetValue(owner, out IGrimoireClosingOwner? actual) ? actual : owner;

    public bool TryAcquireRequestLease(GrimoireRequestKind kind, out IGrimoireRequestLease? lease)
    {
        bool acquired = inner.TryAcquireRequestLease(kind, out IGrimoireRequestLease? actual);

        GrimoireLeaseObservation<GrimoireRequestKind> observation = new(
            kind, actual?.Generation ?? CurrentGeneration, acquired, actual?.MaintenanceRevocation ?? default);

        _requestAttempts.Enqueue(observation);

        lease = actual is null ? null : new ObservedRequest(actual, observation);

        observation.Lease = lease;

        if (lease is not null)
        {
            _requests[lease] = actual!;
        }

        return acquired;
    }

    public bool TryAcquireWorkLease(GrimoireWorkKind kind, out IGrimoireWorkLease? lease)
    {
        bool acquired = inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? actual);

        GrimoireLeaseObservation<GrimoireWorkKind> observation = new(
            kind, actual?.Generation ?? CurrentGeneration, acquired, actual?.MaintenanceRevocation ?? default);

        _workAttempts.Enqueue(observation);

        lease = actual is null ? null : new ObservedWork(actual, observation, _effects);

        observation.Lease = lease;

        if (lease is not null)
        {
            _work[lease] = actual!;
        }

        return acquired;
    }

    public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection)
    {
        IGrimoireConnectionOpenTicket actual = inner.AcquireOrdinaryOpen(connection);

        GrimoireTicketObservation observation = new(connection, actual.Generation);

        _tickets.Enqueue(observation);

        IGrimoireConnectionOpenTicket ticket = new ObservedTicket(actual, observation);

        observation.Ticket = ticket;

        return ticket;
    }

    public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
        CovenantExclusiveRecoveryOwner owner,
        IGrimoireRequestLease? initiatingRequest = null,
        DbConnection? scopedConnection = null)
    {
        Result<IGrimoireClosingOwner> result = inner.BeginOrResumeExclusive(
            owner, initiatingRequest is null ? null : InnerRequest(initiatingRequest), scopedConnection);

        if (result.IsFailure)
        {
            return result;
        }

        // GetOrAdd may evaluate several factories. Only the winning Lazy records a capability,
        // so concurrent resumption cannot publish phantom owners or duplicate observations.
        IGrimoireClosingOwner wrapped = _ownerWrappers.GetOrAdd(result.Value, actual => new(() =>
        {
            GrimoireOwnerObservation observation = new(actual.Owner, actual.Generation);

            _closingOwners.Enqueue(observation);

            IGrimoireClosingOwner wrapper = new ObservedOwner(actual, observation);

            _owners[wrapper] = actual;

            return wrapper;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        return Result<IGrimoireClosingOwner>.Success(wrapped);
    }

    public async ValueTask<Result> DrainRequestAndWorkAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken)
    {
        StageOne.Enter();

        try
        {
            return await inner.DrainRequestAndWorkAsync(InnerOwner(closingOwner), cancellationToken);
        }
        finally
        {
            StageOne.Complete();

            if (StageOne.HoldAfterCompletion)
            {
                await StageOne.AfterCompletion.PauseAsync(cancellationToken);
            }
        }
    }

    public async ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
        IGrimoireClosingOwner closingOwner,
        CancellationToken cancellationToken)
    {
        ValueTask<Result<IGrimoireExclusiveClosedLease>> closing = inner.CloseConnectionAdmissionAsync(InnerOwner(closingOwner), cancellationToken);

        StageTwo.Enter();

        try
        {
            Result<IGrimoireExclusiveClosedLease> result = await closing;

            if (result.IsFailure)
            {
                return result;
            }

            GrimoireOwnerObservation observation = new(result.Value.Owner, result.Value.Generation);

            _closedLeases.Enqueue(observation);

            LastClosedLease = new ObservedClosedLease(result.Value, observation, lane => LastMaintenanceLane = lane);

            return Result<IGrimoireExclusiveClosedLease>.Success(LastClosedLease);
        }
        finally
        {
            StageTwo.Complete();

            if (StageTwo.HoldAfterCompletion)
            {
                await StageTwo.AfterCompletion.PauseAsync(cancellationToken);
            }
        }
    }

    public ValueTask<Result> AbortClosingAsync(
        IGrimoireClosingOwner closingOwner,
        Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
        CancellationToken cancellationToken) =>
        inner.AbortClosingAsync(InnerOwner(closingOwner), proveNoDestructiveEffectAsync, cancellationToken);

    public Task<long> WaitForNextOpenGenerationAsync(long observedGeneration, CancellationToken cancellationToken) =>
        inner.WaitForNextOpenGenerationAsync(observedGeneration, cancellationToken);

    public Task<long> WaitForOpenGenerationAfterRefusalAsync(long refusedGeneration, CancellationToken cancellationToken) =>
        ((IGrimoireConnectionAdmissionGate)inner).WaitForOpenGenerationAfterRefusalAsync(refusedGeneration, cancellationToken);

    public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>> AcquireExpiredLeaseAdoptionInterlockAsync(
        CovenantExclusiveRecoveryOwner candidateOwner,
        Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>> revalidateDurableOwnerAsync,
        CancellationToken cancellationToken) =>
        inner.AcquireExpiredLeaseAdoptionInterlockAsync(candidateOwner, revalidateDurableOwnerAsync, cancellationToken);

    private sealed class ObservedOwner(IGrimoireClosingOwner actual, GrimoireOwnerObservation observation) : IGrimoireClosingOwner
    {
        public CovenantExclusiveRecoveryOwner Owner => actual.Owner;

        public long Generation => actual.Generation;

        public async ValueTask DisposeAsync()
        {
            await actual.DisposeAsync();

            observation.Disposed();
        }
    }

    private sealed class ObservedClosedLease(IGrimoireExclusiveClosedLease actual, GrimoireOwnerObservation observation,
        Action<IGrimoireMaintenanceIoLane> observeLane) : IGrimoireExclusiveClosedLease
    {
        public CovenantExclusiveRecoveryOwner Owner => actual.Owner;

        public long Generation => actual.Generation;

        public Result<IGrimoireScopedConnectionPermit> AcquireScopedConnectionPermit(DbConnection connection) =>
            actual.AcquireScopedConnectionPermit(connection);

        public Result<IGrimoireMaintenanceRenewalTicket> IssueMaintenanceRenewalTicket(IGrimoireMaintenanceIoLane lane) =>
            actual.IssueMaintenanceRenewalTicket(lane);

        public Result<IGrimoireMaintenanceConnectionCapability> IssueMaintenanceConnectionCapability(
            CovenantMaintenanceConnectionPurpose purpose, IGrimoireMaintenanceIoLane lane) =>
            actual.IssueMaintenanceConnectionCapability(purpose, lane);

        public async ValueTask<Result<IGrimoireMaintenanceIoLane>> AcquireMaintenanceIoLaneAsync(
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>> revalidateDurableOwnerAsync,
            CancellationToken cancellationToken)
        {
            var result = await actual.AcquireMaintenanceIoLaneAsync(revalidateDurableOwnerAsync, cancellationToken);

            if (result.IsSuccess)
            {
                observeLane(result.Value);
            }

            return result;
        }

        public async ValueTask<Result> CompleteAsync(CovenantExclusiveLeaseDisposition disposition, CancellationToken cancellationToken)
        {
            Result result = await actual.CompleteAsync(disposition, cancellationToken);

            if (result.IsSuccess)
            {
                observation.Completed(disposition);
            }

            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await actual.DisposeAsync();

            observation.Disposed();
        }
    }

    private sealed class ObservedTicket(IGrimoireConnectionOpenTicket actual, GrimoireTicketObservation observation) : IGrimoireConnectionOpenTicket
    {
        public long Generation => actual.Generation;

        public Result RevalidateAfterNativeOpen() => actual.RevalidateAfterNativeOpen();

        public Result MarkOpened()
        {
            Result result = actual.MarkOpened();

            if (result.IsSuccess)
            {
                observation.Terminated("opened");
            }

            return result;
        }

        public void MarkFailed()
        {
            actual.MarkFailed();

            observation.Terminated("failed");
        }

        public void MarkRefusedAfterOpen()
        {
            actual.MarkRefusedAfterOpen();

            observation.Terminated("refused-after-open");
        }

        public void Dispose()
        {
            actual.Dispose();

            observation.Disposed();
        }
    }

    private sealed class ObservedRequest(
        IGrimoireRequestLease actual,
        GrimoireLeaseObservation<GrimoireRequestKind> observation) : IGrimoireRequestLease
    {
        public GrimoireRequestKind Kind => actual.Kind;

        public long Generation => actual.Generation;

        public CancellationToken MaintenanceRevocation => actual.MaintenanceRevocation;

        public async ValueTask DisposeAsync()
        {
            await actual.DisposeAsync();

            observation.Disposed();
        }
    }

    private sealed class ObservedWork(
        IGrimoireWorkLease actual,
        GrimoireLeaseObservation<GrimoireWorkKind> observation,
        ConcurrentQueue<GrimoireEffectObservation> effects) : IGrimoireWorkLease
    {
        public GrimoireWorkKind Kind => actual.Kind;

        public long Generation => actual.Generation;

        public CancellationToken MaintenanceRevocation => actual.MaintenanceRevocation;

        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup)
        {
            bool acquired = actual.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect);

            effectGroup = null;

            if (effect is not null)
            {
                GrimoireEffectObservation effectObservation = new(actual.Kind);

                effects.Enqueue(effectObservation);

                effectGroup = new ObservedEffect(effect, effectObservation);
            }

            return acquired;
        }

        public async ValueTask DisposeAsync()
        {
            await actual.DisposeAsync();

            observation.Disposed();
        }
    }

    private sealed class ObservedEffect(IGrimoireExternalEffectGroup actual, GrimoireEffectObservation observation) : IGrimoireExternalEffectGroup
    {
        public async ValueTask DisposeAsync()
        {
            await actual.DisposeAsync();

            observation.Disposed();
        }
    }
}

internal sealed class GrimoireEffectObservation(GrimoireWorkKind kind)
{
    internal GrimoireWorkKind Kind { get; } = kind;

    private int _disposals;

    private int _terminal;

    internal int Disposals => Volatile.Read(ref _disposals);

    internal int TerminalCount => Volatile.Read(ref _terminal);

    internal void Disposed()
    {
        Interlocked.Increment(ref _disposals);

        Interlocked.Exchange(ref _terminal, 1);
    }
}

internal sealed class GrimoireOwnerObservation(CovenantExclusiveRecoveryOwner owner, long generation)
{
    private int _disposals;

    private readonly ConcurrentQueue<CovenantExclusiveLeaseDisposition> _dispositions = new();

    internal CovenantExclusiveRecoveryOwner Owner { get; } = owner;

    internal long Generation { get; } = generation;

    internal int Disposals => Volatile.Read(ref _disposals);

    internal IReadOnlyList<CovenantExclusiveLeaseDisposition> Dispositions => _dispositions.ToArray();

    internal void Disposed() => Interlocked.Increment(ref _disposals);

    internal void Completed(CovenantExclusiveLeaseDisposition disposition) => _dispositions.Enqueue(disposition);
}

internal sealed class GrimoireTicketObservation(DbConnection connection, long generation)
{
    private int _disposals;

    private int _terminalCount;

    internal DbConnection Connection { get; } = connection;

    internal long Generation { get; } = generation;

    internal int Disposals => Volatile.Read(ref _disposals);

    internal int TerminalCount => Volatile.Read(ref _terminalCount);

    internal string? Terminal { get; private set; }

    internal IGrimoireConnectionOpenTicket? Ticket { get; set; }

    internal void Disposed() => Interlocked.Increment(ref _disposals);

    internal void Terminated(string terminal)
    {
        Terminal = terminal;

        Interlocked.Increment(ref _terminalCount);
    }
}

internal sealed class MaintenanceStageObservation
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _enteredCount;

    private int _completedCount;

    internal int EnteredCount => Volatile.Read(ref _enteredCount);

    internal int CompletedCount => Volatile.Read(ref _completedCount);

    internal bool HoldAfterCompletion { get; set; }

    internal MaintenanceCheckpoint AfterCompletion { get; } = new();

    internal Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

    internal void Enter()
    {
        Interlocked.Increment(ref _enteredCount);

        _entered.TrySetResult();
    }

    internal void Complete() => Interlocked.Increment(ref _completedCount);
}

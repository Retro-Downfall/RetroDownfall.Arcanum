using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Events;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Builds a real <see cref="McpConnectionManager"/> over inert collaborators for the manager-level
/// MCP tests: an untrusted workspace store, a silent event bus, an empty service scope and an open
/// global admission gate. Callers pass only what the scenario varies.
/// </summary>
internal static class McpConnectionManagerHarness
{
    public static McpConnectionManager Create(
        ArcanumSettings? settings = null,
        ILoggerFactory? loggerFactory = null,
        ILogger<McpConnectionManager>? logger = null,
        ITrustedMcpWorkspaceStore? trustStore = null,
        IEventBus? eventBus = null,
        HttpMessageHandler? httpHandler = null)
    {
        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new SilentEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        McpConnectionManager manager = new(
            logger ?? NullLogger<McpConnectionManager>.Instance,
            new HumanPromptRegistry(),
            scopeFactory,
            pacer,
            eventBus ?? new SilentEventBus(),
            trustStore ?? new UntrustedWorkspaceStore(),
            new FakeHttpClientFactory(httpHandler),
            new TestOptionsMonitor<ArcanumSettings>(settings ?? new ArcanumSettings()),
            loggerFactory ?? NullLoggerFactory.Instance);

        manager.ConfigureGlobalAdmission(
            new GrimoireConnectionAdmissionGate(TimeProvider.System));

        return manager;
    }

    private sealed class SilentEventBus : IEventBus
    {
        public void Publish<T>(T @event)
            where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken)
            where T : notnull =>
            AsyncEnumerable.Empty<T>();
    }

    private sealed class UntrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {
        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTrustedAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsApprovedDigestAsync(
            string workspaceRootPath,
            string sourceDigest,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
            string workspaceRootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(default(TrustedMcpWorkspaceSnapshot));

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="ILoggerFactory"/> that records every entry with the category it was written under.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly object _gate = new();

    private readonly List<CapturedLogEntry> _entries = [];

    private readonly List<string> _createdCategories = [];

    public IReadOnlyList<string> CreatedCategories
    {
        get
        {
            lock (_gate)
            {
                return [.. _createdCategories];
            }
        }
    }

    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        lock (_gate)
        {
            _createdCategories.Add(categoryName);
        }

        return new CapturingLogger(this, categoryName);
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    internal sealed record CapturedLogEntry(
        string Category,
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class CapturingLogger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._gate)
            {
                owner._entries.Add(
                    new CapturedLogEntry(category, logLevel, formatter(state, exception), exception));
            }
        }
    }
}

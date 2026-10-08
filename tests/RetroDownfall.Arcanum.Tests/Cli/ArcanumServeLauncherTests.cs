using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("ProcessEnvironment")]
public sealed class ArcanumServeLauncherTests : IDisposable
{
    private const string ApiKey = "launcher-test-key";

    // The launcher holds a retained file lock and writes its bootstrap log under the installation
    // directory, so every test (xUnit builds one instance per test) gets its own redirected home.
    private readonly ArcanumTestHomeScope _home = new("arcanum-serve-launcher-tests");

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Launch_lock_and_bootstrap_log_resolve_inside_a_redirected_test_home()
    {
        // The launcher takes and holds a real file lock under the installation directory. Without a
        // redirected home every test in this class would create, chmod and hold the developer's own
        // arcanum.serve.lock, and would fail whenever a real client held it.
        Assert.False(
            TestHomeGuard.AmbientHomeIsUnredirected(),
            "The launcher tests would write the real installation directory.");

        Assert.StartsWith(_home.Root, ArcanumServeLauncher.LaunchLockPath, StringComparison.Ordinal);

        Assert.False(
            TestHomeGuard.IsUnderRealDirectory(
                ArcanumServeLauncher.LaunchLockPath,
                TestProcessPaths.OriginalUserProfile,
                TestProcessPaths.OriginalApplicationData));

        Assert.False(
            TestHomeGuard.IsUnderRealDirectory(
                ArcanumServeLauncher.BootstrapLogPath,
                TestProcessPaths.OriginalUserProfile,
                TestProcessPaths.OriginalApplicationData));
    }

    /// <summary>
    /// The launch lock is held for the whole of a launch, up to the 20-second poll. Inside the Grimoire directory it
    /// would be an open handle in the directory a backup restore or an installation reset replaces wholesale, which
    /// Windows refuses to rename; so, like the maintenance and client-mutation locks, it sits beside that directory.
    /// </summary>
    [Fact]
    public void Launch_lock_sits_beside_the_Grimoire_directory_not_inside_it()
    {
        string guarded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ArcanumPaths.GrimoireDirectory));

        string lockPath = Path.GetFullPath(ArcanumServeLauncher.LaunchLockPath);

        Assert.Equal(Path.GetDirectoryName(guarded), Path.GetDirectoryName(lockPath));

        Assert.False(
            lockPath.StartsWith(guarded + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "The launch lock must not be a handle inside the directory a restore or reset replaces.");

        Assert.Equal($".arcanum-serve-{Path.GetFileName(guarded)}.lock", Path.GetFileName(lockPath));
    }

    [Fact]
    public async Task Verified_running_host_uses_the_mirror_once_and_does_not_spawn()
    {
        PresenceSequenceHandler handler = new(
            request => ValidProof(request, ApiKey));

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(
            SecretStoreReadResult.Corrupted("must not be opened"));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        FakeServeProcessLauncher process = new();
        FakeSecureStorageNotice notice = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            lease,
            process,
            notice: notice);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AlreadyRunning, result.Status);
        Assert.Equal(HealthProbeState.Healthy, result.Health);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
        Assert.Equal(0, process.StartCount);
        Assert.Equal(0, notice.BeforeHostBootstrapCount);
    }

    [Fact]
    public async Task Definite_no_listener_spawns_then_waits_for_a_valid_proof()
    {
        PresenceSequenceHandler handler = new(
            _ => throw ConnectionRefused(),
            request => ValidProof(request, ApiKey));

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        FakeServeProcessLauncher process = new();
        FakeSecureStorageNotice notice = new();

        ArcanumServeLauncher launcher = CreateLauncher(
            lease,
            process,
            notice: notice);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Started, result.Status);
        Assert.Equal(HealthProbeState.Healthy, result.Health);
        Assert.Equal(1, process.StartCount);
        Assert.Equal("1", process.LastOptions!.Env[ArcanumServeLauncher.AutoLaunchedEnvVar]);
        Assert.Equal(1, notice.BeforeHostBootstrapCount);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
    }

    [Fact]
    public async Task Foreign_responder_never_reads_a_credential_or_spawns()
    {
        PresenceSequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Failed, result.Status);
        Assert.Equal(HealthProbeState.UnexpectedResponder, result.Health);
        Assert.Equal(0, mirror.ReadCount);
        Assert.Equal(0, operatingSystem.ReadCount);
        Assert.Equal(0, process.StartCount);
        Assert.Contains(
            "not read or sent",
            result.Guidance ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_server_proof_is_auth_failed_and_never_spawns()
    {
        PresenceSequenceHandler handler = new(
            request => ValidProof(request, "different-installation-key"));

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Ok(ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AuthFailed, result.Status);
        Assert.Equal(HealthProbeState.Unauthorized, result.Health);
        Assert.Equal(1, mirror.ReadCount);
        Assert.Equal(1, operatingSystem.ReadCount);
        Assert.Equal(0, process.StartCount);
    }

    [Theory]
    [InlineData(false, "No local API credential was found")]
    [InlineData(true, "could not be read")]
    public async Task Credential_failure_guidance_preserves_missing_versus_unreadable_storage(
        bool corrupted,
        string expectedGuidance)
    {
        PresenceSequenceHandler handler = new(
            request => ValidProof(request, ApiKey));

        SecretStoreReadResult read = corrupted
            ? SecretStoreReadResult.Corrupted("credential unreadable")
            : SecretStoreReadResult.Missing();

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(read),
            new RecordingReader(read));

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AuthFailed, result.Status);
        Assert.Contains(
            expectedGuidance,
            result.Guidance ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, process.StartCount);
    }

    [Fact]
    public async Task Existing_host_can_finish_credential_bootstrap_without_a_second_spawn()
    {
        PresenceSequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            request => ValidProof(request, ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
            new RecordingReader(SecretStoreReadResult.Missing()));

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.AlreadyRunning, result.Status);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, process.StartCount);
    }

    [Fact]
    public async Task Noninteractive_invocation_does_not_probe_or_spawn()
    {
        PresenceSequenceHandler handler = new(
            _ => throw new InvalidOperationException("must not probe"));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
            new RecordingReader(SecretStoreReadResult.Missing()));

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(
            lease,
            process,
            interactive: false);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.LaunchDisabled, result.Status);
        Assert.Equal(HealthProbeState.NotAttempted, result.Health);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, process.StartCount);
    }

    [Fact]
    public async Task Explicit_no_auto_serve_does_not_probe_or_spawn()
    {
        string? original = global::System.Environment.GetEnvironmentVariable(
            ArcanumServeLauncher.NoAutoServeEnvVar);

        try
        {
            global::System.Environment.SetEnvironmentVariable(
                ArcanumServeLauncher.NoAutoServeEnvVar,
                "1");

            PresenceSequenceHandler handler = new(
                _ => throw new InvalidOperationException("must not probe"));

            using ArcanumApiCredentialLease lease = CreateLease(
                handler,
                new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
                new RecordingReader(SecretStoreReadResult.Missing()));

            FakeServeProcessLauncher process = new();
            ArcanumServeLauncher launcher = CreateLauncher(lease, process);

            ServeLaunchResult result = await launcher
                .EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(ServeLaunchStatus.LaunchDisabled, result.Status);
            Assert.Equal(0, handler.RequestCount);
            Assert.Equal(0, process.StartCount);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(
                ArcanumServeLauncher.NoAutoServeEnvVar,
                original);
        }
    }

    [Fact]
    public async Task ListenAny_without_acknowledgement_does_not_spawn()
    {
        string? originalAck = global::System.Environment.GetEnvironmentVariable(
            ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable);

        try
        {
            global::System.Environment.SetEnvironmentVariable(
                ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable,
                null);

            PresenceSequenceHandler handler = new(
                _ => throw ConnectionRefused());

            using ArcanumApiCredentialLease lease = CreateLease(
                handler,
                new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
                new RecordingReader(SecretStoreReadResult.Missing()));

            FakeServeProcessLauncher process = new();

            ArcanumSettings settings = new()
            {
                Host = new HostSettings
                {
                    ListenAny = true,
                    Https = new HttpsSettings { Enabled = true },
                },
            };

            ArcanumServeLauncher launcher = CreateLauncher(
                lease,
                process,
                settings: settings);

            ServeLaunchResult result = await launcher
                .EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(ServeLaunchStatus.Failed, result.Status);
            Assert.Equal(HealthProbeState.ConnectionRefused, result.Health);
            Assert.Equal(0, process.StartCount);
            Assert.Contains(
                "ListenAny",
                result.Guidance ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(
                ListenAnySecurityPolicy.AcknowledgementEnvironmentVariable,
                originalAck);
        }
    }

    [Fact]
    public async Task Spawn_failure_returns_a_typed_failure_with_the_bootstrap_log()
    {
        PresenceSequenceHandler handler = new(
            _ => throw ConnectionRefused());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
            new RecordingReader(SecretStoreReadResult.Missing()));

        FakeServeProcessLauncher process = new(
            new InvalidOperationException("spawn refused"));

        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Failed, result.Status);
        Assert.Equal(ArcanumServeLauncher.BootstrapLogPath, result.LogPath);
        Assert.Contains(
            "spawn refused",
            result.Guidance ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_spawn_missing_listener_times_out_without_repeated_credential_reads()
    {
        PresenceSequenceHandler handler = new(
            _ => throw ConnectionRefused());

        RecordingReader mirror = new(SecretStoreReadResult.Ok(ApiKey));
        RecordingReader operatingSystem = new(SecretStoreReadResult.Missing());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            mirror,
            operatingSystem);

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        TimeSpan? originalDeadline = ArcanumServeLauncher.TestPollDeadline;

        try
        {
            ArcanumServeLauncher.TestPollDeadline =
                TimeSpan.FromMilliseconds(100);

            ServeLaunchResult result = await launcher
                .EnsureRunningAsync(CancellationToken.None);

            Assert.Equal(ServeLaunchStatus.Failed, result.Status);
            Assert.Equal(1, process.StartCount);
            Assert.Equal(0, mirror.ReadCount);
            Assert.Equal(0, operatingSystem.ReadCount);
            Assert.Equal(ArcanumServeLauncher.BootstrapLogPath, result.LogPath);
        }
        finally
        {
            ArcanumServeLauncher.TestPollDeadline = originalDeadline;
        }
    }

    [Fact]
    public async Task Concurrent_launch_waits_for_the_other_launcher_and_adopts_the_host_it_started()
    {
        PresenceSequenceHandler handler = new(
            _ => throw ConnectionRefused(),
            request => ValidProof(request, ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
            new RecordingReader(SecretStoreReadResult.Missing()));

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        RetainedExclusiveFileLockAcquisitionResult other =
            RetainedExclusiveFileLock.Acquire(ArcanumServeLauncher.LaunchLockPath);

        Assert.Equal(
            RetainedExclusiveFileLockAcquisitionDisposition.Acquired,
            other.Disposition);

        Task<ServeLaunchResult> launching = launcher
            .EnsureRunningAsync(CancellationToken.None);

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600));

            // Another launcher owns probe-and-spawn, so this one must not have spawned a second host.
            Assert.False(launching.IsCompleted);
            Assert.Equal(0, process.StartCount);
        }
        finally
        {
            other.Lock!.Dispose();
        }

        ServeLaunchResult result = await launching.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ServeLaunchStatus.AlreadyRunning, result.Status);
        Assert.Equal(0, process.StartCount);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Concurrent_launch_spawns_when_the_other_launcher_left_no_host_behind()
    {
        PresenceSequenceHandler handler = new(
            _ => throw ConnectionRefused());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
            new RecordingReader(SecretStoreReadResult.Missing()));

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        TimeSpan? originalDeadline = ArcanumServeLauncher.TestPollDeadline;

        RetainedExclusiveFileLockAcquisitionResult other =
            RetainedExclusiveFileLock.Acquire(ArcanumServeLauncher.LaunchLockPath);

        try
        {
            ArcanumServeLauncher.TestPollDeadline = TimeSpan.FromSeconds(5);

            Task<ServeLaunchResult> launching = launcher
                .EnsureRunningAsync(CancellationToken.None);

            await Task.Delay(TimeSpan.FromMilliseconds(400));

            Assert.Equal(0, process.StartCount);

            other.Lock!.Dispose();

            ArcanumServeLauncher.TestPollDeadline = TimeSpan.FromMilliseconds(100);

            ServeLaunchResult result = await launching.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(1, process.StartCount);
            Assert.Equal(ServeLaunchStatus.Failed, result.Status);
        }
        finally
        {
            other.Lock?.Dispose();

            ArcanumServeLauncher.TestPollDeadline = originalDeadline;
        }
    }

    [Fact]
    public async Task Launch_gives_up_on_a_lock_that_is_never_released_without_spawning()
    {
        PresenceSequenceHandler handler = new(
            _ => throw ConnectionRefused());

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
            new RecordingReader(SecretStoreReadResult.Missing()));

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        TimeSpan? originalDeadline = ArcanumServeLauncher.TestPollDeadline;

        RetainedExclusiveFileLockAcquisitionResult other =
            RetainedExclusiveFileLock.Acquire(ArcanumServeLauncher.LaunchLockPath);

        try
        {
            ArcanumServeLauncher.TestPollDeadline = TimeSpan.FromMilliseconds(300);

            ServeLaunchResult result = await launcher
                .EnsureRunningAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(ServeLaunchStatus.Failed, result.Status);
            Assert.Equal(0, process.StartCount);
            Assert.Contains(
                "another",
                result.Guidance ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            other.Lock?.Dispose();

            ArcanumServeLauncher.TestPollDeadline = originalDeadline;
        }
    }

    [Fact]
    public async Task Launch_releases_the_lock_when_it_finishes()
    {
        PresenceSequenceHandler handler = new(
            _ => throw ConnectionRefused(),
            request => ValidProof(request, ApiKey));

        using ArcanumApiCredentialLease lease = CreateLease(
            handler,
            new RecordingReader(SecretStoreReadResult.Ok(ApiKey)),
            new RecordingReader(SecretStoreReadResult.Missing()));

        FakeServeProcessLauncher process = new();
        ArcanumServeLauncher launcher = CreateLauncher(lease, process);

        ServeLaunchResult result = await launcher
            .EnsureRunningAsync(CancellationToken.None);

        Assert.Equal(ServeLaunchStatus.Started, result.Status);

        RetainedExclusiveFileLockAcquisitionResult next =
            RetainedExclusiveFileLock.Acquire(ArcanumServeLauncher.LaunchLockPath);

        Assert.Equal(
            RetainedExclusiveFileLockAcquisitionDisposition.Acquired,
            next.Disposition);

        next.Lock!.Dispose();
    }

    private static ArcanumServeLauncher CreateLauncher(
        ArcanumApiCredentialLease lease,
        FakeServeProcessLauncher process,
        bool interactive = true,
        ArcanumSettings? settings = null,
        ISecureStorageNotice? notice = null) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(
                settings ?? new ArcanumSettings()),
            lease,
            new FakeCliEnvironment(interactive),
            process,
            notice ?? new FakeSecureStorageNotice(),
            NullLogger<ArcanumServeLauncher>.Instance);

    private static ArcanumApiCredentialLease CreateLease(
        HttpMessageHandler handler,
        RecordingReader mirror,
        RecordingReader operatingSystem)
    {
        HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        return new ArcanumApiCredentialLease(
            client,
            new Uri("http://localhost:5001/api/presence"),
            mirror.ReadAsync,
            operatingSystem.ReadAsync);
    }

    private static HttpResponseMessage ValidProof(
        HttpRequestMessage request,
        string key)
    {
        Assert.False(request.Headers.Contains(ArcanumApiHeaders.ApiKey));

        string encodedNonce = Assert.Single(
            request.Headers.GetValues(ArcanumApiHeaders.PresenceNonce));

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecode(
                encodedNonce,
                ArcanumPresenceProofProtocol.NonceBytes,
                out byte[]? nonce));

        Assert.True(
            ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                request.RequestUri!,
                out string? authority));

        byte[] encodedKey = Encoding.UTF8.GetBytes(key);
        byte[] digest = SHA256.HashData(encodedKey);
        using ArcanumProcessCapabilityService processCapabilities = new();
        byte[] processCapability = processCapabilities.Issue();
        byte[] capabilityEnvelope =
            ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
                digest,
                nonce!,
                authority!,
                processCapability);
        byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
            digest,
            nonce!,
            authority!,
            capabilityEnvelope);

        try
        {
            HttpResponseMessage response = new(HttpStatusCode.NoContent);

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceVersion,
                ArcanumPresenceProofProtocol.Version);

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceAuthority,
                authority);

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceProof,
                ArcanumPresenceProofProtocol.Encode(proof));

            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceCapability,
                ArcanumPresenceProofProtocol.Encode(capabilityEnvelope));

            return response;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce!);
            CryptographicOperations.ZeroMemory(encodedKey);
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(processCapability);
            CryptographicOperations.ZeroMemory(capabilityEnvelope);
            CryptographicOperations.ZeroMemory(proof);
        }
    }

    private static HttpRequestException ConnectionRefused() =>
        new(
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused));

    private sealed class RecordingReader(SecretStoreReadResult result)
    {
        private int _readCount;

        internal int ReadCount => Volatile.Read(ref _readCount);

        internal Task<SecretStoreReadResult> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);

            return Task.FromResult(result);
        }
    }

    private sealed class PresenceSequenceHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
        : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int index = Interlocked.Increment(ref _requestCount) - 1;

            Func<HttpRequestMessage, HttpResponseMessage> step =
                steps[Math.Min(index, steps.Length - 1)];

            return Task.FromResult(step(request));
        }
    }

    private sealed class FakeCliEnvironment(bool interactive) : ICliEnvironment
    {
        public bool IsInteractive => interactive;

        public bool ColorEnabled => interactive;

        public bool ShouldShowManaBar => interactive;
    }

    private sealed class FakeServeProcessLauncher(Exception? failure = null)
        : IServeProcessLauncher
    {
        internal int StartCount { get; private set; }

        internal ServeProcessStartOptions? LastOptions { get; private set; }

        public Task<StartedProcess> StartServeAsync(
            ServeProcessStartOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StartCount++;
            LastOptions = options;

            return failure is null
                ? Task.FromResult(new StartedProcess(42_001))
                : Task.FromException<StartedProcess>(failure);
        }
    }

    private sealed class FakeSecureStorageNotice : ISecureStorageNotice
    {
        internal int BeforeHostBootstrapCount { get; private set; }

        public void ExplainBeforeHostBootstrap() =>
            BeforeHostBootstrapCount++;

        public void ExplainAfterSetup()
        {
        }

        public void MarkHostBootstrapCompleted()
        {
        }
    }
}

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetHostProcessToolsPairReaderTests
{

    [Fact]
    public async Task ReadAsync_reads_operating_system_marker_before_database_and_uses_the_joiner()
    {

        List<string> events = [];

        HostProcessToolsDatabaseMarkerEvidence database = CleanDatabaseEvidence();

        HostProcessToolsMarkerPairJoinResult expected = new(
            HostProcessToolsMarkerPairDisposition.Clean,
            MatchedPair: null);

        InstallationResetHostProcessToolsPairReader reader = new(
            new RecordingMarkerStore(
                events,
                new HostProcessToolsMarkerReadResult(
                    HostProcessToolsMarkerReadStatus.Absent,
                    Marker: null)),
            new RecordingDatabaseEvidenceReader(events, database),
            new RecordingJoiner(events, expected));

        Result<HostProcessToolsMarkerPairJoinResult> result = await reader.ReadAsync(
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Same(expected, result.Value);

        Assert.Equal(["os", "database", "join"], events);

    }

    [Fact]
    public async Task Host_safe_local_read_returns_external_remediation_without_database_acquisition()
    {

        RecordingStoppedHostDataService database = new(CleanDatabaseEvidence());

        InstallationResetHostProcessToolsPairReader reader = new(
            new RecordingMarkerStore(
                [],
                new HostProcessToolsMarkerReadResult(
                    HostProcessToolsMarkerReadStatus.Absent,
                    Marker: null)),
            database,
            new RecordingJoiner(
                [],
                new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.Clean,
                    MatchedPair: null)));

        Result<HostProcessToolsMarkerPairJoinResult> result =
            await reader.ReadAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationRequired, result.Error.Code);

        Assert.Equal(0, database.PublicReads);

        Assert.Equal(0, database.StoppedHostReads);

        Assert.Equal(0, database.ProviderConstructions);

    }

    [Fact]
    public async Task Stopped_host_read_uses_one_fresh_evidence_capability_after_os_evidence()
    {

        List<string> events = [];

        RecordingStoppedHostDataService database = new(
            CleanDatabaseEvidence(),
            events);

        RecordingStoppedHostIssuer issuer = new();

        HostProcessToolsMarkerPairJoinResult expected = new(
            HostProcessToolsMarkerPairDisposition.Clean,
            MatchedPair: null);

        InstallationResetHostProcessToolsPairReader reader = new(
            new RecordingMarkerStore(
                events,
                new HostProcessToolsMarkerReadResult(
                    HostProcessToolsMarkerReadStatus.Absent,
                    Marker: null)),
            database,
            new RecordingJoiner(events, expected));

        Result<HostProcessToolsMarkerPairJoinResult> result =
            await reader.ReadUnderStoppedHostAuthorityAsync(
                issuer,
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Same(expected, result.Value);

        Assert.Equal(1, issuer.EvidenceAuthoritiesIssued);

        Assert.Equal(1, database.StoppedHostReads);

        Assert.Equal(1, database.ProviderConstructions);

        Assert.Equal(["os", "database", "join"], events);

    }

    [Theory]
    [InlineData((byte)HostProcessToolsMarkerReadStatus.Unavailable)]
    [InlineData((byte)HostProcessToolsMarkerReadStatus.Malformed)]
    public async Task Stopped_host_read_rejects_malformed_os_evidence_before_issuance(
        byte statusValue)
    {

        RecordingStoppedHostDataService database = new(CleanDatabaseEvidence());

        RecordingStoppedHostIssuer issuer = new();

        InstallationResetHostProcessToolsPairReader reader = new(
            new RecordingMarkerStore(
                [],
                new HostProcessToolsMarkerReadResult(
                    (HostProcessToolsMarkerReadStatus)statusValue,
                    Marker: null)),
            database,
            new ThrowingJoiner("join-must-not-run"));

        Result<HostProcessToolsMarkerPairJoinResult> result =
            await reader.ReadUnderStoppedHostAuthorityAsync(
                issuer,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, issuer.EvidenceAuthoritiesIssued);

        Assert.Equal(0, database.StoppedHostReads);

        Assert.Equal(0, database.ProviderConstructions);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopped_host_read_rejects_wrong_or_disposed_issuer_before_provider_construction(
        bool disposed)
    {

        RecordingStoppedHostDataService database = new(CleanDatabaseEvidence());

        RecordingStoppedHostIssuer issuer = new(
            rejectEvidenceAuthority: !disposed);

        if (disposed)
        {

            issuer.Dispose();

        }

        InstallationResetHostProcessToolsPairReader reader = new(
            new RecordingMarkerStore(
                [],
                new HostProcessToolsMarkerReadResult(
                    HostProcessToolsMarkerReadStatus.Absent,
                    Marker: null)),
            database,
            new ThrowingJoiner("join-must-not-run"));

        Result<HostProcessToolsMarkerPairJoinResult> result =
            await reader.ReadUnderStoppedHostAuthorityAsync(
                issuer,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, issuer.EvidenceAuthoritiesIssued);

        Assert.Equal(0, database.ProviderConstructions);

    }

    [Theory]
    [InlineData((byte)HostProcessToolsMarkerReadStatus.Unavailable)]
    [InlineData((byte)HostProcessToolsMarkerReadStatus.Malformed)]
    public async Task ReadAsync_fails_content_free_before_database_for_untrusted_marker_reads(
        byte statusValue)
    {

        const string secret = "marker-secret-that-must-not-escape";

        HostProcessToolsMarkerReadStatus status =
            (HostProcessToolsMarkerReadStatus)statusValue;

        List<string> events = [];

        InstallationResetHostProcessToolsPairReader reader = new(
            new RecordingMarkerStore(
                events,
                new HostProcessToolsMarkerReadResult(status, Marker: null)),
            new ThrowingDatabaseEvidenceReader(secret),
            new ThrowingJoiner(secret));

        Result<HostProcessToolsMarkerPairJoinResult> result = await reader.ReadAsync(
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationRequired, result.Error.Code);

        Assert.DoesNotContain(secret, result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(["os"], events);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_normalizes_database_failure_or_exception_without_joining(
        bool throws)
    {

        const string secret = "database-evidence-that-must-not-escape";

        List<string> events = [];

        InstallationResetHostProcessToolsPairReader reader = new(
            new RecordingMarkerStore(
                events,
                new HostProcessToolsMarkerReadResult(
                    HostProcessToolsMarkerReadStatus.Absent,
                    Marker: null)),
            new FailingDatabaseEvidenceReader(events, secret, throws),
            new ThrowingJoiner(secret));

        Result<HostProcessToolsMarkerPairJoinResult> result = await reader.ReadAsync(
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationRequired, result.Error.Code);

        Assert.DoesNotContain(secret, result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(["os", "database"], events);

    }

    [Fact]
    public void Reset_composition_registers_one_pair_reader_and_database_evidence_reader()
    {

        ServiceCollection services = new();

        services.AddLogging();

        services.AddArcanumCliClientStack();

        services.AddSingleton<IInstallationResetPreDataMutation>(
            NoopInstallationResetPreDataMutation.Instance);

        services.AddArcanumInstallationReset(new ArcanumSettings());

        services.AddArcanumInstallationReset(new ArcanumSettings());

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType
                == typeof(IInstallationResetHostProcessToolsPairReader));

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType
                == typeof(IInstallationResetStoppedHostProcessToolsPairReader));

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType
                == typeof(IInstallationResetHostProcessToolsDatabaseEvidenceReader));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<InstallationResetHostProcessToolsPairReader>(
            scope.ServiceProvider.GetRequiredService<
                IInstallationResetHostProcessToolsPairReader>());

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<
                IInstallationResetHostProcessToolsPairReader>(),
            scope.ServiceProvider.GetRequiredService<
                IInstallationResetStoppedHostProcessToolsPairReader>());

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<InstallationResetExistingGrimoire>(),
            scope.ServiceProvider.GetRequiredService<
                IInstallationResetHostProcessToolsDatabaseEvidenceReader>());

        Assert.IsType<InstallationResetService>(
            scope.ServiceProvider.GetRequiredService<IInstallationResetLockedService>());

    }

    private static HostProcessToolsDatabaseMarkerEvidence CleanDatabaseEvidence() =>
        new(
            "installation-identity",
            CovenantHostToolsState.Clean,
            transitionId: null,
            taintMasterKeyVersion: null,
            taintFingerprint: null);

    private sealed class RecordingMarkerStore(
        List<string> events,
        HostProcessToolsMarkerReadResult result)
        : IHostProcessToolsMarkerStore
    {

        public HostProcessToolsMarkerReadResult Read()
        {

            events.Add("os");

            return result;

        }

        public HostProcessToolsMarkerWriteStatus Write(
            string installationIdentity,
            Guid transitionId,
            ulong taintMasterKeyVersion,
            CovenantDigest taintFingerprint) =>
            throw new InvalidOperationException("The pair reader must not write a marker.");

        public bool CompareDelete(HostProcessToolsOsMarkerEvidence expected) =>
            throw new InvalidOperationException("The pair reader must not delete a marker.");

    }

    private sealed class RecordingDatabaseEvidenceReader(
        List<string> events,
        HostProcessToolsDatabaseMarkerEvidence evidence)
        : IInstallationResetHostProcessToolsDatabaseEvidenceReader
    {

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadMarkerEvidenceAsync(
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            events.Add("database");

            return Task.FromResult(
                Result<HostProcessToolsDatabaseMarkerEvidence>.Success(evidence));

        }

    }

    private sealed class ThrowingDatabaseEvidenceReader(string secret)
        : IInstallationResetHostProcessToolsDatabaseEvidenceReader
    {

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadMarkerEvidenceAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(secret);

    }

    private sealed class FailingDatabaseEvidenceReader(
        List<string> events,
        string secret,
        bool throws)
        : IInstallationResetHostProcessToolsDatabaseEvidenceReader
    {

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadMarkerEvidenceAsync(
            CancellationToken cancellationToken = default)
        {

            events.Add("database");

            if (throws)
            {

                throw new InvalidDataException(secret);

            }

            return Task.FromResult(
                Result<HostProcessToolsDatabaseMarkerEvidence>.Failure(new Error(
                    ErrorCodes.Data.InventoryUnavailable,
                    secret)));

        }

    }

    private sealed class RecordingStoppedHostDataService(
        HostProcessToolsDatabaseMarkerEvidence evidence,
        List<string>? events = null)
        : IInstallationResetHostProcessToolsDatabaseEvidenceReader,
          IInstallationResetStoppedHostDataService
    {

        internal int ProviderConstructions { get; private set; }

        internal int PublicReads { get; private set; }

        internal int StoppedHostReads { get; private set; }

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>>
            ReadMarkerEvidenceAsync(
                CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            PublicReads++;

            throw new InvalidOperationException(
                "The host-safe route must not acquire the local Grimoire.");

        }

        public async Task<Result<HostProcessToolsDatabaseMarkerEvidence>>
            ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(
                IStoppedHostGrimoireAuthorityIssuer issuer,
                CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            StoppedHostReads++;

            Result<IStoppedHostGrimoireConnectionAuthority> issued =
                issuer.IssueStoppedHostInstallationResetHostToolsEvidenceReadAuthority();

            if (issued.IsFailure)
            {

                return Result<HostProcessToolsDatabaseMarkerEvidence>.Failure(
                    issued.Error);

            }

            await using IStoppedHostGrimoireConnectionAuthority authority =
                issued.Value;

            ProviderConstructions++;

            events?.Add("database");

            return Result<HostProcessToolsDatabaseMarkerEvidence>.Success(evidence);

        }

        public Task<Result<DataRetentionPlan>> PlanUnderStoppedHostAuthorityAsync(
            InstallationResetDataPlanRequest request,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetWorkspaceResolution>>
            ResolveWorkspaceUnderStoppedHostAuthorityAsync(
                string invocationDirectory,
                IStoppedHostGrimoireAuthorityIssuer issuer,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<Guid>> ReadIdentityUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<DataRetentionApplyResult>>
            ApplyUnderStoppedHostAuthorityAsync(
                DataRetentionApplyRequest request,
                IStoppedHostGrimoireAuthorityIssuer issuer,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingStoppedHostIssuer(
        bool rejectEvidenceAuthority = false)
        : IStoppedHostGrimoireAuthorityIssuer,
          IDisposable
    {

        private bool _disposed;

        internal int EvidenceAuthoritiesIssued { get; private set; }

        public void Dispose()
        {

            _disposed = true;

        }

        public Result<IStoppedHostGrimoireConnectionAuthority>
            IssueStoppedHostInstallationResetHostToolsEvidenceReadAuthority()
        {

            EvidenceAuthoritiesIssued++;

            return rejectEvidenceAuthority || _disposed
                ? UnavailableAuthority()
                : Result<IStoppedHostGrimoireConnectionAuthority>.Success(
                    new TestStoppedHostAuthority());

        }

        public Result<IStoppedHostGrimoireConnectionAuthority>
            IssueStoppedHostInstallationResetPlanReadAuthority() =>
            UnavailableAuthority();

        public Result<IStoppedHostGrimoireConnectionAuthority>
            IssueStoppedHostInstallationResetWorkspaceResolutionAuthority() =>
            UnavailableAuthority();

        public Result<IStoppedHostGrimoireConnectionAuthority>
            IssueStoppedHostInstallationResetIdentityReadAuthority() =>
            UnavailableAuthority();

        public Result<IStoppedHostGrimoireConnectionAuthority>
            IssueStoppedHostInstallationResetApplyAuthority() =>
            UnavailableAuthority();

        public Result<IStoppedHostGrimoireConnectionAuthority>
            IssueStoppedHostMarkerPairResetAuthority() =>
            UnavailableAuthority();

        private static Result<IStoppedHostGrimoireConnectionAuthority>
            UnavailableAuthority() =>
            Result<IStoppedHostGrimoireConnectionAuthority>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The stopped-host authority is unavailable."));

    }

    private sealed class TestStoppedHostAuthority
        : IStoppedHostGrimoireConnectionAuthority
    {

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class RecordingJoiner(
        List<string> events,
        HostProcessToolsMarkerPairJoinResult result)
        : IHostProcessToolsMarkerPairJoiner
    {

        public HostProcessToolsMarkerPairJoinResult Join(
            HostProcessToolsDatabaseMarkerEvidence database,
            HostProcessToolsOsMarkerEvidence? osMarker)
        {

            events.Add("join");

            return result;

        }

    }

    private sealed class ThrowingJoiner(string secret)
        : IHostProcessToolsMarkerPairJoiner
    {

        public HostProcessToolsMarkerPairJoinResult Join(
            HostProcessToolsDatabaseMarkerEvidence database,
            HostProcessToolsOsMarkerEvidence? osMarker) =>
            throw new InvalidOperationException(secret);

    }

}

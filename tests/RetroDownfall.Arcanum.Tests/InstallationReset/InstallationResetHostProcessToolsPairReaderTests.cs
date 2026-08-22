using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

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

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadAsync(
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

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(secret);

    }

    private sealed class FailingDatabaseEvidenceReader(
        List<string> events,
        string secret,
        bool throws)
        : IInstallationResetHostProcessToolsDatabaseEvidenceReader
    {

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadAsync(
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

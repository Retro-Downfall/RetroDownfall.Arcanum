using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RestoreStagingManagedAuthoritySanitizationCapability =
    RetroDownfall.Arcanum.Infrastructure.Backup.RestoreStagingManagedAuthoritySanitizationCapability;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class HostToolsMarkerPairResetDatabaseTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Marker_projection_selects_only_the_six_allowed_authority_columns()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-marker-projection",
            transitionId: "7CFF26A1-E089-4D69-8128-B34103E81D0E",
            taintVersion: 17L,
            unrelatedAuthorityValues: ["not-an-integer", "not-a-version", "not-a-digest"]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostProcessToolsDatabaseMarkerEvidence> result =
            await session.ReadTaintedAsync(Token);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("installation-marker-projection", result.Value.InstallationIdentity);

        Assert.Equal(CovenantHostToolsState.HostToolsTainted, result.Value.State);

        Assert.Equal(Guid.Parse("7CFF26A1-E089-4D69-8128-B34103E81D0E"), result.Value.TransitionId);

        Assert.Equal(17UL, result.Value.TaintMasterKeyVersion);

    }

    [Fact]
    public async Task Marker_projection_accepts_canonical_eight_byte_blob_taint_version()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        const ulong expectedVersion = ulong.MaxValue - 7;

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-blob-version",
            transitionId: "E25A83A6-EB21-42AB-AEA0-66B6D89379BB",
            taintVersion: HostProcessToolsTaintVersionStorage.Encode(expectedVersion),
            unrelatedAuthorityValues: [1L, 1L, new byte[32]]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostProcessToolsDatabaseMarkerEvidence> result =
            await session.ReadTaintedAsync(Token);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(expectedVersion, result.Value.TaintMasterKeyVersion);

    }

    [Fact]
    public async Task Marker_projection_accepts_legacy_positive_integer_taint_version()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-integer-version",
            transitionId: "B1476AB0-B242-43E4-9A0B-A51056C96B2A",
            taintVersion: long.MaxValue,
            unrelatedAuthorityValues: [1L, 1L, new byte[32]]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostProcessToolsDatabaseMarkerEvidence> result =
            await session.ReadTaintedAsync(Token);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal((ulong)long.MaxValue, result.Value.TaintMasterKeyVersion);

    }

    [Fact]
    public async Task Marker_projection_accepts_valid_guid_text_spelling_and_preserves_its_raw_value()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("2B68A6A1-B3D6-4E0E-9501-C7968A2243E8");

        string rawSpelling = transitionId.ToString("D").ToLowerInvariant();

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-guid-spelling",
            transitionId: rawSpelling,
            taintVersion: 23L,
            unrelatedAuthorityValues: [1L, 1L, new byte[32]]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        HostProcessToolsDatabaseMarkerEvidence expected = new(
            "installation-guid-spelling",
            CovenantHostToolsState.HostToolsTainted,
            transitionId,
            23,
            new CovenantDigest(Enumerable.Repeat((byte)0x5A, 32).ToArray()));

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(expected, Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        await ExecuteAsync(
            session.BorrowCoreConnection(),
            "UPDATE covenant_authority_state SET TransitionId = upper(TransitionId);");

        Result cleared = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Assert.True(cleared.IsFailure);

        Assert.Equal(
            rawSpelling,
            await ScalarStringAsync(
                database.Connection,
                "SELECT TransitionId FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Marker_projection_rejects_malformed_guid_version_state_or_singleton_before_mutation()
    {

        await AssertMalformedProjectionAsync(
            "UPDATE covenant_authority_state SET TransitionId = 'not-a-guid';");

        await AssertMalformedProjectionAsync(
            "UPDATE covenant_authority_state SET TaintTimeMasterVersion = X'0000000000000000';");

        await AssertMalformedProjectionAsync(
            "UPDATE covenant_authority_state SET HostToolsStateCode = 7;");

        await AssertMalformedProjectionAsync(
            "DELETE FROM covenant_authority_state;");

        await AssertMalformedProjectionAsync(
            """
            INSERT INTO covenant_authority_state
            SELECT * FROM covenant_authority_state;
            """);

    }

    [Fact]
    public async Task Begin_capture_rejects_a_complete_pending_marker_before_transaction_mutation()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("3E6AF070-F1AB-4A75-94C1-54901EBD605C");

        await SeedTaintedAsync(
            database.Connection,
            "installation-pending-capture",
            transitionId.ToString("D"),
            23L,
            [1L, 1L, new byte[32]]);

        await ExecuteAsync(
            database.Connection,
            "UPDATE covenant_authority_state SET HostToolsStateCode = 2;");

        long before = await ScalarLongAsync(
            database.Connection,
            "SELECT total_changes();");

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                new HostProcessToolsDatabaseMarkerEvidence(
                    "installation-pending-capture",
                    CovenantHostToolsState.PendingHostToolsTaint,
                    transitionId,
                    23,
                    new CovenantDigest(
                        Enumerable.Repeat((byte)0x5A, 32).ToArray())),
                Token);

        Assert.True(captured.IsFailure);

        Assert.Equal(
            before,
            await ScalarLongAsync(database.Connection, "SELECT total_changes();"));

        Assert.Equal(
            2,
            await ScalarLongAsync(
                database.Connection,
                "SELECT HostToolsStateCode FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Raw_compare_capability_and_session_creation_are_not_assembly_wide_factories()
    {

        const System.Reflection.BindingFlags declared =
            System.Reflection.BindingFlags.DeclaredOnly
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        Assert.Null(typeof(HostToolsMarkerPairResetDatabaseSession).GetConstructor(
            declared,
            binder: null,
            [typeof(SqliteConnection)],
            modifiers: null));

        Assert.DoesNotContain(
            typeof(HostToolsDatabaseMarkerCompareDeleteCapability)
                .GetFields(declared),
            field => field.FieldType == typeof(object[])
                || field.FieldType == typeof(string[]));

        Assert.DoesNotContain(
            typeof(HostToolsDatabaseMarkerCompareDeleteCapability)
                .GetMethods(declared),
            method => method.Name is "Issue" or "TryConsume"
                || method.ReturnType == typeof(object[])
                || method.ReturnType == typeof(string[])
                || method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(object[])
                    || parameter.ParameterType == typeof(string[])
                    || parameter.ParameterType == typeof(object[]).MakeByRefType()
                    || parameter.ParameterType == typeof(string[]).MakeByRefType()));

        Assert.Null(typeof(HostToolsDatabaseMarkerProjectionReader).GetMethod(
            "ReadForResetAsync",
            declared));

        Assert.Null(typeof(HostToolsDatabaseMarkerProjectionReader).GetNestedType(
            "Projection",
            System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic));

        Type? sessionTicket = typeof(HostToolsMarkerPairResetDatabase).GetNestedType(
            "SessionCreationTicket",
            System.Reflection.BindingFlags.NonPublic);

        Type? capabilityTicket = typeof(HostToolsMarkerPairResetDatabaseSession)
            .GetNestedType(
                "CapabilityCreationTicket",
                System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(sessionTicket);

        Assert.NotNull(capabilityTicket);

        Assert.True(sessionTicket.IsNestedPrivate);

        Assert.True(capabilityTicket.IsNestedPrivate);

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        HostToolsMarkerPairResetDatabase owner = new(
            database.MaintenanceConnections(),
            CovenantSqliteConnectionInitializer.Instance);

        System.Reflection.ConstructorInfo sessionConstructor = Assert.Single(
            typeof(HostToolsMarkerPairResetDatabaseSession)
                .GetConstructors(declared),
            constructor => !constructor.IsStatic);

        System.Reflection.TargetInvocationException sessionFailure = Assert.Throws<
            System.Reflection.TargetInvocationException>(() =>
                sessionConstructor.Invoke(
                    [
                        new SqliteConnection(),
                        owner,
                        NoopHostToolsMarkerPairResetDatabaseTestSeam.Instance,
                        TimeSpan.FromSeconds(5),
                        new object(),
                    ]));

        Assert.IsType<InvalidOperationException>(sessionFailure.InnerException);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        System.Reflection.ConstructorInfo capabilityConstructor = Assert.Single(
            typeof(HostToolsDatabaseMarkerCompareDeleteCapability)
                .GetConstructors(declared),
            constructor => !constructor.IsStatic);

        System.Reflection.TargetInvocationException capabilityFailure = Assert.Throws<
            System.Reflection.TargetInvocationException>(() =>
                capabilityConstructor.Invoke([session, new object()]));

        Assert.IsType<InvalidOperationException>(capabilityFailure.InnerException);

    }

    [Fact]
    public async Task Rollback_failure_is_content_free_disposes_session_and_prevents_reuse()
    {

        const string providerSecret = "rollback-provider-secret";

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("84BD9C72-FF36-49E4-B1D0-EA11A830F291");

        await SeedTaintedAsync(
            database.Connection,
            "installation-rollback-failure",
            transitionId.ToString("D"),
            67L,
            [1L, 1L, new byte[32]]);

        LifecycleTestSeam seam = new(providerSecret)
        {
            FailRollback = true,
        };

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database, seam);

        SqliteConnection ownedConnection = session.BorrowCoreConnection();

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                TaintedEvidence(
                    "installation-rollback-failure",
                    transitionId,
                    67),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        await ExecuteAsync(
            ownedConnection,
            "UPDATE covenant_authority_state SET TransitionId = upper(TransitionId);");

        Result result = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.DoesNotContain(providerSecret, result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(1, seam.RollbackCalls);

        Assert.Equal(System.Data.ConnectionState.Closed, ownedConnection.State);

        Result<HostProcessToolsDatabaseMarkerEvidence> reused =
            await session.ReadTaintedAsync(Token);

        Assert.True(reused.IsFailure);

        Assert.DoesNotContain(providerSecret, reused.Error.Message, StringComparison.Ordinal);

        Assert.Throws<ObjectDisposedException>(() => session.BorrowCoreConnection());

        await session.DisposeAsync();

        Assert.Equal(System.Data.ConnectionState.Closed, ownedConnection.State);

    }

    [Fact]
    public async Task Commit_failure_contains_rollback_failure_and_preserves_recoverable_tainted_state()
    {

        const string providerSecret = "commit-and-rollback-provider-secret";

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("4B78A114-9F76-44A7-A80B-39A6FDC39211");

        await SeedTaintedAsync(
            database.Connection,
            "installation-commit-failure",
            transitionId.ToString("D"),
            71L,
            [1L, 1L, new byte[32]]);

        LifecycleTestSeam seam = new(providerSecret)
        {
            FailCommit = true,
            FailRollback = true,
        };

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database, seam);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                TaintedEvidence(
                    "installation-commit-failure",
                    transitionId,
                    71),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Result result = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.DoesNotContain(providerSecret, result.Error.Message, StringComparison.Ordinal);

        Assert.True(seam.CommitToken.CanBeCanceled);

        Assert.Equal(1, seam.RollbackCalls);

        Assert.Equal(
            3,
            await ScalarLongAsync(
                database.Connection,
                "SELECT HostToolsStateCode FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Post_write_caller_cancellation_cannot_replace_bounded_commit_barrier_or_proof_token()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("AB66322D-25A4-4413-9E8C-05AB42146D2E");

        await SeedTaintedAsync(
            database.Connection,
            "installation-post-write-cancellation",
            transitionId.ToString("D"),
            83L,
            [1L, 1L, new byte[32]]);

        using CancellationTokenSource callerCancellation = new();

        LifecycleTestSeam seam = new("unused-provider-secret")
        {
            CancelAfterClear = callerCancellation,
        };

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database, seam);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                TaintedEvidence(
                    "installation-post-write-cancellation",
                    transitionId,
                    83),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Result result = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            callerCancellation.Token);

        Assert.True(callerCancellation.IsCancellationRequested);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(seam.CommitToken.CanBeCanceled);

        Assert.NotEqual(callerCancellation.Token, seam.CommitToken);

        Assert.Equal(
            1,
            await ScalarLongAsync(
                database.Connection,
                "SELECT HostToolsStateCode FROM covenant_authority_state;"));

        Assert.Null(
            await ScalarObjectAsync(
                database.Connection,
                "SELECT TransitionId FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Production_checkpoint_deadline_is_exactly_five_seconds_and_supports_a_short_test_deadline()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("90163FA1-7CA5-4CF4-B47F-C328E8B615BB");

        await SeedTaintedAsync(
            database.Connection,
            "installation-checkpoint-deadline",
            transitionId.ToString("D"),
            89L,
            [1L, 1L, new byte[32]]);

        HostToolsMarkerPairResetDatabase production = new(
            database.MaintenanceConnections(),
            CovenantSqliteConnectionInitializer.Instance);

        System.Reflection.FieldInfo? timeout =
            typeof(HostToolsMarkerPairResetDatabase).GetField(
                "_checkpointTimeout",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(timeout);

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            Assert.IsType<TimeSpan>(timeout.GetValue(production)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HostToolsMarkerPairResetDatabase(
                database.MaintenanceConnections(),
                CovenantSqliteConnectionInitializer.Instance,
                NoopHostToolsMarkerPairResetDatabaseTestSeam.Instance,
                TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HostToolsMarkerPairResetDatabase(
                database.MaintenanceConnections(),
                CovenantSqliteConnectionInitializer.Instance,
                NoopHostToolsMarkerPairResetDatabaseTestSeam.Instance,
                TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(1)));

        const string providerSecret = "checkpoint-deadline-provider-secret";

        CheckpointCancellationTestSeam seam = new(providerSecret);

        HostToolsMarkerPairResetDatabase subject = new(
            database.MaintenanceConnections(),
            CovenantSqliteConnectionInitializer.Instance,
            seam,
            TimeSpan.FromMilliseconds(25));

        Result<HostToolsMarkerPairResetDatabaseSession> opened =
            await subject.OpenAsync(Token);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using HostToolsMarkerPairResetDatabaseSession session = opened.Value;

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                TaintedEvidence(
                    "installation-checkpoint-deadline",
                    transitionId,
                    89),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Task<Result> clearing = session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Result result;

        try
        {

            result = await clearing.WaitAsync(TimeSpan.FromSeconds(5));

        }
        catch (TimeoutException)
        {

            seam.ReleaseAfterOuterGuard();

            _ = await clearing.WaitAsync(TimeSpan.FromSeconds(5));

            throw;

        }

        await seam.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(seam.CommitToken.CanBeCanceled);

        Assert.True(seam.CommitToken.IsCancellationRequested);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.DoesNotContain(providerSecret, result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(
            3,
            await ScalarLongAsync(
                database.Connection,
                "SELECT HostToolsStateCode FROM covenant_authority_state;"));

        Assert.Equal(
            transitionId.ToString("D"),
            await ScalarObjectAsync(
                database.Connection,
                "SELECT TransitionId FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Concurrent_capability_reuse_serializes_one_effect_and_one_content_free_failure()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("74978D90-4C5E-4923-A6B2-F0CDBCE75F64");

        await SeedTaintedAsync(
            database.Connection,
            "installation-concurrent-reuse",
            transitionId.ToString("D"),
            73L,
            [1L, 1L, new byte[32]]);

        ConcurrentReuseTestSeam seam = new();

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database, seam);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                TaintedEvidence(
                    "installation-concurrent-reuse",
                    transitionId,
                    73),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Task<Result> first = session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        await seam.AfterClearReached.Task;

        Task<Result> second = session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        bool secondCompletedWhileFirstWasHeld = second.IsCompleted;

        seam.ReleaseAfterClear.TrySetResult();

        Result[] results = await Task.WhenAll(first, second);

        Assert.False(secondCompletedWhileFirstWasHeld);

        Assert.Single(results, result => result.IsSuccess);

        Result rejected = Assert.Single(results, result => result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, rejected.Error.Code);

        Assert.Equal(1, seam.AfterClearCalls);

        Assert.Equal(
            1,
            await ScalarLongAsync(
                database.Connection,
                "SELECT HostToolsStateCode FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Compare_clear_predicates_all_six_raw_values_and_storage_classes()
    {

        string[] mutations =
        [
            "UPDATE covenant_authority_state SET StateKey = 2;",
            "UPDATE covenant_authority_state SET StateKey = CAST(StateKey AS TEXT);",
            "UPDATE covenant_authority_state SET InstallationIdentity = InstallationIdentity || '-changed';",
            "UPDATE covenant_authority_state SET InstallationIdentity = CAST(InstallationIdentity AS BLOB);",
            "UPDATE covenant_authority_state SET HostToolsStateCode = 2;",
            "UPDATE covenant_authority_state SET HostToolsStateCode = CAST(HostToolsStateCode AS TEXT);",
            "UPDATE covenant_authority_state SET TransitionId = '52FD260F-FEB6-4F2F-B5C1-3D764D0E97D9';",
            "UPDATE covenant_authority_state SET TransitionId = CAST(TransitionId AS BLOB);",
            "UPDATE covenant_authority_state SET TaintTimeMasterVersion = 32;",
            "UPDATE covenant_authority_state SET TaintTimeMasterVersion = X'000000000000001F';",
            "UPDATE covenant_authority_state SET TaintFingerprint = zeroblob(32);",
            "UPDATE covenant_authority_state SET TaintFingerprint = CAST(TaintFingerprint AS TEXT);",
        ];

        foreach (string mutation in mutations)
        {

            await AssertCompareLosesAfterMutationAsync(mutation, 31L, 31);

        }

    }

    [Fact]
    public async Task Compare_clear_loses_when_any_raw_value_or_storage_class_changes()
    {

        string[] mutations =
        [
            "UPDATE covenant_authority_state SET StateKey = 2;",
            "UPDATE covenant_authority_state SET StateKey = CAST(StateKey AS TEXT);",
            "UPDATE covenant_authority_state SET InstallationIdentity = 'replacement-installation';",
            "UPDATE covenant_authority_state SET InstallationIdentity = CAST(InstallationIdentity AS BLOB);",
            "UPDATE covenant_authority_state SET HostToolsStateCode = 2;",
            "UPDATE covenant_authority_state SET HostToolsStateCode = CAST(HostToolsStateCode AS TEXT);",
            "UPDATE covenant_authority_state SET TransitionId = upper(TransitionId);",
            "UPDATE covenant_authority_state SET TransitionId = CAST(TransitionId AS BLOB);",
            "UPDATE covenant_authority_state SET TaintTimeMasterVersion = X'0000000000000020';",
            "UPDATE covenant_authority_state SET TaintTimeMasterVersion = 31;",
            "UPDATE covenant_authority_state SET TaintFingerprint = randomblob(32);",
            "UPDATE covenant_authority_state SET TaintFingerprint = hex(TaintFingerprint);",
        ];

        foreach (string mutation in mutations)
        {

            await AssertCompareLosesAfterMutationAsync(
                mutation,
                HostProcessToolsTaintVersionStorage.Encode(31),
                31);

        }

    }

    [Fact]
    public async Task Compare_clear_changes_only_state_transition_taint_version_and_taint_fingerprint()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("8AB99506-C4EF-45A9-981C-87AA15F95C24");

        byte[] currentFingerprint = Enumerable.Repeat((byte)0xA5, 32).ToArray();

        byte[] taintFingerprint = Enumerable.Repeat((byte)0x5A, 32).ToArray();

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-only-marker-change",
            transitionId: transitionId.ToString("D"),
            taintVersion: HostProcessToolsTaintVersionStorage.Encode(ulong.MaxValue),
            unrelatedAuthorityValues: [41L, 43L, currentFingerprint]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                new HostProcessToolsDatabaseMarkerEvidence(
                    "installation-only-marker-change",
                    CovenantHostToolsState.HostToolsTainted,
                    transitionId,
                    ulong.MaxValue,
                    new CovenantDigest(taintFingerprint)),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Result cleared = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Assert.True(cleared.IsSuccess, cleared.Error.Message);

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.CommandText = "SELECT * FROM covenant_authority_state;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        Assert.Equal(1L, reader.GetInt64(0));

        Assert.Equal("installation-only-marker-change", reader.GetString(1));

        Assert.Equal(41L, reader.GetInt64(2));

        Assert.Equal(43L, reader.GetInt64(3));

        Assert.Equal(currentFingerprint, Assert.IsType<byte[]>(reader.GetValue(4)));

        Assert.Equal(1L, reader.GetInt64(5));

        Assert.Equal(1L, reader.GetInt64(6));

        Assert.True(reader.IsDBNull(7));

        Assert.True(reader.IsDBNull(8));

        Assert.True(reader.IsDBNull(9));

        Assert.False(await reader.ReadAsync(Token));

    }

    [Fact]
    public async Task Compare_clear_does_not_read_or_assign_epoch_master_or_recovery_authority_fields()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await database.ExecuteAsync(
            """
            CREATE TRIGGER forbidden_authority_update
            BEFORE UPDATE OF AuthorityEpoch,
                             CurrentMasterKeyVersion,
                             CurrentMasterKeyFingerprint,
                             RecoveryEnvelopeEpoch
            ON covenant_authority_state
            BEGIN
                SELECT RAISE(ABORT, 'unrelated authority field assigned');
            END;
            """,
            Token);

        Guid transitionId = Guid.Parse("50B8D17E-A265-440D-9EF2-87A292E3085E");

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-forbidden-authority",
            transitionId: transitionId.ToString("D"),
            taintVersion: 37L,
            unrelatedAuthorityValues: ["invalid-epoch", "invalid-master", "invalid-fingerprint"]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                new HostProcessToolsDatabaseMarkerEvidence(
                    "installation-forbidden-authority",
                    CovenantHostToolsState.HostToolsTainted,
                    transitionId,
                    37,
                    new CovenantDigest(Enumerable.Repeat((byte)0x5A, 32).ToArray())),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Result cleared = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Assert.True(cleared.IsSuccess, cleared.Error.Message);

        Assert.Equal(
            "invalid-epoch",
            await ScalarStringAsync(
                database.Connection,
                "SELECT AuthorityEpoch FROM covenant_authority_state;"));

        Assert.Equal(
            "invalid-master",
            await ScalarStringAsync(
                database.Connection,
                "SELECT CurrentMasterKeyVersion FROM covenant_authority_state;"));

        Assert.Equal(
            "invalid-fingerprint",
            await ScalarStringAsync(
                database.Connection,
                "SELECT CurrentMasterKeyFingerprint FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Committed_clear_runs_checked_wal_durability_and_proves_same_installation_clean()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("18FB19F2-B6B4-48C5-8097-EF464EFC4C4B");

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-durable-clear",
            transitionId: transitionId.ToString("D"),
            taintVersion: 47L,
            unrelatedAuthorityValues: [1L, 1L, new byte[32]]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        await ExecuteAsync(session.BorrowCoreConnection(), "PRAGMA wal_autocheckpoint=0;");

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(
                new HostProcessToolsDatabaseMarkerEvidence(
                    "installation-durable-clear",
                    CovenantHostToolsState.HostToolsTainted,
                    transitionId,
                    47,
                    new CovenantDigest(Enumerable.Repeat((byte)0x5A, 32).ToArray())),
                Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        Result cleared = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Assert.True(cleared.IsSuccess, cleared.Error.Message);

        CovenantWalCheckpointOutcome checkpoint =
            await ReadCheckpointAsync(database.Connection);

        Assert.Equal(0, checkpoint.Busy);

        Assert.Equal(0, checkpoint.RemainingFrames);

        await using CovenantSchemaScratchDatabase changed =
            await CreateDatabaseAsync();

        await changed.ExecuteAsync(
            """
            CREATE TRIGGER substitute_installation_after_clear
            AFTER UPDATE OF HostToolsStateCode ON covenant_authority_state
            WHEN NEW.HostToolsStateCode = 1
            BEGIN
                UPDATE covenant_authority_state
                SET InstallationIdentity = 'substituted-installation';
            END;
            """,
            Token);

        await SeedTaintedAsync(
            changed.Connection,
            installationIdentity: "installation-readback",
            transitionId: transitionId.ToString("D"),
            taintVersion: 47L,
            unrelatedAuthorityValues: [1L, 1L, new byte[32]]);

        await using HostToolsMarkerPairResetDatabaseSession changedSession =
            await OpenSessionAsync(changed);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> changedCapture =
            await changedSession.BeginImmediateAndCaptureAsync(
                new HostProcessToolsDatabaseMarkerEvidence(
                    "installation-readback",
                    CovenantHostToolsState.HostToolsTainted,
                    transitionId,
                    47,
                    new CovenantDigest(Enumerable.Repeat((byte)0x5A, 32).ToArray())),
                Token);

        Assert.True(changedCapture.IsSuccess, changedCapture.Error.Message);

        Result changedClear =
            await changedSession.CompareClearCommitAndProveDurableAsync(
                changedCapture.Value,
                Token);

        Assert.True(changedClear.IsFailure);

    }

    [Fact]
    public async Task Recovery_clean_suffix_reruns_checked_wal_durability_before_phase_publication()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await SeedCleanAsync(database.Connection, "installation-recovery-clean");

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        await ExecuteAsync(session.BorrowCoreConnection(), "PRAGMA wal_autocheckpoint=0;");

        Result proven = await session.ProveSameInstallationCleanDurableAsync(
            "installation-recovery-clean",
            Token);

        Assert.True(proven.IsSuccess, proven.Error.Message);

        CovenantWalCheckpointOutcome checkpoint =
            await ReadCheckpointAsync(database.Connection);

        Assert.Equal(0, checkpoint.Busy);

        Assert.Equal(0, checkpoint.RemainingFrames);

    }

    [Fact]
    public async Task Recovery_clean_suffix_barrier_failure_preserves_pair_journaled()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await SeedCleanAsync(database.Connection, "installation-busy-recovery");

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        await using SqliteConnection reader =
            await database.OpenAdditionalConnectionAsync(Token);

        await ExecuteAsync(reader, "BEGIN;");

        _ = await ScalarLongAsync(
            reader,
            "SELECT COUNT(*) FROM covenant_authority_state;");

        await ExecuteAsync(
            session.BorrowCoreConnection(),
            "UPDATE covenant_authority_state SET AuthorityEpoch = AuthorityEpoch + 1;");

        Result proven = await session.ProveSameInstallationCleanDurableAsync(
            "installation-busy-recovery",
            Token);

        Assert.True(proven.IsFailure);

        await ExecuteAsync(reader, "ROLLBACK;");

        Assert.Equal(
            1,
            await ScalarLongAsync(
                database.Connection,
                "SELECT HostToolsStateCode FROM covenant_authority_state;"));

        Assert.Null(
            await ScalarObjectAsync(
                database.Connection,
                "SELECT TransitionId FROM covenant_authority_state;"));

    }

    [Fact]
    public async Task Clean_proof_rejects_missing_duplicate_changed_installation_or_partial_marker_shape()
    {

        await AssertCleanProofFailsAsync(
            static _ => Task.CompletedTask,
            "installation-clean-proof");

        await AssertCleanProofFailsAsync(
            async connection =>
            {

                await SeedCleanAsync(connection, "installation-clean-proof");

                await SeedCleanAsync(connection, "installation-clean-proof");

            },
            "installation-clean-proof");

        await AssertCleanProofFailsAsync(
            connection => SeedCleanAsync(connection, "changed-installation"),
            "installation-clean-proof");

        await AssertCleanProofFailsAsync(
            async connection =>
            {

                await SeedCleanAsync(connection, "installation-clean-proof");

                await ExecuteAsync(
                    connection,
                    "UPDATE covenant_authority_state SET TransitionId = '39A437D3-1F04-4890-A0DE-F019275EBFA2';");

            },
            "installation-clean-proof");

        await AssertCleanProofFailsAsync(
            async connection =>
            {

                await SeedCleanAsync(connection, "installation-clean-proof");

                await ExecuteAsync(
                    connection,
                    "UPDATE covenant_authority_state SET TaintTimeMasterVersion = 79;");

            },
            "installation-clean-proof");

        await AssertCleanProofFailsAsync(
            async connection =>
            {

                await SeedCleanAsync(connection, "installation-clean-proof");

                await ExecuteAsync(
                    connection,
                    "UPDATE covenant_authority_state SET TaintFingerprint = zeroblob(32);");

            },
            "installation-clean-proof");

    }

    [Fact]
    public async Task Recovery_observation_accepts_only_exact_original_tainted_or_same_installation_clean_shape()
    {

        Guid transitionId = Guid.Parse("9DB857A9-5017-4971-8214-89F8D4F0414A");

        HostProcessToolsDatabaseMarkerEvidence expected = new(
            "installation-observation",
            CovenantHostToolsState.HostToolsTainted,
            transitionId,
            53,
            new CovenantDigest(Enumerable.Repeat((byte)0x5A, 32).ToArray()));

        await using CovenantSchemaScratchDatabase tainted =
            await CreateDatabaseAsync();

        await SeedTaintedAsync(
            tainted.Connection,
            expected.InstallationIdentity,
            transitionId.ToString("D"),
            53L,
            [1L, 1L, new byte[32]]);

        await using HostToolsMarkerPairResetDatabaseSession taintedSession =
            await OpenSessionAsync(tainted);

        Result<HostToolsDatabaseMarkerRecoveryObservation> original =
            await taintedSession.ObserveExpectedOrCleanAsync(expected, Token);

        Assert.True(original.IsSuccess, original.Error.Message);

        Assert.Equal(
            HostToolsDatabaseMarkerRecoveryObservation.OriginalTainted,
            original.Value);

        await using CovenantSchemaScratchDatabase clean =
            await CreateDatabaseAsync();

        await SeedCleanAsync(clean.Connection, expected.InstallationIdentity);

        await using HostToolsMarkerPairResetDatabaseSession cleanSession =
            await OpenSessionAsync(clean);

        Result<HostToolsDatabaseMarkerRecoveryObservation> cleanObservation =
            await cleanSession.ObserveExpectedOrCleanAsync(expected, Token);

        Assert.True(cleanObservation.IsSuccess, cleanObservation.Error.Message);

        Assert.Equal(
            HostToolsDatabaseMarkerRecoveryObservation.SameInstallationClean,
            cleanObservation.Value);

        await AssertObservationFailsAsync(
            expected,
            connection => SeedCleanAsync(connection, "changed-installation"));

        await AssertObservationFailsAsync(
            expected,
            async connection =>
            {

                await SeedTaintedAsync(
                    connection,
                    expected.InstallationIdentity,
                    transitionId.ToString("D"),
                    54L,
                    [1L, 1L, new byte[32]]);

            });

        await AssertObservationFailsAsync(
            expected,
            async connection =>
            {

                await SeedTaintedAsync(
                    connection,
                    expected.InstallationIdentity,
                    transitionId.ToString("D"),
                    53L,
                    [1L, 1L, new byte[32]]);

                await ExecuteAsync(
                    connection,
                    "UPDATE covenant_authority_state SET HostToolsStateCode = 2;");

            });

    }

    [Fact]
    public async Task Session_opens_one_unpooled_initialized_core_connection_per_attempt()
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        RecordingMaintenanceConnectionFactory connections = new(
            database.MaintenanceConnections());

        RecordingInitializer initializer = new();

        HostToolsMarkerPairResetDatabase subject = new(connections, initializer);

        Result<HostToolsMarkerPairResetDatabaseSession> first =
            await subject.OpenAsync(Token);

        Result<HostToolsMarkerPairResetDatabaseSession> second =
            await subject.OpenAsync(Token);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True(second.IsSuccess, second.Error.Message);

        await using HostToolsMarkerPairResetDatabaseSession firstSession = first.Value;

        await using HostToolsMarkerPairResetDatabaseSession secondSession = second.Value;

        SqliteConnection firstConnection = firstSession.BorrowCoreConnection();

        SqliteConnection secondConnection = secondSession.BorrowCoreConnection();

        Assert.Equal(2, connections.Opened.Count);

        Assert.Equal(2, initializer.Initialized.Count);

        Assert.NotSame(firstConnection, secondConnection);

        Assert.Same(firstConnection, connections.Opened[0]);

        Assert.Same(secondConnection, connections.Opened[1]);

        Assert.Same(firstConnection, initializer.Initialized[0].Connection);

        Assert.Same(secondConnection, initializer.Initialized[1].Connection);

        Assert.All(
            initializer.Initialized,
            initialized => Assert.Equal(
                CovenantSqliteConnectionMode.ReadWrite,
                initialized.Mode));

        Assert.All(
            connections.Opened,
            connection =>
            {

                Assert.Equal(
                    System.Data.ConnectionState.Open,
                    connection.State);

                Assert.False(
                    new SqliteConnectionStringBuilder(connection.ConnectionString)
                        .Pooling);

            });

        Assert.Same(firstConnection, firstSession.BorrowCoreConnection());

        Assert.Same(secondConnection, secondSession.BorrowCoreConnection());

        using CancellationTokenSource openCancellation = new();

        HostToolsMarkerPairResetDatabase canceledOpen = new(
            connections,
            new CancellingInitializer(openCancellation));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await canceledOpen.OpenAsync(openCancellation.Token));

        Assert.Equal(System.Data.ConnectionState.Closed, connections.Opened[2].State);

        Guid transitionId = Guid.Parse("AA6CA430-9D8F-46ED-88A8-1372681E9ECA");

        await SeedTaintedAsync(
            database.Connection,
            "installation-transaction-capability",
            transitionId.ToString("D"),
            61L,
            [1L, 1L, new byte[32]]);

        HostProcessToolsDatabaseMarkerEvidence expected = new(
            "installation-transaction-capability",
            CovenantHostToolsState.HostToolsTainted,
            transitionId,
            61,
            new CovenantDigest(Enumerable.Repeat((byte)0x5A, 32).ToArray()));

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> firstCapture =
            await firstSession.BeginImmediateAndCaptureAsync(expected, Token);

        Assert.True(firstCapture.IsSuccess, firstCapture.Error.Message);

        await firstSession.DisposeAsync();

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> secondCapture =
            await secondSession.BeginImmediateAndCaptureAsync(expected, Token);

        Assert.True(secondCapture.IsSuccess, secondCapture.Error.Message);

        Result rejectedForeign =
            await secondSession.CompareClearCommitAndProveDurableAsync(
                firstCapture.Value,
                Token);

        Assert.True(rejectedForeign.IsFailure);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> recaptured =
            await secondSession.BeginImmediateAndCaptureAsync(expected, Token);

        Assert.True(recaptured.IsSuccess, recaptured.Error.Message);

        Result rejectedStale =
            await secondSession.CompareClearCommitAndProveDurableAsync(
                secondCapture.Value,
                Token);

        Assert.True(rejectedStale.IsFailure);

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> cancellationCapture =
            await secondSession.BeginImmediateAndCaptureAsync(expected, Token);

        Assert.True(cancellationCapture.IsSuccess, cancellationCapture.Error.Message);

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await secondSession.CompareClearCommitAndProveDurableAsync(
                cancellationCapture.Value,
                cancellation.Token));

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> afterCancellation =
            await secondSession.BeginImmediateAndCaptureAsync(expected, Token);

        Assert.True(afterCancellation.IsSuccess, afterCancellation.Error.Message);

    }

    private static async Task<CovenantSchemaScratchDatabase> CreateDatabaseAsync()
    {

        CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await database.ExecuteAsync(
            """
            CREATE TABLE covenant_authority_state (
                StateKey,
                InstallationIdentity,
                AuthorityEpoch,
                CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint,
                RecoveryEnvelopeEpoch,
                HostToolsStateCode,
                TransitionId,
                TaintTimeMasterVersion,
                TaintFingerprint
            );
            """,
            Token);

        return database;

    }

    private static async Task SeedTaintedAsync(
        SqliteConnection connection,
        string installationIdentity,
        string transitionId,
        object taintVersion,
        object[] unrelatedAuthorityValues)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_authority_state (
                StateKey,
                InstallationIdentity,
                AuthorityEpoch,
                CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint,
                RecoveryEnvelopeEpoch,
                HostToolsStateCode,
                TransitionId,
                TaintTimeMasterVersion,
                TaintFingerprint)
            VALUES (
                1,
                $installationIdentity,
                $authorityEpoch,
                $currentMasterKeyVersion,
                $currentMasterKeyFingerprint,
                1,
                3,
                $transitionId,
                $taintVersion,
                $taintFingerprint);
            """;

        _ = command.Parameters.AddWithValue("$installationIdentity", installationIdentity);

        _ = command.Parameters.AddWithValue("$authorityEpoch", unrelatedAuthorityValues[0]);

        _ = command.Parameters.AddWithValue("$currentMasterKeyVersion", unrelatedAuthorityValues[1]);

        _ = command.Parameters.AddWithValue("$currentMasterKeyFingerprint", unrelatedAuthorityValues[2]);

        _ = command.Parameters.AddWithValue("$transitionId", transitionId);

        _ = command.Parameters.AddWithValue("$taintVersion", taintVersion);

        _ = command.Parameters.AddWithValue("$taintFingerprint", Enumerable.Repeat((byte)0x5A, 32).ToArray());

        _ = await command.ExecuteNonQueryAsync(Token);

    }

    private static async Task SeedCleanAsync(
        SqliteConnection connection,
        string installationIdentity)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_authority_state (
                StateKey,
                InstallationIdentity,
                AuthorityEpoch,
                CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint,
                RecoveryEnvelopeEpoch,
                HostToolsStateCode,
                TransitionId,
                TaintTimeMasterVersion,
                TaintFingerprint)
            VALUES (1, $installationIdentity, 1, 1, zeroblob(32), 1, 1, NULL, NULL, NULL);
            """;

        _ = command.Parameters.AddWithValue("$installationIdentity", installationIdentity);

        _ = await command.ExecuteNonQueryAsync(Token);

    }

    private static async Task<HostToolsMarkerPairResetDatabaseSession> OpenSessionAsync(
        CovenantSchemaScratchDatabase database)
        => await OpenSessionAsync(
            database,
            NoopHostToolsMarkerPairResetDatabaseTestSeam.Instance);

    private static async Task<HostToolsMarkerPairResetDatabaseSession> OpenSessionAsync(
        CovenantSchemaScratchDatabase database,
        IHostToolsMarkerPairResetDatabaseTestSeam testSeam)
    {

        HostToolsMarkerPairResetDatabase subject = new(
            database.MaintenanceConnections(),
            CovenantSqliteConnectionInitializer.Instance,
            testSeam);

        Result<HostToolsMarkerPairResetDatabaseSession> opened =
            await subject.OpenAsync(Token);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        return opened.Value;

    }

    private static HostProcessToolsDatabaseMarkerEvidence TaintedEvidence(
        string installationIdentity,
        Guid transitionId,
        ulong taintVersion) =>
        new(
            installationIdentity,
            CovenantHostToolsState.HostToolsTainted,
            transitionId,
            taintVersion,
            new CovenantDigest(Enumerable.Repeat((byte)0x5A, 32).ToArray()));

    private static async Task AssertMalformedProjectionAsync(string mutation)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-malformed",
            transitionId: "30D6CF73-6A55-4E40-AFC2-A0D71D2F8366",
            taintVersion: 29L,
            unrelatedAuthorityValues: [1L, 1L, new byte[32]]);

        if (mutation.Contains("INSERT", StringComparison.Ordinal))
        {

            await ExecuteAsync(
                database.Connection,
                """
                INSERT INTO covenant_authority_state
                SELECT 1,
                       'duplicate-installation',
                       AuthorityEpoch,
                       CurrentMasterKeyVersion,
                       CurrentMasterKeyFingerprint,
                       RecoveryEnvelopeEpoch,
                       HostToolsStateCode,
                       TransitionId,
                       TaintTimeMasterVersion,
                       TaintFingerprint
                FROM covenant_authority_state;
                """);

        }
        else
        {

            await ExecuteAsync(database.Connection, mutation);

        }

        long before = await ScalarLongAsync(
            database.Connection,
            "SELECT total_changes();");

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostProcessToolsDatabaseMarkerEvidence> result =
            await session.ReadTaintedAsync(Token);

        Assert.True(result.IsFailure);

        Assert.Equal(
            before,
            await ScalarLongAsync(database.Connection, "SELECT total_changes();"));

    }

    private static async Task AssertCompareLosesAfterMutationAsync(
        string mutation,
        object taintVersion,
        ulong expectedVersion)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        Guid transitionId = Guid.Parse("4DF73BD0-4CB6-49D0-8E7A-A18C2BB0FC9B");

        byte[] fingerprint = Enumerable.Repeat((byte)0x5A, 32).ToArray();

        await SeedTaintedAsync(
            database.Connection,
            installationIdentity: "installation-raw-cas",
            transitionId: transitionId.ToString("D"),
            taintVersion,
            unrelatedAuthorityValues: [1L, 1L, new byte[32]]);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        HostProcessToolsDatabaseMarkerEvidence expected = new(
            "installation-raw-cas",
            CovenantHostToolsState.HostToolsTainted,
            transitionId,
            expectedVersion,
            new CovenantDigest(fingerprint));

        Result<HostToolsDatabaseMarkerCompareDeleteCapability> captured =
            await session.BeginImmediateAndCaptureAsync(expected, Token);

        Assert.True(captured.IsSuccess, captured.Error.Message);

        await ExecuteAsync(session.BorrowCoreConnection(), mutation);

        Result cleared = await session.CompareClearCommitAndProveDurableAsync(
            captured.Value,
            Token);

        Assert.True(cleared.IsFailure, mutation);

        Assert.Equal(
            3,
            await ScalarLongAsync(
                database.Connection,
                "SELECT HostToolsStateCode FROM covenant_authority_state;"));

    }

    private static async Task AssertCleanProofFailsAsync(
        Func<SqliteConnection, Task> arrange,
        string expectedInstallationIdentity)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await arrange(database.Connection);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result result = await session.ProveSameInstallationCleanDurableAsync(
            expectedInstallationIdentity,
            Token);

        Assert.True(result.IsFailure);

    }

    private static async Task AssertObservationFailsAsync(
        HostProcessToolsDatabaseMarkerEvidence expected,
        Func<SqliteConnection, Task> arrange)
    {

        await using CovenantSchemaScratchDatabase database =
            await CreateDatabaseAsync();

        await arrange(database.Connection);

        await using HostToolsMarkerPairResetDatabaseSession session =
            await OpenSessionAsync(database);

        Result<HostToolsDatabaseMarkerRecoveryObservation> observed =
            await session.ObserveExpectedOrCleanAsync(expected, Token);

        Assert.True(observed.IsFailure);

    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);

    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Assert.IsType<string>(await command.ExecuteScalarAsync(Token));

    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));

    }

    private static async Task<CovenantWalCheckpointOutcome> ReadCheckpointAsync(
        SqliteConnection connection)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));

        return CovenantWalCheckpointOutcome.Project(reader);

    }

    private static async Task<object?> ScalarObjectAsync(
        SqliteConnection connection,
        string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(Token);

        return value is DBNull ? null : value;

    }

    private sealed class RecordingMaintenanceConnectionFactory(
        ICovenantMaintenanceConnectionFactory inner)
        : ICovenantMaintenanceConnectionFactory
    {

        internal List<SqliteConnection> Opened { get; } = [];

        public string DatabasePath => inner.DatabasePath;

        public async Task<SqliteConnection> OpenAsync(
            CancellationToken cancellationToken)
        {

            SqliteConnection connection =
                await inner.OpenAsync(cancellationToken);

            Opened.Add(connection);

            return connection;

        }

        public Task<SqliteConnection> OpenReadOnlyAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenSidecarFreeReadOnlyAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SqliteConnection> OpenSideFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachSideFileAsync(
            SqliteConnection connection,
            string alias,
            string path,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingInitializer : ICovenantSqliteConnectionInitializer
    {

        internal List<(SqliteConnection Connection, CovenantSqliteConnectionMode Mode)>
            Initialized { get; } = [];

        public async ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            Initialized.Add((connection, mode));

            await CovenantSqliteConnectionInitializer.Instance.InitializeAsync(
                connection,
                mode,
                cancellationToken);

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            CovenantSqliteConnectionInitializer.Instance.Authorize(connection, kind);

        public CovenantSqliteAuthorizationScope
            AuthorizeRestoreStagingManagedAuthoritySanitization(
                RestoreStagingManagedAuthoritySanitizationCapability authority,
                RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            CovenantSqliteConnectionInitializer.Instance
                .AuthorizeRestoreStagingManagedAuthoritySanitization(
                    authority,
                    runIdentity);

    }

    private sealed class CancellingInitializer(CancellationTokenSource cancellation)
        : ICovenantSqliteConnectionInitializer
    {

        public ValueTask InitializeAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken)
        {

            cancellation.Cancel();

            cancellationToken.ThrowIfCancellationRequested();

            throw new InvalidOperationException("The initializer did not observe cancellation.");

        }

        public CovenantSqliteAuthorizationScope Authorize(
            SqliteConnection connection,
            CovenantSqliteAuthorizationKind kind) =>
            throw new NotSupportedException();

        public CovenantSqliteAuthorizationScope
            AuthorizeRestoreStagingManagedAuthoritySanitization(
                RestoreStagingManagedAuthoritySanitizationCapability authority,
                RestoreStagingManagedAuthoritySanitizationCapability.RunIdentity runIdentity) =>
            throw new NotSupportedException();

    }

    private sealed class LifecycleTestSeam(string providerSecret)
        : IHostToolsMarkerPairResetDatabaseTestSeam
    {

        internal bool FailCommit { get; init; }

        internal bool FailRollback { get; init; }

        internal CancellationTokenSource? CancelAfterClear { get; init; }

        internal CancellationToken CommitToken { get; private set; }

        internal int RollbackCalls { get; private set; }

        public void BeforeRollback()
        {

            RollbackCalls++;

            if (FailRollback)
            {

                throw new SqliteException(providerSecret, 1);

            }

        }

        public ValueTask AfterMarkerClearAsync(
            CancellationToken callerCancellationToken)
        {

            CancelAfterClear?.Cancel();

            return ValueTask.CompletedTask;

        }

        public ValueTask BeforeCommitAsync(
            CancellationToken checkpointCancellationToken)
        {

            CommitToken = checkpointCancellationToken;

            if (FailCommit)
            {

                throw new SqliteException(providerSecret, 1);

            }

            return ValueTask.CompletedTask;

        }

    }

    private sealed class ConcurrentReuseTestSeam
        : IHostToolsMarkerPairResetDatabaseTestSeam
    {

        internal TaskCompletionSource AfterClearReached { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseAfterClear { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int AfterClearCalls { get; private set; }

        public void BeforeRollback()
        {
        }

        public async ValueTask AfterMarkerClearAsync(
            CancellationToken callerCancellationToken)
        {

            AfterClearCalls++;

            AfterClearReached.TrySetResult();

            await ReleaseAfterClear.Task.WaitAsync(callerCancellationToken);

        }

        public ValueTask BeforeCommitAsync(
            CancellationToken checkpointCancellationToken) =>
            ValueTask.CompletedTask;

    }

    private sealed class CheckpointCancellationTestSeam(string providerSecret)
        : IHostToolsMarkerPairResetDatabaseTestSeam
    {

        private readonly TaskCompletionSource _neverCompletes = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken CommitToken { get; private set; }

        internal void ReleaseAfterOuterGuard() => _neverCompletes.TrySetResult();

        public void BeforeRollback()
        {
        }

        public ValueTask AfterMarkerClearAsync(
            CancellationToken callerCancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask BeforeCommitAsync(
            CancellationToken checkpointCancellationToken)
        {

            CommitToken = checkpointCancellationToken;

            try
            {

                await _neverCompletes.Task.WaitAsync(checkpointCancellationToken);

            }
            catch (OperationCanceledException)
                when (checkpointCancellationToken.IsCancellationRequested)
            {

                CancellationObserved.TrySetResult();

                throw new OperationCanceledException(
                    providerSecret,
                    innerException: null,
                    checkpointCancellationToken);

            }

        }

    }

}

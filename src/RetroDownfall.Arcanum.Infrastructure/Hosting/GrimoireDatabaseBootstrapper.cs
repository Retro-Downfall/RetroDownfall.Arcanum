using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Secrets.Security;
using Serilog;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Shared first-run Grimoire initialization: SQLCipher passphrase, key probe, and a fresh install of
/// the declarative embedded schema (AOT-safe; no <c>Database.MigrateAsync</c>, no migration chain).
/// </summary>
public static class GrimoireDatabaseBootstrapper
{
    /// <summary>
    /// Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c> on a fresh connection at graceful shutdown
    /// so the <c>-wal</c>/<c>-shm</c> sidecar files do not persist across restarts. Best-effort:
    /// any failure is logged and swallowed so it never blocks shutdown.
    /// </summary>
    public static Task CheckpointOnShutdownAsync(
        IGrimoireDbPassphraseSource passphraseSource,
        CancellationToken cancellationToken) =>
        CheckpointOnShutdownAsync(passphraseSource, ArcanumPaths.GrimoireDatabaseFile, cancellationToken);

    internal static async Task CheckpointOnShutdownAsync(
        IGrimoireDbPassphraseSource passphraseSource,
        string dbPath,
        CancellationToken cancellationToken)
    {
        // W3.4 Group D #9: check the file exists BEFORE accessing the passphrase — the
        // passphrase source throws if uninitialized, and on a cold shutdown where the DB was
        // never created there is nothing to checkpoint. The passphrase access is inside the
        // try below so an uninitialized passphrase on a stray file is also handled best-effort.
        if (!File.Exists(dbPath))
        {
            return;
        }

        try
        {
            // This connection is opened on its own path: a host that shut down without ever opening
            // the Grimoire has installed no provider, and the checkpoint would fail on the raw
            // SQLitePCLRaw error rather than the typed unavailability. Inside the try on purpose —
            // the method is best-effort by contract, and a runtime that will not load belongs in the
            // same swallowed warning as every other failure here rather than thrown out of shutdown.
            SqliteNativeRuntime.Instance.Initialize();

            string passphrase = passphraseSource.Passphrase;

            // Unpooled, or this checkpoint defeats its own purpose. A pooled handle is not closed by
            // disposal, and the engine only removes the two sidecars when the last handle closes —
            // so the connection that truncated the log would leave both files behind it, which is
            // the state this method exists to prevent.
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Password = passphrase,
                Pooling = false,
            }.ToString();

            await using SqliteConnection connection = new(connectionString);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Apply the standard pragmas so busy_timeout/synchronous match the runtime and the
            // checkpoint can wait briefly for any concurrent reader to release the WAL.
            await SqliteConnectionPragmas.ApplyAsync(connection, cancellationToken).ConfigureAwait(false);

            await using SqliteCommand checkpoint = connection.CreateCommand();

            // W3.4 Group D #9: TRUNCATE checkpoints the WAL back into the main database and
            // truncates the -wal file to zero bytes, so it does not persist across restarts.
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

            _ = await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await connection.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Grimoire WAL checkpoint on shutdown failed for {DbPath}; continuing shutdown.", dbPath);
        }
    }

    /// <summary>
    /// Bootstraps the Grimoire while the caller holds the installation maintenance lock. The lock is required:
    /// both production callers (the host's start-up and the CLI's exclusive operations) own it before they
    /// bootstrap, because Covenant authority preparation and topology recovery need exclusive ownership of
    /// the guarded root. A null lock throws <see cref="ArgumentNullException"/> from the full overload these forward to.
    /// </summary>
    internal static Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        ArcanumMaintenanceLock heldInstallationLock,
        Guid? expectedInstallationId,
        CancellationToken cancellationToken) =>
        EnsureInitializedAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            dbPath,
            grimoireDirectory,
            heldInstallationLock,
            expectedInstallationId,
            postRestoreTopology: null,
            restoreDisclosureWriterAfterAuthenticatedTransition: false,
            cancellationToken);

    /// <inheritdoc cref="EnsureInitializedAsync(ISecretStore, IGrimoireDbPassphraseSource, IServiceScopeFactory, string, string, ArcanumMaintenanceLock, Guid?, CancellationToken)"/>
    internal static Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        ArcanumMaintenanceLock heldInstallationLock,
        Guid? expectedInstallationId,
        Func<CancellationToken, Task<MasterApiKeyBootstrapResult?>>?
            postRestoreTopology,
        CancellationToken cancellationToken) =>
        EnsureInitializedAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            dbPath,
            grimoireDirectory,
            heldInstallationLock,
            expectedInstallationId,
            postRestoreTopology,
            restoreDisclosureWriterAfterAuthenticatedTransition: false,
            cancellationToken);

    /// <summary>
    /// Test-only: bootstraps with no installation maintenance lock, which installs the schema and skips only
    /// the topology recovery and authority preparation that need exclusive ownership. No production code may
    /// call this, and none passes a null lock to <c>EnsureInitializedAsync</c> either;
    /// <c>GrimoireBootstrapLockCallSiteTests</c> fails the build of any <c>src/</c> call site that does.
    /// </summary>
    internal static Task EnsureInitializedWithoutInstallationLockForTestsAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        CancellationToken cancellationToken) =>
        EnsureInitializedWithoutInstallationLockForTestsAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            dbPath,
            grimoireDirectory,
            restoreDisclosureWriterAfterAuthenticatedTransition: false,
            cancellationToken);

    /// <inheritdoc cref="EnsureInitializedWithoutInstallationLockForTestsAsync(ISecretStore, IGrimoireDbPassphraseSource, IServiceScopeFactory, string, string, CancellationToken)"/>
    internal static Task EnsureInitializedWithoutInstallationLockForTestsAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        bool restoreDisclosureWriterAfterAuthenticatedTransition,
        CancellationToken cancellationToken) =>
        EnsureInitializedCoreAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            dbPath,
            grimoireDirectory,
            heldInstallationLock: null,
            expectedInstallationId: null,
            postRestoreTopology: null,
            restoreDisclosureWriterAfterAuthenticatedTransition,
            cancellationToken);

    /// <summary>
    /// The full lock-required entry point. The lock is a non-nullable parameter, so the compiler (nullable analysis,
    /// with warnings as errors) refuses any <c>src/</c> call that passes a null literal or a variable that may be
    /// null; the only path that accepts no lock is the private core behind the test-only seam above.
    /// </summary>
    internal static Task EnsureInitializedAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        ArcanumMaintenanceLock heldInstallationLock,
        Guid? expectedInstallationId,
        Func<CancellationToken, Task<MasterApiKeyBootstrapResult?>>?
            postRestoreTopology,
        bool restoreDisclosureWriterAfterAuthenticatedTransition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        return EnsureInitializedCoreAsync(
            secretStore,
            passphraseSource,
            scopeFactory,
            dbPath,
            grimoireDirectory,
            heldInstallationLock,
            expectedInstallationId,
            postRestoreTopology,
            restoreDisclosureWriterAfterAuthenticatedTransition,
            cancellationToken);
    }

    /// <summary>
    /// The one implementation behind every entry point above. <paramref name="heldInstallationLock"/> is
    /// nullable only so the test-only seam can exercise the schema and key paths without a lock; it is private so
    /// that nothing else can reach that shape, and every production caller passes the lock it holds.
    /// </summary>
    private static async Task EnsureInitializedCoreAsync(
        ISecretStore secretStore,
        IGrimoireDbPassphraseSource passphraseSource,
        IServiceScopeFactory scopeFactory,
        string dbPath,
        string grimoireDirectory,
        ArcanumMaintenanceLock? heldInstallationLock,
        Guid? expectedInstallationId,
        Func<CancellationToken, Task<MasterApiKeyBootstrapResult?>>?
            postRestoreTopology,
        bool restoreDisclosureWriterAfterAuthenticatedTransition,
        CancellationToken cancellationToken)
    {
        SqliteNativeRuntime.Instance.Initialize();

        // Before the guarded directory is even created. A restore that died between its two renames
        // leaves no live root at all, and creating an empty one here would occupy the name the rollback
        // has to move back into (§10.19.8).
        await RecoverRestoreTopologyAsync(
            scopeFactory,
            heldInstallationLock,
            grimoireDirectory,
            cancellationToken).ConfigureAwait(false);

        MasterApiKeyBootstrapResult? bootstrappedKey = null;

        if (postRestoreTopology is not null)
        {
            bootstrappedKey = await postRestoreTopology(cancellationToken)
                .ConfigureAwait(false);
        }

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(grimoireDirectory);

        string? apiKey = bootstrappedKey?.ApiKey;

        if (apiKey is null)
        {
            apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Fatal("Grimoire startup aborted: master API key is not present. Persist a key before enabling the database.");

            throw new MissingMasterApiKeyException();
        }

        string passphrase = await ResolveGrimoirePassphraseAsync(secretStore, apiKey, dbPath, cancellationToken).ConfigureAwait(false);

        passphraseSource.SetPassphrase(passphrase);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = passphrase,
        }.ToString();

        if (File.Exists(dbPath))
        {
            try
            {
                await using SqliteConnection probe = new(connectionString);

                await probe.OpenAsync(cancellationToken).ConfigureAwait(false);

                await SqliteConnectionPragmas.ApplyAsync(probe, cancellationToken).ConfigureAwait(false);

                await using SqliteCommand cmd = probe.CreateCommand();

                cmd.CommandText = "SELECT 1;";

                _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A host stopping mid-probe says nothing about the key.
                throw;
            }
            catch (Exception ex) when (IndicatesKeyMismatchOrCorruption(ex))
            {
                Log.Fatal(
                    ex,
                    "Grimoire database exists at {DbPath} but could not be opened with the derived key. Possible tampering, corruption, or master key mismatch. Arcanum will exit.",
                    dbPath);

                throw new GrimoireDatabaseUnavailableException(
                    "Arcanum Grimoire database key verification failed. See logs for recovery steps.");
            }
            catch (Exception ex)
            {
                // Busy, locked, an I/O failure or a failing disk: the probe never reached a verdict on the key,
                // so reporting tampering would send the operator after the wrong cause.
                Log.Fatal(
                    ex,
                    "Grimoire database exists at {DbPath} but could not be read to verify the derived key (the database may be busy or the disk failing); this is not evidence of a key mismatch. Arcanum will exit.",
                    dbPath);

                throw new GrimoireDatabaseUnavailableException(
                    "Arcanum could not read the Grimoire database to verify its key; the database may be busy or the disk failing. See logs.",
                    ex);
            }
        }

        await using SqliteConnection installConnection = new(connectionString);

        await installConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await SqliteConnectionPragmas.ApplyAsync(installConnection, cancellationToken).ConfigureAwait(false);

        DeferredHostProcessToolsDecision? hostProcessToolsDecision =
            await ClassifyHostProcessToolsAsync(
                installConnection,
                scopeFactory,
                cancellationToken).ConfigureAwait(false);

        await InstallSchemaAsync(
            installConnection,
            scopeFactory,
            heldInstallationLock,
            grimoireDirectory,
            apiKey,
            expectedInstallationId,
            hostProcessToolsDecision,
            cancellationToken).ConfigureAwait(false);

        // After host-tools classification and core convergence, and before every database-dependent
        // recovery below it. An active restore journal means the authority this installation now runs
        // under has not been revalidated, and none of those passes may open against it.
        await RecoverRestoreAuthorityAsync(
            scopeFactory,
            heldInstallationLock,
            grimoireDirectory,
            cancellationToken).ConfigureAwait(false);

        // After the schema and restore authority are settled, with no transaction open on the install
        // handle and before any exclusive owner is adopted into the Covenant gate: a keychain read here
        // runs outside every transaction, lease, and closure. It reads only when fingerprints exist, so
        // Covenant agent writes are not withheld after a restart until an operator call resolves the
        // key. A composition without the provider has no chokepoint to warm it for.
        await using (AsyncServiceScope erasureScope = scopeFactory.CreateAsyncScope())
        {
            if (erasureScope.ServiceProvider.GetService<IMemoryErasureKeyProvider>() is { } erasureKeys)
            {
                await MemoryErasureKeyWarmup.RunAsync(installConnection, erasureKeys, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        ProtectedMaintenanceRecovery protectedRecovery = await RecoverProtectedMaintenanceAsync(
            installConnection,
            scopeFactory,
            heldInstallationLock,
            grimoireDirectory,
            apiKey,
            cancellationToken).ConfigureAwait(false);

        await installConnection.CloseAsync().ConfigureAwait(false);

        // After the install handle is physically closed and before readiness. A launch committed by a
        // process that died before publishing its journal is a pre-effect crash: safe to resume, and
        // unsafe to leave, because a nonterminal durable operation blocks every later data-retention
        // operation. Until now it was left to the periodic reconciler, which runs after readiness on a
        // ten-second budget with the host already serving - and a transition closes admission, so it
        // may not begin after the signal every pool and worker waits on has been published.
        await ResumeLaunchGapAsync(
            scopeFactory,
            heldInstallationLock,
            grimoireDirectory,
            protectedRecovery.AdoptedErasureOwner,
            cancellationToken).ConfigureAwait(false);

        if (restoreDisclosureWriterAfterAuthenticatedTransition)
        {
            await RestoreAuthenticatedTransitionDisclosureWriterAsync(
                scopeFactory,
                cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(dbPath))
        {
            SecureFilePermissions.ApplyOwnerOnlyFile(dbPath);
        }

        await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
        {
            IGrimoireDbReadiness readiness = scope.ServiceProvider.GetRequiredService<IGrimoireDbReadiness>();

            protectedRecovery.Gate?.PublishReadiness();

            readiness.MarkReady();
        }
    }

    /// <summary>
    /// Restores the exact disclosure-writer singleton an authenticated startup transition quiesced.
    /// </summary>
    /// <remarks>
    /// Recovery itself cannot do this honestly: it runs before schema health is classified. Normal
    /// bootstrap calls here only after schema and authority publication, protected recovery, physical
    /// install-handle closure, and launch-gap recovery, while Covenant and Grimoire readiness are both
    /// still unpublished. A failure therefore aborts startup without changing the already-terminal
    /// operation or retired offline journal.
    /// </remarks>
    private static async Task RestoreAuthenticatedTransitionDisclosureWriterAsync(
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        CovenantDisclosureWriter writer = scope.ServiceProvider
            .GetRequiredService<CovenantDisclosureWriter>();

        ICovenantDisclosureWriterLifecycle lifecycle = scope.ServiceProvider
            .GetRequiredService<ICovenantDisclosureWriterLifecycle>();

        if (!ReferenceEquals(writer, lifecycle))
        {
            throw new GrimoireDatabaseUnavailableException(
                "Authenticated transition recovery did not resolve the process disclosure writer safely.");
        }

        Result reopened = await lifecycle.ReopenAsync(cancellationToken).ConfigureAwait(false);

        if (reopened.IsFailure)
        {
            throw new GrimoireDatabaseUnavailableException(
                "Authenticated transition recovery could not restore the Covenant disclosure writer.");
        }
    }

    /// <summary>
    /// Converges an interrupted restore's rename topology before any database is opened or classified.
    /// </summary>
    /// <remarks>
    /// First, and deliberately before the guarded directory is created: a process killed between the
    /// live-root and staged-root renames leaves no live root at all, and this is the only pass entitled
    /// to decide which tree takes that name back.
    ///
    /// <para>Skipped without an installation lock, exactly as the protected-maintenance recovery below
    /// is. A CLI running beside a live host does not own the installation, and a second process moving
    /// installation roots would be two owners for one tree.</para>
    ///
    /// <para>The pre-Covenant sweep of §5.4.9 still runs, but only once the authenticated stack has
    /// proved that no V2 evidence exists. <c>BackupRestoreService</c> still writes the plain journal, so
    /// dropping that sweep would strand every restore interrupted by this build; running it while an
    /// authenticated journal is active would let the weaker of the two paths decide.</para>
    /// </remarks>
    private static async Task RecoverRestoreTopologyAsync(
        IServiceScopeFactory scopeFactory,
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        CancellationToken cancellationToken)
    {
        if (heldInstallationLock is null)
        {
            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        if (TryCreateRestoreRecovery(scope, grimoireDirectory, withAuthority: false) is not { } recovery)
        {
            ResolveInterruptedRestores(grimoireDirectory);

            return;
        }

        Result<BackupRestorePhysicalRecoveryOutcome> topology = await recovery
            .RecoverPhysicalTopologyBeforeDatabaseAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (topology.IsFailure || topology.Value is BackupRestorePhysicalRecoveryOutcome.KeptClosed)
        {
            Log.Fatal(
                "An interrupted Arcanum restore could not be converged to a single installation root. "
                + "Arcanum will not start against a tree it cannot identify. Resolve the restore, or "
                + "restore again from a verified generation.");

            throw new GrimoireDatabaseUnavailableException(
                "An interrupted Arcanum restore could not be resolved. See logs for recovery steps.");
        }

        if (topology.Value is BackupRestorePhysicalRecoveryOutcome.NoActiveJournal)
        {
            ResolveInterruptedRestores(grimoireDirectory);
        }
    }

    /// <summary>
    /// Resumes an interrupted restore's exclusive authority before anything can publish readiness.
    /// </summary>
    /// <remarks>
    /// A converged tree is not a finished restore. Until the exact owner has been resumed and its one
    /// disposition has succeeded, the installation is running under authority nobody revalidated — so a
    /// kept-closed outcome stops startup here rather than degrading, which is what keeps host and CLI
    /// readiness off an unproven replacement (§10.19.8).
    /// </remarks>
    private static async Task RecoverRestoreAuthorityAsync(
        IServiceScopeFactory scopeFactory,
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        CancellationToken cancellationToken)
    {
        if (heldInstallationLock is null)
        {
            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        if (TryCreateRestoreRecovery(scope, grimoireDirectory, withAuthority: true) is not { } recovery)
        {
            return;
        }

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await recovery
            .RecoverAuthorityBeforeReadinessAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure || recovered.Value is BackupRestoreStartupRecoveryOutcome.KeptClosed)
        {
            Log.Fatal(
                "An interrupted Arcanum restore's authority could not be revalidated, so Covenant "
                + "admission stays closed and its journal remains active for the next start.");

            throw new GrimoireDatabaseUnavailableException(
                "An interrupted Arcanum restore could not be resolved. See logs for recovery steps.");
        }

        // Every other outcome stops too, so one added later cannot let readiness through by default.
        if (recovered.Value is not (BackupRestoreStartupRecoveryOutcome.NoActiveJournal
            or BackupRestoreStartupRecoveryOutcome.RecoveredReady))
        {
            Log.Fatal(
                "An interrupted Arcanum restore was resolved, but its retained staging still needs an "
                + "operator: its rollback could not reinstate every local secret. Arcanum will not start "
                + "until that staging is resolved.");

            throw new GrimoireDatabaseUnavailableException(
                "An interrupted Arcanum restore could not be resolved. See logs for recovery steps.");
        }
    }

    /// <summary>
    /// Builds the one recovery for this installation, or nothing when the OS-secret boundary is absent.
    /// </summary>
    /// <remarks>
    /// The credential store is an optional resolution for the same reason the authority provider below
    /// is: a narrow container that only installs schema is a legitimate caller. Its absence is never
    /// treated as proven absence of restore evidence — there is simply no anchor to read, so this pass
    /// cannot run at all and says so.
    /// </remarks>
    private static BackupRestoreRecovery? TryCreateRestoreRecovery(
        AsyncServiceScope scope,
        string grimoireDirectory,
        bool withAuthority)
    {
        if (scope.ServiceProvider.GetService<IOsCredentialStore>() is not { } credentials)
        {
            Log.Warning(
                "No OS credential boundary is composed in this container, so the authenticated restore "
                + "journal cannot be read.");

            return null;
        }

        return new BackupRestoreRecovery(
            grimoireDirectory,
            new BackupRestoreJournalAnchorStore(
                credentials,
                new BackupRestoreJournalKeyProvider(credentials),
                new BackupRestoreJournalInstallationIdentityProvider(credentials)),
            withAuthority ? scope.ServiceProvider.GetService<CovenantOperationGate>() : null,
            withAuthority ? scope.ServiceProvider.GetService<ICampaignPathMarkerLifecycle>() : null);
    }

    /// <summary>
    /// Finishes or reverses a restore that a previous process death left mid-commit (issue #38).
    /// </summary>
    /// <remarks>
    /// The pre-Covenant path: a plain journal plus the filesystem's own evidence. It runs under the same
    /// installation lock and before the database is opened, so the host never bootstraps against a
    /// half-swapped tree, and each resolution is logged so an operator can see what happened.
    /// </remarks>
    private static void ResolveInterruptedRestores(string grimoireDirectory)
    {
        try
        {
            bool reconciliationRequired = false;

            foreach (BackupRestoreRecoveryReport report in
                     BackupRestoreRecovery.Resolve(grimoireDirectory))
            {
                Log.Warning(
                    "Interrupted Arcanum restore resolved as {Outcome} at phase {Phase}: {Detail}",
                    report.Outcome,
                    report.Phase,
                    report.Detail);

                reconciliationRequired |= report.Outcome
                    is BackupRestoreRecoveryOutcome.ReconciliationRequired;
            }

            if (reconciliationRequired)
            {
                Log.Fatal(
                    "Legacy interrupted-restore evidence could not be converged safely. "
                    + "Arcanum will not create or open the installation root until it is reconciled.");

                throw new GrimoireDatabaseUnavailableException(
                    "An interrupted Arcanum restore could not be resolved. See logs for recovery steps.");
            }
        }
        catch (GrimoireDatabaseUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Fatal(
                ex,
                "Legacy interrupted-restore recovery could not classify the installation topology. "
                + "Arcanum will not continue startup.");

            throw new GrimoireDatabaseUnavailableException(
                "An interrupted Arcanum restore could not be resolved. See logs for recovery steps.",
                ex);
        }
    }

    /// <summary>
    /// Classifies the host-process-tools startup state before any schema, pool, or key material exists.
    /// </summary>
    /// <remarks>
    /// First, and on the install connection rather than a pool, because its whole purpose is to
    /// decide whether this process may ever hold decrypted Covenant bytes. Classification publishes
    /// only into a provisional policy. The exact decision is held until schema convergence proves the
    /// expected installation identity, so no pre-verification exit can mutate the process singleton.
    /// A hard blocked disposition aborts startup with the offline command (§10.12).
    ///
    /// <para>The gate is optional to resolve for the same reason the authority provider below is: a
    /// narrow container that only installs schema is a legitimate caller. When it is absent, the
    /// runtime policy stays unpublished, which every Covenant consumer already reads as "not
    /// permitted".</para>
    /// </remarks>
    private static async Task<DeferredHostProcessToolsDecision?> ClassifyHostProcessToolsAsync(
        SqliteConnection installConnection,
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IHostProcessToolsMarkerStore? markers =
            scope.ServiceProvider.GetService<IHostProcessToolsMarkerStore>();

        HostProcessToolsRuntimePolicy? policy =
            scope.ServiceProvider.GetService<HostProcessToolsRuntimePolicy>();

        IHostProcessToolsEnvironmentProbe? environment =
            scope.ServiceProvider.GetService<IHostProcessToolsEnvironmentProbe>();

        if (markers is null || policy is null || environment is null)
        {
            return null;
        }

        HostProcessToolsRuntimePolicy provisionalPolicy = new();

        HostProcessToolsStartupGate gate = new(
            markers,
            new HostProcessToolsAuthorityStore(installConnection),
            environment,
            new HostProcessToolsMarkerPairJoiner(),
            provisionalPolicy);

        Result<HostProcessToolsStartupDecision> decision = await gate
            .ClassifyAndPublishAsync(cancellationToken)
            .ConfigureAwait(false);

        if (decision.IsSuccess)
        {
            return new DeferredHostProcessToolsDecision(policy, decision.Value);
        }

        HostProcessToolsStartupDecision blocked = new(
            provisionalPolicy.Disposition
                ?? HostProcessToolsMarkerPairDisposition.MismatchBlocked,
            provisionalPolicy.CovenantPermitted,
            provisionalPolicy.HostProcessToolsPermitted,
            provisionalPolicy.Blocker);

        // One blocked case is not a hard stop yet. `EscapeHatchWithoutTransition` means the durable
        // state is provably clean and this host merely started with the opt-in armed, which is what
        // the offline transition exists to resolve. Hard-failing today would leave a Development host
        // with no way to start and no way to complete that transition, so it degrades: the closed
        // decision is held unpublished until installation identity verification, then becomes the
        // process policy — and since that published decision is what admits host process tools, the
        // degraded host advertises none of them. Every other blocked disposition means the durable
        // evidence disagrees with itself, which cannot happen without a transition having been
        // attempted, and those still stop startup (§10.15).
        if (blocked.Blocker is HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition)
        {
            Log.Warning(
                "Arcanum started with the host-process-tools escape hatch armed and no completed "
                + "transition. Covenant stays closed. {Remediation}",
                HostProcessToolsStartupGate.EscapeHatchRemediation);

            return new DeferredHostProcessToolsDecision(policy, blocked);
        }

        throw new GrimoireDatabaseUnavailableException(decision.Error.Message);
    }

    /// <summary>
    /// Installs the complete Grimoire schema from the declarative <c>Data/Schema/</c> tree as the
    /// three independently validated transaction tiers, then publishes the outcome through
    /// <see cref="CovenantAvailability"/>.
    /// </summary>
    /// <remarks>
    /// Core is not optional, so its failure aborts startup. The two Covenant tiers fail at their own
    /// boundaries and are published as unavailable or degraded, which is why the whole
    /// <see cref="GrimoireSchemaInstallResult"/> is handed to the publisher rather than a success
    /// flag.
    ///
    /// <para><see cref="WeaveIndexAvailability"/> is set to managed-only unconditionally. The
    /// templated <c>vec0</c> accelerator tier it used to reflect no longer exists: the hermetic
    /// SQLCipher runtime ships without extension loading, so managed cosine over the durable BLOB
    /// tables is the only Divination search path and advertising anything else would be a lie
    /// (§21.2).</para>
    /// </remarks>
    private static async Task InstallSchemaAsync(
        SqliteConnection installConnection,
        IServiceScopeFactory scopeFactory,
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        string masterApiKey,
        Guid? expectedInstallationId,
        DeferredHostProcessToolsDecision? hostProcessToolsDecision,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        // Settings resolution runs first and owns its own failure. It used to share a try block with
        // an unrelated GetRequiredService<WeaveIndexAvailability>(), which AddArcanumGrimoireForCli
        // never registered: the throw happened before the dimension assignment, the catch swallowed
        // it, and every CLI bootstrap silently installed with the DEFAULT embedding dimension instead
        // of the configured one. Keep the two resolutions separate, and keep the optional one
        // nullable, so an absent registration can never again decide the embedding width.
        int dimensions = GrimoireEmbeddingDimensionResolver.Resolve(scope.ServiceProvider);

        WeaveIndexAvailability? availability = scope.ServiceProvider.GetService<WeaveIndexAvailability>();

        GrimoireSchemaInstaller installer = scope.ServiceProvider.GetRequiredService<GrimoireSchemaInstaller>();

        GrimoireSchemaInitializationContext context = BuildInitializationContext(
            heldInstallationLock,
            grimoireDirectory,
            masterApiKey);

        GrimoireSchemaInstallResult result;

        try
        {
            result = await installer.InstallAsync(
                installConnection,
                dimensions,
                context,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GrimoireSchemaRefusedException refused)
        {
            // Normalized at the boundary for the same reason the KDF sidecar failure is: the refusal
            // carries the steps the operator has to take, and letting it escape raw puts it in
            // CliFailureMapper's default arm, which prints "An unexpected CLI error occurred." and
            // names nothing. The refusal type stays internal because its reason enum is; what crosses
            // is the sentence.
            Log.Fatal(refused, "The Grimoire schema tier was refused. {Message}", refused.Message);

            throw new GrimoireDatabaseUnavailableException(refused.Message, refused);
        }

        await VerifyExpectedInstallationIdentityAsync(
            installConnection,
            expectedInstallationId,
            cancellationToken).ConfigureAwait(false);

        // The provisional pre-schema classification belongs to this process only after the
        // authenticated active envelope and the converged database authority prove they name the
        // same installation. Keep this immediately before Covenant publication/reconciliation.
        if (hostProcessToolsDecision is not null)
        {
            Result publication = hostProcessToolsDecision.ProcessPolicy.Publish(
                hostProcessToolsDecision.Decision);

            if (publication.IsFailure)
            {
                throw new GrimoireDatabaseUnavailableException(publication.Error.Message);
            }

            // Advertise and invoke sites reach the policy through a static predicate, which has no
            // service to inject. Binding the published policy is what makes them read this decision
            // instead of re-deriving one from the edition and environment the gate has already
            // refused — the difference between a published decision and a log line (§10.12).
            HostProcessToolPolicy.BindStartupDecision(hostProcessToolsDecision.ProcessPolicy);
        }

        CovenantAvailability covenantAvailability = scope.ServiceProvider
            .GetRequiredService<CovenantAvailability>();

        CovenantAvailabilitySnapshot published = covenantAvailability
            .PublishSchema(result, CovenantHealthTransition.Bootstrap);

        // PublishSchema only reports which tiers installed. The dataset generation, the sequences and
        // the accelerator's applied tuple are persisted in covenant_state, and until they are read
        // the snapshot keeps its bootstrap default of a null DatasetGeneration — on which
        // CovenantOperationGate refuses every requireCanonical acquisition, which AcquireOrdinary
        // always is. Publishing here, after the install transactions commit and before readiness, is
        // what makes the ordinary Covenant path reachable at all; it is also the correct ordering,
        // since a generation published earlier could name a transaction that then rolled back.
        published = await CovenantPersistedAvailabilityPublisher.PublishAsync(
                covenantAvailability,
                installConnection,
                result.CovenantAccelerator.IsHealthy,
                CovenantHealthTransition.Bootstrap,
                cancellationToken).ConfigureAwait(false)
            ? covenantAvailability.Current
            : published;

        // Authority publication and envelope key derivation happen after the install transactions
        // commit and before readiness. Publishing earlier would leave the process asserting a
        // generation a rolled-back transaction never recorded (§10.12).
        //
        // Both are optional resolutions, like the Weave availability flag above: a narrow container
        // that installs the schema without composing the authority boundary is a legitimate caller,
        // and a GetRequiredService here would turn that into a startup failure.
        CovenantRuntimeGenerationProvider? runtime =
            scope.ServiceProvider.GetService<CovenantRuntimeGenerationProvider>();

        CovenantEnvelopeMasterKeyProvider? keyProvider =
            scope.ServiceProvider.GetService<CovenantEnvelopeMasterKeyProvider>();

        IHostProcessToolsRuntimePolicy? hostToolsPolicy =
            scope.ServiceProvider.GetService<IHostProcessToolsRuntimePolicy>();

        if (runtime is not null && keyProvider is not null && hostToolsPolicy is not null)
        {
            // The answer is deliberately discarded here. An ordinary start with no operator authority
            // is a degraded installation rather than a failed one, and every consumer already reads an
            // unpublished authority as "none". Pre-readiness transition recovery reads the same answer
            // and refuses on it, because there the authority is what a handler is about to spend.
            _ = await CovenantAuthorityStartupReconciler.ReconcileAsync(
                installConnection,
                runtime,
                keyProvider,
                published,
                hostToolsPolicy,
                masterApiKey,
                cancellationToken).ConfigureAwait(false);
        }

        availability?.SetAvailable(false);
    }

    private sealed record DeferredHostProcessToolsDecision(
        HostProcessToolsRuntimePolicy ProcessPolicy,
        HostProcessToolsStartupDecision Decision);

    /// <summary>
    /// Requires the converged database authority singleton to name the installation authenticated by
    /// the active V2 envelope before any Covenant or readiness publication is allowed.
    /// </summary>
    internal static async Task VerifyExpectedInstallationIdentityAsync(
        SqliteConnection installConnection,
        Guid? expectedInstallationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installConnection);

        if (expectedInstallationId is null)
        {
            return;
        }

        if (expectedInstallationId == Guid.Empty)
        {
            throw InstallationIdentityUnavailable();
        }

        long? stateKey = null;

        string? storedIdentity = null;

        int rowCount = 0;

        try
        {
            await using SqliteCommand command = installConnection.CreateCommand();

            command.CommandText =
                "SELECT StateKey, InstallationIdentity FROM covenant_authority_state ORDER BY StateKey LIMIT 2;";

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowCount++;

                if (rowCount == 1)
                {
                    stateKey = reader.IsDBNull(0) ? null : reader.GetInt64(0);

                    storedIdentity = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidCastException
                or InvalidOperationException
                or OverflowException)
        {
            throw InstallationIdentityUnavailable(exception);
        }

        Guid parsed = Guid.Empty;

        bool canonical = storedIdentity is not null
            && Guid.TryParseExact(storedIdentity, "D", out parsed)
            && parsed != Guid.Empty
            && string.Equals(
                parsed.ToString("D").ToUpperInvariant(),
                storedIdentity,
                StringComparison.Ordinal);

        if (rowCount != 1
            || stateKey != 1
            || !canonical
            || parsed != expectedInstallationId.Value)
        {
            throw InstallationIdentityUnavailable();
        }
    }

    /// <summary>
    /// Whether a recovery read failed because the catalog could not be read right now, rather than
    /// because what it read disagrees: an <see cref="IOException"/>, or SQLite I/O error, busy, or
    /// locked, anywhere in the exception chain. That includes the SQLite failure
    /// <see cref="VerifyExpectedInstallationIdentityAsync"/> wraps in its unavailable exception,
    /// which is the only way an I/O failure reaches a caller of the identity check.
    /// </summary>
    /// <remarks>
    /// Pre-bootstrap transition recovery reads the installation identity on one recovery connection
    /// from more than one arm. One predicate gives the same failure on that connection the same
    /// answer in every arm that classifies it. Corruption, a file that is not a database, and a file
    /// that cannot be opened are not outages; neither is a cancellation.
    /// </remarks>
    internal static bool IsCatalogOutage(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException:
                    return false;

                case IOException:
                    return true;

                case SqliteException sqlite:
                    // SQLITE_BUSY, SQLITE_LOCKED, SQLITE_IOERR.
                    return sqlite.SqliteErrorCode is 5 or 6 or 10;
            }
        }

        return false;
    }

    private static GrimoireDatabaseUnavailableException InstallationIdentityUnavailable(
        Exception? innerException = null)
    {
        const string message =
            "The authenticated installation-reset identity does not match the Grimoire authority row. "
                + "See logs for recovery steps.";

        return innerException is null
            ? new GrimoireDatabaseUnavailableException(message)
            : new GrimoireDatabaseUnavailableException(message, innerException);
    }

    /// <summary>
    /// Resolves any unfinished protected-erasure work and any active schema-repair journal, before
    /// readiness.
    /// </summary>
    /// <remarks>
    /// Both passes run on the install connection, under the caller's already-held installation lock,
    /// and after the tiers have converged. Deleting a file is an effect SQLite cannot roll back, and
    /// a half-changed catalog is one no consumer should open against; readiness is the signal every
    /// pool, worker, and endpoint waits on, so this is the last honest place to resolve either.
    ///
    /// <para>Skipped entirely without an installation lock. A CLI running beside a live host does not
    /// own the installation, and a second process adopting the host's unfinished erasure work would be
    /// two deleters for one file (§10.17).</para>
    ///
    /// <para>A blocked local-erasure pass and a successfully adopted, later kept-closed repair are
    /// logged rather than thrown. An erasure or schema-journal read/decode/adoption failure instead
    /// refuses readiness: after the readiness boundary freezes adoption, no later component could
    /// reconstruct the missing owner safely.</para>
    /// </remarks>
    /// <summary>What the pre-readiness protected pass adopted, and the gate it adopted into.</summary>
    /// <remarks>
    /// The owner travels back rather than staying inside the pass, because resuming it has to happen
    /// after the install connection is physically closed: the handler closes the Grimoire and waits
    /// for every enrolled handle, and this bootstrap is holding one.
    /// </remarks>
    private sealed record ProtectedMaintenanceRecovery(
        CovenantOperationGate? Gate,
        CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner? AdoptedErasureOwner);

    private static async Task<ProtectedMaintenanceRecovery> RecoverProtectedMaintenanceAsync(
        SqliteConnection installConnection,
        IServiceScopeFactory scopeFactory,
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        string masterApiKey,
        CancellationToken cancellationToken)
    {
        if (heldInstallationLock is null)
        {
            return new ProtectedMaintenanceRecovery(Gate: null, AdoptedErasureOwner: null);
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        CovenantOperationGate? gate = scope.ServiceProvider.GetService<CovenantOperationGate>();

        CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner? adoptedErasureOwner = null;

        CovenantSqliteConnectionInitializer initializer = CovenantSqliteConnectionInitializer.Instance;

        initializer.EnsureAuthorizationFunctions(installConnection);

        CovenantSchemaRepairStartupRecovery? repair = null;

        CovenantSchemaRepairStartupRecoveryPreparation? repairPreparation = null;

        if (gate is not null)
        {
            Result<CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner?> erasureOwner = await new
                CovenantErasureStartupRecoveryOwnerAdopter(gate)
                .AdoptBeforeReadinessAsync(installConnection, cancellationToken)
                .ConfigureAwait(false);

            if (erasureOwner.IsFailure)
            {
                throw ProtectedRecoveryUnavailable();
            }

            adoptedErasureOwner = erasureOwner.Value;

            repair = new CovenantSchemaRepairStartupRecovery(
                gate,
                new CovenantSchemaRepairExecutor(
                    scope.ServiceProvider.GetRequiredService<GrimoireSchemaManifestInspector>(),
                    scope.ServiceProvider.GetRequiredService<GrimoireSchemaInstaller>(),
                    BuildInitializationContext(heldInstallationLock, grimoireDirectory, masterApiKey),
                    GrimoireEmbeddingDimensionResolver.Resolve(scope.ServiceProvider)),
                initializer,
                TimeProvider.System);

            Result<CovenantSchemaRepairStartupRecoveryPreparation> prepared = await repair
                .PrepareBeforeEffectsAsync(
                    heldInstallationLock,
                    grimoireDirectory,
                    installConnection,
                    cancellationToken)
                .ConfigureAwait(false);

            if (prepared.IsFailure)
            {
                throw ProtectedRecoveryUnavailable();
            }

            repairPreparation = prepared.Value;
        }

        CovenantLocalErasureStartupRecovery localErasure = new(
            new ManagedFileErasureStateMachine(
                initializer,
                new ManagedFileCapabilityOpener(),
                new ManagedFileOwnershipVerifier(),
                TimeProvider.System));

        Result<CovenantLocalErasureStartupRecoveryOutcome> erasure = await localErasure
            .RecoverBeforeReadinessAsync(
                heldInstallationLock,
                grimoireDirectory,
                installConnection,
                cancellationToken)
            .ConfigureAwait(false);

        if (erasure.IsFailure || erasure.Value is CovenantLocalErasureStartupRecoveryOutcome.Blocked)
        {
            Log.Warning(
                "Unfinished Covenant managed-file erasure work could not be resolved before readiness. "
                + "The affected files, their producer rows, and their labels are untouched.");
        }
        else if (erasure.Value is CovenantLocalErasureStartupRecoveryOutcome.ManualEvidenceReady)
        {
            Log.Warning(
                "A Covenant managed-file erasure ended as a manual blocker: the file did not match the "
                + "ownership Arcanum recorded, so it was left in place with its label intact.");
        }

        if (repair is not null && repairPreparation is not null)
        {
            Result<CovenantSchemaRepairStartupRecoveryOutcome> recovered = await repair
                .RecoverPreparedAsync(
                    heldInstallationLock,
                    grimoireDirectory,
                    installConnection,
                    repairPreparation,
                    cancellationToken)
                .ConfigureAwait(false);

            if (recovered.IsFailure)
            {
                throw ProtectedRecoveryUnavailable();
            }

            if (recovered.Value is CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed)
            {
                Log.Warning(
                    "An interrupted Covenant schema repair could not be completed, so Covenant admission "
                    + "stays closed and its journal remains active for the next start.");
            }
        }

        return new ProtectedMaintenanceRecovery(gate, adoptedErasureOwner);
    }

    /// <summary>
    /// Finishes an adopted launch-gap operation, or refuses readiness.
    /// </summary>
    /// <remarks>
    /// Skipped without an installation lock, exactly as the protected-maintenance recovery above is: a
    /// CLI beside a live host does not own the installation, and a second process resuming a
    /// transition would be two owners for one closed period. Once a launch has been adopted, however,
    /// the composition must carry the exact offline dispatch: generic reconciliation deliberately
    /// excludes owner-bound rows, so readiness cannot be published over a launch nobody else can claim.
    /// </remarks>
    private static async Task ResumeLaunchGapAsync(
        IServiceScopeFactory scopeFactory,
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        CovenantErasureStartupRecoveryOwnerAdopter.AdoptedOwner? adopted,
        CancellationToken cancellationToken)
    {
        if (heldInstallationLock is null || adopted is null)
        {
            return;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        if (scope.ServiceProvider.GetService<IGrimoireOfflineTransitionHandlerDispatch>()
            is not { } dispatch)
        {
            throw ProtectedRecoveryUnavailable();
        }

        Result resumed = await CovenantOfflineTransitionLaunchGapResumption
            .ResumeBeforeReadinessAsync(
                dispatch,
                heldInstallationLock,
                grimoireDirectory,
                adopted,
                cancellationToken)
            .ConfigureAwait(false);

        if (resumed.IsFailure)
        {
            throw ProtectedRecoveryUnavailable();
        }
    }

    private static GrimoireDatabaseUnavailableException ProtectedRecoveryUnavailable() =>
        new(
            "Protected Covenant startup recovery could not prove one durable owner. "
                + "See logs for recovery steps.");

    /// <summary>
    /// Resolves the configured embedding width, falling back to the shipped default when the options
    /// pipeline is not composed in this container.
    /// </summary>
    /// <summary>
    /// Builds the installation-local facts every tier initializer runs against.
    /// </summary>
    /// <remarks>
    /// Both arms compute the same fingerprint from the same master key. The lock is what separates
    /// them: with it the caller is the sole owner of this installation and authority is prepared under
    /// it, and without it the caller is a CLI coexisting with a live host that already prepared
    /// authority, so the context is built directly rather than under an ownership claim nobody holds.
    /// </remarks>
    private static GrimoireSchemaInitializationContext BuildInitializationContext(
        ArcanumMaintenanceLock? heldInstallationLock,
        string grimoireDirectory,
        string masterApiKey)
    {
        DateTimeOffset installedAtUtc = DateTimeOffset.UtcNow;

        if (heldInstallationLock is null)
        {
            return CovenantAuthorityBootstrapper.PrepareWithoutInstallationLock(
                masterApiKey,
                installedAtUtc);
        }

        return new CovenantAuthorityBootstrapper().PrepareUnderInstallationLock(
            heldInstallationLock,
            grimoireDirectory,
            masterApiKey,
            installedAtUtc);
    }

    private static async Task<string> ResolveGrimoirePassphraseAsync(
        ISecretStore secretStore,
        string apiKey,
        string dbPath,
        CancellationToken cancellationToken)
    {
        if (GrimoireKdfSidecarFile.Exists(dbPath))
        {
            GrimoireKdfSidecar sidecar = ReadSidecarOrFailClosed(dbPath);

            string secret = await ResolveActiveSecretAsync(secretStore).ConfigureAwait(false);

            return DeriveWithSidecar(secret, sidecar);
        }

        if (File.Exists(dbPath))
        {
            // A pending salt means a previous KDF upgrade was interrupted. It is written durably
            // before PRAGMA rekey, so it is the only copy of the salt if the rekey committed and
            // the promotion did not land; if the rekey never committed the database is still
            // legacy and the pending salt simply does not open it.
            string? recovered = await TryRecoverPendingKdfUpgradeAsync(secretStore, dbPath, cancellationToken).ConfigureAwait(false);

            if (recovered is not null)
            {
                return recovered;
            }

            return await UpgradeLegacyDatabaseAsync(secretStore, apiKey, dbPath, cancellationToken).ConfigureAwait(false);
        }

        return await CreateNewDatabaseSecretAsync(secretStore, dbPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the KDF sidecar, turning a damaged one into a Grimoire-unavailable failure that names
    /// the file the operator has to restore.
    /// </summary>
    /// <remarks>
    /// The sidecar holds the only copy of the salt, and truncation or byte damage is what it
    /// actually suffers. <c>GrimoireKdfSidecarFile.Read</c> already normalizes that into
    /// <see cref="InvalidDataException"/>, but letting it escape raw put the startup failure into
    /// <c>CliFailureMapper</c>'s default arm, which prints "An unexpected CLI error occurred." and
    /// names nothing — on the one path that cannot continue without this file. This mirrors what
    /// <see cref="ResolveActiveSecretAsync"/> does for a missing or undecryptable grimoire-key.dat.
    /// </remarks>
    private static GrimoireKdfSidecar ReadSidecarOrFailClosed(string dbPath)
    {
        try
        {
            return GrimoireKdfSidecarFile.Read(dbPath);
        }
        catch (Exception ex) when (
            ex is InvalidDataException
                or NotSupportedException
                or FileNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {
            Log.Fatal(
                ex,
                "The Grimoire KDF sidecar at {SidecarPath} exists but cannot be read ({Message}). "
                + "It holds the only copy of the salt the database was keyed with, so the Grimoire cannot be opened without it. "
                + "Restore arcanum.db.kdf from backup alongside the arcanum.db it belongs to, "
                + "run 'arcanum backup restore' against a verified .arcbackup generation, "
                + "or reset the Grimoire (delete arcanum.db and arcanum.db.kdf under ~/.config/arcanum/) to start fresh — session data is otherwise unrecoverable.",
                GrimoireKdfSidecarFile.GetSidecarPath(dbPath),
                ex.Message);

            throw new GrimoireDatabaseUnavailableException(
                "The Grimoire KDF sidecar (arcanum.db.kdf) exists but cannot be read. See logs for recovery steps.",
                ex);
        }
    }

    private static async Task<string?> TryRecoverPendingKdfUpgradeAsync(
        ISecretStore secretStore,
        string dbPath,
        CancellationToken cancellationToken)
    {
        if (!GrimoireKdfSidecarFile.PendingExists(dbPath))
        {
            return null;
        }

        GrimoireKdfSidecar pending;

        try
        {
            pending = GrimoireKdfSidecarFile.ReadPending(dbPath);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "A pending Grimoire KDF salt exists at {PendingPath} but could not be read; falling back to the legacy upgrade path.",
                GrimoireKdfSidecarFile.GetPendingSidecarPath(dbPath));

            return null;
        }

        string? secret = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        string candidate = DeriveWithSidecar(secret, pending);

        if (!await CanOpenDatabaseAsync(dbPath, candidate, cancellationToken).ConfigureAwait(false))
        {
            // The interrupted rekey never committed; the database is still legacy and the pending
            // salt is stale. UpgradeLegacyDatabaseAsync re-drives the upgrade with a fresh salt.
            return null;
        }

        GrimoireKdfSidecarFile.PromotePending(dbPath);

        Log.Warning(
            "A previously interrupted Grimoire KDF upgrade was completed: the pending PBKDF2 salt opened {DbPath} and was promoted to the committed sidecar.",
            dbPath);

        return candidate;
    }

    private static async Task<string> UpgradeLegacyDatabaseAsync(
        ISecretStore secretStore,
        string apiKey,
        string dbPath,
        CancellationToken cancellationToken)
    {
        string? dedicatedSecret = await secretStore.GetGrimoireEncryptionSecretAsync().ConfigureAwait(false);

        if (!string.IsNullOrEmpty(dedicatedSecret))
        {
            string legacyPassphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(dedicatedSecret);

            if (await CanOpenDatabaseAsync(dbPath, legacyPassphrase, cancellationToken).ConfigureAwait(false))
            {
                return await RekeyToPbkdf2Async(secretStore, dedicatedSecret, legacyPassphrase, dbPath, cancellationToken).ConfigureAwait(false);
            }
        }

        string legacyApiPassphrase = GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy(apiKey);

        if (await CanOpenDatabaseAsync(dbPath, legacyApiPassphrase, cancellationToken).ConfigureAwait(false))
        {
            string newDedicatedSecret = await GenerateAndSaveDedicatedSecretAsync(secretStore).ConfigureAwait(false);

            return await RekeyToPbkdf2Async(secretStore, newDedicatedSecret, legacyApiPassphrase, dbPath, cancellationToken).ConfigureAwait(false);
        }

        Log.Fatal(
            "Grimoire database at {DbPath} exists but could not be opened with either the legacy dedicated secret or the master API key.",
            dbPath);

        throw new GrimoireDatabaseUnavailableException(
            "Arcanum Grimoire database key verification failed. See logs for recovery steps.");
    }

    private static async Task<string> RekeyToPbkdf2Async(
        ISecretStore secretStore,
        string secret,
        string oldPassphrase,
        string dbPath,
        CancellationToken cancellationToken)
    {
        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        string newPassphrase = DeriveWithSidecar(secret, sidecar);

        // PRAGMA rekey is irreversible, so the salt must already be on durable storage before it
        // runs — otherwise a crash, a full disk, or a read-only config directory between the rekey
        // and the sidecar write destroys the only copy of the salt and bricks the database.
        // The pending file is promoted to the committed sidecar once the rekey has committed;
        // ResolveGrimoirePassphraseAsync recovers from either side of that window on next start.
        GrimoireKdfSidecarFile.WritePending(dbPath, sidecar);

        await using (SqliteConnection rekeyConnection = new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Password = oldPassphrase,
        }.ToString()))
        {
            await rekeyConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand rekeyCommand = rekeyConnection.CreateCommand();

            rekeyCommand.CommandText = SqlitePragmaStatementFactory.Rekey(newPassphrase);

            await rekeyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await rekeyConnection.CloseAsync().ConfigureAwait(false);
        }

        GrimoireKdfSidecarFile.PromotePending(dbPath);

        Log.Information("Grimoire database upgraded to PBKDF2 KDF (version 2).");

        return newPassphrase;
    }

    private static async Task<string> CreateNewDatabaseSecretAsync(
        ISecretStore secretStore,
        string dbPath,
        CancellationToken cancellationToken)
    {
        string newSecret = await GenerateAndSaveDedicatedSecretAsync(secretStore).ConfigureAwait(false);

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(dbPath, sidecar);

        return DeriveWithSidecar(newSecret, sidecar);
    }

    private static async Task<string> ResolveActiveSecretAsync(
        ISecretStore secretStore)
    {
        SecretStoreReadResult dedicated = await ReadGrimoireSecretResultAsync(secretStore).ConfigureAwait(false);

        if (dedicated.Status == SecretStoreReadStatus.Ok
            && !string.IsNullOrEmpty(dedicated.Value))
        {
            return dedicated.Value;
        }

        if (dedicated.Status == SecretStoreReadStatus.Corrupted)
        {
            // Sidecar-backed databases are keyed from the dedicated secret. Falling back to the
            // API key here yields a wrong passphrase and a confusing "key verification failed"
            // FailFast — surface the real cause (missing/corrupt Data Protection key material).
            Log.Fatal(
                "Grimoire encryption secret store is present but cannot be decrypted ({Message}). "
                + "The Data Protection key that sealed grimoire-key.dat is missing from ~/.config/arcanum/keys/. "
                + "Restore the matching key-*.xml from backup, or reset the Grimoire (delete arcanum.db, arcanum.db.kdf under ~/.config/arcanum/, and grimoire-key.dat under the Application Support arcanum folder) to start fresh — session data is otherwise unrecoverable.",
                dedicated.Message ?? "unknown");

            throw new GrimoireDatabaseUnavailableException(
                "Arcanum Grimoire encryption secret cannot be decrypted (missing Data Protection key). See logs for recovery steps.");
        }

        if (dedicated.Status == SecretStoreReadStatus.Unreadable)
        {
            // Present but not readable at all: its content is unknown, so neither the API-key fallback
            // nor the destructive reset guidance applies. The fix is the file's permissions.
            Log.Fatal(
                "Grimoire encryption secret store is present but could not be read ({Message}). "
                + "It was left unchanged; make grimoire-key.dat an owner-only regular file and restart.",
                dedicated.Message ?? "unknown");

            throw new GrimoireDatabaseUnavailableException(
                "Arcanum Grimoire encryption secret could not be read. See logs for recovery steps.");
        }

        // Same reasoning for an absent secret: every sidecar-backed database was keyed from the
        // dedicated secret (CreateNewDatabaseSecretAsync / RekeyToPbkdf2Async / backup restore),
        // never from the master API key, so PBKDF2(apiKey, salt) can only produce the misleading
        // "key verification failed". Point the operator at the file that is actually missing.
        Log.Fatal(
            "Grimoire encryption secret grimoire-key.dat is missing while a KDF sidecar exists. "
            + "The database was keyed from the dedicated Grimoire secret, not the master API key, so it cannot be opened without it. "
            + "Restore grimoire-key.dat (it lives under the Application Support arcanum folder on macOS/Windows, not beside arcanum.db) together with the matching key-*.xml from ~/.config/arcanum/keys/, "
            + "run 'arcanum backup restore' against a verified .arcbackup generation, or reset the Grimoire (delete arcanum.db and arcanum.db.kdf) to start fresh — session data is otherwise unrecoverable.");

        throw new GrimoireDatabaseUnavailableException(
            "Arcanum Grimoire encryption secret (grimoire-key.dat) is missing. See logs for recovery steps.");
    }

    private static Task<SecretStoreReadResult> ReadGrimoireSecretResultAsync(
        ISecretStore secretStore) =>
        secretStore.GetGrimoireEncryptionSecretReadResultAsync();

    private static async Task<string> GenerateAndSaveDedicatedSecretAsync(ISecretStore secretStore)
    {
        byte[] secretBytes = new byte[32];

        RandomNumberGenerator.Fill(secretBytes);

        string newSecret = Convert.ToBase64String(secretBytes);

        CryptographicOperations.ZeroMemory(secretBytes);

        await secretStore.SaveGrimoireEncryptionSecretAsync(newSecret).ConfigureAwait(false);

        return newSecret;
    }

    private static string DeriveWithSidecar(string secret, GrimoireKdfSidecar sidecar)
    {
        byte[] salt = sidecar.GetSaltBytes();

        try
        {
            return sidecar.Version switch
            {
                GrimoireKeyDerivation.KdfVersion2 => GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt),

                _ => throw new NotSupportedException($"Grimoire KDF version {sidecar.Version} is not supported."),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private const int SqliteCorrupt = 11;

    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// SQLCipher answers a wrong key as "file is not a database" (26), and a damaged file as corrupt (11).
    /// Those are the only failures that are a verdict on the key or the file; busy, locked, I/O and
    /// cannot-open mean the probe never got that far.
    /// </summary>
    internal static bool IndicatesKeyMismatchOrCorruption(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: SqliteNotADatabase or SqliteCorrupt };

    internal static async Task<bool> CanOpenDatabaseAsync(
        string dbPath,
        string passphrase,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(dbPath))
        {
            return false;
        }

        try
        {
            await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Password = passphrase,
            }.ToString());

            await probe.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand cmd = probe.CreateCommand();

            cmd.CommandText = "SELECT 1;";

            _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            await probe.CloseAsync().ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IndicatesKeyMismatchOrCorruption(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            // Not a verdict on this candidate key: the next candidate would fail the same way, and "none of
            // the keys opened it" would be false.
            throw new GrimoireDatabaseUnavailableException(
                "Arcanum could not read the Grimoire database to verify its key; the database may be busy or the disk failing. See logs.",
                ex);
        }
    }
}

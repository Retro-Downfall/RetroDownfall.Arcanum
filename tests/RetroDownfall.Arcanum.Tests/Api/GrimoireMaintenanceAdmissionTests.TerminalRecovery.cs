using System.Buffers.Binary;

using System.Data;

using System.Security.Cryptography;

using System.Text;

using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GrimoireMaintenanceAdmissionTests
{
    [SkippableTheory]
    [MemberData(nameof(TerminalSuffixCases))]
    public async Task Terminal_suffix_restart_converges_before_second_host_readiness(
        GrimoireTransitionEntryPoint entryPoint,
        byte boundaryCode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(
            entryPoint,
            () => AssertTerminalRestartAsync(
                entryPoint,
                (GrimoireOfflineTransitionTerminalSuffixBoundary)boundaryCode));
    }

    [SkippableFact]
    public async Task Exact_terminal_candidate_takes_the_terminal_route_before_authority_load()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            () => AssertTerminalRestartAsync(
                GrimoireTransitionEntryPoint.DirectCovenantReset,
                GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized));
    }

    public static TheoryData<GrimoireTransitionEntryPoint, byte> TerminalSuffixCases
    {
        get
        {
            TheoryData<GrimoireTransitionEntryPoint, byte> cases = new();

            foreach (GrimoireTransitionEntryPoint entryPoint in Enum.GetValues<GrimoireTransitionEntryPoint>())
            {
                foreach (GrimoireOfflineTransitionTerminalSuffixBoundary boundary in
                    Enum.GetValues<GrimoireOfflineTransitionTerminalSuffixBoundary>())
                {
                    cases.Add(entryPoint, (byte)boundary);
                }
            }

            return cases;
        }
    }

    [SkippableTheory]
    [InlineData(GrimoireTransitionEntryPoint.DirectCovenantReset)]
    [InlineData(GrimoireTransitionEntryPoint.StandaloneFactoryReset)]
    public async Task Terminal_suffix_restart_preserves_a_proven_pre_effect_rollback(
        GrimoireTransitionEntryPoint entryPoint)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(
            entryPoint,
            () => AssertTerminalRestartAsync(
                entryPoint,
                GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized,
                rollback: true));
    }

    [SkippableTheory]
    [InlineData("missing-row")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-policy")]
    [InlineData("wrong-version")]
    [InlineData("wrong-reference")]
    [InlineData("payload-whitespace")]
    [InlineData("revision-below-floor")]
    [InlineData("abandoned")]
    [InlineData("crossed-terminal-code")]
    [InlineData("lingering-owner")]
    [InlineData("attempt-zero")]
    [InlineData("nonterminal-with-completed-at")]
    [InlineData("nonterminal-with-terminal-code")]
    [InlineData("running-with-terminal-code")]
    [InlineData("wrong-catalog")]
    [InlineData("recorded-winner-drift")]
    [InlineData("noncanonical-completed-at")]
    [InlineData("invalid-terminal-chronology")]
    [InlineData("corrupt-journal")]
    public async Task Terminal_suffix_candidate_refuses_without_fallback(string mutation)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            () => AssertTerminalRefusalAsync(mutation));
    }

    [SkippableFact]
    public async Task Retained_finalizer_reconciliation_recovers_before_second_host_readiness()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            AssertRetainedFinalizerRecoveryAsync);
    }

    [SkippableTheory]
    [InlineData("anchor:closed-written")]
    [InlineData("anchor:closed-readback")]
    [InlineData("file:retiring-moved")]
    public async Task Closed_anchor_retirement_failure_is_finished_without_terminal_fallback(
        string failingStep)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await AssertEntryPointContextAsync(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            () => AssertClosedAnchorRetryAsync(failingStep));
    }

    private static async Task AssertTerminalRestartAsync(
        GrimoireTransitionEntryPoint entryPoint,
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary,
        bool rollback = false)
    {
        OneShotTerminalSuffixFault fault = new(boundary);

        OneShotOutcomeFault? outcomeFault = rollback
            ? new OneShotOutcomeFault(
                CovenantErasureFaultBoundary.BeforePhaseBegin,
                CovenantResetPhase.CanonicalApplied)
            : null;

        CovenantErasureFaultSeam? outcomeSeam = outcomeFault is null
            ? null
            : outcomeFault.RaiseAsync;

        await using RestartableArcanumProfileFixture profile = new();

        await using GrimoireMaintenanceAdmissionHarness first = await GrimoireMaintenanceAdmissionHarness
            .StartAsync(
                profile,
                faultSeam: outcomeSeam,
                terminalSuffixFaultSeam: fault.RaiseAsync);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));

        await SeedOutcomeDatasetAsync(first, timeout.Token);

        OutcomeDatasetSnapshot before = await CaptureOutcomeDatasetAsync(first, timeout.Token);

        DataRetentionPlan plan = await first.PlanAsync(entryPoint);

        Guid requestedOperationId = Guid.NewGuid();

        using HttpRequestMessage apply = first.CreateApplyRequest(
            entryPoint,
            plan.PlanId,
            requestedOperationId);

        using HttpResponseMessage response = await first.Client.SendAsync(apply, timeout.Token);

        Assert.True(fault.Fired);

        Assert.Equal(rollback, outcomeFault?.Fired ?? false);

        Assert.False(response.IsSuccessStatusCode);

        RecordedMaintenanceTransition terminalTransition = first.Operations.Transitions.Last(transition =>
            transition.State == (rollback
                ? LongRunningOperationState.Failed
                : LongRunningOperationState.Completed));

        LongRunningOperation terminal = Assert.IsType<LongRunningOperation>(first.Operations.Reads
            .Last(read => read.OperationId == terminalTransition.OperationId
                && read.Operation?.State == terminalTransition.State)
            .Operation);

        Assert.Equal(
            rollback
                ? GrimoireOfflineTransitionDatabaseReconciler.PreEffectFailureCode
                : null,
            terminal.TerminalErrorCode);

        Assert.Equal(1, terminal.AttemptCount);

        RecordedMaintenancePublication publication = first.Journal.Publications.Last();

        string[] firstHostSteps = first.Journal.Steps.ToArray();

        Assert.Equal(
            rollback
                ? GrimoireOfflineTransitionTerminalIntent.RollbackAndReopen
                : GrimoireOfflineTransitionTerminalIntent.CommitAndReopen,
            publication.Payload.Lifecycle.TerminalIntent);

        Assert.Null(publication.Payload.InFlightPhase);

        Assert.Equal(
            rollback
                ? CovenantResetPhaseMachine.First
                : CovenantResetPhase.SidecarsVerified,
            publication.Payload.LastCompletedPhase);

        byte[] journalKeyFingerprint = ObservingMaintenanceJournal.JournalKeyFingerprint(
            profile.CredentialStore,
            publication.Raw.Location.ProfileNamespace);

        string masterApiKey = Assert.IsType<string>(await first.Factory.Services
            .GetRequiredService<ISecretStore>().GetApiKeyAsync());

        Guid installationId = publication.Raw.Envelope.InstallationId;

        Assert.Equal(terminal.Id, publication.Payload.Binding.OperationId);

        CovenantDigest expectedWinner = IndependentWinnerDigest(
            publication.Payload.Binding,
            terminal);

        RecordedMaintenancePublication[] terminalPublications = first.Journal.Publications
            .Where(published => published.Payload.Lifecycle.State
                is GrimoireOfflineTransitionState.DatabaseReconciliationPending
                    or GrimoireOfflineTransitionState.RetirementPending)
            .ToArray();

        Assert.Equal(
            ExpectedFirstHostLogicalPrefix(boundary),
            terminalPublications.Select(LogicalSuffixStep));

        for (int index = 0; index < terminalPublications.Length; index++)
        {
            AssertTerminalBoundaryPublication(
                terminalPublications[index],
                (GrimoireOfflineTransitionTerminalSuffixBoundary)(index + 1),
                expectedWinner,
                terminal.Id,
                installationId);

            if (index > 0)
            {
                Assert.Equal(
                    terminalPublications[index - 1].Raw.Envelope.Revision + 1,
                    terminalPublications[index].Raw.Envelope.Revision);
            }
        }

        if (boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized)
        {
            Assert.Null(publication.Payload.Lifecycle.ReconciliationEvidence!
                .DatabaseTerminalWinnerDigest);
        }
        else
        {
            Assert.Equal(
                expectedWinner,
                publication.Payload.Lifecycle.ReconciliationEvidence!
                    .DatabaseTerminalWinnerDigest);
        }

        if (boundary is not GrimoireOfflineTransitionTerminalSuffixBoundary.Retired)
        {
            Assert.True(File.Exists(publication.Raw.Location.JournalPath));

            Assert.Equal(ExpectedState(boundary), publication.Payload.Lifecycle.State);

            Assert.Equal(
                ExpectedStep(boundary),
                publication.Payload.Lifecycle.ReconciliationEvidence?.Step);
        }
        else
        {
            Assert.False(File.Exists(publication.Raw.Location.JournalPath));
        }

        await first.DisposeAsync();

        MaintenanceAdoptionObservation adoption = new();

        RecoveryHostStartupObservation startup = new();

        if (boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.Retired)
        {
            startup.ReleaseTerminalSuffix();
        }

        Task<GrimoireMaintenanceAdmissionHarness> starting = Task.Factory.StartNew(
            () => GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile, adoption, startup),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        GrimoireMaintenanceAdmissionHarness? second = null;

        try
        {
            if (boundary is not GrimoireOfflineTransitionTerminalSuffixBoundary.Retired)
            {
                Assert.Equal(
                    GrimoireOfflineTransitionTerminalSuffixBoundary.RetirementPending,
                    await startup.TerminalSuffixReached.WaitAsync(timeout.Token));

                Assert.False(startup.Readiness.IsReady);

                Assert.False(startup.FinalHostedServiceStarted.IsCompleted);

                Assert.Equal(0, adoption.Calls);

                Assert.Null(startup.AuthorityLoad);

                Assert.Null(startup.AuthorityConsume);

                Assert.Equal(0, Volatile.Read(ref startup.DispatchCalls));

                Assert.Equal(0, startup.DisclosureWriterReopenCalls);

                Assert.NotNull(startup.ActualHostToolsPolicy);

                Assert.False(startup.ActualHostToolsPolicy!.IsPublished);

                Assert.False(startup.ActualHostToolsPolicy.CovenantPermitted);

                Assert.Empty(startup.Operations.Transitions);

                Assert.Equal(default, startup.OrdinaryMutations.Snapshot);

                Assert.Empty(startup.Journal.ParentResolutions);

                Assert.Empty(startup.Admission.Effects);

                Assert.Equal(0, startup.Chat?.Calls ?? 0);

                Assert.Equal(0, startup.Weave?.Calls ?? 0);

                Assert.Empty(startup.Workers?.Scopes ?? []);

                startup.ReleaseTerminalSuffix();
            }

            second = await starting.WaitAsync(timeout.Token);
        }
        finally
        {
            startup.ReleaseTerminalSuffix();

            if (second is null)
            {
                try
                {
                    GrimoireMaintenanceAdmissionHarness orphan = await starting.WaitAsync(timeout.Token);

                    await orphan.DisposeAsync();
                }
                catch
                {
                    // The startup helper owns and disposes its harness on every failed start.
                }
            }
        }

        await using (second)
        {
            Assert.Equal(0, adoption.Calls);

            Assert.Null(startup.AuthorityLoad);

            Assert.Null(startup.AuthorityConsume);

            Assert.Equal(0, Volatile.Read(ref startup.DispatchCalls));

            Assert.Equal(
                boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.Retired ? 0 : 1,
                Volatile.Read(ref startup.TerminalSuffixCalls));

            Assert.Equal(
                boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.Retired ? 0 : 1,
                startup.DisclosureWriterReopenCalls);

            Assert.Equal(
                ExpectedRecoveredLogicalSuffix(boundary),
                startup.Journal.Publications.Select(LogicalSuffixStep));

            RecordedMaintenancePublication[] recoveredPublications = startup.Journal.Publications
                .ToArray();

            for (int index = 0; index < recoveredPublications.Length; index++)
            {
                GrimoireOfflineTransitionTerminalSuffixBoundary recoveredBoundary =
                    (GrimoireOfflineTransitionTerminalSuffixBoundary)((byte)boundary + index + 1);

                AssertTerminalBoundaryPublication(
                    recoveredPublications[index],
                    recoveredBoundary,
                    expectedWinner,
                    terminal.Id,
                    installationId);

                Assert.Equal(
                    publication.Raw.Envelope.Revision + (ulong)index + 1,
                    recoveredPublications[index].Raw.Envelope.Revision);
            }

            Assert.Equal(
                ExpectedRetirementSteps,
                RetirementSteps(
                    boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.Retired
                        ? firstHostSteps
                        : startup.Journal.Steps));

            await using AsyncServiceScope inspection = second.Factory.Services.CreateAsyncScope();

            LongRunningOperation recovered = Assert.IsType<LongRunningOperation>(await inspection
                .ServiceProvider
                .GetRequiredService<ILongRunningOperationStore>()
                .GetAsync(terminal.Id, timeout.Token));

            AssertTerminalOperationUnchanged(terminal, recovered);

            Assert.False(File.Exists(publication.Raw.Location.JournalPath));

            Assert.True(startup.Readiness.IsReady);

            Assert.True(startup.FinalHostedServiceStarted.IsCompletedSuccessfully);

            Assert.Equal(startup.InitialOpenGeneration, second.Admission.CurrentGeneration);

            Assert.True(CryptographicOperations.FixedTimeEquals(
                journalKeyFingerprint,
                ObservingMaintenanceJournal.JournalKeyFingerprint(
                    profile.CredentialStore,
                    publication.Raw.Location.ProfileNamespace)),
                "the journal-key fingerprint changed");

            Assert.Equal(masterApiKey, await second.Factory.Services
                .GetRequiredService<ISecretStore>().GetApiKeyAsync());

            if (rollback)
            {
                await AssertTerminalRollbackCatalogAsync(
                    second,
                    before,
                    terminal,
                    timeout.Token,
                    entryPoint);
            }
            else
            {
                await AssertTerminalRecoveredCatalogAsync(
                    second,
                    before,
                    terminal,
                    installationId,
                    timeout.Token,
                    entryPoint);
            }

            await AssertNoParentReceiptAsync(
                second,
                publication,
                timeout.Token,
                entryPoint);

            await AssertOpenAdmissionAsync(
                second,
                startup.InitialOpenGeneration,
                timeout.Token,
                entryPoint);
        }

        if (!rollback
            && entryPoint is GrimoireTransitionEntryPoint.DirectCovenantReset
            && boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized)
        {
            await AssertRepeatedClosedStartupAsync(profile, timeout.Token);
        }
    }

    private static async Task AssertRepeatedClosedStartupAsync(
        RestartableArcanumProfileFixture profile,
        CancellationToken cancellationToken)
    {
        MaintenanceAdoptionObservation adoption = new();

        RecoveryHostStartupObservation startup = new();

        await using GrimoireMaintenanceAdmissionHarness repeated = await Task.Factory.StartNew(
            () => GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile, adoption, startup),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap().WaitAsync(cancellationToken);

        Assert.Equal(0, adoption.Calls);

        Assert.Null(startup.AuthorityLoad);

        Assert.Null(startup.AuthorityConsume);

        Assert.Equal(0, Volatile.Read(ref startup.DispatchCalls));

        Assert.Equal(0, Volatile.Read(ref startup.TerminalSuffixCalls));

        Assert.Equal(0, startup.DisclosureWriterReopenCalls);

        Assert.False(startup.TerminalSuffixReached.IsCompleted);

        Assert.True(startup.Readiness.IsReady);

        Assert.True(startup.FinalHostedServiceStarted.IsCompletedSuccessfully);

        Assert.Equal(startup.InitialOpenGeneration, repeated.Admission.CurrentGeneration);
    }

    private static async Task AssertRetainedFinalizerRecoveryAsync()
    {
        await using RestartableArcanumProfileFixture profile = new();

        await using GrimoireMaintenanceAdmissionHarness first = await GrimoireMaintenanceAdmissionHarness
            .StartAsync(profile, failCompletedOperationTransitions: true);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));

        await SeedOutcomeDatasetAsync(first, timeout.Token);

        DataRetentionPlan plan = await first.PlanAsync(GrimoireTransitionEntryPoint.DirectCovenantReset);

        using HttpRequestMessage apply = first.CreateApplyRequest(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            plan.PlanId,
            Guid.NewGuid());

        using HttpResponseMessage response = await first.Client.SendAsync(apply, timeout.Token);

        Assert.False(response.IsSuccessStatusCode);

        Assert.True(Volatile.Read(ref first.Operations.RefusedCompletedTransitions) > 0);

        RecordedMaintenanceTransition attention = first.Operations.Transitions.Last();

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, attention.State);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, attention.TerminalErrorCode);

        RecordedMaintenancePublication parked = first.Journal.Publications.Last();

        Assert.Equal(GrimoireOfflineTransitionState.KeepClosed, parked.Payload.Lifecycle.State);

        Assert.Equal(
            GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
            parked.Payload.Lifecycle.ReconciliationEvidence!.Step);

        Assert.Null(parked.Payload.Lifecycle.ReconciliationEvidence.DatabaseTerminalWinnerDigest);

        await first.DisposeAsync();

        MaintenanceAdoptionObservation adoption = new() { PauseAfterAcquisition = true };

        RecoveryHostStartupObservation startup = new();

        Task<GrimoireMaintenanceAdmissionHarness> starting = Task.Factory.StartNew(
            () => GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile, adoption, startup),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        GrimoireMaintenanceAdmissionHarness? second = null;

        try
        {
            Task reached = adoption.Checkpoint.WaitUntilReachedAsync();

            Assert.Same(reached, await Task.WhenAny(reached, starting).WaitAsync(timeout.Token));

            Assert.False(startup.Readiness.IsReady);

            Assert.False(startup.FinalHostedServiceStarted.IsCompleted);

            Assert.Equal(1, adoption.Calls);

            Assert.True(adoption.Result!.Acquired);

            Assert.True(startup.AuthorityLoad is { IsSuccess: true });

            Assert.True(startup.AuthorityConsume is { IsSuccess: true });

            Assert.Equal(1, Volatile.Read(ref startup.DispatchCalls));

            Assert.Empty(startup.Admission.Effects);

            Assert.Equal(default, startup.OrdinaryMutations.Snapshot);

            adoption.Checkpoint.Release();

            second = await starting.WaitAsync(timeout.Token);
        }
        finally
        {
            adoption.Checkpoint.Release();

            if (second is null)
            {
                try
                {
                    Task ownerBoundary = await Task.WhenAny(startup.HarnessCreated, starting);

                    if (ReferenceEquals(ownerBoundary, startup.HarnessCreated))
                    {
                        await (await startup.HarnessCreated).DisposeAsync();
                    }

                    _ = await starting;
                }
                catch
                {
                    // The owned factory is disposed and startup failure is observed.
                }
            }
        }

        await using (second)
        {
            Assert.Equal(1, Volatile.Read(ref startup.DispatchCalls));

            Assert.True(startup.Readiness.IsReady);

            Assert.True(startup.FinalHostedServiceStarted.IsCompletedSuccessfully);

            Assert.Equal(1, startup.DisclosureWriterReopenCalls);

            Assert.NotNull(startup.Journal.VerifiedCandidate);

            Assert.True(startup.Journal.PublishedExactCandidate);

            Assert.True(startup.Journal.PublishResult is { IsSuccess: true });

            Assert.Equal(1, startup.Journal.Steps.Count(step => step is "transition:verified"));

            Assert.Equal(1, startup.Journal.Steps.Count(step => step is "transition:published"));

            RecordedMaintenanceTransition completed = Assert.Single(startup.Operations.Transitions);

            Assert.Equal(LongRunningOperationState.Completed, completed.State);

            Assert.Equal(attention.OperationId, completed.OperationId);

            Assert.False(File.Exists(parked.Raw.Location.JournalPath));

            await AssertOpenAdmissionAsync(
                second,
                startup.InitialOpenGeneration + 1,
                timeout.Token,
                GrimoireTransitionEntryPoint.DirectCovenantReset);
        }
    }

    private static async Task AssertClosedAnchorRetryAsync(string failingStep)
    {
        OneShotTerminalSuffixFault fault = new(
            GrimoireOfflineTransitionTerminalSuffixBoundary.RetirementPending);

        await using RestartableArcanumProfileFixture profile = new();

        await using GrimoireMaintenanceAdmissionHarness first = await GrimoireMaintenanceAdmissionHarness
            .StartAsync(profile, terminalSuffixFaultSeam: fault.RaiseAsync);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));

        await SeedOutcomeDatasetAsync(first, timeout.Token);

        DataRetentionPlan plan = await first.PlanAsync(GrimoireTransitionEntryPoint.DirectCovenantReset);

        using HttpRequestMessage apply = first.CreateApplyRequest(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            plan.PlanId,
            Guid.NewGuid());

        using HttpResponseMessage response = await first.Client.SendAsync(apply, timeout.Token);

        Assert.False(response.IsSuccessStatusCode);

        LongRunningOperation terminal = Assert.IsType<LongRunningOperation>(first.Operations.Reads
            .Last(read => read.Operation?.State == LongRunningOperationState.Completed)
            .Operation);

        RecordedMaintenancePublication publication = first.Journal.Publications.Last();

        await first.DisposeAsync();

        MaintenanceAdoptionObservation secondAdoption = new();

        RecoveryHostStartupObservation secondStartup = new();

        bool failed = false;

        secondStartup.Journal.AfterStep = step =>
        {
            if (!failed && string.Equals(step, failingStep, StringComparison.Ordinal))
            {
                failed = true;

                throw new IOException("Injected Closed-anchor retirement interruption.");
            }
        };

        Task<GrimoireMaintenanceAdmissionHarness> starting = Task.Factory.StartNew(
            () => GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(
                profile,
                secondAdoption,
                secondStartup),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        Assert.Equal(
            GrimoireOfflineTransitionTerminalSuffixBoundary.RetirementPending,
            await secondStartup.TerminalSuffixReached.WaitAsync(timeout.Token));

        secondStartup.ReleaseTerminalSuffix();

        if (failingStep is "file:retiring-moved")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                _ = await starting.WaitAsync(timeout.Token));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                _ = await starting.WaitAsync(timeout.Token));
        }

        Assert.True(failed);

        Assert.False(secondStartup.Readiness.IsReady);

        Assert.False(secondStartup.FinalHostedServiceStarted.IsCompleted);

        Assert.Equal(0, secondAdoption.Calls);

        Assert.Null(secondStartup.AuthorityLoad);

        Assert.Null(secondStartup.AuthorityConsume);

        Assert.Equal(0, Volatile.Read(ref secondStartup.DispatchCalls));

        Assert.Equal(1, Volatile.Read(ref secondStartup.TerminalSuffixCalls));

        Assert.Empty(secondStartup.Operations.Transitions);

        if (failingStep is "file:retiring-moved")
        {
            Assert.False(File.Exists(publication.Raw.Location.JournalPath));

            Assert.True(File.Exists(publication.Raw.Location.RetiringPath));
        }
        else
        {
            Assert.True(File.Exists(publication.Raw.Location.JournalPath));

            Assert.False(File.Exists(publication.Raw.Location.RetiringPath));
        }

        MaintenanceAdoptionObservation thirdAdoption = new();

        RecoveryHostStartupObservation thirdStartup = new();

        await using GrimoireMaintenanceAdmissionHarness third = await Task.Factory.StartNew(
            () => GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(
                profile,
                thirdAdoption,
                thirdStartup),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap().WaitAsync(timeout.Token);

        Assert.Equal(0, thirdAdoption.Calls);

        Assert.Null(thirdStartup.AuthorityLoad);

        Assert.Null(thirdStartup.AuthorityConsume);

        Assert.Equal(0, Volatile.Read(ref thirdStartup.DispatchCalls));

        Assert.Equal(0, Volatile.Read(ref thirdStartup.TerminalSuffixCalls));

        Assert.Equal(0, thirdStartup.DisclosureWriterReopenCalls);

        Assert.False(thirdStartup.TerminalSuffixReached.IsCompleted);

        Assert.True(thirdStartup.Readiness.IsReady);

        Assert.True(thirdStartup.FinalHostedServiceStarted.IsCompletedSuccessfully);

        Assert.False(File.Exists(publication.Raw.Location.JournalPath));

        Assert.False(File.Exists(publication.Raw.Location.RetiringPath));

        await using AsyncServiceScope inspection = third.Factory.Services.CreateAsyncScope();

        LongRunningOperation recovered = Assert.IsType<LongRunningOperation>(await inspection
            .ServiceProvider
            .GetRequiredService<ILongRunningOperationStore>()
            .GetAsync(terminal.Id, timeout.Token));

        AssertTerminalOperationUnchanged(terminal, recovered);
    }

    private static async Task AssertTerminalRefusalAsync(string mutation)
    {
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary =
            mutation is "recorded-winner-drift"
                ? GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalWinner
                : GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized;

        OneShotTerminalSuffixFault fault = new(boundary);

        await using RestartableArcanumProfileFixture profile = new();

        await using GrimoireMaintenanceAdmissionHarness first = await GrimoireMaintenanceAdmissionHarness
            .StartAsync(profile, terminalSuffixFaultSeam: fault.RaiseAsync);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));

        await SeedOutcomeDatasetAsync(first, timeout.Token);

        DataRetentionPlan plan = await first.PlanAsync(GrimoireTransitionEntryPoint.DirectCovenantReset);

        using HttpRequestMessage apply = first.CreateApplyRequest(
            GrimoireTransitionEntryPoint.DirectCovenantReset,
            plan.PlanId,
            Guid.NewGuid());

        using HttpResponseMessage response = await first.Client.SendAsync(apply, timeout.Token);

        Assert.False(response.IsSuccessStatusCode);

        Assert.True(fault.Fired);

        LongRunningOperation terminal = Assert.IsType<LongRunningOperation>(first.Operations.Reads
            .Last(read => read.Operation?.State == LongRunningOperationState.Completed)
            .Operation);

        RecordedMaintenancePublication publication = first.Journal.Publications.Last();

        await first.DisposeAsync();

        string databasePath = Path.Combine(
            profile.TempHome,
            ".config",
            "arcanum",
            "arcanum.db");

        await MutateTerminalCandidateAsync(
            profile,
            databasePath,
            publication.Raw.Location.JournalPath,
            terminal.Id,
            mutation,
            timeout.Token);

        RawTerminalCandidate before = await ReadRawTerminalCandidateAsync(
            profile,
            databasePath,
            publication.Raw.Location.JournalPath,
            terminal.Id,
            timeout.Token);

        MaintenanceAdoptionObservation adoption = new();

        RecoveryHostStartupObservation startup = new();

        GrimoireMaintenanceAdmissionHarness? unexpected = null;

        Task<GrimoireMaintenanceAdmissionHarness> starting = Task.Factory.StartNew(
            () => GrimoireMaintenanceAdmissionHarness.StartRecoveryAsync(profile, adoption, startup),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        try
        {
            Task firstBoundary = await Task.WhenAny(starting, startup.TerminalSuffixReached)
                .WaitAsync(timeout.Token);

            if (ReferenceEquals(firstBoundary, startup.TerminalSuffixReached))
            {
                startup.ReleaseTerminalSuffix();
            }

            unexpected = await starting.WaitAsync(timeout.Token);
        }
        catch (InvalidOperationException)
        {
            // Exact refusal is surfaced by hosted-service startup failure.
        }
        finally
        {
            startup.ReleaseTerminalSuffix();

            if (!starting.IsCompleted)
            {
                Task ownerBoundary = await Task.WhenAny(startup.HarnessCreated, starting);

                if (ReferenceEquals(ownerBoundary, startup.HarnessCreated))
                {
                    await (await startup.HarnessCreated).DisposeAsync();
                }

                try
                {
                    _ = await starting;
                }
                catch
                {
                    // The owned factory is disposed and startup failure is observed.
                }
            }
        }

        if (unexpected is not null)
        {
            await unexpected.DisposeAsync();

            Assert.Fail($"The {mutation} terminal candidate unexpectedly reached readiness.");
        }

        Assert.False(startup.Readiness.IsReady);

        Assert.False(startup.FinalHostedServiceStarted.IsCompleted);

        Assert.Equal(0, adoption.Calls);

        Assert.Null(startup.AuthorityLoad);

        Assert.Null(startup.AuthorityConsume);

        Assert.Equal(0, Volatile.Read(ref startup.DispatchCalls));

        Assert.Empty(startup.Operations.Transitions);

        Assert.Equal(default, startup.OrdinaryMutations.Snapshot);

        Assert.Empty(startup.Journal.ParentResolutions);

        Assert.Empty(startup.AdmissionOrNull?.Effects ?? []);

        RawTerminalCandidate after = await ReadRawTerminalCandidateAsync(
            profile,
            databasePath,
            publication.Raw.Location.JournalPath,
            terminal.Id,
            timeout.Token);

        Assert.Equal(before.OperationColumns, after.OperationColumns);

        Assert.Equal(before.JournalBytes, after.JournalBytes);
    }

    private static async Task AssertTerminalRecoveredCatalogAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        OutcomeDatasetSnapshot before,
        LongRunningOperation operation,
        Guid installationId,
        CancellationToken cancellationToken,
        GrimoireTransitionEntryPoint entryPoint)
    {
        Result<GrimoireOfflineTransitionLaunchBinding> projected = GrimoireOfflineTransitionLaunch
            .FromCommittedCheckpoint(operation.CheckpointVersion, operation.CheckpointPayload!);

        Assert.True(projected.IsSuccess, $"checkpoint projection: {projected.Error.Message}");

        var fresh = await harness.Factory.Services.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken);

        Assert.True(fresh.IsSuccess, $"fresh catalog open: {fresh.Error.Message}");

        await using (fresh.Value)
        {
            Assert.Equal(ConnectionState.Open, fresh.Value.Connection.State);

            if (entryPoint is GrimoireTransitionEntryPoint.DirectCovenantReset)
            {
                Result<CovenantOfflineTransitionLaunchV4> decoded = CovenantRecoveryCheckpointCodec
                    .DecodeCovenantOfflineTransitionLaunch(operation.CheckpointPayload!);

                Assert.True(decoded.IsSuccess, decoded.Error.Message);

                await AssertRecoveredCovenantCatalogAsync(
                    fresh.Value.Connection,
                    decoded.Value,
                    installationId,
                    cancellationToken);
            }
            else
            {
                Result<DataRetentionFactoryTransitionLaunchV2> decoded = CovenantRecoveryCheckpointCodec
                    .DecodeDataRetentionFactoryTransitionLaunch(operation.CheckpointPayload!);

                Assert.True(decoded.IsSuccess, decoded.Error.Message);

                await AssertInstallationIdentityAsync(
                    fresh.Value.Connection,
                    installationId,
                    cancellationToken);

                await AssertFactoryCatalogAsync(fresh.Value.Connection, decoded.Value, cancellationToken);
            }
        }

        if (entryPoint is GrimoireTransitionEntryPoint.DirectCovenantReset)
        {
            OutcomeDatasetSnapshot after = await CaptureOutcomeDatasetAsync(harness, cancellationToken);

            AssertManagedFilesEqual(before, after, entryPoint);

            string[] erasedTables =
            [
                "covenant_state",
                "covenant_entries",
                "covenant_versions",
                "covenant_heads",
                "artifact_sensitivity",
                "managed_file_write_intents",
                "session_attachment_index_state",
                "session_attachment_chunks",
                "session_attachment_embeddings",
            ];

            foreach (string retained in OutcomeTables.Except(erasedTables, StringComparer.Ordinal))
            {
                Assert.Equal(
                    before.Rows.Where(row => row.StartsWith($"{retained}:", StringComparison.Ordinal)),
                    after.Rows.Where(row => row.StartsWith($"{retained}:", StringComparison.Ordinal)));
            }
        }
        else
        {
            Assert.All(before.ManagedFiles.Keys, path => Assert.False(File.Exists(path), path));
        }
    }

    private static async Task AssertTerminalRollbackCatalogAsync(
        GrimoireMaintenanceAdmissionHarness harness,
        OutcomeDatasetSnapshot before,
        LongRunningOperation operation,
        CancellationToken cancellationToken,
        GrimoireTransitionEntryPoint entryPoint)
    {
        Result<GrimoireOfflineTransitionLaunchBinding> launch = GrimoireOfflineTransitionLaunch
            .FromCommittedCheckpoint(operation.CheckpointVersion, operation.CheckpointPayload!);

        Assert.True(launch.IsSuccess, launch.Error.Message);

        var fresh = await harness.Factory.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken);

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

        await using (fresh.Value)
        {
            await using SqliteCommand command = fresh.Value.Connection.CreateCommand();

            command.CommandText =
                """
                SELECT DatasetGeneration, AcceleratorEpoch, KeyReclamationEpoch, EnvelopeKeyEpoch
                FROM covenant_state
                WHERE StateKey = 1;
                """;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));

            Assert.Equal(launch.Value.SourceDatasetGeneration, new Guid(reader.GetFieldValue<byte[]>(0)));

            Assert.Equal(
                launch.Value.SourceEpochs,
                new GrimoireOfflineTransitionEpochTuple(
                    (ulong)reader.GetInt64(1),
                    (ulong)reader.GetInt64(2),
                    (ulong)reader.GetInt64(3)));

            Assert.False(await reader.ReadAsync(cancellationToken));
        }

        OutcomeDatasetSnapshot after = await CaptureOutcomeDatasetAsync(harness, cancellationToken);

        string[] hostedIndexTables =
        [
            "session_attachment_index_state",
            "session_attachment_chunks",
            "session_attachment_embeddings",
        ];

        foreach (string retained in OutcomeTables.Except(hostedIndexTables, StringComparer.Ordinal))
        {
            Assert.Equal(
                before.Rows.Where(row => row.StartsWith($"{retained}:", StringComparison.Ordinal)),
                after.Rows.Where(row => row.StartsWith($"{retained}:", StringComparison.Ordinal)));
        }

        AssertManagedFilesEqual(before, after, entryPoint);
    }

    private static async Task MutateTerminalCandidateAsync(
        RestartableArcanumProfileFixture profile,
        string databasePath,
        string journalPath,
        Guid operationId,
        string mutation,
        CancellationToken cancellationToken)
    {
        if (mutation is "corrupt-journal")
        {
            byte[] bytes = await File.ReadAllBytesAsync(journalPath, cancellationToken);

            bytes[^1] ^= 0x01;

            await File.WriteAllBytesAsync(journalPath, bytes, cancellationToken);

            return;
        }

        await using SqliteConnection connection = TerminalRecoveryConnection(profile, databasePath);

        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = mutation switch
        {
            "missing-row" =>
                "DELETE FROM LongRunningOperations WHERE Id = @id;",
            "wrong-kind" =>
                "UPDATE LongRunningOperations SET Kind = 'data-retention-factory-reset' WHERE Id = @id;",
            "wrong-policy" =>
                $"UPDATE LongRunningOperations SET RecoveryPolicy = {(int)LongRunningOperationRecoveryPolicy.RestartIdempotently} WHERE Id = @id;",
            "wrong-version" =>
                "UPDATE LongRunningOperations SET CheckpointVersion = CheckpointVersion + 1 WHERE Id = @id;",
            "wrong-reference" =>
                "UPDATE LongRunningOperations SET CheckpointReference = 'foreign:' || Id WHERE Id = @id;",
            "payload-whitespace" =>
                "UPDATE LongRunningOperations SET CheckpointPayload = CAST(CAST(CheckpointPayload AS TEXT) || ' ' AS BLOB) WHERE Id = @id;",
            "revision-below-floor" =>
                "UPDATE LongRunningOperations SET Revision = 0 WHERE Id = @id;",
            "abandoned" =>
                $"UPDATE LongRunningOperations SET State = {(int)LongRunningOperationState.Abandoned} WHERE Id = @id;",
            "crossed-terminal-code" =>
                "UPDATE LongRunningOperations SET TerminalErrorCode = 'grimoire.offline_transition_not_applied' WHERE Id = @id;",
            "lingering-owner" =>
                "UPDATE LongRunningOperations SET LeaseOwner = 'foreign-owner' WHERE Id = @id;",
            "attempt-zero" =>
                "UPDATE LongRunningOperations SET AttemptCount = 0 WHERE Id = @id;",
            "nonterminal-with-completed-at" =>
                $"UPDATE LongRunningOperations SET State = {(int)LongRunningOperationState.ReconciliationRequired} WHERE Id = @id;",
            "nonterminal-with-terminal-code" =>
                $"""
                UPDATE LongRunningOperations
                SET State = {(int)LongRunningOperationState.ReconciliationRequired},
                    CompletedAt = NULL,
                    TerminalErrorCode = 'grimoire.offline_transition_not_applied'
                WHERE Id = @id;
                """,
            "running-with-terminal-code" =>
                $"""
                UPDATE LongRunningOperations
                SET State = {(int)LongRunningOperationState.Running},
                    CompletedAt = NULL,
                    TerminalErrorCode = 'grimoire.offline_transition_not_applied'
                WHERE Id = @id;
                """,
            "wrong-catalog" =>
                "UPDATE covenant_state SET DatasetGeneration = randomblob(16) WHERE StateKey = 1;",
            "recorded-winner-drift" =>
                "UPDATE LongRunningOperations SET Revision = Revision + 1 WHERE Id = @id;",
            "noncanonical-completed-at" =>
                "UPDATE LongRunningOperations SET CompletedAt = lower(CompletedAt) WHERE Id = @id;",
            "invalid-terminal-chronology" =>
                "UPDATE LongRunningOperations SET CompletedAt = CreatedAt WHERE Id = @id;",
            _ => throw new InvalidOperationException($"Unknown terminal candidate mutation '{mutation}'."),
        };

        _ = command.Parameters.AddWithValue("@id", operationId.ToString("N"));

        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task<RawTerminalCandidate> ReadRawTerminalCandidateAsync(
        RestartableArcanumProfileFixture profile,
        string databasePath,
        string journalPath,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = TerminalRecoveryConnection(profile, databasePath);

        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                "Id", "Kind", "State", "RecoveryPolicy", "RootOperationId", "ParentOperationId",
                "SessionId", "RunId", "InferenceRunId", "BudgetReservationId", "IdempotencyClaimId",
                "CreatedAt", "StartedAt", "HeartbeatAt", "CompletedAt", "LeaseOwner", "LeaseExpiresAt",
                "AttemptCount", "CheckpointVersion", "CheckpointPayload", "CheckpointReference",
                "PublicSummary", "TerminalErrorCode", "Revision"
            FROM "LongRunningOperations"
            WHERE "Id" = @id;
            """;

        _ = command.Parameters.AddWithValue("@id", operationId.ToString("N"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        string[] columns;

        if (!await reader.ReadAsync(cancellationToken))
        {
            columns = [];
        }
        else
        {
            columns = Enumerable.Range(0, reader.FieldCount)
                .Select(index => RawValue(reader.GetValue(index)))
                .ToArray();

            Assert.False(await reader.ReadAsync(cancellationToken));
        }

        byte[] journal = File.Exists(journalPath)
            ? await File.ReadAllBytesAsync(journalPath, cancellationToken)
            : [];

        return new RawTerminalCandidate(columns, journal);
    }

    private static SqliteConnection TerminalRecoveryConnection(
        RestartableArcanumProfileFixture profile,
        string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Password = profile.PassphraseSource.Passphrase,
            Pooling = false,
        }.ToString());

    private static string RawValue(object value) => value switch
    {
        DBNull => "null",
        byte[] bytes => "blob:" + Convert.ToHexString(bytes),
        string text => "text:" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)),
        long integer => "integer:" + integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double real => "real:" + real.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + value,
    };

    private static CovenantDigest IndependentWinnerDigest(
        GrimoireOfflineTransitionBinding binding,
        LongRunningOperation operation)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(
            "arcanum.grimoire.offline-transition.terminal-winner.v1"));

        hash.AppendData([0]);

        hash.AppendData(binding.DatabaseOperationLaunchBindingDigest.Bytes);

        hash.AppendData(operation.Id.ToByteArray(bigEndian: true));

        hash.AppendData([(byte)operation.State]);

        byte[] code = Encoding.UTF8.GetBytes(operation.TerminalErrorCode ?? string.Empty);

        Span<byte> codeLength = stackalloc byte[sizeof(ushort)];

        BinaryPrimitives.WriteUInt16BigEndian(codeLength, checked((ushort)code.Length));

        hash.AppendData(codeLength);

        hash.AppendData(code);

        Span<byte> revision = stackalloc byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(revision, operation.Revision);

        hash.AppendData(revision);

        return new CovenantDigest(hash.GetHashAndReset());
    }

    private static void AssertTerminalBoundaryPublication(
        RecordedMaintenancePublication publication,
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary,
        CovenantDigest winner,
        Guid operationId,
        Guid installationId)
    {
        Assert.Equal(operationId, publication.Raw.Envelope.OperationId);

        Assert.Equal(installationId, publication.Raw.Envelope.InstallationId);

        GrimoireOfflineTransitionReconciliationEvidence evidence = Assert.IsType<
            GrimoireOfflineTransitionReconciliationEvidence>(
                publication.Payload.Lifecycle.ReconciliationEvidence);

        Assert.Equal(ExpectedStep(boundary), evidence.Step);

        Assert.Equal(
            boundary >= GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalWinner
                ? winner
                : null,
            evidence.DatabaseTerminalWinnerDigest);

        Assert.Equal(
            boundary >= GrimoireOfflineTransitionTerminalSuffixBoundary.ParentReceiptSatisfied,
            evidence.ParentReceiptNotRequired);

        Assert.Null(evidence.ParentReceiptDigest);

        Assert.Equal(
            boundary >= GrimoireOfflineTransitionTerminalSuffixBoundary.LaneClosed,
            evidence.LaneClosed);

        Assert.Equal(
            boundary >= GrimoireOfflineTransitionTerminalSuffixBoundary.CovenantDispositionInFlight
                ? publication.Payload.Lifecycle.TerminalIntent
                : null,
            evidence.CovenantDispositionIntent);
    }

    private static GrimoireOfflineTransitionState ExpectedState(
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary) =>
        boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.RetirementPending
            ? GrimoireOfflineTransitionState.RetirementPending
            : GrimoireOfflineTransitionState.DatabaseReconciliationPending;

    private static GrimoireOfflineTransitionReconciliationStep ExpectedStep(
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary) => boundary switch
        {
            GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalized =>
                GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
            GrimoireOfflineTransitionTerminalSuffixBoundary.DatabaseTerminalWinner =>
                GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner,
            GrimoireOfflineTransitionTerminalSuffixBoundary.ParentReceiptSatisfied =>
                GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied,
            GrimoireOfflineTransitionTerminalSuffixBoundary.LaneClosed =>
                GrimoireOfflineTransitionReconciliationStep.LaneClosed,
            GrimoireOfflineTransitionTerminalSuffixBoundary.CovenantDispositionInFlight =>
                GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight,
            _ => GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified,
        };

    private static readonly string[] FullLogicalSuffix =
    [
        $"{GrimoireOfflineTransitionState.DatabaseReconciliationPending}:"
            + GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner,
        $"{GrimoireOfflineTransitionState.DatabaseReconciliationPending}:"
            + GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied,
        $"{GrimoireOfflineTransitionState.DatabaseReconciliationPending}:"
            + GrimoireOfflineTransitionReconciliationStep.LaneClosed,
        $"{GrimoireOfflineTransitionState.DatabaseReconciliationPending}:"
            + GrimoireOfflineTransitionReconciliationStep.CovenantDispositionInFlight,
        $"{GrimoireOfflineTransitionState.DatabaseReconciliationPending}:"
            + GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified,
        $"{GrimoireOfflineTransitionState.RetirementPending}:"
            + GrimoireOfflineTransitionReconciliationStep.CovenantDispositionVerified,
    ];

    private static string[] ExpectedFirstHostLogicalPrefix(
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary) =>
        boundary is GrimoireOfflineTransitionTerminalSuffixBoundary.Retired
            ?
            [
                $"{GrimoireOfflineTransitionState.DatabaseReconciliationPending}:"
                    + GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
                .. FullLogicalSuffix,
            ]
            :
            [
                $"{GrimoireOfflineTransitionState.DatabaseReconciliationPending}:"
                    + GrimoireOfflineTransitionReconciliationStep.CandidateVerified,
                .. FullLogicalSuffix.Take((byte)boundary - 1),
            ];

    private static readonly string[] ExpectedRetirementSteps =
    [
        "anchor:closed-written",
        "anchor:closed-readback",
        "file:retiring-moved",
        "file:retiring-verified",
        "file:retiring-parent-flushed",
        "file:retiring-unlinked",
        "file:retiring-zero-link-verified",
        "file:delete-parent-flushed",
        "file:absence-parent-flushed",
        "file:absence-proved",
    ];

    private static string[] ExpectedRecoveredLogicalSuffix(
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary) =>
        FullLogicalSuffix.Skip((byte)boundary - 1).ToArray();

    private static string LogicalSuffixStep(RecordedMaintenancePublication publication) =>
        $"{publication.Payload.Lifecycle.State}:"
            + publication.Payload.Lifecycle.ReconciliationEvidence!.Step;

    private static string[] RetirementSteps(IEnumerable<string> steps) =>
        steps.Where(step =>
            step.StartsWith("anchor:closed", StringComparison.Ordinal)
            || step.StartsWith("file:retiring", StringComparison.Ordinal)
            || step is "file:delete-parent-flushed"
                or "file:absence-parent-flushed"
                or "file:absence-proved")
        .ToArray();

    private static void AssertTerminalOperationUnchanged(
        LongRunningOperation expected,
        LongRunningOperation actual)
    {
        Assert.Equal(
            expected with { CheckpointPayload = null },
            actual with { CheckpointPayload = null });

        Assert.Equal(expected.CheckpointPayload, actual.CheckpointPayload);
    }

    private sealed class OneShotTerminalSuffixFault(
        GrimoireOfflineTransitionTerminalSuffixBoundary boundary)
    {
        private int _fired;

        internal bool Fired => Volatile.Read(ref _fired) != 0;

        internal Task<Result> RaiseAsync(
            GrimoireOfflineTransitionTerminalSuffixBoundary raised,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                raised == boundary
                && Interlocked.CompareExchange(ref _fired, 1, 0) == 0
                    ? Result.Failure(new Error(
                        ErrorCodes.Covenant.MaintenanceFailed,
                        "Injected terminal-suffix interruption."))
                    : Result.Success());
    }

    private sealed record RawTerminalCandidate(string[] OperationColumns, byte[] JournalBytes);
}

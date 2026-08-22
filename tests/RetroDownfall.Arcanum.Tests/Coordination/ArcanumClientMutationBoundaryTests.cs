using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Tests.Coordination;

[Collection("WorkspacePathPolicy")]
public sealed class ArcanumClientMutationBoundaryTests : IDisposable
{

    private readonly string _container;

    private readonly string _guardedRoot;

    public ArcanumClientMutationBoundaryTests()
    {

        _container = Path.Combine(
            Path.GetTempPath(),
            "arcanum-client-boundary-" + Guid.NewGuid().ToString("N"));

        _guardedRoot = Path.Combine(_container, "arcanum");

        Directory.CreateDirectory(_guardedRoot);

    }

    public void Dispose()
    {

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Fact]
    public async Task A_synchronous_mutation_runs_wholly_under_the_exclusive_client_mutex()
    {

        ArcanumClientMutationBoundary boundary = Boundary(
            ClientMutationEvidenceResult.Clear());

        ArcanumClientMutationResult<int> result = await boundary.RunAsync(
            () =>
            {

                ArcanumClientMutationLockAcquisitionResult nested =
                    ArcanumClientMutationLock.AcquireDetailed(_guardedRoot);

                Assert.Equal(
                    ArcanumClientMutationLockAcquisitionDisposition.Contended,
                    nested.Disposition);

                return 42;

            });

        Assert.Equal(ArcanumClientMutationDisposition.Completed, result.Disposition);

        Assert.Equal(42, result.Value);

        using ArcanumClientMutationLock released = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

    }

    [Fact]
    public async Task An_asynchronous_mutation_retains_the_mutex_until_its_task_finishes()
    {

        TaskCompletionSource entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ArcanumClientMutationBoundary boundary = Boundary(
            ClientMutationEvidenceResult.Clear());

        Task<ArcanumClientMutationResult<string>> running = boundary.RunAsync(
            async cancellationToken =>
            {

                entered.SetResult();

                await release.Task.WaitAsync(cancellationToken);

                return "complete";

            });

        await entered.Task;

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Contended,
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot).Disposition);

        release.SetResult();

        Assert.Equal("complete", (await running).Value);

        using ArcanumClientMutationLock released = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

    }

    [Theory]
    [InlineData(
        (byte)ArcanumClientMutationLockAcquisitionDisposition.Contended,
        (byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData(
        (byte)ArcanumClientMutationLockAcquisitionDisposition.Unsafe,
        (byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task Lock_refusal_is_typed_and_never_runs_the_mutation(
        byte lockDispositionValue,
        byte expectedValue)
    {

        ArcanumClientMutationLockAcquisitionDisposition lockDisposition =
            (ArcanumClientMutationLockAcquisitionDisposition)lockDispositionValue;

        ArcanumClientMutationDisposition expected =
            (ArcanumClientMutationDisposition)expectedValue;

        int calls = 0;

        ArcanumClientMutationBoundary boundary = new(
            _guardedRoot,
            new FakeEvidenceProbe(ClientMutationEvidenceResult.Clear()),
            _ => lockDisposition is ArcanumClientMutationLockAcquisitionDisposition.Contended
                ? ArcanumClientMutationLockAcquisitionResult.Contended()
                : ArcanumClientMutationLockAcquisitionResult.Unsafe());

        ArcanumClientMutationResult<int> result = await boundary.RunAsync(
            () => ++calls);

        Assert.Equal(expected, result.Disposition);

        Assert.Equal(0, calls);

        Assert.Throws<InvalidOperationException>(() => result.Value);

    }

    [Theory]
    [InlineData(
        (byte)ClientMutationEvidenceDisposition.Blocked,
        (byte)ArcanumClientMutationDisposition.Blocked)]
    [InlineData(
        (byte)ClientMutationEvidenceDisposition.Unsafe,
        (byte)ArcanumClientMutationDisposition.Unsafe)]
    public async Task Evidence_refusal_is_typed_and_never_runs_the_mutation(
        byte evidenceDispositionValue,
        byte expectedValue)
    {

        ClientMutationEvidenceDisposition evidenceDisposition =
            (ClientMutationEvidenceDisposition)evidenceDispositionValue;

        ArcanumClientMutationDisposition expected =
            (ArcanumClientMutationDisposition)expectedValue;

        int calls = 0;

        ClientMutationEvidenceResult evidence = evidenceDisposition
            is ClientMutationEvidenceDisposition.Blocked
            ? ClientMutationEvidenceResult.Blocked(new Error(
                ErrorCodes.Data.ResetInProgress,
                "A maintenance operation is active."))
            : ClientMutationEvidenceResult.Unsafe(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "Maintenance evidence is unsafe."));

        ArcanumClientMutationResult<int> result = await Boundary(evidence)
            .RunAsync(() => ++calls);

        Assert.Equal(expected, result.Disposition);

        Assert.Equal(evidence.Error, result.Error);

        Assert.Equal(0, calls);

    }

    [Fact]
    public async Task Mutation_failure_releases_the_mutex_without_collapsing_the_exception()
    {

        ArcanumClientMutationBoundary boundary = Boundary(
            ClientMutationEvidenceResult.Clear());

        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.RunAsync<int>(
            static _ => throw new InvalidOperationException("mutation failed")));

        using ArcanumClientMutationLock released = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

    }

    [Fact]
    public async Task Cancellation_observed_after_evidence_admission_runs_no_async_mutation_and_releases_mutex()
    {

        using CancellationTokenSource cancellation = new();

        int calls = 0;

        ArcanumClientMutationBoundary boundary = new(
            _guardedRoot,
            new CancellingEvidenceProbe(cancellation),
            ArcanumClientMutationLock.AcquireDetailed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            boundary.RunAsync(
                _ =>
                {

                    calls++;

                    return Task.FromResult(42);

                },
                cancellation.Token));

        Assert.Equal(0, calls);

        using ArcanumClientMutationLock released = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

    }

    private ArcanumClientMutationBoundary Boundary(
        ClientMutationEvidenceResult evidence) =>
        new(
            _guardedRoot,
            new FakeEvidenceProbe(evidence),
            ArcanumClientMutationLock.AcquireDetailed);

    private sealed class FakeEvidenceProbe(
        ClientMutationEvidenceResult evidence) : IClientMutationEvidenceProbe
    {

        public Task<ClientMutationEvidenceResult> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(evidence);

        }

    }

    private sealed class CancellingEvidenceProbe(
        CancellationTokenSource cancellation) : IClientMutationEvidenceProbe
    {

        public Task<ClientMutationEvidenceResult> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            cancellation.Cancel();

            return Task.FromResult(ClientMutationEvidenceResult.Clear());

        }

    }

}

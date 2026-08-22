using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

public enum ArcanumClientMutationDisposition : byte
{

    Completed,

    Blocked,

    Unsafe,

}

public sealed class ArcanumClientMutationResult<T>
{

    private readonly T? _value;

    private ArcanumClientMutationResult(
        ArcanumClientMutationDisposition disposition,
        T? value,
        Error error)
    {

        if (disposition is ArcanumClientMutationDisposition.Completed
            && error != Error.None)
        {

            throw new ArgumentException(
                "A completed client mutation cannot carry an error.",
                nameof(error));

        }

        if (disposition is not ArcanumClientMutationDisposition.Completed
            && error == Error.None)
        {

            throw new ArgumentException(
                "A refused client mutation must carry an error.",
                nameof(error));

        }

        Disposition = disposition;

        _value = value;

        Error = error;

    }

    public ArcanumClientMutationDisposition Disposition { get; }

    public bool IsCompleted =>
        Disposition is ArcanumClientMutationDisposition.Completed;

    public T Value => IsCompleted
        ? _value!
        : throw new InvalidOperationException(
            "A refused client mutation does not carry a completed value.");

    public Error Error { get; }

    public static ArcanumClientMutationResult<T> Completed(T value) =>
        new(
            ArcanumClientMutationDisposition.Completed,
            value,
            Error.None);

    public static ArcanumClientMutationResult<T> Blocked(Error error) =>
        new(
            ArcanumClientMutationDisposition.Blocked,
            value: default,
            error);

    public static ArcanumClientMutationResult<T> Unsafe(Error error) =>
        new(
            ArcanumClientMutationDisposition.Unsafe,
            value: default,
            error);

}

public interface IArcanumClientMutationBoundary
{

    Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<T> mutation,
        CancellationToken cancellationToken = default);

    Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken = default);

}

internal enum ClientMutationEvidenceDisposition : byte
{

    Clear,

    Blocked,

    Unsafe,

}

internal readonly record struct ClientMutationEvidenceResult(
    ClientMutationEvidenceDisposition Disposition,
    Error Error)
{

    internal static ClientMutationEvidenceResult Clear() =>
        new(ClientMutationEvidenceDisposition.Clear, Error.None);

    internal static ClientMutationEvidenceResult Blocked(Error error) =>
        new(ClientMutationEvidenceDisposition.Blocked, error);

    internal static ClientMutationEvidenceResult Unsafe(Error error) =>
        new(ClientMutationEvidenceDisposition.Unsafe, error);

}

internal interface IClientMutationEvidenceProbe
{

    Task<ClientMutationEvidenceResult> InspectAsync(
        CancellationToken cancellationToken);

}

public sealed class ArcanumClientMutationBoundary : IArcanumClientMutationBoundary
{

    private readonly string _guardedRoot;

    private readonly IClientMutationEvidenceProbe _evidence;

    private readonly Func<
        string,
        ArcanumClientMutationLockAcquisitionResult> _acquire;

    internal ArcanumClientMutationBoundary(
        string guardedRoot,
        IClientMutationEvidenceProbe evidence,
        Func<
            string,
            ArcanumClientMutationLockAcquisitionResult>? acquire = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        _guardedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(guardedRoot));

        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));

        _acquire = acquire ?? ArcanumClientMutationLock.AcquireDetailed;

    }

    public async Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<T> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(mutation);

        ArcanumClientMutationResult<ArcanumClientMutationLock> admission =
            await AcquireAdmittedLockAsync(cancellationToken).ConfigureAwait(false);

        if (!admission.IsCompleted)
        {

            return Refusal<T>(admission);

        }

        using ArcanumClientMutationLock held = admission.Value;

        cancellationToken.ThrowIfCancellationRequested();

        return ArcanumClientMutationResult<T>.Completed(mutation());

    }

    public async Task<ArcanumClientMutationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(mutation);

        ArcanumClientMutationResult<ArcanumClientMutationLock> admission =
            await AcquireAdmittedLockAsync(cancellationToken).ConfigureAwait(false);

        if (!admission.IsCompleted)
        {

            return Refusal<T>(admission);

        }

        using ArcanumClientMutationLock held = admission.Value;

        cancellationToken.ThrowIfCancellationRequested();

        T value = await mutation(cancellationToken).ConfigureAwait(false);

        return ArcanumClientMutationResult<T>.Completed(value);

    }

    private async Task<ArcanumClientMutationResult<ArcanumClientMutationLock>>
        AcquireAdmittedLockAsync(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        ArcanumClientMutationLockAcquisitionResult acquired =
            _acquire(_guardedRoot);

        if (acquired.Disposition
            is ArcanumClientMutationLockAcquisitionDisposition.Contended)
        {

            return ArcanumClientMutationResult<ArcanumClientMutationLock>.Blocked(
                new Error(
                    ErrorCodes.Data.FileLocked,
                    "Another Arcanum client mutation or installation maintenance operation is active."));

        }

        if (acquired.Disposition
            is ArcanumClientMutationLockAcquisitionDisposition.Unsafe)
        {

            return ArcanumClientMutationResult<ArcanumClientMutationLock>.Unsafe(
                new Error(
                    ErrorCodes.Data.ControlPathUnavailable,
                    "The client-mutation lock topology, identity, or owner-only permissions could not be validated safely."));

        }

        ArcanumClientMutationLock held = acquired.BorrowAcquiredLock();

        ClientMutationEvidenceResult evidence;

        try
        {

            evidence = await _evidence
                .InspectAsync(cancellationToken)
                .ConfigureAwait(false);

        }
        catch
        {

            held.Dispose();

            throw;

        }

        if (evidence.Disposition is ClientMutationEvidenceDisposition.Clear)
        {

            return ArcanumClientMutationResult<ArcanumClientMutationLock>.Completed(held);

        }

        held.Dispose();

        return evidence.Disposition is ClientMutationEvidenceDisposition.Blocked
            ? ArcanumClientMutationResult<ArcanumClientMutationLock>.Blocked(evidence.Error)
            : ArcanumClientMutationResult<ArcanumClientMutationLock>.Unsafe(evidence.Error);

    }

    private static ArcanumClientMutationResult<T> Refusal<T>(
        ArcanumClientMutationResult<ArcanumClientMutationLock> admission) =>
        admission.Disposition is ArcanumClientMutationDisposition.Blocked
            ? ArcanumClientMutationResult<T>.Blocked(admission.Error)
            : ArcanumClientMutationResult<T>.Unsafe(admission.Error);

}

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal sealed record GrimoireOfflineTransitionDecodedPayload(
    IGrimoireOfflineTransitionHandler Handler,
    IGrimoireOfflineTransitionPayload Payload,
    GrimoireOfflineTransitionHandlerOutcome Outcome);

internal sealed class GrimoireOfflineTransitionHandlerRegistry
{

    private const string RecoveryMessage =
        "The authenticated offline transition payload cannot be recovered by this build.";

    private readonly IReadOnlyDictionary<
        (GrimoireOfflineTransitionKind Kind, byte Version),
        IGrimoireOfflineTransitionHandler> _handlers;

    private GrimoireOfflineTransitionHandlerRegistry(
        IReadOnlyDictionary<
            (GrimoireOfflineTransitionKind Kind, byte Version),
            IGrimoireOfflineTransitionHandler> handlers)
    {

        _handlers = handlers;

    }

    internal static GrimoireOfflineTransitionHandlerRegistry Production { get; } =
        Value(Create(
        [
            new CovenantResetOfflineTransitionHandlerV1(),
            new HealthyCatalogFactoryErasureOfflineTransitionHandlerV1(),
        ]));

    internal static Result<GrimoireOfflineTransitionHandlerRegistry> Create(
        IEnumerable<IGrimoireOfflineTransitionHandler> handlers)
    {

        if (handlers is null)
        {

            return Failure<GrimoireOfflineTransitionHandlerRegistry>();

        }

        Dictionary<
            (GrimoireOfflineTransitionKind Kind, byte Version),
            IGrimoireOfflineTransitionHandler> registrations = [];

        foreach (IGrimoireOfflineTransitionHandler? handler in handlers)
        {

            if (handler is null
                || !Enum.IsDefined(handler.Kind)
                || handler.PayloadVersion == 0
                || !registrations.TryAdd((handler.Kind, handler.PayloadVersion), handler))
            {

                return Failure<GrimoireOfflineTransitionHandlerRegistry>();

            }

        }

        if (registrations.Count == 0)
        {

            return Failure<GrimoireOfflineTransitionHandlerRegistry>();

        }

        return new GrimoireOfflineTransitionHandlerRegistry(registrations);

    }

    internal Result<IGrimoireOfflineTransitionHandler> Resolve(
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion)
    {

        if (!Enum.IsDefined(kind)
            || payloadVersion == 0
            || !_handlers.TryGetValue((kind, payloadVersion), out IGrimoireOfflineTransitionHandler? handler))
        {

            return Failure<IGrimoireOfflineTransitionHandler>();

        }

        return Result<IGrimoireOfflineTransitionHandler>.Success(handler);

    }

    internal Result<byte[]> Encode(IGrimoireOfflineTransitionPayload payload)
    {

        if (payload is null || !GrimoireOfflineTransitionLifecycleValidator.ValidPayload(payload))
        {

            return Failure<byte[]>();

        }

        Result<IGrimoireOfflineTransitionHandler> resolved = Resolve(
            payload.Binding.Kind,
            payload.Binding.PayloadVersion);

        return resolved.IsFailure
            ? Failure<byte[]>()
            : Normalize(resolved.Value.Encode(payload));

    }

    internal Result<GrimoireOfflineTransitionDecodedPayload> Decode(
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ReadOnlySpan<byte> payloadBytes,
        GrimoireOfflineTransitionBinding expectedBinding)
    {

        if (expectedBinding is null
            || kind != expectedBinding.Kind
            || payloadVersion != expectedBinding.PayloadVersion
            || payloadBytes.IsEmpty
            || payloadBytes.Length
                > GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes)
        {

            return Failure<GrimoireOfflineTransitionDecodedPayload>();

        }

        Result<GrimoireOfflineTransitionDecodedPayload> decoded = DecodeAuthenticated(
            kind,
            payloadVersion,
            payloadBytes,
            expectedBinding.OperationId,
            expectedBinding.SlotEpoch);

        if (decoded.IsFailure || decoded.Value.Payload.Binding != expectedBinding)
        {

            return Failure<GrimoireOfflineTransitionDecodedPayload>();

        }

        return decoded;

    }

    internal Result<GrimoireOfflineTransitionDecodedPayload> DecodeAuthenticated(
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ReadOnlySpan<byte> payloadBytes,
        Guid operationId,
        ulong slotEpoch)
    {

        if (operationId == Guid.Empty || slotEpoch == 0)
        {

            return Failure<GrimoireOfflineTransitionDecodedPayload>();

        }

        Result<IGrimoireOfflineTransitionHandler> resolved = Resolve(kind, payloadVersion);

        if (resolved.IsFailure)
        {

            return Failure<GrimoireOfflineTransitionDecodedPayload>();

        }

        GrimoireOfflineTransitionAuthenticatedBinding authenticated = new(
            operationId,
            kind,
            payloadVersion,
            slotEpoch);

        Result<IGrimoireOfflineTransitionPayload> decoded = resolved.Value.Decode(
            payloadBytes,
            authenticated);

        if (decoded.IsFailure
            || !GrimoireOfflineTransitionLifecycleValidator.ValidPayload(decoded.Value))
        {

            return Failure<GrimoireOfflineTransitionDecodedPayload>();

        }

        return new GrimoireOfflineTransitionDecodedPayload(
            resolved.Value,
            decoded.Value,
            resolved.Value.ResolveOutcome(decoded.Value));

    }

    private static Result<T> Normalize<T>(Result<T> result) =>
        result.IsSuccess ? result : Failure<T>();

    private static Result<T> Failure<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        RecoveryMessage));

    private static GrimoireOfflineTransitionHandlerRegistry Value(
        Result<GrimoireOfflineTransitionHandlerRegistry> result) => result.IsSuccess
        ? result.Value
        : throw new InvalidOperationException(RecoveryMessage);

}

internal static class GrimoireOfflineTransitionCodec
{

    private const string RecoveryMessage =
        "The authenticated offline transition payload cannot be recovered by this build.";

    internal static Result<byte[]> Encode<TPayload>(
        TPayload payload,
        JsonTypeInfo<TPayload> typeInfo)
        where TPayload : class, IGrimoireOfflineTransitionPayload
    {

        try
        {

            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);

            return encoded.Length == 0
                || encoded.Length
                    > GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes
                ? Failure<byte[]>()
                : encoded;

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return Failure<byte[]>();

        }

    }

    internal static Result<TPayload> Decode<TPayload>(
        ReadOnlySpan<byte> payloadBytes,
        JsonTypeInfo<TPayload> typeInfo,
        GrimoireOfflineTransitionAuthenticatedBinding expectedBinding)
        where TPayload : class, IGrimoireOfflineTransitionPayload
    {

        if (payloadBytes.IsEmpty
            || payloadBytes.Length
                > GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes)
        {

            return Failure<TPayload>();

        }

        try
        {

            TPayload? payload = JsonSerializer.Deserialize(payloadBytes, typeInfo);

            if (payload is null
                || payload.Binding.OperationId != expectedBinding.OperationId
                || payload.Binding.Kind != expectedBinding.Kind
                || payload.Binding.PayloadVersion != expectedBinding.PayloadVersion
                || payload.Binding.SlotEpoch != expectedBinding.SlotEpoch
                || !GrimoireOfflineTransitionLifecycleValidator.ValidPayload(payload))
            {

                return Failure<TPayload>();

            }

            byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);

            return payloadBytes.SequenceEqual(canonical)
                ? payload
                : Failure<TPayload>();

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return Failure<TPayload>();

        }

    }

    private static Result<T> Failure<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        RecoveryMessage));

}

internal sealed partial class CovenantResetOfflineTransitionHandlerV1
    : IGrimoireOfflineTransitionHandler
{

    public GrimoireOfflineTransitionKind Kind =>
        GrimoireOfflineTransitionKind.CovenantReset;

    public byte PayloadVersion => 1;

    public Result<IGrimoireOfflineTransitionPayload> Decode(
        ReadOnlySpan<byte> payloadBytes,
        GrimoireOfflineTransitionAuthenticatedBinding expectedBinding)
    {

        Result<CovenantResetOfflineTransitionPayloadV1> decoded =
            GrimoireOfflineTransitionCodec.Decode(
                payloadBytes,
                GrimoireOfflineTransitionLifecycleJsonContext.Default
                    .CovenantResetOfflineTransitionPayloadV1,
                expectedBinding);

        return decoded.IsSuccess
            ? Result<IGrimoireOfflineTransitionPayload>.Success(decoded.Value)
            : Result<IGrimoireOfflineTransitionPayload>.Failure(decoded.Error);

    }

    public Result<byte[]> Encode(IGrimoireOfflineTransitionPayload payload) =>
        payload is CovenantResetOfflineTransitionPayloadV1 reset
            && reset.Binding.Kind == Kind
            && reset.Binding.PayloadVersion == PayloadVersion
            ? GrimoireOfflineTransitionCodec.Encode(
                reset,
                GrimoireOfflineTransitionLifecycleJsonContext.Default
                    .CovenantResetOfflineTransitionPayloadV1)
            : Failure<byte[]>();

    public Result ValidateAdvance(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        current is CovenantResetOfflineTransitionPayloadV1 typedCurrent
            && next is CovenantResetOfflineTransitionPayloadV1 typedNext
            ? ValidateAdvance(typedCurrent, typedNext)
            : Failure();

    public GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
        IGrimoireOfflineTransitionPayload payload) =>
        payload is CovenantResetOfflineTransitionPayloadV1 typed
            ? ResolveOutcome(typed)
            : GrimoireOfflineTransitionHandlerOutcome.KeepClosed;

    private static Result Failure() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The authenticated offline transition payload cannot be recovered by this build.");

    private static Result<T> Failure<T>() => Result<T>.Failure(Failure().Error);

}

internal sealed partial class HealthyCatalogFactoryErasureOfflineTransitionHandlerV1
    : IGrimoireOfflineTransitionHandler
{

    public GrimoireOfflineTransitionKind Kind =>
        GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure;

    public byte PayloadVersion => 1;

    public Result<IGrimoireOfflineTransitionPayload> Decode(
        ReadOnlySpan<byte> payloadBytes,
        GrimoireOfflineTransitionAuthenticatedBinding expectedBinding)
    {

        Result<HealthyCatalogFactoryErasureOfflineTransitionPayloadV1> decoded =
            GrimoireOfflineTransitionCodec.Decode(
                payloadBytes,
                GrimoireOfflineTransitionLifecycleJsonContext.Default
                    .HealthyCatalogFactoryErasureOfflineTransitionPayloadV1,
                expectedBinding);

        return decoded.IsSuccess
            ? Result<IGrimoireOfflineTransitionPayload>.Success(decoded.Value)
            : Result<IGrimoireOfflineTransitionPayload>.Failure(decoded.Error);

    }

    public Result<byte[]> Encode(IGrimoireOfflineTransitionPayload payload) =>
        payload is HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory
            && factory.Binding.Kind == Kind
            && factory.Binding.PayloadVersion == PayloadVersion
            ? GrimoireOfflineTransitionCodec.Encode(
                factory,
                GrimoireOfflineTransitionLifecycleJsonContext.Default
                    .HealthyCatalogFactoryErasureOfflineTransitionPayloadV1)
            : Failure<byte[]>();

    public Result ValidateAdvance(
        IGrimoireOfflineTransitionPayload current,
        IGrimoireOfflineTransitionPayload next) =>
        current is HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 typedCurrent
            && next is HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 typedNext
            ? ValidateAdvance(typedCurrent, typedNext)
            : Failure();

    public GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
        IGrimoireOfflineTransitionPayload payload) =>
        payload is HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 typed
            ? ResolveOutcome(typed)
            : GrimoireOfflineTransitionHandlerOutcome.KeepClosed;

    private static Result Failure() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The authenticated offline transition payload cannot be recovered by this build.");

    private static Result<T> Failure<T>() => Result<T>.Failure(Failure().Error);

}

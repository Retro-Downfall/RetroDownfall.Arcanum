using System.Text;

using System.Text.Json;

using System.Text.Json.Nodes;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionHandlerRegistryTests
{

    [Fact]
    public void Production_registry_is_closed_over_exactly_the_two_current_kind_version_pairs()
    {

        GrimoireOfflineTransitionHandlerRegistry registry =
            GrimoireOfflineTransitionHandlerRegistry.Production;

        Assert.Equal(
            (GrimoireOfflineTransitionKind[])
            [
                GrimoireOfflineTransitionKind.CovenantReset,
                GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            ],
            Enum.GetValues<GrimoireOfflineTransitionKind>());

        Assert.IsType<CovenantResetOfflineTransitionHandlerV1>(
            Value(registry.Resolve(GrimoireOfflineTransitionKind.CovenantReset, 1)));

        Assert.IsType<HealthyCatalogFactoryErasureOfflineTransitionHandlerV1>(
            Value(registry.Resolve(
                GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
                1)));

        Assert.True(registry.Resolve(GrimoireOfflineTransitionKind.CovenantReset, 2).IsFailure);

        Assert.True(registry.Resolve((GrimoireOfflineTransitionKind)99, 1).IsFailure);

        Assert.True(registry.Resolve(GrimoireOfflineTransitionKind.CovenantReset, 0).IsFailure);

    }

    [Fact]
    public void Registry_rejects_duplicates_zero_versions_and_undefined_kinds()
    {

        CovenantResetOfflineTransitionHandlerV1 reset = new();

        Assert.True(GrimoireOfflineTransitionHandlerRegistry.Create([reset, reset]).IsFailure);

        Assert.True(GrimoireOfflineTransitionHandlerRegistry.Create(
            [new TestHandler(GrimoireOfflineTransitionKind.CovenantReset, 0)]).IsFailure);

        Assert.True(GrimoireOfflineTransitionHandlerRegistry.Create(
            [new TestHandler((GrimoireOfflineTransitionKind)99, 1)]).IsFailure);

    }

    [Fact]
    public void Each_production_codec_round_trips_only_its_strict_canonical_payload()
    {

        GrimoireOfflineTransitionHandlerRegistry registry =
            GrimoireOfflineTransitionHandlerRegistry.Production;

        CovenantResetOfflineTransitionPayloadV1 reset = ResetPayload();

        byte[] resetBytes = Value(registry.Encode(reset));

        GrimoireOfflineTransitionDecodedPayload resetDecoded = Value(registry.Decode(
            reset.Binding.Kind,
            reset.Binding.PayloadVersion,
            resetBytes,
            reset.Binding));

        Assert.Equal(reset, Assert.IsType<CovenantResetOfflineTransitionPayloadV1>(
            resetDecoded.Payload));

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory = new(
            reset.Binding with
            {
                Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            },
            reset.Lifecycle,
            reset.LastCompletedPhase,
            reset.InFlightPhase,
            reset.InFlightBeforeState,
            reset.ReplacementEvidence,
            OrdinaryFactoryContinuationCompleted: false);

        byte[] factoryBytes = Value(registry.Encode(factory));

        GrimoireOfflineTransitionDecodedPayload factoryDecoded = Value(registry.Decode(
            factory.Binding.Kind,
            factory.Binding.PayloadVersion,
            factoryBytes,
            factory.Binding));

        Assert.Equal(factory, Assert.IsType<HealthyCatalogFactoryErasureOfflineTransitionPayloadV1>(
            factoryDecoded.Payload));

        Assert.NotEqual(resetBytes, factoryBytes);

    }

    [Fact]
    public void Codec_collapses_malformed_unknown_noncanonical_and_changed_payloads_to_one_error()
    {

        GrimoireOfflineTransitionHandlerRegistry registry =
            GrimoireOfflineTransitionHandlerRegistry.Production;

        CovenantResetOfflineTransitionPayloadV1 payload = ResetPayload();

        byte[] canonical = Value(registry.Encode(payload));

        string json = Encoding.UTF8.GetString(canonical);

        byte[][] refused =
        [
            Encoding.UTF8.GetBytes("{"),
            Encoding.UTF8.GetBytes(json[..^1] + ",\"unknown\":true}"),
            Encoding.UTF8.GetBytes(" " + json),
            Encoding.UTF8.GetBytes(json.Replace("\"Prepared\"", "\"future-state\"", StringComparison.Ordinal)),
        ];

        List<Error> errors = [];

        foreach (byte[] candidate in refused)
        {

            Result<GrimoireOfflineTransitionDecodedPayload> result = registry.Decode(
                payload.Binding.Kind,
                payload.Binding.PayloadVersion,
                candidate,
                payload.Binding);

            Assert.True(result.IsFailure);

            errors.Add(result.Error);

        }

        Result<GrimoireOfflineTransitionDecodedPayload> changedBinding = registry.Decode(
            payload.Binding.Kind,
            payload.Binding.PayloadVersion,
            canonical,
            payload.Binding with { OperationId = Guid.NewGuid() });

        Assert.True(changedBinding.IsFailure);

        errors.Add(changedBinding.Error);

        Assert.Single(errors.Select(static error => error.Code).Distinct());

        Assert.Single(errors.Select(static error => error.Message).Distinct());

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, errors[0].Code);

        Assert.DoesNotContain("prepared", errors[0].Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Production_codecs_collapse_null_members_and_malformed_digests_without_throwing()
    {

        GrimoireOfflineTransitionHandlerRegistry registry =
            GrimoireOfflineTransitionHandlerRegistry.Production;

        CovenantResetOfflineTransitionPayloadV1 reset = ResetPayload();

        HealthyCatalogFactoryErasureOfflineTransitionPayloadV1 factory = new(
            reset.Binding with
            {
                Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            },
            reset.Lifecycle,
            reset.LastCompletedPhase,
            reset.InFlightPhase,
            reset.InFlightBeforeState,
            reset.ReplacementEvidence,
            OrdinaryFactoryContinuationCompleted: false);

        List<Error> errors = [];

        foreach (IGrimoireOfflineTransitionPayload payload in
            (IGrimoireOfflineTransitionPayload[])[reset, factory])
        {

            IGrimoireOfflineTransitionHandler handler = Value(registry.Resolve(
                payload.Binding.Kind,
                payload.Binding.PayloadVersion));

            byte[] canonical = Value(registry.Encode(payload));

            foreach (byte[] malformed in MalformedMembers(canonical))
            {

                GrimoireOfflineTransitionAuthenticatedBinding authenticated = new(
                    payload.Binding.OperationId,
                    payload.Binding.Kind,
                    payload.Binding.PayloadVersion,
                    payload.Binding.SlotEpoch);

                Result<IGrimoireOfflineTransitionPayload>? handlerResult = null;

                Exception? handlerException = Record.Exception(() =>
                    handlerResult = handler.Decode(malformed, authenticated));

                Assert.Null(handlerException);

                Assert.True(handlerResult!.IsFailure);

                errors.Add(handlerResult.Error);

                Result<GrimoireOfflineTransitionDecodedPayload>? authenticatedResult = null;

                Exception? authenticatedException = Record.Exception(() =>
                    authenticatedResult = registry.DecodeAuthenticated(
                        payload.Binding.Kind,
                        payload.Binding.PayloadVersion,
                        malformed,
                        payload.Binding.OperationId,
                        payload.Binding.SlotEpoch));

                Assert.Null(authenticatedException);

                Assert.True(authenticatedResult!.IsFailure);

                errors.Add(authenticatedResult.Error);

                Result<GrimoireOfflineTransitionDecodedPayload>? registryResult = null;

                Exception? registryException = Record.Exception(() =>
                    registryResult = registry.Decode(
                        payload.Binding.Kind,
                        payload.Binding.PayloadVersion,
                        malformed,
                        payload.Binding));

                Assert.Null(registryException);

                Assert.True(registryResult!.IsFailure);

                errors.Add(registryResult.Error);

            }

        }

        Assert.NotEmpty(errors);

        Assert.All(errors, static error =>
        {

            Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, error.Code);

            Assert.Equal(
                "The authenticated offline transition payload cannot be recovered by this build.",
                error.Message);

        });

    }

    [Fact]
    public void Authenticated_applying_recovery_requires_the_exact_successor_in_flight_phase()
    {

        GrimoireOfflineTransitionHandlerRegistry registry =
            GrimoireOfflineTransitionHandlerRegistry.Production;

        CovenantResetOfflineTransitionPayloadV1 applying = ResetPayload() with
        {
            Lifecycle = ResetPayload().Lifecycle with
            {
                State = GrimoireOfflineTransitionState.Applying,
                ClosingEvidence = new(
                    true,
                    true,
                    true,
                    true,
                    true,
                    ResetPayload().Binding.SourceDatasetGeneration),
            },
            InFlightPhase = CovenantResetPhase.CanonicalApplied,
            InFlightBeforeState = new(Digest(0x31), Digest(0x32)),
        };

        Assert.True(DecodeRaw(registry, applying).IsSuccess);

        CovenantResetOfflineTransitionPayloadV1[] invalid =
        [
            applying with { InFlightPhase = CovenantResetPhase.ManagedArtifactsProcessed },
            applying with { InFlightPhase = CovenantResetPhase.InventoryPrepared },
            applying with
            {
                LastCompletedPhase = CovenantResetPhase.ManagedArtifactsProcessed,
                InFlightPhase = CovenantResetPhase.CanonicalApplied,
            },
            applying with
            {
                LastCompletedPhase = CovenantResetPhase.SidecarsVerified,
                InFlightPhase = CovenantResetPhase.ReopenedVerified,
            },
            applying with
            {
                Lifecycle = applying.Lifecycle with
                {
                    State = GrimoireOfflineTransitionState.KeepClosed,
                    Blocker = new(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        GrimoireOfflineTransitionState.Applying,
                        Digest(0x71),
                        Digest(0x71)),
                },
                InFlightPhase = CovenantResetPhase.ManagedArtifactsProcessed,
            },
        ];

        Assert.All(invalid, candidate => Assert.True(DecodeRaw(registry, candidate).IsFailure));

    }

    [Fact]
    public void Registry_refuses_payload_handler_binding_disagreement()
    {

        GrimoireOfflineTransitionHandlerRegistry registry =
            GrimoireOfflineTransitionHandlerRegistry.Production;

        CovenantResetOfflineTransitionPayloadV1 payload = ResetPayload() with
        {
            Binding = ResetPayload().Binding with
            {
                Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            },
        };

        Assert.True(registry.Encode(payload).IsFailure);

    }

    [Fact]
    public void Test_constructed_codec_and_handler_prove_the_registry_extension_seam()
    {

        TestHandler extension = new(
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 77);

        GrimoireOfflineTransitionHandlerRegistry registry = Value(
            GrimoireOfflineTransitionHandlerRegistry.Create([extension]));

        Assert.Equal(extension, Value(registry.Resolve(
            GrimoireOfflineTransitionKind.CovenantReset,
            77)));

        TestPayload payload = new(
            ResetPayload().Binding with { PayloadVersion = 77 },
            ResetPayload().Lifecycle,
            ResetPayload().LastCompletedPhase,
            null,
            null,
            null);

        byte[] encoded = Value(registry.Encode(payload));

        GrimoireOfflineTransitionDecodedPayload decoded = Value(registry.Decode(
            payload.Binding.Kind,
            payload.Binding.PayloadVersion,
            encoded,
            payload.Binding));

        Assert.Equal(payload, decoded.Payload);

    }

    private static CovenantResetOfflineTransitionPayloadV1 ResetPayload() => new(
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            GrimoireOfflineTransitionKind.CovenantReset,
            PayloadVersion: 1,
            SlotEpoch: 9,
            Digest(0x11),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            new(1, 2, 3),
            new(2, 3, 4),
            Digest(0x12),
            ExpectedDatabaseOperationRevision: 4,
            ParentReceiptBindingDigest: null),
        new(
            GrimoireOfflineTransitionState.Prepared,
            GrimoireOfflineTransitionTerminalIntent.Undecided,
            new(false, false, false, false, false, null),
            new(false, false, false),
            ReconciliationEvidence: null,
            Blocker: null),
        CovenantResetPhase.InventoryPrepared,
        InFlightPhase: null,
        InFlightBeforeState: null,
        ReplacementEvidence: null);

    private static IEnumerable<byte[]> MalformedMembers(byte[] canonical)
    {

        JsonNode bindingNull = JsonNode.Parse(canonical)!;

        bindingNull["binding"] = null;

        yield return Encoding.UTF8.GetBytes(bindingNull.ToJsonString());

        JsonNode lifecycleNull = JsonNode.Parse(canonical)!;

        lifecycleNull["lifecycle"] = null;

        yield return Encoding.UTF8.GetBytes(lifecycleNull.ToJsonString());

        JsonNode malformedDigest = JsonNode.Parse(canonical)!;

        malformedDigest["binding"]!["effectDigest"]!["bytes"] =
            Convert.ToBase64String([0x01]);

        yield return Encoding.UTF8.GetBytes(malformedDigest.ToJsonString());

        JsonNode malformedDigestValue = JsonNode.Parse(canonical)!;

        malformedDigestValue["binding"]!["effectDigest"]!["bytes"] = "not-base64";

        yield return Encoding.UTF8.GetBytes(malformedDigestValue.ToJsonString());

    }

    private static Result<GrimoireOfflineTransitionDecodedPayload> DecodeRaw(
        GrimoireOfflineTransitionHandlerRegistry registry,
        CovenantResetOfflineTransitionPayloadV1 payload)
    {

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            GrimoireOfflineTransitionLifecycleJsonContext.Default
                .CovenantResetOfflineTransitionPayloadV1);

        return registry.Decode(
            payload.Binding.Kind,
            payload.Binding.PayloadVersion,
            bytes,
            payload.Binding);

    }

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Code + ":" + result.Error.Message);

        return result.Value;

    }

    private sealed record TestPayload(
        GrimoireOfflineTransitionBinding Binding,
        GrimoireOfflineTransitionLifecycle Lifecycle,
        CovenantResetPhase LastCompletedPhase,
        CovenantResetPhase? InFlightPhase,
        GrimoireOfflineTransitionBeforeStateEvidence? InFlightBeforeState,
        GrimoireOfflineTransitionReplacementEvidence? ReplacementEvidence)
        : IGrimoireOfflineTransitionPayload;

    private sealed class TestHandler(
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion) : IGrimoireOfflineTransitionHandler
    {

        public GrimoireOfflineTransitionKind Kind => kind;

        public byte PayloadVersion => payloadVersion;

        public Result<IGrimoireOfflineTransitionPayload> Decode(
            ReadOnlySpan<byte> payloadBytes,
            GrimoireOfflineTransitionAuthenticatedBinding expectedBinding) =>
            payloadBytes.SequenceEqual("test"u8)
                ? new TestPayload(
                    ResetPayload().Binding with
                    {
                        OperationId = expectedBinding.OperationId,
                        Kind = expectedBinding.Kind,
                        PayloadVersion = expectedBinding.PayloadVersion,
                        SlotEpoch = expectedBinding.SlotEpoch,
                    },
                    ResetPayload().Lifecycle,
                    CovenantResetPhase.InventoryPrepared,
                    null,
                    null,
                    null)
                : Failure<IGrimoireOfflineTransitionPayload>();

        public Result<byte[]> Encode(IGrimoireOfflineTransitionPayload payload) =>
            payload is TestPayload
                && payload.Binding.Kind == Kind
                && payload.Binding.PayloadVersion == PayloadVersion
                ? "test"u8.ToArray()
                : Failure<byte[]>();

        public Result ValidateAdvance(
            IGrimoireOfflineTransitionPayload current,
            IGrimoireOfflineTransitionPayload next) => Result.Success();

        public GrimoireOfflineTransitionHandlerOutcome ResolveOutcome(
            IGrimoireOfflineTransitionPayload payload) =>
            GrimoireOfflineTransitionHandlerOutcome.NotApplied;

        private static Result<T> Failure<T>() => Result<T>.Failure(new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The authenticated offline transition payload cannot be recovered by this build."));

    }

}

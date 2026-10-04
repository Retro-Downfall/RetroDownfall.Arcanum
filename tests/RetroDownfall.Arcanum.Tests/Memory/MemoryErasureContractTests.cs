using System.Reflection;

using System.Text.Json;

using System.Text.Json.Serialization;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// The store-neutral erasure wire contracts and the ports every store's erasure implements.
/// </summary>
public sealed class MemoryErasureContractTests
{
    [Fact]
    public void Store_codes_are_the_memory_review_codes()
    {
        Assert.Equal(1, (byte)MemoryReviewStore.Covenant);

        Assert.Equal(2, (byte)MemoryReviewStore.Saga);

        Assert.Equal(3, (byte)MemoryReviewStore.Lexicon);
    }

    [Theory]
    [InlineData(typeof(MemoryLocalErasureOutcome))]
    [InlineData(typeof(MemoryErasureScrubPendingReason))]
    [InlineData(typeof(MemoryErasureWalCheckpointAttempt))]
    [InlineData(typeof(MemoryExternalChannel))]
    [InlineData(typeof(MemoryExternalEvidence))]
    [InlineData(typeof(MemoryExternalRevocation))]
    [InlineData(typeof(MemoryRetainedLocalCopy))]
    [InlineData(typeof(MemoryErasureNote))]
    [InlineData(typeof(MemoryErasureReleaseOutcome))]
    [InlineData(typeof(MemoryErasureKeyStatus))]
    public void Every_erasure_enum_is_string_only_and_has_no_zero_member(Type type)
    {
        Assert.False(
            Enum.IsDefined(type, Enum.ToObject(type, 0)),
            $"{type.Name} declares a zero member, which a missing field would silently bind to.");

        JsonConverterAttribute? converter = type.GetCustomAttribute<JsonConverterAttribute>();

        Assert.True(
            converter?.ConverterType is { IsGenericType: true } converterType
                && converterType.GetGenericTypeDefinition() == typeof(StringOnlyJsonStringEnumConverter<>),
            $"{type.Name} is not string-only on the wire.");

        JsonTypeInfo typeInfo = Assert.IsAssignableFrom<JsonTypeInfo>(ArcanumJsonContext.Default.GetTypeInfo(type));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("1", typeInfo));
    }

    /// <summary>
    /// Each store's erasure port carries prepare and apply and nothing else. Release and administration
    /// live on their own interfaces, so a store implementation can never grow a second release path.
    /// </summary>
    [Fact]
    public void Store_erasure_ports_carry_exactly_prepare_and_apply()
    {
        AssertPort(
            typeof(ISagaMemoryErasureService),
            [typeof(SagaErasePrepareRequest), typeof(CancellationToken)],
            [typeof(SagaEraseRequest), typeof(OperatorAuthorityContext), typeof(CancellationToken)]);

        AssertPort(
            typeof(ILexiconErasureService),
            [typeof(LexiconErasePrepareRequest), typeof(CancellationToken)],
            [typeof(LexiconEraseRequest), typeof(OperatorAuthorityContext), typeof(CancellationToken)]);

        AssertPort(
            typeof(ICovenantEntryErasureService),
            [typeof(CovenantErasePrepareRequest), typeof(OperatorAuthorityContext), typeof(CancellationToken)],
            [typeof(CovenantEraseRequest), typeof(OperatorAuthorityContext), typeof(CancellationToken)]);
    }

    [Fact]
    public void Scrub_reason_mask_uses_one_bit_per_code()
    {
        Assert.Equal(1, MemoryErasureScrubPendingReasons.ToMask([MemoryErasureScrubPendingReason.WalCheckpointPending]));

        Assert.Equal(6, MemoryErasureScrubPendingReasons.ToMask(
        [
            MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified,
            MemoryErasureScrubPendingReason.VectorIndexScrubUnverified,
        ]));

        Assert.Equal(0, MemoryErasureScrubPendingReasons.ToMask([]));

        Assert.Equal(
            [
                MemoryErasureScrubPendingReason.WalCheckpointPending,
                MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified,
                MemoryErasureScrubPendingReason.VectorIndexScrubUnverified,
            ],
            MemoryErasureScrubPendingReasons.FromMask(7));

        Assert.Empty(MemoryErasureScrubPendingReasons.FromMask(0));

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryErasureScrubPendingReasons.FromMask(8));

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryErasureScrubPendingReasons.FromMask(-1));

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryErasureScrubPendingReasons.ToMask([(MemoryErasureScrubPendingReason)4]));
    }

    [Fact]
    public void Retained_copy_mask_uses_one_bit_per_code()
    {
        MemoryRetainedLocalCopy[] all =
        [
            MemoryRetainedLocalCopy.SessionTranscripts,
            MemoryRetainedLocalCopy.SearchAndSummaryDerivatives,
            MemoryRetainedLocalCopy.Attachments,
            MemoryRetainedLocalCopy.ResponseCaches,
            MemoryRetainedLocalCopy.ApplicationLogs,
            MemoryRetainedLocalCopy.AuditLog,
            MemoryRetainedLocalCopy.BackupArchives,
            MemoryRetainedLocalCopy.OtherLocalState,
        ];

        MemoryRetainedLocalCopy[] withoutAudit = [.. all.Where(static copy => copy != MemoryRetainedLocalCopy.AuditLog)];

        Assert.Equal(255, MemoryRetainedLocalCopies.ToMask(all));

        Assert.Equal(223, MemoryRetainedLocalCopies.ToMask(withoutAudit));

        Assert.Equal(withoutAudit, MemoryRetainedLocalCopies.FromMask(223));

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryRetainedLocalCopies.FromMask(256));

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryRetainedLocalCopies.FromMask(-1));

        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryRetainedLocalCopies.ToMask([(MemoryRetainedLocalCopy)9]));
    }

    [Fact]
    public void Result_dto_writes_camel_case_names_and_enum_names()
    {
        MemoryErasureResultDto result = new(
            MemoryReviewStore.Saga,
            Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"),
            Replayed: false,
            new string('A', 64),
            new MemoryErasureLocalResultDto(
                MemoryLocalErasureOutcome.RowsRemovedScrubPending,
                [MemoryErasureScrubPendingReason.WalCheckpointPending],
                MemoryErasureWalCheckpointAttempt.Busy,
                ErasedItemCount: 1,
                RemovedRowCount: 4,
                RemovedLabelCount: 0,
                RemovedRetirementSuppressionCount: 0,
                SuppressionFingerprintRecorded: true),
            new MemoryErasureExternalExposureDto(
                MemoryExternalRevocation.NotPerformed,
                [new MemoryExternalExposureChannelDto(MemoryExternalChannel.InferenceProviderAuthorship, MemoryExternalEvidence.Known)]),
            [MemoryRetainedLocalCopy.SessionTranscripts],
            [MemoryErasureNote.OtherScopesUnaffected]);

        string json = JsonSerializer.Serialize(result, ArcanumJsonContext.Default.MemoryErasureResultDto);

        Assert.Contains("\"outcome\":\"RowsRemovedScrubPending\"", json, StringComparison.Ordinal);

        Assert.Contains("\"revocation\":\"NotPerformed\"", json, StringComparison.Ordinal);

        Assert.Contains("\"store\":\"Saga\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Apply recomputes the request digest from the fields it was sent, so the projection onto the
    /// prepared request has to carry every one of them and nothing from the token.
    /// </summary>
    [Fact]
    public void Covenant_erase_request_projects_every_prepared_field()
    {
        CovenantEraseRequest request = new(
            CovenantScope.Campaign,
            Guid.Parse("3F2504E0-4F89-11D3-9A0C-0305E82C3301"),
            "persona.tone",
            Guid.Parse("BBBBBBBB-2222-4222-8222-222222222222"),
            new CovenantEraseHeadExpectation(Guid.Parse("44444444-5555-4666-8777-888888888888"), 4),
            new CovenantEraseHeadExpectation(Guid.Parse("55555555-5555-4666-8777-888888888888"), 2),
            Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"),
            "token");

        Assert.Equal(
            new CovenantErasePrepareRequest(
                request.Scope,
                request.CampaignId,
                request.Key,
                request.EntryId,
                request.Confirmed,
                request.Proposed,
                request.MutationId),
            request.ToPrepareRequest());
    }

    private static void AssertPort(Type port, Type[] prepare, Type[] apply)
    {
        Assert.True(port.IsInterface, $"{port.Name} is not an interface.");

        MethodInfo[] methods = port.GetMethods();

        Assert.Equal(["ApplyAsync", "PrepareAsync"], methods.Select(static method => method.Name).Order(StringComparer.Ordinal));

        MethodInfo prepareMethod = Assert.Single(methods, static method => method.Name == "PrepareAsync");

        Assert.Equal(prepare, prepareMethod.GetParameters().Select(static parameter => parameter.ParameterType));

        Assert.Equal(typeof(Task<Result<MemoryErasurePreflightDto>>), prepareMethod.ReturnType);

        MethodInfo applyMethod = Assert.Single(methods, static method => method.Name == "ApplyAsync");

        Assert.Equal(apply, applyMethod.GetParameters().Select(static parameter => parameter.ParameterType));

        Assert.Equal(typeof(Task<Result<MemoryErasureResultDto>>), applyMethod.ReturnType);
    }
}

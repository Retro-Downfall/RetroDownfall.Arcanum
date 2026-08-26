using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Covenant;

[Collection(CovenantCanonicalCultureCollection.Name)]
public sealed class CovenantCanonicalEncoderTests
{
    [Fact]
    public void Fixed_width_integers_use_big_endian_twos_complement()
    {
        CovenantCanonicalEncoder encoder = new(64);

        encoder.WriteByte(0x7f);
        encoder.WriteSByte(-1);
        encoder.WriteUInt16(0x1234);
        encoder.WriteInt16(-2);
        encoder.WriteUInt32(0x12345678);
        encoder.WriteInt32(-3);
        encoder.WriteUInt64(0x0123456789abcdef);
        encoder.WriteInt64(-4);

        Assert.Equal(
            "7FFF1234FFFE12345678FFFFFFFD0123456789ABCDEFFFFFFFFFFFFFFFFC",
            Convert.ToHexString(encoder.WrittenSpan));
    }

    [Fact]
    public void Guid_uses_rfc_4122_network_byte_order()
    {
        CovenantCanonicalEncoder encoder = new(16);

        encoder.WriteGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(encoder.WrittenSpan));
    }

    [Fact]
    public void Binary64_is_finite_big_endian_and_normalizes_negative_zero()
    {
        CovenantCanonicalEncoder encoder = new(24);

        encoder.WriteBinary64(-0.0d);
        encoder.WriteBinary64(1.5d);
        encoder.WriteBinary64(-2.25d);

        Assert.Equal(
            "00000000000000003FF8000000000000C002000000000000",
            Convert.ToHexString(encoder.WrittenSpan));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Nonfinite_binary64_faults_without_publishable_partial_output(double value)
    {
        CovenantCanonicalEncoder encoder = new(16);

        encoder.WriteByte(0x42);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            encoder.WriteBinary64(value);
        });
        Assert.Equal(0, encoder.WrittenCount);
        Assert.Throws<InvalidOperationException>(() =>
        {
            encoder.WriteByte(0x43);
        });
    }

    [Fact]
    public void Byte_strings_and_strict_utf8_strings_have_u32_lengths()
    {
        CovenantCanonicalEncoder encoder = new(32);

        encoder.WriteBytes([0xde, 0xad, 0xbe, 0xef]);
        encoder.WriteUtf8("é😀");

        Assert.Equal(
            "00000004DEADBEEF00000006C3A9F09F9880",
            Convert.ToHexString(encoder.WrittenSpan));
    }

    [Fact]
    public void Fixed_32_writes_raw_bytes_while_arbitrary_bytes_retain_their_length()
    {
        byte[] fixedValue = Enumerable.Range(0, CovenantLimits.DigestBytes).Select(static value => (byte)value).ToArray();
        CovenantCanonicalEncoder encoder = new(40);

        encoder.WriteFixed32(fixedValue);
        encoder.WriteBytes([0xaa, 0xbb, 0xcc, 0xdd]);

        Assert.Equal(
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F00000004AABBCCDD",
            Convert.ToHexString(encoder.WrittenSpan));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Fixed_32_rejects_every_other_length_atomically(int length)
    {
        CovenantCanonicalEncoder encoder = new(64);

        encoder.WriteByte(0x42);

        Assert.Throws<ArgumentException>(() => encoder.WriteFixed32(new byte[length]));
        Assert.Equal(0, encoder.WrittenCount);
    }

    [Fact]
    public void Streaming_writer_matches_buffered_primitives_and_finalizes_once()
    {
        byte[] fixedValue = Enumerable.Range(0, CovenantLimits.DigestBytes).Select(static value => (byte)value).ToArray();
        CovenantCanonicalEncoder buffered = new(256);

        WriteComparisonFixture(buffered, fixedValue);

        CovenantDigest expected = new(SHA256.HashData(buffered.WrittenSpan));
        using CovenantCanonicalHashWriter streaming = new();

        WriteComparisonFixture(streaming, fixedValue);

        Assert.Equal(expected, streaming.FinalizeDigest());
        Assert.Throws<InvalidOperationException>(() => streaming.FinalizeDigest());
        Assert.Throws<InvalidOperationException>(() => streaming.WriteByte(0x01));
    }

    [Fact]
    public void Streaming_writer_does_not_allocate_a_whole_preimage_replay_buffer()
    {
        byte[] payload = new byte[60_000];

        using (CovenantCanonicalHashWriter warmup = new())
        {
            warmup.WriteBytes(payload);
            _ = warmup.FinalizeDigest();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        using CovenantCanonicalHashWriter writer = new();

        writer.WriteBytes(payload);
        _ = writer.FinalizeDigest();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < payload.Length / 2, $"Streaming hash allocated {allocated} bytes.");
    }

    [Fact]
    public void Streaming_writer_faults_atomically_for_fixed_utf8_and_nonfinite_inputs()
    {
        using CovenantCanonicalHashWriter fixedWriter = new();
        using CovenantCanonicalHashWriter utf8Writer = new();
        using CovenantCanonicalHashWriter finiteWriter = new();

        fixedWriter.WriteByte(0x42);
        utf8Writer.WriteByte(0x42);
        finiteWriter.WriteByte(0x42);

        Assert.Throws<ArgumentException>(() => fixedWriter.WriteFixed32(new byte[31]));
        Assert.Throws<EncoderFallbackException>(() => utf8Writer.WriteUtf8("\ud800"));
        Assert.Throws<ArgumentOutOfRangeException>(() => finiteWriter.WriteBinary64(double.PositiveInfinity));
        Assert.Throws<InvalidOperationException>(() => fixedWriter.FinalizeDigest());
        Assert.Throws<InvalidOperationException>(() => utf8Writer.FinalizeDigest());
        Assert.Throws<InvalidOperationException>(() => finiteWriter.FinalizeDigest());
    }

    [Fact]
    public void Streaming_writer_argument_and_callback_failures_latch_before_publication()
    {
        AssertFaulted(static writer => writer.WriteOptional<int>(1, null!));
        AssertFaulted(static writer => writer.WriteOptionalReference<string>("value", null!));
        AssertFaulted(static writer => writer.WriteList<int>(null!, static (valueWriter, value) => valueWriter.WriteInt32(value)));
        AssertFaulted(static writer => writer.WriteList([1], null!));
        AssertFaulted(static writer => writer.WriteCount((ulong)uint.MaxValue + 1));
        AssertFaulted(static writer => writer.WriteOptional<int>(1, static (valueWriter, value) =>
        {
            valueWriter.WriteInt32(value);

            throw new InvalidOperationException("callback failure");
        }));
        AssertFaulted(static writer => writer.WriteOptionalReference("value", static (valueWriter, value) =>
        {
            valueWriter.WriteUtf8(value);

            throw new InvalidOperationException("callback failure");
        }));
        AssertFaulted(static writer => writer.WriteList([1], static (valueWriter, value) =>
        {
            valueWriter.WriteInt32(value);

            throw new InvalidOperationException("callback failure");
        }));

        static void AssertFaulted(Action<CovenantCanonicalHashWriter> action)
        {
            using CovenantCanonicalHashWriter writer = new();

            writer.WriteByte(0x42);

            Assert.ThrowsAny<Exception>(() => action(writer));
            Assert.Throws<InvalidOperationException>(() => writer.WriteByte(0x43));
            Assert.Throws<InvalidOperationException>(() => writer.FinalizeDigest());
        }
    }

    [Fact]
    public void Streaming_writer_disposal_and_finalization_misuse_cannot_publish_another_digest()
    {
        CovenantCanonicalHashWriter disposed = new();

        disposed.WriteByte(0x42);
        disposed.Dispose();

        Assert.Throws<ObjectDisposedException>(() => disposed.WriteByte(0x43));
        Assert.Throws<ObjectDisposedException>(() => disposed.FinalizeDigest());

        using CovenantCanonicalHashWriter finalized = new();

        finalized.WriteByte(0x42);
        _ = finalized.FinalizeDigest();

        Assert.Throws<InvalidOperationException>(() => finalized.WriteByte(0x43));
        Assert.Throws<InvalidOperationException>(() => finalized.FinalizeDigest());
    }

    [Fact]
    public void Optional_reference_streaming_bytes_match_buffered_presence_frames()
    {
        CovenantCanonicalEncoder buffered = new(32);

        buffered.WriteOptionalReference<string>(null, static (writer, value) => writer.WriteUtf8(value));
        buffered.WriteOptionalReference("", static (writer, value) => writer.WriteUtf8(value));

        Assert.Equal("000100000000", Convert.ToHexString(buffered.WrittenSpan));

        using CovenantCanonicalHashWriter streaming = new();

        streaming.WriteOptionalReference<string>(null, static (writer, value) => writer.WriteUtf8(value));
        streaming.WriteOptionalReference("", static (writer, value) => writer.WriteUtf8(value));

        Assert.Equal(new CovenantDigest(SHA256.HashData(buffered.WrittenSpan)), streaming.FinalizeDigest());
    }

    [Fact]
    public void Malformed_utf16_faults_without_publishable_partial_output()
    {
        CovenantCanonicalEncoder encoder = new(32);

        encoder.WriteByte(0x42);

        Assert.Throws<EncoderFallbackException>(() =>
        {
            encoder.WriteUtf8("\ud800");
        });
        Assert.Equal(0, encoder.WrittenCount);
    }

    [Fact]
    public void Null_utf16_faults_without_publishable_partial_output()
    {
        CovenantCanonicalEncoder encoder = new(32);

        encoder.WriteByte(0x42);

        Assert.Throws<ArgumentNullException>(() =>
        {
            encoder.WriteUtf8(null!);
        });
        Assert.Equal(0, encoder.WrittenCount);
    }

    [Fact]
    public void Domain_tags_are_ascii_and_nul_framed()
    {
        CovenantCanonicalEncoder encoder = new(64);

        encoder.WriteDomainTag(CovenantDomainTag.Authored);

        Assert.Equal(
            "417263616E756D2E436F76656E616E742E417574686F7265642E763100",
            Convert.ToHexString(encoder.WrittenSpan));
    }

    [Fact]
    public void Optionals_and_lists_preserve_presence_and_input_order()
    {
        CovenantCanonicalEncoder encoder = new(64);

        encoder.WriteOptional<int>(null, static (writer, value) => writer.WriteInt32(value));
        encoder.WriteOptional<int>(42, static (writer, value) => writer.WriteInt32(value));
        encoder.WriteList(
            new[] { "second", "first" },
            static (writer, value) => writer.WriteUtf8(value));

        Assert.Equal(
            "00010000002A00000002000000067365636F6E64000000056669727374",
            Convert.ToHexString(encoder.WrittenSpan));
    }

    [Fact]
    public void Length_count_and_capacity_overflow_fail_atomically()
    {
        CovenantCanonicalEncoder countEncoder = new(8);

        countEncoder.WriteByte(0x42);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            countEncoder.WriteCount((ulong)uint.MaxValue + 1);
        });
        Assert.Equal(0, countEncoder.WrittenCount);

        CovenantCanonicalEncoder capacityEncoder = new(4);

        Assert.Throws<ArgumentException>(() =>
        {
            capacityEncoder.WriteBytes([0x01]);
        });
        Assert.Equal(0, capacityEncoder.WrittenCount);
    }

    [Fact]
    public void Policy_v1_domain_tags_are_exact_and_complete()
    {
        string[] expected =
        [
            "Arcanum.Covenant.Authored.v1",
            "Arcanum.Covenant.Fragment.v1",
            "Arcanum.Covenant.Section.v1",
            "Arcanum.Covenant.Request.v1",
            "Arcanum.Covenant.PreflightBody.v1",
            "Arcanum.Covenant.Authorization.v1",
            "Arcanum.Covenant.Mutation.v1",
            "Arcanum.Covenant.Snapshot.v1",
            "Arcanum.Covenant.Plan.v1",
            "Arcanum.Covenant.Materialization.v1",
            "Arcanum.Covenant.Sensitivity.v1",
            "Arcanum.Covenant.ArtifactLabel.v1",
            "Arcanum.Covenant.SessionTurnRequest.v1",
            "Arcanum.Covenant.SessionTurnExecution.v1",
            "Arcanum.Covenant.ProviderOptions.v1",
            "Arcanum.Covenant.ProviderCall.v1",
            "Arcanum.Covenant.Admission.v1",
            "Arcanum.Covenant.AttemptChain.v1",
            "Arcanum.Covenant.BranchChain.v1",
            "Arcanum.Covenant.WardEvidence.v1",
            "Arcanum.Covenant.ProviderDispatchEffect.v1",
            "Arcanum.Covenant.MaintenanceDispatchEffect.v1",
            "Arcanum.Covenant.ToolEgressEffect.v1",
            "Arcanum.Covenant.ManagedFileEffect.v1",
            "Arcanum.Covenant.BackupDisclosureEffect.v1",
            "Arcanum.Covenant.ExternalDisclosure.v1",
            "Arcanum.Covenant.DisclosureChain.v1",
            "Arcanum.Covenant.ExternalDisclosureState.v1",
            "Arcanum.Campaign.PathApplyRequest.v1",
            "Arcanum.Session.CampaignBindingApplyRequest.v1",
            "Arcanum.Covenant.FamilyReinitializeApplyRequest.v1",
            "Arcanum.Covenant.Receipt.v1",
            "Arcanum.Covenant.TurnAggregate.v1",
            "Arcanum.Covenant.CursorFilter.v1",
            "Arcanum.Covenant.DependentHeadVector.v1",
            "Arcanum.Covenant.CurationRequest.v1",
            "Arcanum.Covenant.CurationDependentHeads.v1",
            "Arcanum.Covenant.CurationEffect.v1"
        ];

        Assert.Equal(expected.Length, CovenantPolicyV1Manifest.DomainTags.Count);

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], CovenantPolicyV1Manifest.GetDomainTag(CovenantPolicyV1Manifest.DomainTags[index]));
        }
    }

    [Fact]
    public void Policy_v1_enum_codes_are_exact_and_closed()
    {
        AssertCodes(CovenantScope.Global, 1, CovenantScope.Campaign, 2);
        AssertCodes(SessionCampaignBindingKind.GlobalOnly, 1, SessionCampaignBindingKind.Campaign, 2, SessionCampaignBindingKind.LegacyUnresolved, 3);
        AssertCodes(CovenantLane.Confirmed, 1, CovenantLane.Proposed, 2);
        AssertCodes(CovenantOperation.Set, 1, CovenantOperation.Retire, 2);
        AssertCodes(CovenantOrigin.Operator, 1, CovenantOrigin.AgentProposed, 2, CovenantOrigin.AgentApproved, 3);
        AssertCodes(CovenantMutationKind.OperatorSet, 1, CovenantMutationKind.OperatorRetire, 2, CovenantMutationKind.AgentPropose, 3, CovenantMutationKind.AgentRetire, 4);
        AssertCodes(CovenantPlacement.GlobalConfirmed, 1, CovenantPlacement.CampaignConfirmed, 2, CovenantPlacement.CampaignProposed, 3);
        AssertCodes(CovenantPlanDecision.EligibleConfirmed, 1, CovenantPlanDecision.EligibleProposed, 2, CovenantPlanDecision.Shadowed, 3, CovenantPlanDecision.ReviewOnly, 4, CovenantPlanDecision.Quarantined, 5, CovenantPlanDecision.Invalid, 6);
        AssertCodes(CovenantAdmissionDecision.Admitted, 1, CovenantAdmissionDecision.Pressured, 2, CovenantAdmissionDecision.RequiredNoFit, 3);
        AssertCodes(CovenantAuthorizationMode.None, 0, CovenantAuthorizationMode.ApiMasterKey, 1, CovenantAuthorizationMode.WardInteractive, 2, CovenantAuthorizationMode.WardConfiguredAutoApproval, 3);
        AssertCodes(CovenantMutationOutcome.Applied, 1, CovenantMutationOutcome.NoChange, 2);
        AssertCodes(CovenantFinalOutcome.Completed, 1, CovenantFinalOutcome.Failed, 2, CovenantFinalOutcome.Cancelled, 3, CovenantFinalOutcome.Interrupted, 4);
        AssertCodes(AssistantFinalizationOrigin.Committed, 1, AssistantFinalizationOrigin.Discarded, 2, AssistantFinalizationOrigin.CommittedImported, 3, AssistantFinalizationOrigin.CommittedForked, 4);
        AssertCodes(CovenantWardDecision.Approved, 1, CovenantWardDecision.Denied, 2, CovenantWardDecision.Cancelled, 3);
        AssertCodes(CovenantProviderRole.System, 1, CovenantProviderRole.User, 2, CovenantProviderRole.Assistant, 3, CovenantProviderRole.Tool, 4);
        AssertCodes(CovenantProviderDispatchMode.Buffered, 1, CovenantProviderDispatchMode.Streaming, 2);
        AssertCodes(CovenantProviderContentPart.Text, 1, CovenantProviderContentPart.Binary, 2, CovenantProviderContentPart.ToolCall, 3, CovenantProviderContentPart.ToolResult, 4, CovenantProviderContentPart.Json, 5, CovenantProviderContentPart.Uri, 6, CovenantProviderContentPart.TextReasoning, 7);
        AssertCodes(CovenantToolRiskIdentity.Ordinary, 1, CovenantToolRiskIdentity.ConfiguredForbiddenArt, 2, CovenantToolRiskIdentity.IntrinsicForbiddenArt, 3, CovenantToolRiskIdentity.CovenantSensitiveEgress, 4);
        AssertCodes(ContentSensitivity.None, 0, ContentSensitivity.CovenantDerived, 1);
        AssertCodes(GenerationProvenanceMode.Exact, 1, GenerationProvenanceMode.BloomOverflow, 2);
        AssertCodes(SensitiveArtifactKind.AssistantEntry, 1, SensitiveArtifactKind.TurnEvidence, 2, SensitiveArtifactKind.Summary, 3, SensitiveArtifactKind.ToolArtifact, 4, SensitiveArtifactKind.SessionTitle, 5, SensitiveArtifactKind.Saga, 6, SensitiveArtifactKind.Lexicon, 7, SensitiveArtifactKind.Embedding, 8, SensitiveArtifactKind.SearchProjection, 9, SensitiveArtifactKind.AuditProjection, 10, SensitiveArtifactKind.Notification, 11, SensitiveArtifactKind.ManagedWorkspaceFile, 12, SensitiveArtifactKind.IdempotencyClaim, 13);
        AssertCodes(CovenantEgressDestination.Provider, 1, CovenantEgressDestination.ManagedWorkspaceFile, 2, CovenantEgressDestination.UnmanagedWorkspaceFile, 3, CovenantEgressDestination.Process, 4, CovenantEgressDestination.Network, 5, CovenantEgressDestination.ExternalMcp, 6, CovenantEgressDestination.Message, 7, CovenantEgressDestination.EncryptedBackup, 8);
        AssertCodes(CovenantDisclosureSubjectKind.Turn, 1, CovenantDisclosureSubjectKind.Operation, 2);
        AssertCodes(CovenantDisclosureRevocability.LocallyRevocable, 1, CovenantDisclosureRevocability.Nonrevocable, 2);
        AssertCodes(CovenantDisclosureCountKind.Exact, 1, CovenantDisclosureCountKind.LowerBound, 2);
        AssertCodes(SessionTurnSurface.Intelligence, 1, SessionTurnSurface.PromptExecute, 2, SessionTurnSurface.SpellExecute, 3);
        AssertCodes(CovenantContextPolicy.Default, 1, CovenantContextPolicy.None, 2);
        AssertCodes(CovenantToolPolicyCode.AllTools, 1, CovenantToolPolicyCode.NoTools, 2, CovenantToolPolicyCode.ReadOnlyTools, 3, CovenantToolPolicyCode.NoForbiddenArts, 4);
        AssertCodes(InvocationAttendance.Attended, 1, InvocationAttendance.Unattended, 2);
        AssertCodes(CovenantMaintenanceStep.Summary, 1, CovenantMaintenanceStep.Title, 2, CovenantMaintenanceStep.Saga, 3, CovenantMaintenanceStep.Lexicon, 4);
        AssertCodes(CovenantMaintenanceCheckpoint.Prepared, 1, CovenantMaintenanceCheckpoint.Committed, 2, CovenantMaintenanceCheckpoint.Failed, 3);
        AssertCodes(SessionTurnClaimState.PendingMaintenance, 1, SessionTurnClaimState.Begun, 2, SessionTurnClaimState.Committed, 3, SessionTurnClaimState.Discarded, 4, SessionTurnClaimState.Erased, 5, SessionTurnClaimState.RestoredInterrupted, 6);
        AssertCodes(BackupDisclosurePhase.SnapshotRead, 1, BackupDisclosurePhase.EncryptedArchiveWrite, 2);
        AssertCodes(CampaignPathIdentityOperation.Register, 1, CampaignPathIdentityOperation.Update, 2, CampaignPathIdentityOperation.RepairMoved, 3, CampaignPathIdentityOperation.Deregister, 4, CampaignPathIdentityOperation.TakeoverOrphan, 5);
        AssertCodes(ProviderToolChoice.Auto, 1, ProviderToolChoice.None, 2, ProviderToolChoice.Required, 3, ProviderToolChoice.Named, 4);
        AssertCodes(ProviderResponseFormat.Text, 1, ProviderResponseFormat.JsonObject, 2, ProviderResponseFormat.JsonSchema, 3);
        AssertCodes(CovenantReasoningEffort.None, 1, CovenantReasoningEffort.Minimal, 2, CovenantReasoningEffort.Low, 3, CovenantReasoningEffort.Medium, 4, CovenantReasoningEffort.High, 5, CovenantReasoningEffort.ExtraHigh, 6);
        AssertCodes(CovenantReasoningOutput.None, 1, CovenantReasoningOutput.Summary, 2, CovenantReasoningOutput.Full, 3);
        AssertCodes(CovenantReasoningWireDialect.Standard, 1, CovenantReasoningWireDialect.OpenRouter, 2, CovenantReasoningWireDialect.TopLevelReasoningBudget, 3, CovenantReasoningWireDialect.AnthropicThinking, 4);
        AssertCodes(CovenantTriStateBoolean.Absent, 0, CovenantTriStateBoolean.False, 1, CovenantTriStateBoolean.True, 2);
        AssertCodes(CovenantImageDetail.Auto, 1, CovenantImageDetail.Low, 2, CovenantImageDetail.High, 3);
        AssertCodes(CovenantPromptAttribution.DataHeader, 1, CovenantPromptAttribution.CovenantProposed, 2, CovenantPromptAttribution.DataBody, 3, CovenantPromptAttribution.WorkspaceContext, 4, CovenantPromptAttribution.CovenantConfirmed, 5, CovenantPromptAttribution.ContextBody, 6, CovenantPromptAttribution.SpecialOrUncovered, 7, CovenantPromptAttribution.Preamble, 8, CovenantPromptAttribution.Instructions, 9);
        AssertCodes(CovenantMaterializationContainer.SystemPrompt, 1, CovenantMaterializationContainer.MessagePart, 2);
        AssertCodes(CovenantMaterializationOccurrence.Utf16TextRange, 1, CovenantMaterializationOccurrence.WholeBinaryPart, 2);
        AssertCodes(CovenantMaterializationSourceRange.WholeSource, 1, CovenantMaterializationSourceRange.Utf16Range, 2, CovenantMaterializationSourceRange.ByteRange, 3);
        AssertCodes(CovenantCursorEndpoint.List, 1, CovenantCursorEndpoint.FtsQuery, 2, CovenantCursorEndpoint.FallbackQuery, 3, CovenantCursorEndpoint.Versions, 4);
        AssertCodes(CovenantCursorScopeSelection.Global, 1, CovenantCursorScopeSelection.Campaign, 2, CovenantCursorScopeSelection.AllScopes, 3);
        AssertCodes(CovenantLifecycle.Set, 1, CovenantLifecycle.Retired, 2, CovenantLifecycle.Any, 3);
        AssertCodes(CovenantCursorSort.CanonicalHeads, 1, CovenantCursorSort.FtsRank, 2, CovenantCursorSort.FallbackHeads, 3, CovenantCursorSort.VersionDescending, 4);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CovenantPolicyV1Manifest.GetCode((CovenantScope)0));
    }

    [Fact]
    public void Unsupported_wider_backed_enum_fails_before_code_encoding()
    {
        Assert.Throws<ArgumentException>(
            () => CovenantPolicyV1Manifest.GetCode(UnsupportedWiderBackedCode.One));
    }

    [Fact]
    public void Binary_encoding_is_identical_under_every_installed_culture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        const string expected = "417263616E756D2E436F76656E616E742E50726F76696465724F7074696F6E732E7631003FF800000000000000000006C3A9F09F9880";

        try
        {
            foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;

                CovenantCanonicalEncoder encoder = new(128);

                encoder.WriteDomainTag(CovenantDomainTag.ProviderOptions);
                encoder.WriteBinary64(1.5d);
                encoder.WriteUtf8("é😀");

                Assert.Equal(expected, Convert.ToHexString(encoder.WrittenSpan));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void AssertCodes<TEnum>(TEnum firstValue, uint firstCode, params object[] remainingValuesAndCodes)
        where TEnum : struct, Enum
    {
        Assert.Equal(firstCode, CovenantPolicyV1Manifest.GetCode(firstValue));

        for (int index = 0; index < remainingValuesAndCodes.Length; index += 2)
        {
            TEnum value = Assert.IsType<TEnum>(remainingValuesAndCodes[index]);
            uint expected = Convert.ToUInt32(remainingValuesAndCodes[index + 1], CultureInfo.InvariantCulture);

            Assert.Equal(expected, CovenantPolicyV1Manifest.GetCode(value));
        }
    }

    private static void WriteComparisonFixture(CovenantCanonicalEncoder writer, byte[] fixedValue)
    {
        writer.WriteDomainTag(CovenantDomainTag.ProviderCall);
        writer.WriteByte(0x7f);
        writer.WriteSByte(-1);
        writer.WriteUInt16(0x1234);
        writer.WriteInt16(-2);
        writer.WriteUInt32(0x10203040);
        writer.WriteInt32(-3);
        writer.WriteUInt64(0x0123456789abcdef);
        writer.WriteInt64(-5);
        writer.WriteGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        writer.WriteFixed32(fixedValue);
        writer.WriteUtf8("é😀");
        writer.WriteBytes([0xaa, 0xbb]);
        writer.WriteBinary64(-0d);
        writer.WriteCount(2);
        writer.WriteOptional<int>(null, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteOptional<int>(42, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteList(new[] { 3, 4 }, static (valueWriter, value) => valueWriter.WriteInt32(value));
    }

    private static void WriteComparisonFixture(CovenantCanonicalHashWriter writer, byte[] fixedValue)
    {
        writer.WriteDomainTag(CovenantDomainTag.ProviderCall);
        writer.WriteByte(0x7f);
        writer.WriteSByte(-1);
        writer.WriteUInt16(0x1234);
        writer.WriteInt16(-2);
        writer.WriteUInt32(0x10203040);
        writer.WriteInt32(-3);
        writer.WriteUInt64(0x0123456789abcdef);
        writer.WriteInt64(-5);
        writer.WriteGuid(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        writer.WriteFixed32(fixedValue);
        writer.WriteUtf8("é😀");
        writer.WriteBytes([0xaa, 0xbb]);
        writer.WriteBinary64(-0d);
        writer.WriteCount(2);
        writer.WriteOptional<int>(null, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteOptional<int>(42, static (valueWriter, value) => valueWriter.WriteInt32(value));
        writer.WriteList(new[] { 3, 4 }, static (valueWriter, value) => valueWriter.WriteInt32(value));
    }

    private enum UnsupportedWiderBackedCode : ushort
    {
        One = 1
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CovenantCanonicalCultureCollection
{
    public const string Name = "Covenant canonical culture";
}

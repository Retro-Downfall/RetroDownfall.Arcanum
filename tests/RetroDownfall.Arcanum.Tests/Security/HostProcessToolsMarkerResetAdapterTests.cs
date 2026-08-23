using System.Reflection;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The reset-only view of the host-tools marker slot, and the gate every mutation is held under.
/// </summary>
/// <remarks>
/// The adapter's whole job is mapping: it turns a Secrets-owned closed status into an Infrastructure
/// closed status without adding a step. So most of what is asserted here is what it refuses to
/// invent — an absence it was never shown, a delete against a capability somebody else minted, a
/// second durability barrier of its own.
/// </remarks>
public sealed class HostProcessToolsMarkerResetAdapterTests
{

    private const string Installation = "5f9d0dc7-0d4a-4a6a-9c2a-0f0b8b0d2a11";

    private static readonly Guid Transition = Guid.Parse("2b7f8a10-1c4b-4f3d-9a52-9e1c7a0d3b44");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void An_opened_slot_becomes_evidence_and_a_capability()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out byte[] payload);

        HostProcessToolsMarkerResetAdapter adapter = Adapter(slot);

        HostToolsMarkerPairResetOsOpenResult opened = adapter.OpenExact();

        Assert.Equal(HostToolsMarkerPairResetOsOpenStatus.Opened, opened.Status);

        HostProcessToolsOsMarkerEvidence evidence =
            Assert.IsType<HostProcessToolsOsMarkerEvidence>(opened.Evidence);

        Assert.Equal(Installation, evidence.InstallationIdentity);

        Assert.Equal(Transition, evidence.TransitionId);

        Assert.Equal(7ul, evidence.TaintMasterKeyVersion);

        Assert.Equal(HostProcessToolsMarkerPayload.DigestOf(payload), evidence.MarkerBytesDigest);

        Assert.Equal(
            HostProcessToolsMarkerStore.SlotIdentityDigest(),
            evidence.DurableIdentityDigest);

        opened.Capability!.Dispose();

    }

    [Fact]
    public void An_absent_slot_is_absent_and_an_unreachable_one_is_unavailable()
    {

        InMemoryHostProcessToolsMarkerSlot absent = new();

        Assert.Equal(
            HostToolsMarkerPairResetOsOpenStatus.Absent,
            Adapter(absent).OpenExact().Status);

        InMemoryHostProcessToolsMarkerSlot unreachable = new();

        unreachable.SetUnavailable(true);

        Assert.Equal(
            HostToolsMarkerPairResetOsOpenStatus.Unavailable,
            Adapter(unreachable).OpenExact().Status);

    }

    /// <summary>
    /// Definitely-present, definitely-unusable content is a mismatch and never an absence.
    /// </summary>
    /// <remarks>
    /// Reporting absence here would let a reset believe it had already removed a marker that is
    /// demonstrably still in the slot — which is the one wrong answer that lets an operation
    /// continue past evidence it should have stopped on.
    /// </remarks>
    [Fact]
    public void A_present_but_invalid_slot_maps_to_mismatch_without_minting_a_capability()
    {

        InMemoryHostProcessToolsMarkerSlot slot = new();

        slot.SetPresentInvalid();

        HostToolsMarkerPairResetOsOpenResult opened = Adapter(slot).OpenExact();

        Assert.Equal(HostToolsMarkerPairResetOsOpenStatus.Mismatch, opened.Status);

        Assert.Null(opened.Capability);

        Assert.Null(opened.Evidence);

    }

    [Theory]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    public void Malformed_marker_content_maps_to_mismatch(string encoded)
    {

        InMemoryHostProcessToolsMarkerSlot slot = new();

        slot.Set(Encoding.UTF8.GetBytes(encoded));

        Assert.Equal(
            HostToolsMarkerPairResetOsOpenStatus.Mismatch,
            Adapter(slot).OpenExact().Status);

    }

    /// <summary>
    /// Base64 with embedded whitespace decodes to the right payload and is still refused.
    /// </summary>
    /// <remarks>
    /// <c>Convert.FromBase64String</c> accepts it, which is exactly why this arm exists: two
    /// different stored texts would then produce the same evidence, and only one of them is what the
    /// transition wrote. A noncanonical encoding is a changed marker.
    /// </remarks>
    [Fact]
    public void Noncanonical_base64_of_a_valid_payload_maps_to_mismatch()
    {

        byte[] payload = Payload();

        string canonical = Convert.ToBase64String(payload);

        string spaced = canonical.Insert(canonical.Length / 2, "\n");

        Assert.Equal(payload, Convert.FromBase64String(spaced));

        InMemoryHostProcessToolsMarkerSlot slot = new();

        slot.Set(Encoding.UTF8.GetBytes(spaced));

        Assert.Equal(
            HostToolsMarkerPairResetOsOpenStatus.Mismatch,
            Adapter(slot).OpenExact().Status);

    }

    [Fact]
    public void Reopen_accepts_byte_identical_evidence_and_refuses_anything_else()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out byte[] payload);

        HostProcessToolsMarkerResetAdapter adapter = Adapter(slot);

        HostProcessToolsOsMarkerEvidence expected = Evidence(payload);

        HostToolsMarkerPairResetOsOpenResult reopened = adapter.ReopenExact(expected);

        Assert.Equal(HostToolsMarkerPairResetOsOpenStatus.Opened, reopened.Status);

        reopened.Capability!.Dispose();

        HostProcessToolsOsMarkerEvidence foreign = new(
            Installation,
            Guid.Parse("11112222-3333-4444-5555-666677778888"),
            7,
            expected.TaintFingerprint,
            expected.MarkerBytesDigest,
            expected.DurableIdentityDigest);

        Assert.Equal(
            HostToolsMarkerPairResetOsOpenStatus.Mismatch,
            adapter.ReopenExact(foreign).Status);

        Assert.Equal(
            HostToolsMarkerPairResetOsOpenStatus.Unavailable,
            adapter.ReopenExact(null!).Status);

    }

    [Fact]
    public async Task Compare_delete_removes_the_opened_record_and_the_slot_then_proves_absent()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out byte[] payload);

        HostProcessToolsMarkerResetAdapter adapter = Adapter(slot);

        HostToolsMarkerPairResetOsOpenResult opened = adapter.OpenExact();

        using IHostToolsMarkerPairResetOsCapability capability =
            Assert.IsAssignableFrom<IHostToolsMarkerPairResetOsCapability>(opened.Capability);

        Assert.Equal(
            HostToolsMarkerPairResetOsDeleteStatus.Deleted,
            await adapter.CompareDeleteExactAsync(capability, Evidence(payload), Token));

        Assert.Equal(
            HostToolsMarkerPairResetOsAbsenceStatus.Absent,
            await adapter.ProveExactAbsenceAsync(Token));

    }

    /// <summary>
    /// A capability another adapter minted is uncertainty, not authority.
    /// </summary>
    /// <remarks>
    /// It may well be a perfectly good capability over the same slot. That is precisely the case
    /// where deleting is wrong: the record it retains belongs to an operation this adapter knows
    /// nothing about, and destroying it would resolve somebody else's compare-and-delete for them.
    /// </remarks>
    [Fact]
    public async Task A_capability_from_another_adapter_is_refused_as_unavailable()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out byte[] payload);

        HostProcessToolsMarkerResetAdapter minting = Adapter(slot);

        HostProcessToolsMarkerResetAdapter other = Adapter(slot);

        HostToolsMarkerPairResetOsOpenResult opened = minting.OpenExact();

        using IHostToolsMarkerPairResetOsCapability capability =
            Assert.IsAssignableFrom<IHostToolsMarkerPairResetOsCapability>(opened.Capability);

        Assert.Equal(
            HostToolsMarkerPairResetOsDeleteStatus.Unavailable,
            await other.CompareDeleteExactAsync(capability, Evidence(payload), Token));

        // Refused, not consumed: the adapter that minted it can still finish its own operation.
        Assert.Equal(
            HostToolsMarkerPairResetOsDeleteStatus.Deleted,
            await minting.CompareDeleteExactAsync(capability, Evidence(payload), Token));

    }

    [Fact]
    public async Task A_disposed_or_already_consumed_capability_is_refused_as_unavailable()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out byte[] payload);

        HostProcessToolsMarkerResetAdapter adapter = Adapter(slot);

        IHostToolsMarkerPairResetOsCapability consumed =
            Assert.IsAssignableFrom<IHostToolsMarkerPairResetOsCapability>(
                adapter.OpenExact().Capability);

        Assert.Equal(
            HostToolsMarkerPairResetOsDeleteStatus.Deleted,
            await adapter.CompareDeleteExactAsync(consumed, Evidence(payload), Token));

        Assert.Equal(
            HostToolsMarkerPairResetOsDeleteStatus.Unavailable,
            await adapter.CompareDeleteExactAsync(consumed, Evidence(payload), Token));

        slot.Set(Encoding.UTF8.GetBytes(Convert.ToBase64String(payload)));

        IHostToolsMarkerPairResetOsCapability disposed =
            Assert.IsAssignableFrom<IHostToolsMarkerPairResetOsCapability>(
                adapter.OpenExact().Capability);

        disposed.Dispose();

        disposed.Dispose();

        Assert.Equal(
            HostToolsMarkerPairResetOsDeleteStatus.Unavailable,
            await adapter.CompareDeleteExactAsync(disposed, Evidence(payload), Token));

    }

    [Fact]
    public async Task A_delete_whose_expectation_changed_since_the_open_is_a_mismatch()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out _);

        HostProcessToolsMarkerResetAdapter adapter = Adapter(slot);

        using IHostToolsMarkerPairResetOsCapability capability =
            Assert.IsAssignableFrom<IHostToolsMarkerPairResetOsCapability>(
                adapter.OpenExact().Capability);

        byte[] different = HostProcessToolsMarkerPayload.Encode(
            Installation,
            Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00001111"),
            7,
            Fingerprint(3));

        Assert.Equal(
            HostToolsMarkerPairResetOsDeleteStatus.Mismatch,
            await adapter.CompareDeleteExactAsync(capability, Evidence(different), Token));

    }

    /// <summary>
    /// A slot that answers again during an absence proof is a mismatch, never an absence.
    /// </summary>
    [Fact]
    public async Task A_present_slot_during_the_absence_proof_maps_to_mismatch()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out _);

        Assert.Equal(
            HostToolsMarkerPairResetOsAbsenceStatus.Mismatch,
            await Adapter(slot).ProveExactAbsenceAsync(Token));

        InMemoryHostProcessToolsMarkerSlot unreachable = new();

        unreachable.SetUnavailable(true);

        Assert.Equal(
            HostToolsMarkerPairResetOsAbsenceStatus.Unavailable,
            await Adapter(unreachable).ProveExactAbsenceAsync(Token));

    }

    /// <summary>
    /// The gate is held across the complete delete, and a concurrent absence proof observes it.
    /// </summary>
    /// <remarks>
    /// The blocking record parks inside the Secrets-owned delete, which is the only place a second
    /// mutation could interleave. If the gate were taken around anything narrower — or taken by the
    /// absence proof and not the delete — the proof below would complete while the delete is still
    /// in flight and would be describing a slot mid-operation.
    /// </remarks>
    [Fact]
    public async Task The_shared_gate_is_held_across_the_complete_delete_and_absence_proof()
    {

        using BlockingSlot slot = new(Convert.ToBase64String(Payload()));

        HostProcessToolsMarkerMutationGate gate = new();

        HostProcessToolsMarkerResetAdapter adapter = new(slot, gate);

        using IHostToolsMarkerPairResetOsCapability capability =
            Assert.IsAssignableFrom<IHostToolsMarkerPairResetOsCapability>(
                adapter.OpenExact().Capability);

        Task<HostToolsMarkerPairResetOsDeleteStatus> deleting =
            Task.Run(() => adapter.CompareDeleteExactAsync(capability, Evidence(Payload()), Token));

        Assert.True(slot.Entered.Wait(TimeSpan.FromSeconds(10)));

        Task<HostToolsMarkerPairResetOsAbsenceStatus> proving =
            Task.Run(() => adapter.ProveExactAbsenceAsync(Token));

        // Excluded: the proof cannot even begin while the delete owns the gate.
        Assert.NotSame(
            proving,
            await Task.WhenAny(proving, Task.Delay(TimeSpan.FromMilliseconds(250), Token)));

        Assert.Equal(0, slot.AbsenceProofs);

        slot.Release.Set();

        Assert.Equal(HostToolsMarkerPairResetOsDeleteStatus.Deleted, await deleting);

        Assert.Equal(HostToolsMarkerPairResetOsAbsenceStatus.Absent, await proving);

        Assert.Equal(1, slot.AbsenceProofs);

    }

    /// <summary>
    /// The adapter's own copy of the marker is zeroed on disposal, and double disposal is safe.
    /// </summary>
    /// <remarks>
    /// This copy is the only plaintext marker Infrastructure ever holds. Reflection is the only way
    /// to observe it, and the alternative — asserting that the capability now refuses — passes just
    /// as well when the bytes are still sitting in a collected array.
    /// </remarks>
    [Fact]
    public void Disposal_zeroes_the_adapter_owned_copy_exactly_once()
    {

        InMemoryHostProcessToolsMarkerSlot slot = Seeded(out byte[] payload);

        IHostToolsMarkerPairResetOsCapability capability =
            Assert.IsAssignableFrom<IHostToolsMarkerPairResetOsCapability>(
                Adapter(slot).OpenExact().Capability);

        byte[] before = OwnedCopy(capability);

        Assert.Equal(Encoding.UTF8.GetBytes(Convert.ToBase64String(payload)), before);

        capability.Dispose();

        capability.Dispose();

        // The same array instance, now zeroed: the copy was cleared rather than replaced.
        Assert.Same(before, OwnedCopy(capability));

        Assert.Equal(new byte[before.Length], before);

    }

    private static byte[] OwnedCopy(IHostToolsMarkerPairResetOsCapability capability)
    {

        FieldInfo? backing = capability.GetType().GetField(
            "_encoded",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(backing);

        return Assert.IsType<byte[]>(backing.GetValue(capability));

    }

    [Fact]
    public async Task Cancellation_before_the_gate_is_honoured_and_reaches_no_backend()
    {

        InMemoryHostProcessToolsMarkerSlot slot = new();

        HostProcessToolsMarkerResetAdapter adapter = Adapter(slot);

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.ProveExactAbsenceAsync(cancellation.Token));

        Assert.Equal(0, slot.AbsenceReads);

    }

    private static HostProcessToolsMarkerResetAdapter Adapter(
        IHostProcessToolsMarkerCredentialCapabilitySource slot) =>
        new(slot, new HostProcessToolsMarkerMutationGate());

    private static InMemoryHostProcessToolsMarkerSlot Seeded(out byte[] payload)
    {

        payload = Payload();

        InMemoryHostProcessToolsMarkerSlot slot = new();

        slot.Set(Encoding.UTF8.GetBytes(Convert.ToBase64String(payload)));

        return slot;

    }

    private static byte[] Payload() =>
        HostProcessToolsMarkerPayload.Encode(Installation, Transition, 7, Fingerprint(3));

    private static HostProcessToolsOsMarkerEvidence Evidence(byte[] payload)
    {

        Assert.True(HostProcessToolsMarkerPayload.TryDecode(
            payload,
            out HostProcessToolsMarkerFields fields));

        return new HostProcessToolsOsMarkerEvidence(
            fields.InstallationIdentity,
            fields.TransitionId,
            fields.TaintMasterKeyVersion,
            fields.TaintFingerprint,
            HostProcessToolsMarkerPayload.DigestOf(payload),
            HostProcessToolsMarkerStore.SlotIdentityDigest());

    }

    private static CovenantDigest Fingerprint(byte seed)
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(seed + index);

        }

        return new CovenantDigest(bytes);

    }

    /// <summary>A backend that parks inside its delete until the test lets it finish.</summary>
    private sealed class BlockingSlot(string encoded)
        : IHostProcessToolsMarkerCredentialCapabilitySource, IDisposable
    {

        private bool _deleted;

        internal ManualResetEventSlim Entered { get; } = new(false);

        internal ManualResetEventSlim Release { get; } = new(false);

        internal int AbsenceProofs { get; private set; }

        public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot() =>
            _deleted
                ? HostProcessToolsMarkerCredentialOpenResult.Absent()
                : HostProcessToolsMarkerCredentialOpenResult.Opened(
                    HostProcessToolsMarkerCredentialCapability.CreateOwned(
                        Encoding.UTF8.GetBytes(encoded),
                        new BlockingRecord(this)));

        public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent()
        {

            AbsenceProofs++;

            return _deleted
                ? HostProcessToolsMarkerCredentialAbsenceResult.Absent()
                : HostProcessToolsMarkerCredentialAbsenceResult.Present();

        }

        public void Dispose()
        {

            Entered.Dispose();

            Release.Dispose();

        }

        private sealed class BlockingRecord(BlockingSlot owner)
            : IHostProcessToolsMarkerNativeRecordCapability
        {

            public HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
                ReadOnlySpan<byte> expectedEncodedSecretUtf8)
            {

                owner.Entered.Set();

                _ = owner.Release.Wait(TimeSpan.FromSeconds(30));

                owner._deleted = true;

                return HostProcessToolsMarkerCredentialDeleteStatus.Deleted;

            }

            public void Dispose()
            {
            }

        }

    }

}

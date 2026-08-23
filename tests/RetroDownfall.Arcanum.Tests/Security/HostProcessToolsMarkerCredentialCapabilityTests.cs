using System.Reflection;
using System.Text;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The Secrets-owned fixed-slot capability: what it copies, what it refuses, and what it zeroes.
/// </summary>
/// <remarks>
/// Every claim here is about a capability that has taken ownership of something — a byte array it
/// must zero, a platform record it must release exactly once, and a single comparison it must spend
/// exactly once. None of those is observable from the outside afterwards, so each is pinned through
/// a recording record that reports what it was actually asked to do.
/// </remarks>
public sealed class HostProcessToolsMarkerCredentialCapabilityTests
{

    private static readonly byte[] Value = Encoding.UTF8.GetBytes("marker-slot-value");

    [Fact]
    public void Create_owned_copies_the_value_and_leaves_the_caller_span_untouched()
    {

        byte[] source = [.. Value];

        RecordingNativeRecord record = new(HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        using HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(source, record);

        Assert.Equal(Value.Length, capability.EncodedSecretUtf8Length);

        // The capability holds a copy, so a caller that zeroes its own buffer — which it should —
        // does not empty the capability underneath itself.
        Array.Clear(source);

        byte[] destination = new byte[capability.EncodedSecretUtf8Length];

        Assert.True(capability.TryCopyEncodedSecretUtf8(destination, out int written));

        Assert.Equal(Value.Length, written);

        Assert.Equal(Value, destination);

    }

    [Theory]
    [InlineData(0)]
    [InlineData(HostProcessToolsMarkerCredentialCapability.MaxEncodedSecretUtf8Bytes + 1)]
    public void Create_owned_refuses_an_empty_or_over_bound_value(int length)
    {

        RecordingNativeRecord record = new(HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            HostProcessToolsMarkerCredentialCapability.CreateOwned(new byte[length], record));

    }

    [Fact]
    public void Create_owned_accepts_a_value_at_exactly_the_pinned_bound()
    {

        RecordingNativeRecord record = new(HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        using HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(
                new byte[HostProcessToolsMarkerCredentialCapability.MaxEncodedSecretUtf8Bytes],
                record);

        Assert.Equal(
            HostProcessToolsMarkerCredentialCapability.MaxEncodedSecretUtf8Bytes,
            capability.EncodedSecretUtf8Length);

    }

    [Fact]
    public void A_short_destination_receives_nothing_at_all()
    {

        using HostProcessToolsMarkerCredentialCapability capability = Capability(
            HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        byte[] destination = new byte[Value.Length - 1];

        Assert.False(capability.TryCopyEncodedSecretUtf8(destination, out int written));

        Assert.Equal(0, written);

        // A partial copy is worse than no copy: a prefix is indistinguishable from a whole value.
        Assert.Equal(new byte[Value.Length - 1], destination);

    }

    [Fact]
    public void Dispose_zeroes_the_owned_copy_and_releases_the_record_exactly_once()
    {

        RecordingNativeRecord record = new(HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(Value, record);

        capability.Dispose();

        capability.Dispose();

        Assert.Equal(1, record.Disposals);

        Assert.Equal(0, capability.EncodedSecretUtf8Length);

        Assert.False(capability.TryCopyEncodedSecretUtf8(new byte[Value.Length], out int written));

        Assert.Equal(0, written);

        // The reported length going to zero is a flag being set; this is the memory. Reflection is
        // the only way to see it, and without it a capability that stopped zeroing would leave the
        // marker in a collected array while every other assertion in this test still passed.
        Assert.Equal(new byte[Value.Length], OwnedCopy(capability));

    }

    /// <summary>The owned plaintext copy, read past the capability's own accessors.</summary>
    private static byte[] OwnedCopy(HostProcessToolsMarkerCredentialCapability capability)
    {

        FieldInfo? backing = typeof(HostProcessToolsMarkerCredentialCapability).GetField(
            "_encodedSecretUtf8",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(backing);

        return Assert.IsType<byte[]>(backing.GetValue(capability));

    }

    [Fact]
    public void Compare_delete_delegates_the_complete_native_operation_once()
    {

        RecordingNativeRecord record = new(HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        using HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(Value, record);

        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Deleted,
            capability.CompareDeleteExact(Value));

        Assert.Equal(1, record.CompareDeletes);

        Assert.Equal(Value, record.LastExpected);

        // Consumed. A second attempt is uncertainty, never a second delete and never an absence.
        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Unavailable,
            capability.CompareDeleteExact(Value));

        Assert.Equal(1, record.CompareDeletes);

    }

    // The status is internal to Secrets, so the theory carries its byte and widens it here rather
    // than making a public test signature depend on a type the assembly only sees through
    // InternalsVisibleTo.
    [Theory]
    [InlineData((byte)HostProcessToolsMarkerCredentialDeleteStatus.Mismatch)]
    [InlineData((byte)HostProcessToolsMarkerCredentialDeleteStatus.Unavailable)]
    public void Every_closed_outcome_consumes_the_capability(byte outcomeCode)
    {

        HostProcessToolsMarkerCredentialDeleteStatus outcome =
            (HostProcessToolsMarkerCredentialDeleteStatus)outcomeCode;

        RecordingNativeRecord record = new(outcome);

        using HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(Value, record);

        Assert.Equal(outcome, capability.CompareDeleteExact(Value));

        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Unavailable,
            capability.CompareDeleteExact(Value));

        Assert.Equal(1, record.CompareDeletes);

    }

    [Fact]
    public void A_caller_whose_expectation_never_matched_is_a_mismatch_and_reaches_no_record()
    {

        RecordingNativeRecord record = new(HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        using HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(Value, record);

        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Mismatch,
            capability.CompareDeleteExact(Encoding.UTF8.GetBytes("a-different-value")));

        Assert.Equal(0, record.CompareDeletes);

    }

    [Fact]
    public void A_native_record_that_throws_is_uncertainty_rather_than_an_escaping_diagnostic()
    {

        ThrowingNativeRecord record = new();

        using HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(Value, record);

        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Unavailable,
            capability.CompareDeleteExact(Value));

    }

    [Fact]
    public void A_disposed_capability_refuses_to_compare_delete()
    {

        RecordingNativeRecord record = new(HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        HostProcessToolsMarkerCredentialCapability capability =
            HostProcessToolsMarkerCredentialCapability.CreateOwned(Value, record);

        capability.Dispose();

        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Unavailable,
            capability.CompareDeleteExact(Value));

        Assert.Equal(0, record.CompareDeletes);

    }

    [Fact]
    public void Open_and_absence_result_factories_enforce_their_exact_nullable_shapes()
    {

        using HostProcessToolsMarkerCredentialCapability capability = Capability(
            HostProcessToolsMarkerCredentialDeleteStatus.Deleted);

        HostProcessToolsMarkerCredentialOpenResult opened =
            HostProcessToolsMarkerCredentialOpenResult.Opened(capability);

        Assert.Equal(HostProcessToolsMarkerCredentialOpenStatus.Opened, opened.Status);

        Assert.Same(capability, opened.Capability);

        foreach (HostProcessToolsMarkerCredentialOpenResult closed in new[]
        {
            HostProcessToolsMarkerCredentialOpenResult.Absent(),
            HostProcessToolsMarkerCredentialOpenResult.Unavailable(),
            HostProcessToolsMarkerCredentialOpenResult.PresentInvalid(),
        })
        {

            Assert.Null(closed.Capability);

            Assert.NotEqual(HostProcessToolsMarkerCredentialOpenStatus.Opened, closed.Status);

        }

        _ = Assert.Throws<ArgumentNullException>(() =>
            HostProcessToolsMarkerCredentialOpenResult.Opened(null!));

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Absent,
            HostProcessToolsMarkerCredentialAbsenceResult.Absent().Status);

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Present,
            HostProcessToolsMarkerCredentialAbsenceResult.Present().Status);

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Unavailable,
            HostProcessToolsMarkerCredentialAbsenceResult.Unavailable().Status);

    }

    /// <summary>
    /// The race the retained record exists to lose safely.
    /// </summary>
    /// <remarks>
    /// A replacement written after the capability was opened carries a later record identity. Its
    /// bytes are identical, so every comparison a value-keyed store could make says "same" — and
    /// deleting it would destroy an item this operation never compared and nobody authorized it to
    /// touch.
    /// </remarks>
    [Fact]
    public void A_byte_identical_live_replacement_is_not_deleted_by_the_retained_record()
    {

        InMemoryHostProcessToolsMarkerSlot slot = new();

        slot.Set(Value);

        HostProcessToolsMarkerCredentialOpenResult opened = slot.OpenFixedSlot();

        using HostProcessToolsMarkerCredentialCapability capability =
            Assert.IsType<HostProcessToolsMarkerCredentialCapability>(opened.Capability);

        slot.ReplaceForTest(Value);

        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Mismatch,
            capability.CompareDeleteExact(Value));

        // Still there: the replacement survived a delete aimed at the record it replaced.
        Assert.Equal(
            HostProcessToolsMarkerCredentialOpenStatus.Opened,
            slot.OpenFixedSlot().Status);

    }

    [Fact]
    public void The_retained_record_deletes_the_item_it_opened()
    {

        InMemoryHostProcessToolsMarkerSlot slot = new();

        slot.Set(Value);

        using HostProcessToolsMarkerCredentialCapability capability =
            Assert.IsType<HostProcessToolsMarkerCredentialCapability>(
                slot.OpenFixedSlot().Capability);

        Assert.Equal(
            HostProcessToolsMarkerCredentialDeleteStatus.Deleted,
            capability.CompareDeleteExact(Value));

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Absent,
            slot.ProveFixedSlotDurablyAbsent().Status);

    }

    [Fact]
    public void The_absence_proof_reads_twice_around_one_barrier()
    {

        InMemoryHostProcessToolsMarkerSlot slot = new();

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Absent,
            slot.ProveFixedSlotDurablyAbsent().Status);

        Assert.Equal(2, slot.AbsenceReads);

        Assert.Equal(1, slot.Barriers);

    }

    [Fact]
    public void A_present_or_unreachable_slot_is_never_proven_absent()
    {

        InMemoryHostProcessToolsMarkerSlot present = new();

        present.Set(Value);

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Present,
            present.ProveFixedSlotDurablyAbsent().Status);

        // Invalid data is still data: definitely-present is not absence either.
        InMemoryHostProcessToolsMarkerSlot invalid = new();

        invalid.SetPresentInvalid();

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Present,
            invalid.ProveFixedSlotDurablyAbsent().Status);

        Assert.Equal(
            HostProcessToolsMarkerCredentialOpenStatus.PresentInvalid,
            invalid.OpenFixedSlot().Status);

        InMemoryHostProcessToolsMarkerSlot unreachable = new();

        unreachable.SetUnavailable(true);

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Unavailable,
            unreachable.ProveFixedSlotDurablyAbsent().Status);

        Assert.Equal(
            HostProcessToolsMarkerCredentialOpenStatus.Unavailable,
            unreachable.OpenFixedSlot().Status);

    }

    [Fact]
    public void A_throwing_backend_surfaces_as_unavailable_rather_than_as_a_diagnostic()
    {

        HostProcessToolsMarkerCredentialCapabilitySource source = new(new ThrowingSlot());

        Assert.Equal(
            HostProcessToolsMarkerCredentialOpenStatus.Unavailable,
            source.OpenFixedSlot().Status);

        Assert.Equal(
            HostProcessToolsMarkerCredentialAbsenceStatus.Unavailable,
            source.ProveFixedSlotDurablyAbsent().Status);

    }

    private static HostProcessToolsMarkerCredentialCapability Capability(
        HostProcessToolsMarkerCredentialDeleteStatus outcome) =>
        HostProcessToolsMarkerCredentialCapability.CreateOwned(Value, new RecordingNativeRecord(outcome));

    private sealed class RecordingNativeRecord(HostProcessToolsMarkerCredentialDeleteStatus outcome)
        : IHostProcessToolsMarkerNativeRecordCapability
    {

        internal int CompareDeletes { get; private set; }

        internal int Disposals { get; private set; }

        internal byte[] LastExpected { get; private set; } = [];

        public HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
            ReadOnlySpan<byte> expectedEncodedSecretUtf8)
        {

            CompareDeletes++;

            LastExpected = expectedEncodedSecretUtf8.ToArray();

            return outcome;

        }

        public void Dispose() => Disposals++;

    }

    private sealed class ThrowingNativeRecord : IHostProcessToolsMarkerNativeRecordCapability
    {

        public HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
            ReadOnlySpan<byte> expectedEncodedSecretUtf8) =>
            throw new InvalidOperationException("The native diagnostic must not escape.");

        public void Dispose()
        {
        }

    }

    private sealed class ThrowingSlot : IHostProcessToolsMarkerCredentialCapabilitySource
    {

        public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot() =>
            throw new InvalidOperationException("The backend diagnostic must not escape.");

        public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent() =>
            throw new InvalidOperationException("The backend diagnostic must not escape.");

    }

}

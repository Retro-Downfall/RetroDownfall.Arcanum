using System.Buffers;

using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Lexicon;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>
/// The keyed, domain-separated digests selective erasure records and compares.
/// </summary>
/// <remarks>
/// <para>Every digest is HMAC-SHA256 under the installation's erasure key over one preimage. Each
/// preimage starts with its own ASCII domain label and a zero byte, then a fixed sequence of fields:
/// <c>u8</c>; <c>u32be</c>; <c>u64be</c>; <c>lp</c>, a <c>u32be</c> byte length and the bytes;
/// <c>guid</c>, sixteen RFC 4122 big-endian bytes; <c>opt</c>, a zero byte or a one byte and the value;
/// and <c>list</c>, a <c>u32be</c> count and the items. Text is strict UTF-8, which refuses an unpaired
/// surrogate rather than substituting for it.</para>
///
/// <para>The durable digests (request, subject, and effect) bind identifiers, counts, and flags only.
/// Content hashes, snapshot digests, names, and keys are compared at apply time and never enter them,
/// so a stored receipt cannot be used to test a guess about what was erased. Only the fingerprint and
/// the short-lived Saga content binding read content.</para>
///
/// <para>Row ids are bound in one canonical spelling, upper-case dashed, so a lower-case or undashed
/// stored id yields the same digest. A shape the grammar cannot describe throws
/// <see cref="ArgumentException"/> rather than producing a digest of something else.</para>
/// </remarks>
public static class MemoryErasureDigestGrammar
{
    public const int KeyBytes = 32;

    public const int KeyIdBytes = 16;

    public const int DigestBytes = 32;

    private const int EvidenceChannels = 5;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> KeyIdLabel => "Arcanum.MemoryErasure.KeyId.v1\0"u8;

    private static ReadOnlySpan<byte> FingerprintLabel => "Arcanum.MemoryErasure.Fingerprint.v1\0"u8;

    private static ReadOnlySpan<byte> RequestLabel => "Arcanum.MemoryErasure.Request.v1\0"u8;

    private static ReadOnlySpan<byte> SubjectLabel => "Arcanum.MemoryErasure.Subject.v1\0"u8;

    private static ReadOnlySpan<byte> EffectLabel => "Arcanum.MemoryErasure.Effect.v1\0"u8;

    private static ReadOnlySpan<byte> ContentBindingLabel => "Arcanum.MemoryErasure.ContentBinding.v1\0"u8;

    /// <summary>The first sixteen bytes of the key's labelled HMAC, which name the key without revealing it.</summary>
    public static byte[] KeyId(ReadOnlySpan<byte> key)
    {
        RequireKey(key);

        Span<byte> mac = stackalloc byte[DigestBytes];

        HMACSHA256.HashData(key, KeyIdLabel, mac);

        return mac[..KeyIdBytes].ToArray();
    }

    /// <summary>One erased identity in one exact scope.</summary>
    public static byte[] Fingerprint(ReadOnlySpan<byte> key, MemoryErasureIdentity identity)
    {
        RequireKey(key);

        identity.ThrowIfInvalid();

        using Preimage preimage = new(FingerprintLabel);

        preimage.WriteByte((byte)identity.Store);

        preimage.WriteByte((byte)identity.Scope);

        preimage.WriteOptionalGuid(identity.CampaignId);

        preimage.WriteText(identity.Value);

        return preimage.Mac(key);
    }

    /// <summary>A Saga erase request: the memory and, when it has one, the claim version it expects.</summary>
    public static byte[] SagaRequest(ReadOnlySpan<byte> key, Guid mutationId, string memoryId, Guid? expectedClaimVersionId)
    {
        RequireKey(key);

        string rowId = CanonicalRowId(memoryId);

        using Preimage preimage = new(RequestLabel);

        preimage.WriteByte((byte)MemoryReviewStore.Saga);

        preimage.WriteGuid(mutationId);

        preimage.WriteText(rowId);

        preimage.WriteOptionalGuid(expectedClaimVersionId);

        return preimage.Mac(key);
    }

    /// <summary>
    /// A Lexicon erase request: the exact entry, its curation generation, its Annals head version, and
    /// its label, and nothing of its name, snapshot, lifecycle, or content hashes.
    /// </summary>
    public static byte[] LexiconRequest(ReadOnlySpan<byte> key, Guid mutationId, LexiconCurationTarget target)
    {
        RequireKey(key);

        ArgumentNullException.ThrowIfNull(target);

        if (target.Validate().IsFailure)
        {
            throw new ArgumentException("The Lexicon erase target is incomplete or malformed.", nameof(target));
        }

        // The version id is read only when a head is present. Validate accepts any nonblank text, and a
        // stored Annals id may be lower-case or undashed, so it is parsed rather than bound as text.
        Guid? annalVersionId = target.AnnalHead.IsPresent
            ? ParseGuid(target.AnnalHead.VersionId!, nameof(target))
            : null;

        MemoryErasureScopeKind scope = target.Scope.Kind == LexiconScopeKind.Campaign
            ? MemoryErasureScopeKind.Campaign
            : MemoryErasureScopeKind.Global;

        using Preimage preimage = new(RequestLabel);

        preimage.WriteByte((byte)MemoryReviewStore.Lexicon);

        preimage.WriteGuid(mutationId);

        preimage.WriteByte((byte)scope);

        preimage.WriteOptionalGuid(target.Scope.CampaignId);

        preimage.WriteGuid(target.EntryId);

        preimage.WriteUInt64((ulong)target.CurationGeneration);

        preimage.WriteOptionalGuid(annalVersionId);

        if (target.SensitivityLabel.IsPresent)
        {
            preimage.WriteByte(1);

            preimage.WriteGuid(target.SensitivityLabel.LabelId!.Value);

            preimage.WriteUInt64(target.SensitivityLabel.ArtifactRevision!.Value);
        }
        else
        {
            preimage.WriteByte(0);
        }

        return preimage.Mac(key);
    }

    /// <summary>A Covenant erase request: the exact entry and both lane heads, and never its key.</summary>
    public static byte[] CovenantRequest(ReadOnlySpan<byte> key, Guid mutationId, CovenantErasePrepareRequest request)
    {
        RequireKey(key);

        ArgumentNullException.ThrowIfNull(request);

        MemoryErasureScopeKind scope = MemoryErasureIdentity.ScopeOf(request.Scope);

        MemoryErasureIdentity.RequireCampaignPairing(scope, request.CampaignId);

        if (scope == MemoryErasureScopeKind.Global && request.Proposed is not null)
        {
            throw new ArgumentException("A Global Covenant entry has no Proposed lane to expect.", nameof(request));
        }

        using Preimage preimage = new(RequestLabel);

        preimage.WriteByte((byte)MemoryReviewStore.Covenant);

        preimage.WriteGuid(mutationId);

        preimage.WriteByte((byte)scope);

        preimage.WriteOptionalGuid(request.CampaignId);

        preimage.WriteGuid(request.EntryId);

        WriteHead(preimage, request.Confirmed);

        WriteHead(preimage, request.Proposed);

        return preimage.Mac(key);
    }

    /// <summary>One erased row, which a receipt records so a later prepare can answer that it is gone.</summary>
    public static byte[] Subject(ReadOnlySpan<byte> key, MemoryReviewStore store, string rowId)
    {
        RequireKey(key);

        RequireStore(store);

        string canonical = CanonicalRowId(rowId);

        using Preimage preimage = new(SubjectLabel);

        preimage.WriteByte((byte)store);

        preimage.WriteText(canonical);

        return preimage.Mac(key);
    }

    /// <summary>Everything one plan would do, with its rows sorted and each version moved alongside its row.</summary>
    public static byte[] Effect(ReadOnlySpan<byte> key, MemoryErasureEffectFacts facts)
    {
        RequireKey(key);

        ArgumentNullException.ThrowIfNull(facts);

        (string RowId, Guid? Version)[] rows = OrderedRows(facts);

        byte[] targetCodes = TargetCodes(facts.Targets);

        RequireEffectShape(facts);

        using Preimage preimage = new(EffectLabel);

        preimage.WriteByte((byte)facts.Store);

        preimage.WriteUInt32((uint)rows.Length);

        foreach ((string rowId, _) in rows)
        {
            preimage.WriteText(rowId);
        }

        preimage.WriteUInt32((uint)rows.Length);

        foreach ((_, Guid? version) in rows)
        {
            preimage.WriteOptionalGuid(version);
        }

        preimage.WriteUInt32((uint)targetCodes.Length);

        for (int index = 0; index < targetCodes.Length; index++)
        {
            preimage.WriteByte(targetCodes[index]);

            preimage.WriteUInt64((ulong)facts.Targets[index].Rows);
        }

        preimage.WriteUInt32((uint)facts.Labels);

        preimage.WriteUInt32((uint)facts.RetirementSuppressions);

        preimage.WriteByte((byte)facts.Flags);

        if (facts.Covenant is { } covenant)
        {
            preimage.WriteGuid(covenant.DatasetGeneration);

            preimage.WriteUInt64((ulong)covenant.KeyEpoch);

            preimage.WriteUInt64((ulong)covenant.KeyReclamationEpoch);
        }

        foreach (MemoryExternalEvidence evidence in facts.Evidence)
        {
            preimage.WriteByte((byte)evidence);
        }

        preimage.WriteUInt32((uint)facts.RetainedCopiesMask);

        return preimage.Mac(key);
    }

    /// <summary>
    /// A Saga memory's exact stored content bound to its row. Carried only in the short-lived preflight
    /// token and recomputed over the live row at apply; never stored.
    /// </summary>
    public static byte[] SagaContentBinding(ReadOnlySpan<byte> key, string memoryId, string content)
    {
        RequireKey(key);

        ArgumentNullException.ThrowIfNull(content);

        string rowId = CanonicalRowId(memoryId);

        using Preimage preimage = new(ContentBindingLabel);

        preimage.WriteByte((byte)MemoryReviewStore.Saga);

        preimage.WriteText(rowId);

        preimage.WriteText(content);

        return preimage.Mac(key);
    }

    /// <summary>The one spelling every digest binds a row id in: upper-case, dashed.</summary>
    /// <exception cref="ArgumentException">The row id is not a GUID.</exception>
    public static string CanonicalRowId(string rowId)
    {
        ArgumentNullException.ThrowIfNull(rowId);

        return ParseGuid(rowId, nameof(rowId)).ToString("D").ToUpperInvariant();
    }

    private static (string RowId, Guid? Version)[] OrderedRows(MemoryErasureEffectFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts.RowIds);

        ArgumentNullException.ThrowIfNull(facts.Versions);

        if (facts.RowIds.Count != facts.Versions.Count)
        {
            throw new ArgumentException("An erasure effect names exactly one optional version per row.", nameof(facts));
        }

        (string RowId, Guid? Version)[] rows = new (string, Guid?)[facts.RowIds.Count];

        for (int index = 0; index < rows.Length; index++)
        {
            rows[index] = (CanonicalRowId(facts.RowIds[index]), facts.Versions[index]);
        }

        Array.Sort(rows, static (left, right) => string.CompareOrdinal(left.RowId, right.RowId));

        for (int index = 1; index < rows.Length; index++)
        {
            if (string.Equals(rows[index - 1].RowId, rows[index].RowId, StringComparison.Ordinal))
            {
                throw new ArgumentException("An erasure effect names each row once.", nameof(facts));
            }
        }

        return rows;
    }

    private static byte[] TargetCodes(IReadOnlyList<MemoryErasureTableCount> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        byte[] codes = new byte[targets.Count];

        HashSet<byte> seen = [];

        for (int index = 0; index < codes.Length; index++)
        {
            MemoryErasureTableCount target = targets[index] ?? throw new ArgumentException("An erasure plan target is never null.", nameof(targets));

            codes[index] = MemoryErasureTableCodes.For(target.Table);

            if (target.Rows < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targets), "An erasure plan target never removes a negative number of rows.");
            }

            // A table listed twice would be counted twice. Refusing it makes a caller that appends a
            // table its plan runner already listed fail loudly instead of doubling that table's count.
            if (!seen.Add(codes[index]))
            {
                throw new ArgumentException("An erasure plan lists each target table once.", nameof(targets));
            }
        }

        return codes;
    }

    private static void RequireEffectShape(MemoryErasureEffectFacts facts)
    {
        RequireStore(facts.Store);

        if (facts.Labels < 0 || facts.RetirementSuppressions < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(facts), "An erasure effect never removes a negative number of labels or suppressions.");
        }

        MemoryErasureEffectFlags allowed = facts.Store switch
        {
            MemoryReviewStore.Saga => MemoryErasureEffectFlags.Pinned,
            MemoryReviewStore.Lexicon => MemoryErasureEffectFlags.Pinned | MemoryErasureEffectFlags.GlobalEntryResurfaces,
            _ => MemoryErasureEffectFlags.Pinned
                | MemoryErasureEffectFlags.ReclaimsKey
                | MemoryErasureEffectFlags.RetainsCampaignMask
                | MemoryErasureEffectFlags.GlobalConfirmedResurfaces,
        };

        if ((facts.Flags & ~allowed) != 0)
        {
            throw new ArgumentException("An erasure effect carries a flag its store never sets.", nameof(facts));
        }

        if (facts.Store == MemoryReviewStore.Covenant)
        {
            CovenantErasureEffectFacts covenant = facts.Covenant
                ?? throw new ArgumentException("A Covenant erasure effect carries its Covenant facts.", nameof(facts));

            if (covenant.KeyEpoch < 0 || covenant.KeyReclamationEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(facts), "A Covenant erasure effect never binds a negative epoch.");
            }
        }
        else if (facts.Covenant is not null)
        {
            throw new ArgumentException("Only a Covenant erasure effect carries Covenant facts.", nameof(facts));
        }

        ArgumentNullException.ThrowIfNull(facts.Evidence);

        if (facts.Evidence.Count != EvidenceChannels)
        {
            throw new ArgumentException("An erasure effect carries exactly one evidence value per external channel.", nameof(facts));
        }

        foreach (MemoryExternalEvidence evidence in facts.Evidence)
        {
            if (evidence is not (MemoryExternalEvidence.Known
                or MemoryExternalEvidence.ReceiptWindow
                or MemoryExternalEvidence.NotRecorded
                or MemoryExternalEvidence.NotApplicable))
            {
                throw new ArgumentOutOfRangeException(nameof(facts), "An erasure effect carries only recognized evidence values.");
            }
        }

        _ = MemoryRetainedLocalCopies.FromMask(facts.RetainedCopiesMask);
    }

    private static void WriteHead(Preimage preimage, CovenantEraseHeadExpectation? head)
    {
        if (head is null)
        {
            preimage.WriteByte(0);

            return;
        }

        if (head.LaneRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(head), "A Covenant lane revision is never negative.");
        }

        preimage.WriteByte(1);

        preimage.WriteGuid(head.VersionId);

        preimage.WriteUInt64((ulong)head.LaneRevision);
    }

    private static Guid ParseGuid(string value, string parameterName)
    {
        try
        {
            return Guid.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("An erasure row or version id is a GUID.", parameterName, exception);
        }
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
        {
            throw new ArgumentException($"An erasure key is exactly {KeyBytes} bytes.", nameof(key));
        }
    }

    private static void RequireStore(MemoryReviewStore store)
    {
        if (store is not (MemoryReviewStore.Saga or MemoryReviewStore.Lexicon or MemoryReviewStore.Covenant))
        {
            throw new ArgumentOutOfRangeException(nameof(store), "An erasure digest names a recognized store.");
        }
    }

    /// <summary>
    /// One preimage under assembly, in a pooled buffer that is cleared whenever it is released.
    /// </summary>
    /// <remarks>
    /// A preimage can hold content, so every buffer it ever occupied is zeroed before it goes back to
    /// the pool, including the smaller one a growth copied out of. A plain growable writer would leave
    /// that earlier copy behind uncleared.
    /// </remarks>
    private sealed class Preimage : IDisposable
    {
        private byte[] _buffer;

        private int _length;

        public Preimage(ReadOnlySpan<byte> label)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(256);

            Append(label);
        }

        public void WriteByte(byte value) => Reserve(1)[0] = value;

        public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Reserve(4), value);

        public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Reserve(8), value);

        public void WriteGuid(Guid value) => _ = value.TryWriteBytes(Reserve(16), bigEndian: true, out _);

        public void WriteOptionalGuid(Guid? value)
        {
            WriteByte(value is null ? (byte)0 : (byte)1);

            if (value is { } present)
            {
                WriteGuid(present);
            }
        }

        /// <summary>Writes <c>lp(strict UTF-8 bytes)</c>.</summary>
        public void WriteText(string value)
        {
            int count = Strict.GetByteCount(value);

            WriteUInt32((uint)count);

            _ = Strict.GetBytes(value, Reserve(count));
        }

        public byte[] Mac(ReadOnlySpan<byte> key) => HMACSHA256.HashData(key, _buffer.AsSpan(0, _length));

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);

            _buffer = [];

            _length = 0;
        }

        private void Append(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

        private Span<byte> Reserve(int count)
        {
            if (_buffer.Length - _length < count)
            {
                byte[] grown = ArrayPool<byte>.Shared.Rent(checked(Math.Max(_buffer.Length * 2, _length + count)));

                _buffer.AsSpan(0, _length).CopyTo(grown);

                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);

                _buffer = grown;
            }

            Span<byte> reserved = _buffer.AsSpan(_length, count);

            _length += count;

            return reserved;
        }
    }
}

using System.Buffers.Binary;

using System.Buffers.Text;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionJournalAuthenticationTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-offline-transition-journal-" + Guid.NewGuid().ToString("N"));

    private readonly string _guarded;

    private readonly ArcanumMaintenanceLock _lock;

    private readonly InMemoryOsCredentialStore _credentials = new();

    public GrimoireOfflineTransitionJournalAuthenticationTests()
    {

        Directory.CreateDirectory(_root);

        _guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_guarded);

        _lock = ArcanumMaintenanceLock.TryAcquire(_guarded)
            ?? throw new InvalidOperationException("The test could not take its maintenance lock.");

    }

    public void Dispose()
    {

        _lock.Dispose();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public void Location_digest_binds_profile_parent_identity_and_fixed_leaf_without_path_text()
    {

        CovenantDigest profile = Digest(1);

        CovenantDigest parent = Digest(2);

        CovenantDigest baseline = Value(GrimoireOfflineTransitionJournalAuthenticator.JournalLocation(
            profile,
            parent,
            "offline-transition.v1.json"));

        Assert.Equal(baseline, Value(GrimoireOfflineTransitionJournalAuthenticator.JournalLocation(
            profile,
            parent,
            "offline-transition.v1.json")));

        Assert.NotEqual(baseline, Value(GrimoireOfflineTransitionJournalAuthenticator.JournalLocation(
            Digest(3),
            parent,
            "offline-transition.v1.json")));

        Assert.NotEqual(baseline, Value(GrimoireOfflineTransitionJournalAuthenticator.JournalLocation(
            profile,
            Digest(4),
            "offline-transition.v1.json")));

        Assert.NotEqual(baseline, Value(GrimoireOfflineTransitionJournalAuthenticator.JournalLocation(
            profile,
            parent,
            "offline-transition.v1.json.other")));

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.JournalLocation(
            profile,
            parent,
            "../offline-transition.v1.json").IsFailure);

    }

    [Fact]
    public void Seal_and_open_round_trip_exact_opaque_payload_bytes()
    {

        byte[] payload = RandomNumberGenerator.GetBytes(1024);

        GrimoireOfflineTransitionEnvelopeV1 envelope;

        using (GrimoireOfflineTransitionJournalKeyLease sealing = CreateLease())
        {

            envelope = Value(GrimoireOfflineTransitionJournalAuthenticator.Seal(
                sealing,
                Digest(1),
                Installation,
                1,
                Operation,
                GrimoireOfflineTransitionKind.CovenantReset,
                7,
                1,
                Digest(2),
                Digest(3),
                payload));

        }

        using GrimoireOfflineTransitionJournalKeyLease opening = OpenLease();

        byte[] opened = Value(GrimoireOfflineTransitionJournalAuthenticator.Open(
            opening,
            Digest(1),
            Installation,
            Digest(3),
            envelope));

        Assert.Equal(payload, opened);

    }

    [Fact]
    public void Seal_uses_the_exact_specified_associated_data_bytes()
    {

        byte[] key = Enumerable.Range(0, GrimoireOfflineTransitionJournalAuthenticator.KeyBytes)
            .Select(static value => (byte)value)
            .ToArray();

        GrimoireOfflineTransitionEnvelopeV1 envelope;

        using (GrimoireOfflineTransitionJournalKeyLease lease =
               GrimoireOfflineTransitionJournalKeyLease.Mint(key.ToArray()))
        {

            envelope = Value(GrimoireOfflineTransitionJournalAuthenticator.Seal(
                lease,
                Digest(1),
                Installation,
                1,
                Operation,
                GrimoireOfflineTransitionKind.CovenantReset,
                7,
                1,
                Digest(2),
                Digest(3),
                [4, 5, 6]));

        }

        byte[] nonce = Decode(envelope.NonceBase64Url);

        byte[] ciphertext = Decode(envelope.CiphertextBase64Url);

        byte[] tag = Decode(envelope.AuthenticationTagBase64Url);

        byte[] plaintext = new byte[ciphertext.Length];

        byte[] aad = ExpectedAssociatedData(envelope);

        Assert.Equal(
            Encoding.ASCII.GetByteCount(GrimoireOfflineTransitionJournalAuthenticator.EnvelopeAssociatedDataDomain)
            + 1 + 32 + 16 + 8 + 16 + 1 + 1 + 8 + 32 + 32,
            aad.Length);

        using AesGcm aes = new(key, GrimoireOfflineTransitionJournalAuthenticator.TagBytes);

        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);

        CryptographicOperations.ZeroMemory(plaintext);

        CryptographicOperations.ZeroMemory(key);

    }

    [Fact]
    public void Transition_journal_limits_are_exact_and_accept_their_legal_edges()
    {

        Assert.Equal(1, GrimoireOfflineTransitionJournalAuthenticator.EnvelopeVersion);

        Assert.Equal(1, GrimoireOfflineTransitionJournalAuthenticator.AnchorVersion);

        Assert.Equal(32, GrimoireOfflineTransitionJournalAuthenticator.KeyBytes);

        Assert.Equal(12, GrimoireOfflineTransitionJournalAuthenticator.NonceBytes);

        Assert.Equal(16, GrimoireOfflineTransitionJournalAuthenticator.TagBytes);

        Assert.Equal(256 * 1024, GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes);

        Assert.Equal(512 * 1024, GrimoireOfflineTransitionJournalAuthenticator.MaxPlaintextBytes);

        Assert.Equal(1024 * 1024, GrimoireOfflineTransitionJournalAuthenticator.MaxJournalFileBytes);

        Assert.Equal(2048, GrimoireOfflineTransitionJournalAuthenticator.MaxAnchorCharacters);

        Assert.Equal(1_000_000UL, GrimoireOfflineTransitionJournalAuthenticator.MaxRevision);

        Assert.Equal(1_000_000UL, GrimoireOfflineTransitionJournalAuthenticator.MaxSlotEpoch);

        using GrimoireOfflineTransitionJournalKeyLease lease = CreateLease();

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.Seal(
            lease,
            Digest(1),
            Installation,
            GrimoireOfflineTransitionJournalAuthenticator.MaxSlotEpoch,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            GrimoireOfflineTransitionJournalAuthenticator.MaxRevision,
            Digest(2),
            Digest(3),
            new byte[GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes]).IsSuccess);

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.ValidateAnchor(
            ActiveAnchor(
                GrimoireOfflineTransitionJournalAuthenticator.MaxRevision,
                Digest(4),
                GrimoireOfflineTransitionJournalAuthenticator.MaxSlotEpoch)).IsSuccess);

        GrimoireOfflineTransitionEnvelopeV1 maximumPlaintextEnvelope = new(
            GrimoireOfflineTransitionJournalAuthenticator.EnvelopeVersion,
            Digest(1),
            Installation,
            1,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            1,
            Digest(2),
            Digest(3),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12)),
            Base64Url.EncodeToString(
                RandomNumberGenerator.GetBytes(
                    GrimoireOfflineTransitionJournalAuthenticator.MaxPlaintextBytes)),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(
            maximumPlaintextEnvelope).IsSuccess);

    }

    [Theory]
    [InlineData("profile")]
    [InlineData("installation")]
    [InlineData("epoch")]
    [InlineData("operation")]
    [InlineData("kind")]
    [InlineData("payload-version")]
    [InlineData("revision")]
    [InlineData("previous")]
    [InlineData("location")]
    public void Envelope_aad_rejects_each_changed_header_field(string header)
    {

        GrimoireOfflineTransitionEnvelopeV1 envelope = Seal();

        GrimoireOfflineTransitionEnvelopeV1 changed = header switch
        {
            "profile" => envelope with { ProfileNamespaceDigest = Digest(10) },
            "installation" => envelope with { InstallationId = Guid.NewGuid() },
            "epoch" => envelope with { SlotEpoch = 2 },
            "operation" => envelope with { OperationId = Guid.NewGuid() },
            "kind" => envelope with { Kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure },
            "payload-version" => envelope with { PayloadVersion = 8 },
            "revision" => envelope with { Revision = 2 },
            "previous" => envelope with { PreviousEnvelopeDigest = Digest(9) },
            "location" => envelope with { JournalLocationDigest = Digest(8) },
            _ => throw new ArgumentOutOfRangeException(nameof(header)),
        };

        using GrimoireOfflineTransitionJournalKeyLease opening = OpenLease();

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.Open(
            opening,
            Digest(1),
            Installation,
            Digest(3),
            changed).IsFailure);

    }

    [Fact]
    public void Ciphertext_tag_wrong_key_and_resealed_same_revision_are_refused()
    {

        GrimoireOfflineTransitionEnvelopeV1 envelope = Seal();

        GrimoireOfflineTransitionEnvelopeV1 resealed;

        using (GrimoireOfflineTransitionJournalKeyLease sealing = CreateLease())
        {

            resealed = Value(GrimoireOfflineTransitionJournalAuthenticator.Seal(
                sealing,
                Digest(1),
                Installation,
                1,
                Operation,
                GrimoireOfflineTransitionKind.CovenantReset,
                7,
                1,
                Digest(2),
                Digest(3),
                [9, 9, 9]));

        }

        using (GrimoireOfflineTransitionJournalKeyLease wrong =
               GrimoireOfflineTransitionJournalKeyLease.Mint(RandomNumberGenerator.GetBytes(32)))
        {

            Assert.True(GrimoireOfflineTransitionJournalAuthenticator.Open(
                wrong,
                Digest(1),
                Installation,
                Digest(3),
                envelope).IsFailure);

        }

        foreach (GrimoireOfflineTransitionEnvelopeV1 tampered in
                 (GrimoireOfflineTransitionEnvelopeV1[])
                 [
                     envelope with { CiphertextBase64Url = ToggleBase64Url(envelope.CiphertextBase64Url) },
                     envelope with { AuthenticationTagBase64Url = ToggleBase64Url(envelope.AuthenticationTagBase64Url) },
                 ])
        {

            using GrimoireOfflineTransitionJournalKeyLease opening = OpenLease();

            Assert.True(GrimoireOfflineTransitionJournalAuthenticator.Open(
                opening,
                Digest(1),
                Installation,
                Digest(3),
                tampered).IsFailure);

        }

        Assert.NotEqual(
            Value(GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(envelope)),
            Value(GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(resealed)));

    }

    [Fact]
    public void Envelope_and_anchor_require_one_canonical_source_generated_encoding()
    {

        GrimoireOfflineTransitionEnvelopeV1 envelope = Seal();

        byte[] encodedEnvelope = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(envelope));

        Assert.Equal(encodedEnvelope, Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(
            Value(GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(encodedEnvelope)))));

        GrimoireOfflineTransitionAnchorV1 anchor = ActiveAnchor(1, Digest(4));

        string encodedAnchor = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeAnchor(anchor));

        Assert.Equal(encodedAnchor, Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeAnchor(
            Value(GrimoireOfflineTransitionJournalAuthenticator.DecodeAnchor(encodedAnchor)))));

    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("reordered")]
    [InlineData("leading-whitespace")]
    [InlineData("trailing-whitespace")]
    [InlineData("unknown-version")]
    public void Unknown_duplicate_reordered_trailing_and_unknown_version_json_are_refused(string mutation)
    {

        string envelope = Encoding.UTF8.GetString(Value(
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(Seal())));

        string anchor = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeAnchor(ActiveAnchor(1, Digest(4))));

        string changedEnvelope = MutateJson(envelope, mutation);

        string changedAnchor = MutateJson(anchor, mutation);

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
            Encoding.UTF8.GetBytes(changedEnvelope)).IsFailure);

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.DecodeAnchor(changedAnchor).IsFailure);

    }

    [Fact]
    public void Payload_envelope_anchor_and_revision_bounds_fail_before_unbounded_allocation()
    {

        using GrimoireOfflineTransitionJournalKeyLease lease = CreateLease();

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.Seal(
            lease,
            Digest(1),
            Installation,
            1,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            1,
            Digest(2),
            Digest(3),
            new byte[GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes + 1]).IsFailure);

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
            new byte[GrimoireOfflineTransitionJournalAuthenticator.MaxJournalFileBytes + 1]).IsFailure);

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.DecodeAnchor(
            new string('a', GrimoireOfflineTransitionJournalAuthenticator.MaxAnchorCharacters + 1)).IsFailure);

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.ValidateAnchor(
            ActiveAnchor(GrimoireOfflineTransitionJournalAuthenticator.MaxRevision + 1, Digest(4))).IsFailure);

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.ValidateAnchor(
            ActiveAnchor(1, Digest(4), GrimoireOfflineTransitionJournalAuthenticator.MaxSlotEpoch + 1)).IsFailure);

    }

    [Theory]
    [InlineData("closed-genesis", true)]
    [InlineData("active-empty", true)]
    [InlineData("active-published", true)]
    [InlineData("closed-never-published", true)]
    [InlineData("closed-published", true)]
    [InlineData("missing-operation", false)]
    [InlineData("empty-operation", false)]
    [InlineData("undefined-kind", false)]
    [InlineData("zero-payload-version", false)]
    [InlineData("invalid-profile", false)]
    [InlineData("invalid-location", false)]
    [InlineData("missing-digest-after-publication", false)]
    [InlineData("digest-before-publication", false)]
    [InlineData("active-genesis", false)]
    [InlineData("closed-genesis-with-operation", false)]
    public void Anchor_shape_accepts_only_closed_genesis_active_revision_zero_and_exact_tombstones(
        string shape,
        bool expected)
    {

        GrimoireOfflineTransitionAnchorV1 anchor = shape switch
        {
            "closed-genesis" => new(
                1, Digest(1), Installation, 0, GrimoireOfflineTransitionAnchorState.Closed,
                null, null, null, 0, null, Digest(3)),
            "active-empty" => ActiveAnchor(0, null),
            "active-published" => ActiveAnchor(1, Digest(4)),
            "closed-never-published" => new(
                1, Digest(1), Installation, 1, GrimoireOfflineTransitionAnchorState.Closed,
                Operation, GrimoireOfflineTransitionKind.CovenantReset, 7, 0, null, Digest(3)),
            "closed-published" => new(
                1, Digest(1), Installation, 1, GrimoireOfflineTransitionAnchorState.Closed,
                Operation, GrimoireOfflineTransitionKind.CovenantReset, 7, 1, Digest(4), Digest(3)),
            "missing-operation" => new(
                1, Digest(1), Installation, 1, GrimoireOfflineTransitionAnchorState.Active,
                null, null, null, 1, Digest(4), Digest(3)),
            "empty-operation" => new(
                1, Digest(1), Installation, 1, GrimoireOfflineTransitionAnchorState.Active,
                Guid.Empty, GrimoireOfflineTransitionKind.CovenantReset, 7, 0, null, Digest(3)),
            "undefined-kind" => new(
                1, Digest(1), Installation, 1, GrimoireOfflineTransitionAnchorState.Active,
                Operation, (GrimoireOfflineTransitionKind)99, 7, 0, null, Digest(3)),
            "zero-payload-version" => new(
                1, Digest(1), Installation, 1, GrimoireOfflineTransitionAnchorState.Active,
                Operation, GrimoireOfflineTransitionKind.CovenantReset, 0, 0, null, Digest(3)),
            "invalid-profile" => new(
                1, default, Installation, 1, GrimoireOfflineTransitionAnchorState.Active,
                Operation, GrimoireOfflineTransitionKind.CovenantReset, 7, 0, null, Digest(3)),
            "invalid-location" => new(
                1, Digest(1), Installation, 1, GrimoireOfflineTransitionAnchorState.Active,
                Operation, GrimoireOfflineTransitionKind.CovenantReset, 7, 0, null, default),
            "missing-digest-after-publication" => ActiveAnchor(1, null),
            "digest-before-publication" => ActiveAnchor(0, Digest(4)),
            "active-genesis" => new(
                1, Digest(1), Installation, 0, GrimoireOfflineTransitionAnchorState.Active,
                null, null, null, 0, null, Digest(3)),
            "closed-genesis-with-operation" => new(
                1, Digest(1), Installation, 0, GrimoireOfflineTransitionAnchorState.Closed,
                Operation, GrimoireOfflineTransitionKind.CovenantReset, 7, 0, null, Digest(3)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        Assert.Equal(expected, GrimoireOfflineTransitionJournalAuthenticator.ValidateAnchor(anchor).IsSuccess);

    }

    [Fact]
    public void Every_anchor_nullable_combination_outside_the_five_documented_shapes_is_refused()
    {

        foreach (GrimoireOfflineTransitionAnchorState state in
                 (GrimoireOfflineTransitionAnchorState[])
                 [
                     GrimoireOfflineTransitionAnchorState.Active,
                     GrimoireOfflineTransitionAnchorState.Closed,
                 ])
        {

            foreach (ulong revision in (ulong[])[0, 1])
            {

                for (int flags = 0; flags < 16; flags++)
                {

                    bool operationPresent = (flags & 1) != 0;

                    bool kindPresent = (flags & 2) != 0;

                    bool payloadVersionPresent = (flags & 4) != 0;

                    bool digestPresent = (flags & 8) != 0;

                    GrimoireOfflineTransitionAnchorV1 anchor = new(
                        1,
                        Digest(1),
                        Installation,
                        1,
                        state,
                        operationPresent ? Operation : null,
                        kindPresent ? GrimoireOfflineTransitionKind.CovenantReset : null,
                        payloadVersionPresent ? (byte)7 : null,
                        revision,
                        digestPresent ? Digest(4) : null,
                        Digest(3));

                    bool expected = operationPresent
                        && kindPresent
                        && payloadVersionPresent
                        && (revision == 0 ? !digestPresent : digestPresent);

                    Assert.Equal(
                        expected,
                        GrimoireOfflineTransitionJournalAuthenticator.ValidateAnchor(anchor).IsSuccess);

                }

            }

        }

    }

    [Fact]
    public void Key_lease_is_single_take_and_zeroes_on_dispose()
    {

        byte[] material = Enumerable.Repeat((byte)7, 32).ToArray();

        using GrimoireOfflineTransitionJournalKeyLease lease =
            GrimoireOfflineTransitionJournalKeyLease.Mint(material);

        Assert.True(lease.TryTakeKey(out byte[]? taken));

        Assert.False(lease.TryTakeKey(out _));

        CryptographicOperations.ZeroMemory(taken!);

        using GrimoireOfflineTransitionJournalKeyLease unspent =
            GrimoireOfflineTransitionJournalKeyLease.Mint(material = Enumerable.Repeat((byte)8, 32).ToArray());

        unspent.Dispose();

        Assert.All(material, value => Assert.Equal((byte)0, value));

    }

    [Fact]
    public void Seal_spends_and_zeroes_key_material_through_the_production_authenticator()
    {

        byte[] material = Enumerable.Repeat((byte)9, 32).ToArray();

        using GrimoireOfflineTransitionJournalKeyLease lease =
            GrimoireOfflineTransitionJournalKeyLease.Mint(material);

        Result<GrimoireOfflineTransitionEnvelopeV1> sealedEnvelope =
            GrimoireOfflineTransitionJournalAuthenticator.Seal(
                lease,
                Digest(1),
                Installation,
                1,
                Operation,
                GrimoireOfflineTransitionKind.CovenantReset,
                7,
                1,
                Digest(2),
                Digest(3),
                [1, 2, 3]);

        Assert.True(sealedEnvelope.IsSuccess);

        Assert.True(lease.IsSpent);

        Assert.All(material, value => Assert.Equal((byte)0, value));

    }

    [Fact]
    public void Recovery_key_open_never_creates_or_repairs_material()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        GrimoireOfflineTransitionJournalKeyProvider provider = new(_credentials);

        Assert.True(provider.OpenExisting(profile).IsFailure);

        string account = ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(profile.AccountSuffix);

        Assert.Equal(OsCredentialStoreStatus.NotFound, _credentials.TryGet(
            ArcanumCredentialIdentity.Service,
            account).Status);

        _credentials.Set(ArcanumCredentialIdentity.Service, account, "not-canonical");

        Assert.True(provider.OpenExisting(profile).IsFailure);

        Assert.Equal("not-canonical", _credentials.TryGet(
            ArcanumCredentialIdentity.Service,
            account).Value);

    }

    private GrimoireOfflineTransitionEnvelopeV1 Seal()
    {

        using GrimoireOfflineTransitionJournalKeyLease sealing = CreateLease();

        return Value(GrimoireOfflineTransitionJournalAuthenticator.Seal(
            sealing,
            Digest(1),
            Installation,
            1,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            7,
            1,
            Digest(2),
            Digest(3),
            [1, 2, 3]));

    }

    private GrimoireOfflineTransitionJournalKeyLease CreateLease()
    {

        GrimoireOfflineTransitionJournalKeyProvider provider = new(_credentials);

        return Value(provider.CreateOrOpen(_lock, _guarded, Namespace()));

    }

    private GrimoireOfflineTransitionJournalKeyLease OpenLease()
    {

        GrimoireOfflineTransitionJournalKeyProvider provider = new(_credentials);

        return Value(provider.OpenExisting(Namespace()));

    }

    private static GrimoireOfflineTransitionAnchorV1 ActiveAnchor(
        ulong revision,
        CovenantDigest? envelopeDigest,
        ulong slotEpoch = 1) =>
        new(
            1,
            Digest(1),
            Installation,
            slotEpoch,
            GrimoireOfflineTransitionAnchorState.Active,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            7,
            revision,
            envelopeDigest,
            Digest(3));

    private BackupRestoreProfileNamespace Namespace() =>
        new(Digest(1), Digest(2), "arcanum");

    private static string MutateJson(string value, string mutation) => mutation switch
    {
        "unknown" => value[..^1] + ",\"unknown\":1}",
        "duplicate" => value[..^1] + ",\"revision\":1}",
        "reordered" => "{" + value[1..].Split(',', 2)[1] + "," + value[1..].Split(',', 2)[0] + ",",
        "leading-whitespace" => " " + value,
        "trailing-whitespace" => value + " ",
        "unknown-version" => value.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };

    private static byte[] ExpectedAssociatedData(GrimoireOfflineTransitionEnvelopeV1 envelope)
    {

        byte[] bytes = new byte[
            Encoding.ASCII.GetByteCount(GrimoireOfflineTransitionJournalAuthenticator.EnvelopeAssociatedDataDomain)
            + 1 + 32 + 16 + 8 + 16 + 1 + 1 + 8 + 32 + 32];

        int written = Encoding.ASCII.GetBytes(
            GrimoireOfflineTransitionJournalAuthenticator.EnvelopeAssociatedDataDomain,
            bytes);

        bytes[written++] = envelope.Version;

        envelope.ProfileNamespaceDigest.Bytes.CopyTo(bytes.AsSpan(written));

        written += 32;

        envelope.InstallationId.TryWriteBytes(bytes.AsSpan(written), bigEndian: true, out _);

        written += 16;

        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(written), envelope.SlotEpoch);

        written += 8;

        envelope.OperationId.TryWriteBytes(bytes.AsSpan(written), bigEndian: true, out _);

        written += 16;

        bytes[written++] = (byte)envelope.Kind;

        bytes[written++] = envelope.PayloadVersion;

        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(written), envelope.Revision);

        written += 8;

        envelope.PreviousEnvelopeDigest.Bytes.CopyTo(bytes.AsSpan(written));

        written += 32;

        envelope.JournalLocationDigest.Bytes.CopyTo(bytes.AsSpan(written));

        return bytes;

    }

    private static byte[] Decode(string value)
    {

        byte[] decoded = new byte[Base64Url.GetMaxDecodedLength(value.Length)];

        Assert.True(Base64Url.TryDecodeFromChars(value, decoded, out int written));

        return decoded[..written];

    }

    private static string ToggleBase64Url(string value) =>
        (value[0] is 'A' ? "B" : "A") + value[1..];

    private static readonly Guid Installation = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Operation = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    private static T Value<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new Xunit.Sdk.XunitException(result.Error.Message);

}

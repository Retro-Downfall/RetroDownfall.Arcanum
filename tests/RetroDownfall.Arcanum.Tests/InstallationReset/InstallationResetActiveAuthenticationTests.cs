using System.Buffers.Binary;

using System.Buffers.Text;

using System.Collections.Immutable;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetActiveAuthenticationTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-reset-active-auth-" + Guid.NewGuid().ToString("N"));

    private readonly string _guarded;

    private readonly ArcanumMaintenanceLock _lock;

    public InstallationResetActiveAuthenticationTests()
    {

        Directory.CreateDirectory(_root);

        _guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_guarded);

        _lock = ArcanumMaintenanceLock.TryAcquire(_guarded)
            ?? throw new InvalidOperationException("The test could not take its own maintenance lock.");

    }

    [Fact]
    public void Active_location_digest_binds_profile_parent_identity_and_active_leaf()
    {

        // Mutation caught: omitting the domain separator, either digest, the UInt16BE leaf length,
        // or the leaf bytes would make this hand-derived fixture or one of the substitutions match.
        CovenantDigest profile = DigestRange(0x00);

        CovenantDigest parent = DigestRange(0x20);

        CovenantDigest actual = Value(
            InstallationResetActiveRecordAuthenticator.ActiveLocation(
                profile,
                parent,
                "active.json"));

        Assert.Equal(
            "5a3d5867d8e2cee6395cb9a9be524bd1a2cd2e3e2b0f356b73cf66672acb1ae9",
            Convert.ToHexStringLower(actual.Bytes));

        Assert.NotEqual(
            actual,
            Value(InstallationResetActiveRecordAuthenticator.ActiveLocation(
                DigestRange(0x01),
                parent,
                "active.json")));

        Assert.NotEqual(
            actual,
            Value(InstallationResetActiveRecordAuthenticator.ActiveLocation(
                profile,
                DigestRange(0x21),
                "active.json")));

        Assert.NotEqual(
            actual,
            Value(InstallationResetActiveRecordAuthenticator.ActiveLocation(
                profile,
                parent,
                "active2.json")));

        Assert.True(
            InstallationResetActiveRecordAuthenticator.ActiveLocation(
                profile,
                parent,
                "../active.json").IsFailure);

        Assert.True(
            InstallationResetActiveRecordAuthenticator.ActiveLocation(
                profile,
                parent,
                string.Empty).IsFailure);

        BackupRestoreProfileNamespace resolvedProfile = Namespace();

        InstallationResetActiveLocation resolved = Value(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                _guarded,
                resolvedProfile));

        Assert.Equal(new InstallationResetActiveStore(_guarded).ActivePath, resolved.ActivePath);

        Assert.Equal(resolvedProfile.Digest, resolved.ProfileNamespaceDigest);

        Assert.Equal(
            resolvedProfile.ParentPhysicalIdentityDigest,
            resolved.GuardedParentPhysicalIdentityDigest);

        Assert.Equal(Path.GetFileName(resolved.ActivePath), resolved.ActiveLeaf);

        Assert.Equal(
            Value(InstallationResetActiveRecordAuthenticator.ActiveLocation(
                resolved.ProfileNamespaceDigest,
                resolved.GuardedParentPhysicalIdentityDigest,
                resolved.ActiveLeaf)),
            resolved.Digest);

        Error expectedMismatch = AssertFailure(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                _guarded,
                resolvedProfile with { Digest = DigestRange(0x70) }));

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, expectedMismatch.Code);

        Assert.Equal(
            "The installation-reset active location does not belong to this profile namespace.",
            expectedMismatch.Message);

        Error parentMismatch = AssertFailure(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                _guarded,
                resolvedProfile with
                {

                    ParentPhysicalIdentityDigest = DigestRange(0x70),

                }));

        Error leafMismatch = AssertFailure(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                _guarded,
                resolvedProfile with { ChildLeaf = "substituted-arcanum" }));

        Assert.Equal(expectedMismatch, parentMismatch);

        Assert.Equal(expectedMismatch, leafMismatch);

    }

    [Fact]
    public void V2_envelope_uses_exact_aad_canonical_base64url_and_digest_encoding()
    {

        // Mutation caught: changing the AAD field order/encodings, omitting the zero separators or
        // ciphertext/tag from the digest, or accepting padded base64url breaks these fixed vectors.
        InstallationResetActiveLocation location = FixtureLocation();

        InstallationResetActiveEnvelopeV2 fixture = FixtureEnvelope();

        InstallationResetActivePayloadV2 expectedPayload = FixturePayload();

        byte[] encoded = Value(
            InstallationResetActiveRecordAuthenticator.EncodeEnvelope(fixture));

        Assert.Equal(FixtureEnvelopeJson, Encoding.UTF8.GetString(encoded));

        InstallationResetActiveEnvelopeV2 decoded = Value(
            InstallationResetActiveRecordAuthenticator.DecodeEnvelope(encoded));

        Assert.Equal(fixture, decoded);

        byte[] openMaterial = Enumerable.Range(0, 32)
            .Select(static value => (byte)value)
            .ToArray();

        using InstallationResetActiveRecordKeyLease openKey =
            InstallationResetActiveRecordKeyLease.Mint(openMaterial);

        InstallationResetActivePayloadV2 opened = Value(
            InstallationResetActiveRecordAuthenticator.Open(
                openKey,
                location,
                fixture.InstallationId,
                fixture));

        Assert.Equal(expectedPayload.Version, opened.Version);

        Assert.Equal(expectedPayload.OperationId, opened.OperationId);

        Assert.Equal(expectedPayload.PlanId, opened.PlanId);

        Assert.Equal(expectedPayload.Scope, opened.Scope);

        Assert.Equal(expectedPayload.AcceptedBinding.BindingId, opened.AcceptedBinding.BindingId);

        Assert.True(opened.AcceptedBinding.SelectedRoots.IsEmpty);

        Assert.True(opened.CredentialResults.IsEmpty);

        Assert.Null(opened.HostToolsMarkerPairReset);

        Assert.True(openMaterial.All(static value => value == 0));

        CovenantDigest digest = Value(
            InstallationResetActiveRecordAuthenticator.EnvelopeDigest(fixture));

        Assert.Equal(
            "9d53e4976d1741d430140f2dfc83aff24b40a38be527d6d647b037ccc55181b8",
            Convert.ToHexStringLower(digest.Bytes));

        byte[] sealMaterial = Enumerable.Range(0, 32)
            .Select(static value => (byte)value)
            .ToArray();

        using InstallationResetActiveRecordKeyLease sealKey =
            InstallationResetActiveRecordKeyLease.Mint(sealMaterial);

        InstallationResetActiveEnvelopeV2 sealedEnvelope = Value(
            InstallationResetActiveRecordAuthenticator.Seal(
                sealKey,
                location,
                fixture.InstallationId,
                fixture.Revision,
                fixture.PreviousEnvelopeDigest,
                expectedPayload));

        Assert.True(sealMaterial.All(static value => value == 0));

        Assert.Equal(16, sealedEnvelope.NonceBase64Url.Length);

        Assert.Equal(22, sealedEnvelope.AuthenticationTagBase64Url.Length);

        Assert.DoesNotContain('=', sealedEnvelope.NonceBase64Url);

        Assert.DoesNotContain('=', sealedEnvelope.CiphertextBase64Url);

        Assert.DoesNotContain('=', sealedEnvelope.AuthenticationTagBase64Url);

        using InstallationResetActiveRecordKeyLease reopenKey =
            InstallationResetActiveRecordKeyLease.Mint(
                Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

        Assert.True(
            InstallationResetActiveRecordAuthenticator.Open(
                reopenKey,
                location,
                fixture.InstallationId,
                sealedEnvelope).IsSuccess);

    }

    [Fact]
    public void V2_envelope_refuses_wrong_key_tag_profile_installation_operation_location_scope_or_plan_without_detail()
    {

        // Mutation caught: returning a field-specific error or omitting any authenticated header,
        // nonce, ciphertext, or tag field lets one tamper case authenticate or reveal what matched.
        InstallationResetActiveEnvelopeV2 fixture = FixtureEnvelope();

        Guid installation = fixture.InstallationId;

        InstallationResetActiveLocation location = FixtureLocation();

        Result<InstallationResetActivePayloadV2>[] refusals =
        [
            OpenFixture(
                Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray(),
                location,
                installation,
                fixture),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { Version = 1 }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { ProfileNamespaceDigest = DigestRange(0x21) }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { InstallationId = Guid.Parse("11112233-4455-6677-8899-aabbccddeeff") }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { OperationId = Guid.Parse("20213243-5465-7687-98a9-bacbdcedfe0f") }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { Revision = 0x0102030405060709UL }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { PreviousEnvelopeDigest = DigestRange(0x41) }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { ActiveLocationDigest = DigestRange(0x10) }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { Scope = InstallationResetScope.Global }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { PlanId = "other-plan" }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { NonceBase64Url = "AAAAAAAAAAAAAAAA" }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { CiphertextBase64Url = "AA" }),
            OpenFixture(
                FixtureKey(),
                location,
                installation,
                fixture with { AuthenticationTagBase64Url = "AKYWp71SRxBDaU46MaLFhg" }),
        ];

        Error expected = AssertFailure(refusals[0]);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, expected.Code);

        Assert.Equal(
            "This installation-reset active evidence did not authenticate.",
            expected.Message);

        Assert.All(
            refusals,
            refusal =>
            {

                Error error = AssertFailure(refusal);

                Assert.Equal(expected.Code, error.Code);

                Assert.Equal(expected.Message, error.Message);

            });

    }

    [Fact]
    public void V2_envelope_rejects_unknown_missing_unmapped_trailing_or_noncanonical_json()
    {

        // Mutation caught: accepting a deserialized value without byte-for-byte canonical re-encode
        // would admit whitespace, duplicate/missing fields, or a newer member this build ignores.
        string unknown = FixtureEnvelopeJson[..^1] + ",\"unknown\":true}";

        string missing = FixtureEnvelopeJson.Replace(
            "\"planId\":\"plan-\\u03B2\",",
            string.Empty,
            StringComparison.Ordinal);

        string unmappedNested = FixtureEnvelopeJson.Replace(
            "\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\"}",
            "\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\",\"unknown\":0}",
            StringComparison.Ordinal);

        string duplicate = FixtureEnvelopeJson.Replace(
            "{\"version\":2,",
            "{\"version\":2,\"version\":2,",
            StringComparison.Ordinal);

        string trailing = FixtureEnvelopeJson + "{}";

        string noncanonical = "\n" + FixtureEnvelopeJson;

        string paddedBase64Url = FixtureEnvelopeJson.Replace(
            "\"oKGio6Slpqeoqaqr\"",
            "\"oKGio6Slpqeoqaqr=\"",
            StringComparison.Ordinal);

        foreach (string candidate in
                 new[]
                 {
                     unknown,
                     missing,
                     unmappedNested,
                     duplicate,
                     trailing,
                     noncanonical,
                     paddedBase64Url,
                 })
        {

            Error error = AssertFailure(
                InstallationResetActiveRecordAuthenticator.DecodeEnvelope(
                    Encoding.UTF8.GetBytes(candidate)));

            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, error.Code);

            Assert.Equal(
                "This installation-reset active evidence did not authenticate.",
                error.Message);

        }

        Assert.True(
            InstallationResetActiveRecordAuthenticator.DecodeEnvelope(
                new byte[InstallationResetActiveRecordAuthenticator.MaxActiveFileBytes + 1])
            .IsFailure);

        InstallationResetActiveEnvelopeV2 fixture = FixtureEnvelope();

        InstallationResetActiveEnvelopeV2[] invalidFields =
        [
            fixture with { Version = 1 },
            fixture with { InstallationId = Guid.Empty },
            fixture with { OperationId = Guid.Empty },
            fixture with { Revision = 0 },
            fixture with { Revision = ulong.MaxValue },
            fixture with { PreviousEnvelopeDigest = default },
            fixture with { ActiveLocationDigest = default },
            fixture with { Scope = (InstallationResetScope)99 },
            fixture with { PlanId = string.Empty },
            fixture with { PlanId = new string('x', 1025) },
            fixture with { NonceBase64Url = "oKGio6Slpqeoqaqr=" },
            fixture with { NonceBase64Url = "oKGio6Slpqeoqao" },
            fixture with { CiphertextBase64Url = string.Empty },
            fixture with { AuthenticationTagBase64Url = "8KYWp71SRxBDaU46MaLFhh" },
        ];

        Assert.All(
            invalidFields,
            invalid =>
            {

                Assert.True(
                    InstallationResetActiveRecordAuthenticator.EncodeEnvelope(invalid).IsFailure);

                Assert.True(
                    InstallationResetActiveRecordAuthenticator.EnvelopeDigest(invalid).IsFailure);

            });

    }

    [Fact]
    public void V2_payload_rejects_a_nonnull_reserved_host_tools_marker_pair()
    {

        // Mutation caught: treating the reserved JsonElement as opaque-but-acceptable after tag
        // verification would let this slice persist full-reset authority it does not define.
        string payloadJson = FixturePayloadJson.Replace(
            "\"hostToolsMarkerPairReset\":null",
            "\"hostToolsMarkerPairReset\":{}",
            StringComparison.Ordinal);

        InstallationResetActiveEnvelopeV2 authenticated =
            AuthenticatedFixtureEnvelope(payloadJson);

        Error error = AssertFailure(
            OpenFixture(
                FixtureKey(),
                FixtureLocation(),
                authenticated.InstallationId,
                authenticated));

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, error.Code);

        Assert.Equal(
            "This installation-reset active evidence did not authenticate.",
            error.Message);

    }

    [Fact]
    public void Active_anchor_requires_canonical_profile_bound_revision_and_digest()
    {

        // Mutation caught: accepting a noncanonical anchor, an unbound digest, or a revision-zero
        // tombstone/nonzero digest would turn ambiguous credential evidence into rollback authority.
        InstallationResetActiveAnchorV1 anchor = new(
            Version: 1,
            State: InstallationResetActiveAnchorState.Active,
            ProfileNamespaceDigest: DigestRange(0x20),
            InstallationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            OperationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            Revision: 1,
            EnvelopeDigest: DigestRange(0x10),
            ActiveLocationDigest: new CovenantDigest(Convert.FromHexString(
                "83e4f8e16469d66ea8d49a4ff8f7439ceab05df25da82ce08150dbdc69a1b879")));

        string encoded = Value(
            InstallationResetActiveRecordAuthenticator.EncodeAnchor(anchor));

        Assert.Equal(FixtureAnchorJson, encoded);

        Assert.Equal(
            anchor,
            Value(InstallationResetActiveRecordAuthenticator.DecodeAnchor(encoded)));

        CovenantDigest zero = new(new byte[32]);

        Assert.True(
            InstallationResetActiveRecordAuthenticator.EncodeAnchor(
                anchor with { Revision = 0, EnvelopeDigest = zero }).IsSuccess);

        InstallationResetActiveAnchorV1[] invalid =
        [
            anchor with { Version = 2 },
            anchor with { State = (InstallationResetActiveAnchorState)0 },
            anchor with { ProfileNamespaceDigest = default },
            anchor with { InstallationId = Guid.Empty },
            anchor with { OperationId = Guid.Empty },
            anchor with { Revision = 0 },
            anchor with { Revision = 1, EnvelopeDigest = zero },
            anchor with { Revision = ulong.MaxValue },
            anchor with { ActiveLocationDigest = default },
            anchor with
            {

                State = InstallationResetActiveAnchorState.Closed,

                Revision = 0,

                EnvelopeDigest = zero,

            },
        ];

        Assert.All(
            invalid,
            candidate => Assert.True(
                InstallationResetActiveRecordAuthenticator.EncodeAnchor(candidate).IsFailure));

        foreach (string candidate in
                 new[]
                 {
                     " " + encoded,
                     encoded[..^1] + ",\"unknown\":true}",
                     encoded.Replace("\"revision\":1,", string.Empty, StringComparison.Ordinal),
                     encoded + "{}",
                 })
        {

            Assert.True(
                InstallationResetActiveRecordAuthenticator.DecodeAnchor(candidate).IsFailure);

        }

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
    public void Active_key_provider_creates_reads_back_and_returns_only_canonical_32_byte_material()
    {

        RecordingCredentialStore credentials = new();

        BackupRestoreProfileNamespace profile = Namespace();

        InstallationResetActiveRecordKeyProvider keys = new(credentials);

        using InstallationResetActiveRecordKeyLease created = Value(
            keys.CreateOrOpen(_lock, _guarded, profile));

        string account = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
            profile.AccountSuffix);

        string stored = Assert.IsType<string>(credentials.Values[account]);

        Assert.Equal(43, stored.Length);

        Assert.DoesNotContain('=', stored);

        Assert.DoesNotContain('+', stored);

        Assert.DoesNotContain('/', stored);

        Assert.Equal(2, credentials.ProbeCounts[account]);

        Assert.Equal(1, credentials.SetCount);

        Assert.True(Value(keys.IsPresent(profile)));

        Assert.Equal(3, credentials.ProbeCounts[account]);

        Assert.True(created.TryTakeKey(out byte[]? material));

        Assert.Equal(32, material!.Length);

        Span<byte> decoded = stackalloc byte[32];

        Assert.True(Base64Url.TryDecodeFromChars(stored, decoded, out int written));

        Assert.Equal(32, written);

        Assert.True(material.AsSpan().SequenceEqual(decoded));

        CryptographicOperations.ZeroMemory(material);

        using InstallationResetActiveRecordKeyLease reopened = Value(
            keys.CreateOrOpen(_lock, _guarded, profile));

        Assert.Equal(1, credentials.SetCount);

        RecordingCredentialStore mismatched = new()
        {
            SubstituteAfterSet = Base64Url.EncodeToString(Enumerable.Repeat((byte)0x5a, 32).ToArray()),
        };

        InstallationResetActiveRecordKeyProvider mismatchKeys = new(mismatched);

        Assert.True(mismatchKeys.CreateOrOpen(_lock, _guarded, profile).IsFailure);

        Assert.Equal(2, mismatched.ProbeCounts[account]);

    }

    [Fact]
    public void Active_key_provider_recovery_never_creates_substitutes_or_repairs_a_key()
    {

        // Mutation caught: recovery deleting a missing, malformed, or unavailable key can leave the
        // visible value unchanged while still exercising destructive credential-store authority.
        RecordingCredentialStore credentials = new();

        BackupRestoreProfileNamespace profile = Namespace();

        string account = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
            profile.AccountSuffix);

        InstallationResetActiveRecordKeyProvider keys = new(credentials);

        Assert.True(keys.OpenExisting(profile).IsFailure);

        Assert.False(Value(keys.IsPresent(profile)));

        Assert.Equal(0, credentials.SetCount);

        Assert.Equal(0, credentials.DeleteCount);

        credentials.Values[account] = "not-canonical";

        Assert.True(keys.OpenExisting(profile).IsFailure);

        Assert.True(keys.IsPresent(profile).IsFailure);

        Assert.Equal("not-canonical", credentials.Values[account]);

        Assert.Equal(0, credentials.SetCount);

        Assert.Equal(0, credentials.DeleteCount);

        credentials.IsAvailable = false;

        Assert.True(keys.OpenExisting(profile).IsFailure);

        Assert.True(keys.IsPresent(profile).IsFailure);

        Assert.Equal("not-canonical", credentials.Values[account]);

        Assert.Equal(0, credentials.SetCount);

        Assert.Equal(0, credentials.DeleteCount);

    }

    [Fact]
    public void Active_key_lease_is_single_take_zeroized_and_not_serializable()
    {

        byte[] untakenMaterial = Enumerable.Repeat((byte)0x7c, 32).ToArray();

        using InstallationResetActiveRecordKeyLease untaken =
            InstallationResetActiveRecordKeyLease.Mint(untakenMaterial);

        untaken.Dispose();

        Assert.True(untakenMaterial.All(static value => value == 0));

        Assert.True(untaken.IsSpent);

        Assert.False(untaken.TryTakeKey(out _));

        byte[] takeMaterial = Enumerable.Repeat((byte)0x3d, 32).ToArray();

        using InstallationResetActiveRecordKeyLease taken =
            InstallationResetActiveRecordKeyLease.Mint(takeMaterial);

        Assert.True(taken.TryTakeKey(out byte[]? key));

        Assert.Same(takeMaterial, key);

        Assert.False(taken.TryTakeKey(out _));

        Assert.True(taken.IsSpent);

        try
        {

            Assert.Contains(key!, static value => value != 0);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key!);

        }

        Assert.True(takeMaterial.All(static value => value == 0));

        Assert.Empty(typeof(InstallationResetActiveRecordKeyLease).GetConstructors());

        Assert.Null(
            InstallationResetActiveJsonContext.Default.GetTypeInfo(
                typeof(InstallationResetActiveRecordKeyLease)));

        _ = Assert.Throws<ArgumentException>(
            () => InstallationResetActiveRecordKeyLease.Mint(new byte[31]));

    }

    [Fact]
    public void Active_key_removal_requires_the_held_installation_lock_and_verifies_absence()
    {

        RecordingCredentialStore credentials = new();

        BackupRestoreProfileNamespace profile = Namespace();

        string account = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
            profile.AccountSuffix);

        InstallationResetActiveRecordKeyProvider keys = new(credentials);

        Value(keys.CreateOrOpen(_lock, _guarded, profile)).Dispose();

        string otherGuarded = Path.Combine(_root, "other");

        Directory.CreateDirectory(otherGuarded);

        _ = Assert.Throws<InvalidOperationException>(
            () => keys.RemoveAndVerifyAbsent(_lock, otherGuarded, profile));

        Assert.Equal(0, credentials.DeleteCount);

        Assert.True(credentials.Values.ContainsKey(account));

        Result removed = keys.RemoveAndVerifyAbsent(_lock, _guarded, profile);

        Assert.True(removed.IsSuccess, removed.Error.Message);

        Assert.Equal(1, credentials.DeleteCount);

        Assert.False(credentials.Values.ContainsKey(account));

        RecordingCredentialStore retaining = new() { RetainAfterDelete = true };

        retaining.Values[account] = Base64Url.EncodeToString(new byte[32]);

        Assert.True(
            new InstallationResetActiveRecordKeyProvider(retaining)
                .RemoveAndVerifyAbsent(_lock, _guarded, profile)
                .IsFailure);

        Assert.True(retaining.Values.ContainsKey(account));

    }

    private BackupRestoreProfileNamespace Namespace() =>
        Value(BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guarded));

    private static InstallationResetActiveLocation FixtureLocation() =>
        new(
            "/fixture/active.json",
            DigestRange(0x20),
            DigestRange(0x60),
            "active.json",
            new CovenantDigest(Convert.FromHexString(
                "83e4f8e16469d66ea8d49a4ff8f7439ceab05df25da82ce08150dbdc69a1b879")));

    private static byte[] FixtureKey() =>
        Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();

    private static InstallationResetActiveEnvelopeV2 AuthenticatedFixtureEnvelope(
        string payloadJson)
    {

        byte[] key = FixtureKey();

        byte[] nonce = Enumerable.Range(0xa0, 12)
            .Select(static value => (byte)value)
            .ToArray();

        byte[] plaintext = Encoding.UTF8.GetBytes(payloadJson);

        byte[] ciphertext = new byte[plaintext.Length];

        byte[] tag = new byte[16];

        byte[] aad = Convert.FromHexString(FixtureAssociatedDataHex);

        try
        {

            using AesGcm aes = new(key, tag.Length);

            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

            return FixtureEnvelope() with
            {

                CiphertextBase64Url = Base64Url.EncodeToString(ciphertext),

                AuthenticationTagBase64Url = Base64Url.EncodeToString(tag),

            };

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(plaintext);

            CryptographicOperations.ZeroMemory(ciphertext);

            CryptographicOperations.ZeroMemory(tag);

            CryptographicOperations.ZeroMemory(aad);

        }

    }

    private static Result<InstallationResetActivePayloadV2> OpenFixture(
        byte[] key,
        InstallationResetActiveLocation location,
        Guid installationId,
        InstallationResetActiveEnvelopeV2 envelope)
    {

        using InstallationResetActiveRecordKeyLease lease =
            InstallationResetActiveRecordKeyLease.Mint(key);

        return InstallationResetActiveRecordAuthenticator.Open(
            lease,
            location,
            installationId,
            envelope);

    }

    private static Error AssertFailure<T>(Result<T> result)
    {

        Assert.True(result.IsFailure);

        return result.Error;

    }

    private static InstallationResetActiveEnvelopeV2 FixtureEnvelope() =>
        new(
            Version: 2,
            ProfileNamespaceDigest: DigestRange(0x20),
            InstallationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            OperationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            Revision: 0x0102030405060708UL,
            PreviousEnvelopeDigest: DigestRange(0x40),
            ActiveLocationDigest: new CovenantDigest(Convert.FromHexString(
                "83e4f8e16469d66ea8d49a4ff8f7439ceab05df25da82ce08150dbdc69a1b879")),
            Scope: InstallationResetScope.All,
            PlanId: "plan-β",
            NonceBase64Url: "oKGio6Slpqeoqaqr",
            CiphertextBase64Url:
                "nToKSDe4a9AMR73hK1ivrhXeOGT72Cwl-CwcpE6bRzDhRHPMghdnC2qxM_4xTa7A"
                + "f3p_ZQCxeRwlPW46whW11paQpx9cxIqpkMDFOv-irdTgV96U9oOrWMJXTfne6ii2ta"
                + "Zqx2-Mo7nptHQ79viMS0FyW8CrWfcQDa69x9ElhiihU9kRtyvucBVjhalwI_-y4bfK"
                + "ItrywK6fpNORxqlTCeo6bNTaDyNi5255TQCmxcNNfaC0IAPWXe73Elw40HA4QEVu8z"
                + "uMl8-DLyW8LFZE0DarhYtUFqjLz_sDuHHkuRUNNVdPzQS4U4HfITA3ZTQMKYeZYqVw"
                + "FOYfzP0GBF1h9mQd89yysY7CFss29gIZVvFoY28g2simTtpLNKqN2epZ1C4IzXJnCb"
                + "iXA2wWlc7qLb07_CdkEbDnF8wI2fDplznl8jG2lxlQZNe9B2zjYbCFQfOqM_M9Q5k"
                + "1fuCvDHi9OSRg5jW6VFw9nuM5rF-BcLyoiGs_8VRjrPZhPmWxk6DLEJD8Ftcu93AMH"
                + "dYaRN_rpHuCPqK_TukXBszCqRgAOkLpJS8uKJXI5FYVT9LtMOYGZNWrxRbJtjOMKvX"
                + "XmQZNyjqMjIKJtvnI_v6xP51kbbSlrKQiQX_lBXz8jVOVOMuUItqzOu5pfv0",
            AuthenticationTagBase64Url: "8KYWp71SRxBDaU46MaLFhg");

    private static InstallationResetActivePayloadV2 FixturePayload() =>
        new(
            Version: 2,
            OperationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            PlanId: "plan-β",
            Scope: InstallationResetScope.All,
            Workspace: null,
            AcceptedBinding: new InstallationResetActiveAcceptedBindingV2(
                "binding",
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<InstallationResetActivePreservedBackupV2>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty),
            Phase: InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: ImmutableArray<InstallationResetActiveCredentialResultV2>.Empty,
            LastErrorCode: null,
            DataHandoff: null,
            OnlineDataCompletion: null,
            HostToolsMarkerPairReset: null);

    private const string FixtureEnvelopeJson =
        "{\"version\":2,\"profileNamespaceDigest\":{\"bytes\":"
        + "\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\"},"
        + "\"installationId\":\"00112233-4455-6677-8899-aabbccddeeff\","
        + "\"operationId\":\"10213243-5465-7687-98a9-bacbdcedfe0f\","
        + "\"revision\":72623859790382856,\"previousEnvelopeDigest\":{\"bytes\":"
        + "\"QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8=\"},"
        + "\"activeLocationDigest\":{\"bytes\":"
        + "\"g+T44WRp1m6o1JpP+PdDnOqwXfJdqCzggVDb3GmhuHk=\"},"
        + "\"scope\":\"All\",\"planId\":\"plan-\\u03B2\","
        + "\"nonceBase64Url\":\"oKGio6Slpqeoqaqr\",\"ciphertextBase64Url\":\""
        + "nToKSDe4a9AMR73hK1ivrhXeOGT72Cwl-CwcpE6bRzDhRHPMghdnC2qxM_4xTa7A"
        + "f3p_ZQCxeRwlPW46whW11paQpx9cxIqpkMDFOv-irdTgV96U9oOrWMJXTfne6ii2ta"
        + "Zqx2-Mo7nptHQ79viMS0FyW8CrWfcQDa69x9ElhiihU9kRtyvucBVjhalwI_-y4bfK"
        + "ItrywK6fpNORxqlTCeo6bNTaDyNi5255TQCmxcNNfaC0IAPWXe73Elw40HA4QEVu8z"
        + "uMl8-DLyW8LFZE0DarhYtUFqjLz_sDuHHkuRUNNVdPzQS4U4HfITA3ZTQMKYeZYqVw"
        + "FOYfzP0GBF1h9mQd89yysY7CFss29gIZVvFoY28g2simTtpLNKqN2epZ1C4IzXJnCb"
        + "iXA2wWlc7qLb07_CdkEbDnF8wI2fDplznl8jG2lxlQZNe9B2zjYbCFQfOqM_M9Q5k"
        + "1fuCvDHi9OSRg5jW6VFw9nuM5rF-BcLyoiGs_8VRjrPZhPmWxk6DLEJD8Ftcu93AMH"
        + "dYaRN_rpHuCPqK_TukXBszCqRgAOkLpJS8uKJXI5FYVT9LtMOYGZNWrxRbJtjOMKvX"
        + "XmQZNyjqMjIKJtvnI_v6xP51kbbSlrKQiQX_lBXz8jVOVOMuUItqzOu5pfv0\","
        + "\"authenticationTagBase64Url\":\"8KYWp71SRxBDaU46MaLFhg\"}";

    private const string FixturePayloadJson =
        "{\"version\":2,\"operationId\":\"10213243-5465-7687-98a9-bacbdcedfe0f\","
        + "\"planId\":\"plan-\\u03B2\",\"scope\":\"All\",\"workspace\":null,"
        + "\"acceptedBinding\":{\"bindingId\":\"binding\",\"selectedRoots\":[],"
        + "\"excludedRoots\":[],\"preservedBackups\":[],\"credentialAccounts\":[],"
        + "\"dataPlanIds\":[]},\"phase\":\"Prepared\",\"pointOfNoReturn\":false,"
        + "\"rowsDeleted\":0,\"filesDeleted\":0,\"estimatedBytesDeleted\":0,"
        + "\"credentialResults\":[],\"lastErrorCode\":null,\"dataHandoff\":null,"
        + "\"onlineDataCompletion\":null,\"hostToolsMarkerPairReset\":null}";

    private const string FixtureAssociatedDataHex =
        "417263616e756d2e496e7374616c6c6174696f6e52657365742e41637469766545"
        + "6e76656c6f70652e76320002202122232425262728292a2b2c2d2e2f3031323334"
        + "35363738393a3b3c3d3e3f00112233445566778899aabbccddeeff102132435465"
        + "768798a9bacbdcedfe0f0102030405060708404142434445464748494a4b4c4d4e"
        + "4f505152535455565758595a5b5c5d5e5f83e4f8e16469d66ea8d49a4ff8f743"
        + "9ceab05df25da82ce08150dbdc69a1b8790300000007706c616e2dceb2";

    private const string FixtureAnchorJson =
        "{\"version\":1,\"state\":1,\"profileNamespaceDigest\":{\"bytes\":"
        + "\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\"},"
        + "\"installationId\":\"00112233-4455-6677-8899-aabbccddeeff\","
        + "\"operationId\":\"10213243-5465-7687-98a9-bacbdcedfe0f\",\"revision\":1,"
        + "\"envelopeDigest\":{\"bytes\":\"EBESExQVFhcYGRobHB0eHyAhIiMkJSYnKCkqKywtLi8=\"},"
        + "\"activeLocationDigest\":{\"bytes\":"
        + "\"g+T44WRp1m6o1JpP+PdDnOqwXfJdqCzggVDb3GmhuHk=\"}}";

    private static CovenantDigest DigestRange(byte first) =>
        new(Enumerable.Range(first, 32).Select(static value => (byte)value).ToArray());

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

    private sealed class RecordingCredentialStore : IOsCredentialStore
    {

        public bool IsAvailable { get; set; } = true;

        public string? SubstituteAfterSet { get; init; }

        public bool RetainAfterDelete { get; init; }

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> ProbeCounts { get; } = new(StringComparer.Ordinal);

        public int SetCount { get; private set; }

        public int DeleteCount { get; private set; }

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            ProbeCounts[account] = ProbeCounts.GetValueOrDefault(account) + 1;

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            return Values.TryGetValue(account, out string? value)
                ? OsCredentialStoreResult.Ok(value)
                : OsCredentialStoreResult.NotFound();

        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            SetCount++;

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            Values[account] = SubstituteAfterSet ?? secret;

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            DeleteCount++;

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            if (!RetainAfterDelete)
            {

                _ = Values.Remove(account);

            }

            return OsCredentialStoreResult.Ok(string.Empty);

        }

    }

}

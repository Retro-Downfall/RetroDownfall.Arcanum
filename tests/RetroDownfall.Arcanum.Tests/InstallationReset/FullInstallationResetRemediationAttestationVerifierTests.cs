using System.Buffers.Text;

using System.Security.Cryptography;

using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class FullInstallationResetRemediationAttestationVerifierTests
{

    [Fact]

    public void Canonical_preimage_uses_the_exact_v1_field_order_and_widths()
    {

        FullInstallationResetExternalRemediationAttestation attestation = new(
            Version: 1,
            OperationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            InstallationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            HostToolsTransitionId: Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
            TaintMasterKeyVersion: 0x0102030405060708,
            AuthorityFingerprint: Digest(0x11),
            DatabaseMarkerDigest: Digest(0x22),
            OsMarkerDigest: Digest(0x33),
            RemediationActionDigest: Digest(0x44),
            NonceBase64Url: "oKGio6SlpqeoqaqrrK2urw",
            Issuer: "RetroDownfall.Remediation.v1",
            IssuedAtUtc: DateTimeOffset.UnixEpoch,
            ExpiresAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(1),
            SignatureBase64Url: string.Empty);

        Result<byte[]> result =
            FullInstallationResetRemediationPreimage.Build(attestation);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "45787465726e616c52656d6564696174696f6e2e76310001001122334455"
                + "66778899aabbccddeeff102132435465768798a9bacbdcedfe0fffeeddcc"
                + "bbaa99887766554433221100010203040506070811111111111111111111"
                + "111111111111111111111111111111111111111111112222222222222222"
                + "2222222222222222222222222222222222222222222222223333333333"
                + "333333333333333333333333333333333333333333333333333333444444"
                + "444444444444444444444444444444444444444444444444444444444400"
                + "10a0a1a2a3a4a5a6a7a8a9aaabacadaeaf001c526574726f446f776e66"
                + "616c6c2e52656d6564696174696f6e2e7631000000000000000000000000"
                + "00000001"),
            result.Value);

    }

    [Fact]

    public void Authorization_normalizes_the_accepted_instant_to_utc_whole_seconds()
    {

        DateTimeOffset issuedAtUtc = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        DateTimeOffset observedNow = issuedAtUtc.AddMilliseconds(987);

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation signed = Sign(
            signer,
            UnsignedAttestation(pair, issuedAtUtc));

        FakeTimeProvider clock = new();

        clock.SetUtcNow(observedNow);

        FullInstallationResetRemediationAttestationVerifier verifier = new(
            new FakeTrustRootProvider(
                new FullInstallationResetRemediationTrustRoot(
                    signer.ExportSubjectPublicKeyInfo())),
            clock);

        Result<FullInstallationResetRemediationAuthorization> result =
            verifier.Verify(signed, signed.InstallationId, pair);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(issuedAtUtc, result.Value.AcceptedAtUtc);

        Assert.Equal(TimeSpan.Zero, result.Value.AcceptedAtUtc.Offset);

        Assert.Equal(0, result.Value.AcceptedAtUtc.Ticks % TimeSpan.TicksPerSecond);

    }

    [Fact]

    public void Reset_composition_registers_one_code_pinned_independent_p256_trust_root()
    {

        ServiceCollection services = new();

        services.AddArcanumInstallationReset(new ArcanumSettings());

        services.AddArcanumInstallationReset(new ArcanumSettings());

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType
                == typeof(IFullInstallationResetRemediationTrustRootProvider));

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType
                == typeof(IFullInstallationResetRemediationAttestationVerifier));

        using ServiceProvider provider = services.BuildServiceProvider();

        IFullInstallationResetRemediationTrustRootProvider roots = provider
            .GetRequiredService<IFullInstallationResetRemediationTrustRootProvider>();

        Assert.True(roots.TryResolve(
            "RetroDownfall.Remediation.v1",
            out FullInstallationResetRemediationTrustRoot? root));

        Assert.NotNull(root);

        Assert.Equal(
            "cc8f456c657c698ee8843e22fb2a195eeb50db2764b8f7e4853bf0c92ff5fc95",
            Convert.ToHexString(SHA256.HashData(root.SubjectPublicKeyInfo)).ToLowerInvariant());

        using ECDsa key = ECDsa.Create();

        key.ImportSubjectPublicKeyInfo(root.SubjectPublicKeyInfo, out int bytesRead);

        Assert.Equal(root.SubjectPublicKeyInfo.Length, bytesRead);

        Assert.Equal(256, key.KeySize);

        Assert.False(roots.TryResolve(
            "retrodownfall.remediation.v1",
            out FullInstallationResetRemediationTrustRoot? unknown));

        Assert.Null(unknown);

        Assert.IsType<FullInstallationResetRemediationAttestationVerifier>(
            provider.GetRequiredService<
                IFullInstallationResetRemediationAttestationVerifier>());

    }

    [Fact]

    public void Full_reset_request_contract_binds_the_operation_apply_and_attestation()
    {

        Type? requestType = typeof(InstallationResetApplyRequest).Assembly.GetType(
            "RetroDownfall.Arcanum.Core.DataLifecycle.FullInstallationResetRequest");

        Assert.NotNull(requestType);

        var parameters = Assert.Single(requestType.GetConstructors()).GetParameters();

        Assert.Equal(
            [
                typeof(Guid),
                typeof(InstallationResetApplyRequest),
                typeof(FullInstallationResetExternalRemediationAttestation),
            ],
            parameters.Select(static parameter => parameter.ParameterType));

        var properties = requestType.GetProperties().ToDictionary(
            static property => property.Name,
            StringComparer.Ordinal);

        Assert.Null(properties[nameof(FullInstallationResetRequest.OperationId)]
            .GetCustomAttributes(
            typeof(JsonRequiredAttribute),
            inherit: false).SingleOrDefault());

        Assert.NotNull(properties[nameof(FullInstallationResetRequest.Apply)]
            .GetCustomAttributes(
            typeof(JsonRequiredAttribute),
            inherit: false).SingleOrDefault());

        Assert.NotNull(properties[nameof(FullInstallationResetRequest.ExternalRemediation)]
            .GetCustomAttributes(
            typeof(JsonRequiredAttribute),
            inherit: false).SingleOrDefault());

    }

    [Fact]

    public void Wire_inputs_have_one_cli_owner_and_closed_authority_types_have_no_json_owner()
    {

        Assert.NotNull(CliJsonContext.Default.GetTypeInfo(
            typeof(FullInstallationResetExternalRemediationAttestation)));

        Assert.NotNull(CliJsonContext.Default.GetTypeInfo(
            typeof(FullInstallationResetRequest)));

        Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(
            typeof(FullInstallationResetExternalRemediationAttestation)));

        Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(
            typeof(FullInstallationResetRequest)));

        Assert.Null(InstallationResetActiveJsonContext.Default.GetTypeInfo(
            typeof(FullInstallationResetExternalRemediationAttestation)));

        Assert.Null(InstallationResetActiveJsonContext.Default.GetTypeInfo(
            typeof(FullInstallationResetRequest)));

        foreach (Type closed in (Type[])
                 [
                     typeof(FullInstallationResetRemediationAuthorization),
                     typeof(FullInstallationResetRemediationTrustRoot),
                 ])
        {

            Assert.Null(CliJsonContext.Default.GetTypeInfo(closed));

            Assert.Null(ArcanumJsonContext.Default.GetTypeInfo(closed));

            Assert.Null(InstallationResetActiveJsonContext.Default.GetTypeInfo(closed));

        }

    }

    [Fact]

    public void Authenticated_claim_match_accepts_the_exact_claim_after_expiry_without_reauthorization()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation signed = Sign(
            signer,
            UnsignedAttestation(pair, now));

        FakeTrustRootProvider roots = new(
            new FullInstallationResetRemediationTrustRoot(
                signer.ExportSubjectPublicKeyInfo()));

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(
            roots,
            clock);

        Result<FullInstallationResetRemediationAuthorization> authorized =
            verifier.Verify(signed, signed.InstallationId, pair);

        Assert.True(authorized.IsSuccess, authorized.Error.Message);

        clock.SetUtcNow(signed.ExpiresAtUtc.AddSeconds(1));

        bool matches = verifier.MatchesAuthenticatedClaim(
            signed,
            signed.InstallationId,
            pair,
            authorized.Value.OperationId,
            authorized.Value.InstallationId,
            authorized.Value.AttestationDigest,
            authorized.Value.NonceDigest,
            authorized.Value.IssuerDigest);

        Assert.True(matches);

        Assert.Equal(1, roots.ResolveCount);

    }

    [Fact]

    public void Authenticated_claim_match_rejects_noncanonical_shape_action_and_signature_without_root_lookup()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation valid = Sign(
            signer,
            UnsignedAttestation(pair, now));

        FakeTrustRootProvider roots = new(
            new FullInstallationResetRemediationTrustRoot(
                signer.ExportSubjectPublicKeyInfo()));

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(roots, clock);

        Result<FullInstallationResetRemediationAuthorization> authorized =
            verifier.Verify(valid, valid.InstallationId, pair);

        Assert.True(authorized.IsSuccess, authorized.Error.Message);

        FullInstallationResetExternalRemediationAttestation[] malformed =
        [
            valid with { Version = 2 },
            valid with { RemediationActionDigest = Digest(0x64) },
            valid with { NonceBase64Url = valid.NonceBase64Url + "=" },
            valid with { IssuedAtUtc = valid.IssuedAtUtc.AddTicks(1) },
            valid with { ExpiresAtUtc = valid.IssuedAtUtc.AddHours(24).AddSeconds(1) },
            valid with { SignatureBase64Url = valid.SignatureBase64Url + "=" },
            valid with { SignatureBase64Url = Base64Url.EncodeToString(new byte[63]) },
        ];

        foreach (FullInstallationResetExternalRemediationAttestation candidate in malformed)
        {

            bool matches = MatchesAuthenticatedClaim(
                verifier,
                candidate,
                valid.InstallationId,
                pair,
                authorized.Value);

            Assert.False(matches);

        }

        Assert.Equal(1, roots.ResolveCount);

    }

    [Fact]

    public void Authenticated_claim_match_rejects_each_current_evidence_mismatch_without_root_lookup()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation valid = Sign(
            signer,
            UnsignedAttestation(pair, now));

        FakeTrustRootProvider roots = new(
            new FullInstallationResetRemediationTrustRoot(
                signer.ExportSubjectPublicKeyInfo()));

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(roots, clock);

        Result<FullInstallationResetRemediationAuthorization> authorized =
            verifier.Verify(valid, valid.InstallationId, pair);

        Assert.True(authorized.IsSuccess, authorized.Error.Message);

        FullInstallationResetExternalRemediationAttestation[] mismatched =
        [
            valid with { InstallationId = Guid.NewGuid() },
            valid with { HostToolsTransitionId = Guid.NewGuid() },
            valid with { TaintMasterKeyVersion = ulong.MaxValue },
            valid with { AuthorityFingerprint = Digest(0x61) },
            valid with { DatabaseMarkerDigest = Digest(0x62) },
            valid with { OsMarkerDigest = Digest(0x63) },
        ];

        foreach (FullInstallationResetExternalRemediationAttestation candidate in mismatched)
        {

            bool matches = MatchesAuthenticatedClaim(
                verifier,
                candidate,
                valid.InstallationId,
                pair,
                authorized.Value);

            Assert.False(matches);

        }

        Assert.False(MatchesAuthenticatedClaim(
            verifier,
            valid,
            Guid.NewGuid(),
            pair,
            authorized.Value));

        Assert.Equal(1, roots.ResolveCount);

    }

    [Fact]

    public void Authenticated_claim_match_rejects_each_changed_stored_identity_field()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation signed = Sign(
            signer,
            UnsignedAttestation(pair, now));

        FakeTrustRootProvider roots = new(
            new FullInstallationResetRemediationTrustRoot(
                signer.ExportSubjectPublicKeyInfo()));

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(
            roots,
            clock);

        Result<FullInstallationResetRemediationAuthorization> authorized =
            verifier.Verify(signed, signed.InstallationId, pair);

        Assert.True(authorized.IsSuccess, authorized.Error.Message);

        (Guid OperationId, Guid InstallationId, CovenantDigest AttestationDigest,
            CovenantDigest NonceDigest, CovenantDigest IssuerDigest)[] changed =
        [
            (
                Guid.NewGuid(),
                authorized.Value.InstallationId,
                authorized.Value.AttestationDigest,
                authorized.Value.NonceDigest,
                authorized.Value.IssuerDigest),
            (
                authorized.Value.OperationId,
                Guid.NewGuid(),
                authorized.Value.AttestationDigest,
                authorized.Value.NonceDigest,
                authorized.Value.IssuerDigest),
            (
                authorized.Value.OperationId,
                authorized.Value.InstallationId,
                Digest(0x71),
                authorized.Value.NonceDigest,
                authorized.Value.IssuerDigest),
            (
                authorized.Value.OperationId,
                authorized.Value.InstallationId,
                authorized.Value.AttestationDigest,
                Digest(0x72),
                authorized.Value.IssuerDigest),
            (
                authorized.Value.OperationId,
                authorized.Value.InstallationId,
                authorized.Value.AttestationDigest,
                authorized.Value.NonceDigest,
                Digest(0x73)),
        ];

        foreach (var accepted in changed)
        {

            Assert.False(verifier.MatchesAuthenticatedClaim(
                signed,
                signed.InstallationId,
                pair,
                accepted.OperationId,
                accepted.InstallationId,
                accepted.AttestationDigest,
                accepted.NonceDigest,
                accepted.IssuerDigest));

        }

        Assert.Equal(1, roots.ResolveCount);

    }

    [Fact]

    public void Verifier_accepts_the_exact_current_pair_and_fixed_width_p1363_signature()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation unsigned =
            UnsignedAttestation(pair, now);

        Result<byte[]> preimage =
            FullInstallationResetRemediationPreimage.Build(unsigned);

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        byte[] signature = signer.SignData(
            preimage.Value,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        FullInstallationResetExternalRemediationAttestation signed = unsigned with
        {

            SignatureBase64Url = Base64Url.EncodeToString(signature),

        };

        FakeTrustRootProvider roots = new(
            new FullInstallationResetRemediationTrustRoot(
                signer.ExportSubjectPublicKeyInfo()));

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(
            roots,
            clock);

        Result<FullInstallationResetRemediationAuthorization> result =
            verifier.Verify(signed, signed.InstallationId, pair);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(signed.OperationId, result.Value.OperationId);

        Assert.Equal(signed.InstallationId, result.Value.InstallationId);

        Assert.Equal(now, result.Value.AcceptedAtUtc);

        Assert.True(result.Value.AttestationDigest.IsValid);

        Assert.True(result.Value.NonceDigest.IsValid);

        Assert.True(result.Value.IssuerDigest.IsValid);

        Assert.Equal(
            "7027da2e8b1281ca0cb1821d156c0a95b0484e9cd75051c03e3d5d010900d760",
            Convert.ToHexString(result.Value.NonceDigest.Bytes).ToLowerInvariant());

        Assert.Equal(
            "cec9885069640b56ce4dd3f216045fa6f0ea33e3cf1a192abb4e78b1b80a29f4",
            Convert.ToHexString(result.Value.IssuerDigest.Bytes).ToLowerInvariant());

        Assert.Equal(1, roots.ResolveCount);

    }

    [Fact]

    public void Verifier_rejects_a_wrong_key_der_signature_and_non_p256_root()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation unsigned =
            UnsignedAttestation(pair, now);

        using ECDsa trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(
            new FakeTrustRootProvider(
                new FullInstallationResetRemediationTrustRoot(
                    trusted.ExportSubjectPublicKeyInfo())),
            clock);

        Result<FullInstallationResetRemediationAuthorization> wrongKey =
            verifier.Verify(
                Sign(attacker, unsigned),
                unsigned.InstallationId,
                pair);

        AssertInvalid(wrongKey, unsigned);

        Result<byte[]> preimage =
            FullInstallationResetRemediationPreimage.Build(unsigned);

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        byte[] derSignature = trusted.SignData(
            preimage.Value,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        Result<FullInstallationResetRemediationAuthorization> der =
            verifier.Verify(
                unsigned with
                {

                    SignatureBase64Url = Base64Url.EncodeToString(derSignature),

                },
                unsigned.InstallationId,
                pair);

        AssertInvalid(der, unsigned);

        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        FullInstallationResetRemediationAttestationVerifier wrongAlgorithm = new(
            new FakeTrustRootProvider(
                new FullInstallationResetRemediationTrustRoot(
                    p384.ExportSubjectPublicKeyInfo())),
            clock);

        Result<FullInstallationResetRemediationAuthorization> algorithm =
            wrongAlgorithm.Verify(
                Sign(trusted, unsigned),
                unsigned.InstallationId,
                pair);

        AssertInvalid(algorithm, unsigned);

    }

    [Fact]

    public void Verifier_rejects_every_attested_pair_field_mismatch_even_when_resigned()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation unsigned =
            UnsignedAttestation(pair, now);

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(
            new FakeTrustRootProvider(
                new FullInstallationResetRemediationTrustRoot(
                    signer.ExportSubjectPublicKeyInfo())),
            clock);

        (string Name, Func<
            FullInstallationResetExternalRemediationAttestation,
            FullInstallationResetExternalRemediationAttestation> Mutate)[] mutations =
        [
            ("installation", value => value with { InstallationId = Guid.NewGuid() }),
            ("transition", value => value with { HostToolsTransitionId = Guid.NewGuid() }),
            ("taint version", value => value with { TaintMasterKeyVersion = ulong.MaxValue }),
            ("fingerprint", value => value with { AuthorityFingerprint = Digest(0x61) }),
            ("database digest", value => value with { DatabaseMarkerDigest = Digest(0x62) }),
            ("OS digest", value => value with { OsMarkerDigest = Digest(0x63) }),
            ("remediation action", value => value with { RemediationActionDigest = Digest(0x64) }),
        ];

        foreach (var (name, mutate) in mutations)
        {

            FullInstallationResetExternalRemediationAttestation candidate =
                Sign(signer, mutate(unsigned));

            Result<FullInstallationResetRemediationAuthorization> result =
                verifier.Verify(candidate, unsigned.InstallationId, pair);

            Assert.True(result.IsFailure, name);

            Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, result.Error.Code);

        }

        Result<FullInstallationResetRemediationAuthorization> staleCurrent =
            verifier.Verify(
                Sign(signer, unsigned),
                Guid.NewGuid(),
                pair);

        AssertInvalid(staleCurrent, unsigned);

    }

    [Fact]

    public void Verifier_rejects_noncanonical_or_out_of_bounds_attestation_shapes()
    {

        DateTimeOffset now = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        HostProcessToolsMatchedPair pair = MatchedPair();

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        FullInstallationResetExternalRemediationAttestation valid = Sign(
            signer,
            UnsignedAttestation(pair, now));

        FakeTimeProvider clock = new();

        clock.SetUtcNow(now);

        FullInstallationResetRemediationAttestationVerifier verifier = new(
            new FakeTrustRootProvider(
                new FullInstallationResetRemediationTrustRoot(
                    signer.ExportSubjectPublicKeyInfo())),
            clock);

        FullInstallationResetExternalRemediationAttestation[] malformed =
        [
            valid with { Version = 2 },
            valid with { OperationId = Guid.Empty },
            valid with { InstallationId = Guid.Empty },
            valid with { HostToolsTransitionId = Guid.Empty },
            valid with { TaintMasterKeyVersion = 0 },
            valid with { AuthorityFingerprint = default },
            valid with { DatabaseMarkerDigest = default },
            valid with { OsMarkerDigest = default },
            valid with { RemediationActionDigest = default },
            valid with { NonceBase64Url = Base64Url.EncodeToString(new byte[15]) },
            valid with { NonceBase64Url = Base64Url.EncodeToString(new byte[33]) },
            valid with { NonceBase64Url = valid.NonceBase64Url + "=" },
            valid with { NonceBase64Url = valid.NonceBase64Url[..^1] + "x" },
            valid with { Issuer = "retrodownfall.remediation.v1" },
            valid with { IssuedAtUtc = valid.IssuedAtUtc.AddTicks(1) },
            valid with { IssuedAtUtc = valid.IssuedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            valid with { ExpiresAtUtc = valid.IssuedAtUtc },
            valid with { ExpiresAtUtc = valid.IssuedAtUtc.AddHours(24).AddSeconds(1) },
            valid with { SignatureBase64Url = valid.SignatureBase64Url + "=" },
            valid with { SignatureBase64Url = Base64Url.EncodeToString(new byte[63]) },
        ];

        foreach (FullInstallationResetExternalRemediationAttestation candidate in malformed)
        {

            Result<FullInstallationResetRemediationAuthorization> result =
                verifier.Verify(candidate, valid.InstallationId, pair);

            AssertInvalid(result, candidate);

        }

    }

    [Fact]

    public void Verifier_accepts_only_the_signed_half_open_time_window()
    {

        DateTimeOffset issuedAtUtc = new(2026, 8, 22, 14, 0, 0, TimeSpan.Zero);

        HostProcessToolsMatchedPair pair = MatchedPair();

        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        FullInstallationResetExternalRemediationAttestation signed = Sign(
            signer,
            UnsignedAttestation(pair, issuedAtUtc));

        foreach (DateTimeOffset refused in (DateTimeOffset[])
                 [
                     issuedAtUtc.AddTicks(-1),
                     signed.ExpiresAtUtc,
                     signed.ExpiresAtUtc.AddTicks(1),
                 ])
        {

            FakeTimeProvider clock = new();

            clock.SetUtcNow(refused);

            FullInstallationResetRemediationAttestationVerifier verifier = new(
                new FakeTrustRootProvider(
                    new FullInstallationResetRemediationTrustRoot(
                        signer.ExportSubjectPublicKeyInfo())),
                clock);

            Result<FullInstallationResetRemediationAuthorization> result =
                verifier.Verify(signed, signed.InstallationId, pair);

            AssertInvalid(result, signed);

        }

    }

    private static HostProcessToolsMatchedPair MatchedPair()
    {

        const string installation = "10213243-5465-7687-98a9-bacbdcedfe0f";

        Guid transition = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

        CovenantDigest fingerprint = Digest(0x11);

        HostProcessToolsDatabaseMarkerEvidence database = new(
            installation,
            CovenantHostToolsState.HostToolsTainted,
            transition,
            0x0102030405060708,
            fingerprint);

        HostProcessToolsOsMarkerEvidence marker = new(
            installation,
            transition,
            0x0102030405060708,
            fingerprint,
            Digest(0x33),
            Digest(0x55));

        return new HostProcessToolsMatchedPair(database, marker);

    }

    private static FullInstallationResetExternalRemediationAttestation UnsignedAttestation(
        HostProcessToolsMatchedPair pair,
        DateTimeOffset issuedAtUtc) =>
        new(
            Version: 1,
            OperationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            InstallationId: Guid.Parse(pair.Database.InstallationIdentity),
            HostToolsTransitionId: pair.Database.TransitionId!.Value,
            TaintMasterKeyVersion: pair.Database.TaintMasterKeyVersion!.Value,
            AuthorityFingerprint: pair.Database.TaintFingerprint!.Value,
            DatabaseMarkerDigest: pair.Database.DatabaseMarkerDigest,
            OsMarkerDigest: pair.OsMarker.MarkerBytesDigest,
            RemediationActionDigest: new CovenantDigest(
                Convert.FromHexString(
                    "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9")),
            NonceBase64Url: "oKGio6SlpqeoqaqrrK2urw",
            Issuer: "RetroDownfall.Remediation.v1",
            IssuedAtUtc: issuedAtUtc,
            ExpiresAtUtc: issuedAtUtc.AddHours(1),
            SignatureBase64Url: string.Empty);

    private static FullInstallationResetExternalRemediationAttestation Sign(
        ECDsa signer,
        FullInstallationResetExternalRemediationAttestation unsigned)
    {

        Result<byte[]> preimage =
            FullInstallationResetRemediationPreimage.Build(unsigned);

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        byte[] signature = signer.SignData(
            preimage.Value,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return unsigned with
        {

            SignatureBase64Url = Base64Url.EncodeToString(signature),

        };

    }

    private static CovenantDigest Digest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

    private static bool MatchesAuthenticatedClaim(
        FullInstallationResetRemediationAttestationVerifier verifier,
        FullInstallationResetExternalRemediationAttestation attestation,
        Guid currentInstallationId,
        HostProcessToolsMatchedPair matchedPair,
        FullInstallationResetRemediationAuthorization accepted) =>
        verifier.MatchesAuthenticatedClaim(
            attestation,
            currentInstallationId,
            matchedPair,
            accepted.OperationId,
            accepted.InstallationId,
            accepted.AttestationDigest,
            accepted.NonceDigest,
            accepted.IssuerDigest);

    private static void AssertInvalid<T>(
        Result<T> result,
        FullInstallationResetExternalRemediationAttestation attestation)
    {

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, result.Error.Code);

        Assert.Equal(
            "The external remediation attestation could not be verified.",
            result.Error.Message);

        Assert.DoesNotContain(
            attestation.NonceBase64Url,
            result.Error.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            attestation.Issuer,
            result.Error.Message,
            StringComparison.Ordinal);

        if (!string.IsNullOrEmpty(attestation.SignatureBase64Url))
        {

            Assert.DoesNotContain(
                attestation.SignatureBase64Url,
                result.Error.Message,
                StringComparison.Ordinal);

        }

    }

    private sealed class FakeTrustRootProvider(
        FullInstallationResetRemediationTrustRoot root)
        : IFullInstallationResetRemediationTrustRootProvider
    {

        internal int ResolveCount { get; private set; }

        public bool TryResolve(
            string issuer,
            out FullInstallationResetRemediationTrustRoot? trustRoot)
        {

            ResolveCount++;

            trustRoot = string.Equals(
                issuer,
                "RetroDownfall.Remediation.v1",
                StringComparison.Ordinal)
                ? root
                : null;

            return trustRoot is not null;

        }

    }

}

using System.Buffers.Text;

using System.Runtime.Versioning;

using System.Security.AccessControl;

using System.Security.Principal;

using System.Text.Json;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class FullInstallationResetAttestationFileReaderTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-remediation-reader-" + Guid.NewGuid().ToString("N"));

    public FullInstallationResetAttestationFileReaderTests()
    {

        Directory.CreateDirectory(_root);

    }

    [Fact]

    public async Task Reader_uses_bounded_strict_source_generated_json_without_disclosing_input()
    {

        // Mutations caught: File.ReadAllBytes, reflection-backed JSON, permissive unknown members,
        // taint-version narrowing, and echoing the input path or signature in an error.
        string path = Path.Combine(_root, "remediation-secret.json");

        FullInstallationResetExternalRemediationAttestation attestation = CreateAttestation();

        string json = JsonSerializer.Serialize(
            attestation,
            CliJsonContext.Default.FullInstallationResetExternalRemediationAttestation);

        await File.WriteAllTextAsync(path, json);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        FullInstallationResetAttestationFileReader reader = new();

        Assert.Equal(16, FullInstallationResetAttestationFileReader.DecoderMaximumDepth);

        Assert.Equal(
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            FullInstallationResetAttestationFileReader.DecoderUnmappedMemberHandling);

        Result<FullInstallationResetExternalRemediationAttestation> read =
            await reader.ReadAsync(path, CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(ulong.MaxValue, read.Value.TaintMasterKeyVersion);

        string unknown = json[..^1] + ",\"unsigned\":true}";

        await File.WriteAllTextAsync(path, unknown);

        Result<FullInstallationResetExternalRemediationAttestation> rejected =
            await reader.ReadAsync(path, CancellationToken.None);

        Assert.True(rejected.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, rejected.Error.Code);

        Assert.DoesNotContain(path, rejected.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(
            attestation.SignatureBase64Url,
            rejected.Error.Message,
            StringComparison.Ordinal);

        await File.WriteAllBytesAsync(path, new byte[(64 * 1024) + 1]);

        Result<FullInstallationResetExternalRemediationAttestation> oversized =
            await reader.ReadAsync(path, CancellationToken.None);

        Assert.True(oversized.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, oversized.Error.Code);

    }

    [Fact]

    public async Task Reader_rejects_a_file_not_controlled_only_by_the_current_owner()
    {

        string path = Path.Combine(_root, "shared-remediation.json");

        string json = JsonSerializer.Serialize(
            CreateAttestation(),
            CliJsonContext.Default.FullInstallationResetExternalRemediationAttestation);

        await File.WriteAllTextAsync(path, json);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        if (OperatingSystem.IsWindows())
        {

            GrantWindowsWorldRead(path);

        }
        else
        {

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead);

        }

        FullInstallationResetAttestationFileReader reader = new();

        Result<FullInstallationResetExternalRemediationAttestation> rejected =
            await reader.ReadAsync(path, CancellationToken.None);

        Assert.True(rejected.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, rejected.Error.Code);

        Assert.DoesNotContain(path, rejected.Error.Message, StringComparison.Ordinal);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    private static FullInstallationResetExternalRemediationAttestation CreateAttestation() =>
        new(
            Version: 1,
            OperationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            InstallationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            HostToolsTransitionId: Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f"),
            TaintMasterKeyVersion: ulong.MaxValue,
            AuthorityFingerprint: Digest(0x10),
            DatabaseMarkerDigest: Digest(0x20),
            OsMarkerDigest: Digest(0x30),
            RemediationActionDigest: Digest(0x40),
            NonceBase64Url: Base64Url.EncodeToString(new byte[16]),
            Issuer: "RetroDownfall.Remediation.v1",
            IssuedAtUtc: new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            ExpiresAtUtc: new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
            SignatureBase64Url: Base64Url.EncodeToString(new byte[64]));

    private static CovenantDigest Digest(byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

    [SupportedOSPlatform("windows")]
    private static void GrantWindowsWorldRead(string path)
    {

        FileInfo file = new(path);

        FileSecurity security = file.GetAccessControl();

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            FileSystemRights.Read,
            AccessControlType.Allow));

        file.SetAccessControl(security);

    }

}

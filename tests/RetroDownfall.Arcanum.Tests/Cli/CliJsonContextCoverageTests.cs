using System.Reflection;
using System.Text.Json;

using System.Text.Json.Serialization;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #49 — every structured CLI payload must resolve through source-generated metadata so
/// <c>--json</c> keeps working under Native AOT, and the wire policy (camelCase, one compact
/// document, string enum names, fixed error envelope) must not drift silently.
/// </summary>
public sealed class CliJsonContextCoverageTests
{

    [Fact]

    public void Cli_output_context_does_not_inherit_attestation_input_limits()
    {

        Assert.Equal(0, CliJsonContext.Default.Options.MaxDepth);

        Assert.Equal(
            JsonUnmappedMemberHandling.Skip,
            CliJsonContext.Default.Options.UnmappedMemberHandling);

    }

    [Fact]

    public void Full_reset_attestation_is_source_generated_strict_and_preserves_ulong()
    {

        // Mutations caught: leaving the CLI-only input out of source generation, accepting an
        // unknown field, or narrowing the signed taint version below the full UInt64 domain.
        FullInstallationResetExternalRemediationAttestation attestation = new(
            Version: 1,
            OperationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            InstallationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            HostToolsTransitionId: Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f"),
            TaintMasterKeyVersion: ulong.MaxValue,
            AuthorityFingerprint: Digest(0x10),
            DatabaseMarkerDigest: Digest(0x20),
            OsMarkerDigest: Digest(0x30),
            RemediationActionDigest: Digest(0x40),
            NonceBase64Url: "AAECAwQFBgcICQoLDA0ODw",
            Issuer: "RetroDownfall.Remediation.v1",
            IssuedAtUtc: new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            ExpiresAtUtc: new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
            SignatureBase64Url: new string('A', 86));

        JsonTypeInfo typeInfo = Assert.IsAssignableFrom<JsonTypeInfo>(
            CliJsonContext.Default.GetTypeInfo(attestation.GetType()));

        string json = JsonSerializer.Serialize(attestation, typeInfo);

        FullInstallationResetExternalRemediationAttestation roundTrip = Assert.IsType<
            FullInstallationResetExternalRemediationAttestation>(
                JsonSerializer.Deserialize(json, typeInfo));

        Assert.Equal(ulong.MaxValue, roundTrip.TaintMasterKeyVersion);

        string negative = json.Replace(
            "\"taintMasterKeyVersion\":18446744073709551615",
            "\"taintMasterKeyVersion\":-1",
            StringComparison.Ordinal);

        string overflow = json.Replace(
            "\"taintMasterKeyVersion\":18446744073709551615",
            "\"taintMasterKeyVersion\":18446744073709551616",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(negative, typeInfo));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(overflow, typeInfo));

    }

    /// <summary>
    /// Discovers the CLI's structured output types by convention rather than by an assumed
    /// namespace, so a new command that adds a payload but forgets to register it fails here.
    /// </summary>
    private static IReadOnlyList<Type> DiscoverPayloadTypes() =>
        [.. typeof(CliExitCode).Assembly
            .GetTypes()
            .Where(static candidate =>
                candidate is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && candidate.Name.EndsWith("Payload", StringComparison.Ordinal))
            .OrderBy(static candidate => candidate.FullName, StringComparer.Ordinal)];

    private static CovenantDigest Digest(byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

    public static TheoryData<Type> CliPayloadTypes
    {

        get
        {

            TheoryData<Type> data = [];

            foreach (Type type in DiscoverPayloadTypes())
            {

                data.Add(type);

            }

            return data;

        }

    }

    [Fact]
    public void The_discovery_convention_actually_finds_the_cli_payloads()
    {

        IReadOnlyList<Type> discovered = DiscoverPayloadTypes();

        Assert.NotEmpty(discovered);

        Assert.Contains(typeof(CliErrorPayload), discovered);

        Assert.Contains(typeof(CredentialInventoryPayload), discovered);

    }

    [Theory]
    [MemberData(nameof(CliPayloadTypes))]
    public void Every_cli_payload_type_is_registered_in_a_source_generated_context(Type type)
    {

        JsonTypeInfo? typeInfo = CliJsonContext.Default.GetTypeInfo(type);

        Assert.True(
            typeInfo is not null,
            $"{type.FullName} is a CLI payload but is not reachable from CliJsonContext. "
            + "Add a [JsonSerializable] entry (directly or through an owning payload).");

    }

    [Theory]
    [MemberData(nameof(CliPayloadTypes))]
    public void Every_registered_payload_resolves_property_metadata_without_reflection(Type type)
    {

        JsonTypeInfo typeInfo = CliJsonContext.Default.GetTypeInfo(type)!;

        Assert.NotNull(typeInfo.Properties);

        Assert.All(
            typeInfo.Properties,
            static property => Assert.False(string.IsNullOrEmpty(property.Name)));

    }

    [Fact]
    public void Payload_properties_use_camel_case_on_the_wire()
    {

        string json = JsonSerializer.Serialize(
            new CredentialInventoryPayload(
                [
                    new CredentialInventoryEntryPayload(
                        "master-api-key",
                        "master",
                        "Master API key",
                        "os-credential-store-with-encrypted-mirror",
                        "missing",
                        "none",
                        null,
                        "recovery"),
                ]),
            CliJsonContext.Default.CredentialInventoryPayload);

        Assert.Contains("\"credentials\"", json, StringComparison.Ordinal);

        Assert.Contains("\"displayName\"", json, StringComparison.Ordinal);

        Assert.Contains("\"environmentVariable\"", json, StringComparison.Ordinal);

        Assert.DoesNotContain("\"DisplayName\"", json, StringComparison.Ordinal);

    }

    [Fact]
    public void Payloads_are_written_as_one_compact_line()
    {

        string json = JsonSerializer.Serialize(
            new CliTextPayload("output", 0),
            CliJsonContext.Default.CliTextPayload);

        Assert.DoesNotContain('\n', json);

        Assert.DoesNotContain('\r', json);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

    }

    /// <summary>
    /// The CLI contract keeps optional members present-and-null rather than omitted, so a consumer
    /// can distinguish "known to be absent" from "this build does not emit that field".
    /// </summary>
    [Fact]
    public void Optional_members_stay_present_as_explicit_nulls()
    {

        string json = JsonSerializer.Serialize(
            new CredentialInventoryEntryPayload(
                "id",
                "inference-provider",
                "alpha",
                "environment-reference-or-secure-store",
                "missing",
                "none",
                null,
                "recovery"),
            CliJsonContext.Default.CredentialInventoryEntryPayload);

        Assert.Contains("\"environmentVariable\":null", json, StringComparison.Ordinal);

    }

    [Fact]
    public void Enums_reach_the_wire_as_stable_names_not_ordinals()
    {

        string json = JsonSerializer.Serialize(
            new SetupPlanPayloadFixture().Value,
            CliJsonContext.Default.SetupPlanPayload);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(
            JsonValueKind.String,
            document.RootElement.GetProperty("step").ValueKind);

        Assert.Equal(
            JsonValueKind.String,
            document.RootElement.GetProperty("connectivity").GetProperty("status").ValueKind);

    }

    [Fact]
    public void The_error_envelope_has_a_fixed_shape()
    {

        string json = JsonSerializer.Serialize(
            new CliErrorPayload("A network operation failed.", (int)CliExitCode.NetworkError),
            CliJsonContext.Default.CliErrorPayload);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(2, document.RootElement.EnumerateObject().Count());

        Assert.Equal(
            "A network operation failed.",
            document.RootElement.GetProperty("error").GetString());

        Assert.Equal(
            (int)CliExitCode.NetworkError,
            document.RootElement.GetProperty("exitCode").GetInt32());

    }

    [Fact]
    public void A_serialization_failure_emits_the_fixed_error_envelope_and_no_partial_document()
    {

        StringWriter standardOutput = new();

        StringWriter standardError = new();

        ConsoleDispatcher dispatcher = new(
            standardOutput,
            standardError,
            new CliInvocationOptions(Json: true, Plain: false, Yes: false));

        dispatcher.WriteJson(
            new CliTextPayload("ignored", 0),
            new ThrowingTypeInfoFactory().Create());

        using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());

        Assert.Equal(
            (int)CliExitCode.GenericError,
            document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.Contains(
            "could not be serialized",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "could not be serialized",
            standardError.ToString(),
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void The_dispatcher_never_serializes_without_explicit_type_metadata()
    {

        MethodInfo[] writeJson = [.. typeof(IConsoleDispatcher)
            .GetMethods()
            .Where(static method => method.Name == nameof(IConsoleDispatcher.WriteJson))];

        Assert.Equal(2, writeJson.Length);

        Assert.Contains(
            writeJson,
            static method => method.GetParameters()
                .Any(static parameter =>
                    parameter.ParameterType.IsGenericType
                    && parameter.ParameterType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>)));

        Assert.Contains(
            writeJson,
            static method => method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(JsonElement));

    }

    private sealed class SetupPlanPayloadFixture
    {

        public RetroDownfall.Arcanum.Cli.Services.Setup.SetupPlanPayload Value { get; } =
            new(
                "Review",
                true,
                false,
                [],
                [],
                new RetroDownfall.Arcanum.Cli.Services.Setup.SetupConnectivityPayload(
                    "Reachable",
                    12,
                    1,
                    true,
                    "reachable"),
                [],
                [],
                new RetroDownfall.Arcanum.Cli.Services.Setup.SetupCompletionSummaryPayload(
                    "General Assistant",
                    "alpha / gpt-test",
                    "Loopback",
                    "Workspace: .; Campaign: none",
                    ["loopback"],
                    ["none"],
                    "Ward enabled",
                    "loopback host binding",
                    "arcanum run \"Hello\""));

    }

    /// <summary>
    /// Produces metadata whose converter always throws, standing in for a payload that cannot be
    /// serialized at run time.
    /// </summary>
    private sealed class ThrowingTypeInfoFactory
    {

        public JsonTypeInfo<CliTextPayload> Create() =>
            JsonMetadataServices.CreateValueInfo<CliTextPayload>(
                new JsonSerializerOptions(),
                new ThrowingConverter());

        private sealed class ThrowingConverter
            : System.Text.Json.Serialization.JsonConverter<CliTextPayload>
        {

            public override CliTextPayload Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options) =>
                throw new NotSupportedException("read is not supported");

            public override void Write(
                Utf8JsonWriter writer,
                CliTextPayload value,
                JsonSerializerOptions options) =>
                throw new NotSupportedException("write is not supported");

        }

    }

}

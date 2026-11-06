using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildAuthorityContractTests
{
    [Fact]
    public void Peer_binding_uses_stable_string_id_and_host_derived_revision_digest()
    {
        Assert.NotNull(typeof(GuildCallerContext).GetProperty("PeerId"));

        Assert.Equal(typeof(string), typeof(GuildCallerContext).GetProperty("PeerId")!.PropertyType);

        Assert.Equal(typeof(string), typeof(GuildCallerContext).GetProperty("PeerGrantRevision")!.PropertyType);
    }

    [Fact]
    public void Operator_verification_has_a_separate_constructor_without_borrowing_a_run()
    {
        Assert.NotNull(typeof(GuildVerifiedAuthority).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(GuildCallerContext), typeof(GuildAuthorityAction), typeof(Guid), typeof(long)]));
    }

    [Fact]
    public void Execution_binding_preserves_the_exact_issuer_identity()
    {
        Assert.NotNull(typeof(GuildExecutionHandle).GetProperty("IssuerSignetId"));
    }

    [Fact]
    public void Host_handles_refuse_serialization_and_deserialization_even_with_explicit_metadata()
    {
        Guid rootId = Guid.NewGuid();

        GuildExecutionHandle execution = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), rootId, rootId, 1, 0);

        GuildCallerContext caller = new(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        GuildVerifiedAuthority authority = new(execution, GuildAuthorityAction.WorkspaceMutate, Guid.NewGuid(), 1);

        object[] handles =
        [
            execution,
            caller,
            authority,
            new GuildRecoveryContext(Guid.NewGuid(), Guid.NewGuid(), 0),
            new GuildAdmissionContext(caller, Guid.NewGuid(), new(Guid.NewGuid(), "digest")),
            new GuildInboxClaim(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, 0),
            new GuildMutationPermit(Guid.NewGuid(), 1, authority, "digest"),
            new GuildGrantScopeEvidence([], []),
        ];

        foreach (object handle in handles)
        {
            System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo = GuildHandleTestJsonContext.Default.GetTypeInfo(handle.GetType())!;

            Assert.NotNull(typeInfo);

            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(handle, typeInfo));

            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", typeInfo));

            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("null", typeInfo));
        }
    }

    [Fact]
    public void Host_caller_kinds_require_their_exact_authenticated_bindings()
    {
        Guid principal = Guid.NewGuid();

        Guid invocation = Guid.NewGuid();

        Guid installation = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new GuildCallerContext(GuildCallerKind.Run, principal, invocation, installation));

        Assert.Throws<ArgumentException>(() => new GuildCallerContext(GuildCallerKind.Peer, principal, invocation, installation));

        Assert.Throws<ArgumentException>(() => new GuildCallerContext(GuildCallerKind.HostReceipt, principal, invocation, installation));

        Assert.Throws<ArgumentException>(() => new GuildCallerContext(GuildCallerKind.Operator, principal, invocation, installation, peerGrantRevision: "digest"));

        Assert.Throws<ArgumentException>(() => new GuildCallerContext((GuildCallerKind)999, principal, invocation, installation));

        GuildCallerContext peer = new("configured-peer", "digest-of-security-binding", invocation, installation);

        Assert.Equal("digest-of-security-binding", peer.PeerGrantRevision);

        Assert.Equal("configured-peer", peer.PeerId);

        Assert.Null(peer.PrincipalId);

        Assert.Throws<ArgumentException>(() => new GuildCallerContext("configured-peer", " ", invocation, installation));

        Assert.Throws<ArgumentException>(() => new GuildCallerContext("", "digest", invocation, installation));

        Assert.Equal(GuildCallerKind.HostReceipt, new GuildCallerContext(GuildCallerKind.HostReceipt, principal, invocation, installation,
            receiptId: Guid.NewGuid(), receiptSource: "quest-result").Kind);
    }

    [Fact]
    public void Native_operator_mutation_permit_uses_its_own_principal_without_an_execution()
    {
        GuildCallerContext caller = new(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        GuildVerifiedAuthority authority = new(caller, GuildAuthorityAction.WorkspaceMutate, Guid.NewGuid(), 1);

        GuildMutationPermit permit = new(Guid.NewGuid(), 1, authority, "exact-plan-digest");

        Assert.Same(caller, permit.Authority.OperatorContext);

        Assert.Null(permit.Authority.Execution);

        GuildCallerContext peer = new("peer", "digest", Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => new GuildVerifiedAuthority(peer, GuildAuthorityAction.WorkspaceMutate, Guid.NewGuid(), 1));
    }

    [Fact]
    public void Execution_handles_keep_root_and_child_issuer_shapes_distinct()
    {
        Guid root = Guid.NewGuid();

        Guid child = Guid.NewGuid();

        Guid issuer = Guid.NewGuid();

        Assert.Null(new GuildExecutionHandle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), root, root, 1, 0).IssuerSignetId);

        Assert.Equal(issuer, new GuildExecutionHandle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), child, root, 1, 0, issuer).IssuerSignetId);

        Assert.Throws<ArgumentException>(() => new GuildExecutionHandle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), child, root, 1, 0));

        Assert.Throws<ArgumentException>(() => new GuildExecutionHandle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), root, root, 1, 0, issuer));
    }

    [Theory]
    [InlineData("GuildCallerContext")]
    [InlineData("GuildExecutionHandle")]
    [InlineData("GuildVerifiedAuthority")]
    [InlineData("GuildRecoveryContext")]
    [InlineData("GuildAdmissionContext")]
    [InlineData("GuildInboxClaim")]
    [InlineData("GuildMutationPermit")]
    public void Host_handle_is_sealed_and_has_no_public_construction_or_mutation(string name)
    {
        Type? type = typeof(Result).Assembly.GetType($"RetroDownfall.Arcanum.Core.Guild.{name}");

        Assert.NotNull(type);

        Assert.True(type.IsSealed);

        Assert.False(type.IsValueType);

        Assert.Empty(type.GetConstructors());

        Assert.Empty(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));

        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));

        JsonConverterAttribute? converter = type.GetCustomAttribute<JsonConverterAttribute>();

        Assert.NotNull(converter);

        Assert.NotNull(converter.ConverterType);
    }
}

[JsonSerializable(typeof(GuildCallerContext))]
[JsonSerializable(typeof(GuildExecutionHandle))]
[JsonSerializable(typeof(GuildVerifiedAuthority))]
[JsonSerializable(typeof(GuildRecoveryContext))]
[JsonSerializable(typeof(GuildAdmissionContext))]
[JsonSerializable(typeof(GuildInboxClaim))]
[JsonSerializable(typeof(GuildMutationPermit))]
[JsonSerializable(typeof(GuildGrantScopeEvidence))]
internal partial class GuildHandleTestJsonContext : JsonSerializerContext;

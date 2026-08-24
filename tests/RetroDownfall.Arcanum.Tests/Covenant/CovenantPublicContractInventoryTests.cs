using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Issue #88 — the frozen public contract inventory, enforced in both directions.
/// </summary>
/// <remarks>
/// The defect this prevents is a second wire shape for something that already has one. A duplicate
/// DTO compiles, ships, and then diverges: one route grows a field, the other does not, and the
/// difference is discovered by a client. The inventory is the single list, and these tests fail both
/// when a declared entry names a type that no longer exists and when a public Covenant wire type is
/// not declared — either direction alone rots, because an inventory nobody adds to is just a comment.
///
/// <para>The reflection-fallback check is not decoration. A payload that resolves through the
/// reflection resolver works in Debug and throws in a Native AOT binary, which is the configuration
/// operators actually run.</para>
/// </remarks>
public sealed class CovenantPublicContractInventoryTests
{

    private static readonly Assembly CoreAssembly = typeof(CovenantOperationScope).Assembly;

    private static readonly ImmutableArray<Assembly> ShippingAssemblies =
    [
        CoreAssembly,
        typeof(ArcanumJsonContext).Assembly,
        typeof(CovenantRecoveryJsonContext).Assembly,
    ];

    [Fact]
    public void The_inventory_is_not_empty_and_declares_no_type_twice()
    {

        ImmutableArray<CovenantPublicContract> declared = CovenantPublicContractInventory.Contracts;

        Assert.NotEmpty(declared);

        string[] duplicates =
        [
            .. declared
                .GroupBy(static entry => entry.WireTypeName, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
        ];

        Assert.Empty(duplicates);

    }

    [Fact]
    public void Every_declared_wire_type_still_exists()
    {

        foreach (CovenantPublicContract entry in CovenantPublicContractInventory.Contracts)
        {

            Assert.True(
                Resolve(entry.WireTypeName) is not null,
                $"The inventory declares {entry.WireTypeName}, which no longer exists.");

        }

    }

    [Fact]
    public void Every_declared_port_still_exists_and_is_an_interface()
    {

        Assert.NotEmpty(CovenantPublicContractInventory.Ports);

        foreach (CovenantServicePort port in CovenantPublicContractInventory.Ports)
        {

            Type? resolved = Resolve(port.PortTypeName);

            Assert.True(resolved is not null, $"The inventory declares the port {port.PortTypeName}, which no longer exists.");

            Assert.True(resolved!.IsInterface, $"{port.PortTypeName} is declared as a service port but is not an interface.");

        }

    }

    /// <summary>
    /// The reverse direction. A new public request or response shape in the Covenant namespace that
    /// nobody declared is exactly the duplicate this inventory exists to prevent.
    /// </summary>
    [Fact]
    public void Every_public_covenant_wire_type_is_declared()
    {

        HashSet<string> declared =
        [
            .. CovenantPublicContractInventory.Contracts.Select(static entry => entry.WireTypeName),
            .. CovenantPublicContractInventory.NonWireShapes.Select(static entry => entry.TypeName),
        ];

        string[] undeclared =
        [
            .. CoreAssembly
                .GetTypes()
                .Where(IsCovenantShapeKind)
                .Where(HasWireShapeName)
                .Select(static type => type.FullName!)
                .Where(name => !declared.Contains(name))
                .OrderBy(static name => name, StringComparer.Ordinal)
        ];

        Assert.Empty(undeclared);

    }

    /// <summary>
    /// The discovery this freeze runs on reaches value types, not only classes.
    /// </summary>
    /// <remarks>
    /// The Covenant namespace is full of public record structs, and a class-only filter made every one
    /// of them invisible to the freeze — a shape named <c>…Dto</c> could ship undeclared purely by
    /// being a struct. Asserted against a real public record struct in that namespace so the check
    /// cannot pass by describing itself.
    /// </remarks>
    [Fact]
    public void Discovery_reaches_a_public_record_struct_in_the_covenant_namespace()
    {

        Assert.True(typeof(CovenantScopeCensusRow).IsValueType);

        Assert.False(typeof(CovenantScopeCensusRow).IsClass);

        Assert.True(IsCovenantShapeKind(typeof(CovenantScopeCensusRow)));

        Assert.False(IsCovenantShapeKind(typeof(CovenantScope)));

        Assert.False(IsCovenantShapeKind(typeof(ICovenantStore)));

    }

    private static bool IsCovenantShapeKind(Type type) =>
        type.IsPublic
        && !type.IsAbstract
        && !type.IsEnum
        && !type.IsInterface
        && (type.IsClass || type.IsValueType)
        && type.Namespace == "RetroDownfall.Arcanum.Core.Covenant";

    private static bool HasWireShapeName(Type type) =>
        type.Name.EndsWith("Dto", StringComparison.Ordinal)
        || type.Name.EndsWith("Request", StringComparison.Ordinal);

    /// <summary>
    /// An exclusion has to be honest. A type declared as "not a wire shape" that turns out to be
    /// registered with the public wire context is a payload hiding behind a comment.
    /// </summary>
    [Fact]
    public void Every_declared_non_wire_shape_exists_and_really_is_not_on_the_wire()
    {

        Assert.NotEmpty(CovenantPublicContractInventory.NonWireShapes);

        foreach (CovenantNonWireShape entry in CovenantPublicContractInventory.NonWireShapes)
        {

            Type? resolved = Resolve(entry.TypeName);

            Assert.True(resolved is not null, $"The inventory excludes {entry.TypeName}, which no longer exists.");

            Assert.True(
                ArcanumJsonContext.Default.GetTypeInfo(resolved!) is null,
                $"{entry.TypeName} is declared as a non-wire shape but is registered with ArcanumJsonContext.");

        }

    }

    /// <summary>
    /// Every operator-API shape resolves through the API's own source-generated context, and nothing
    /// else. A payload that reached the reflection resolver would work here and throw under AOT.
    /// </summary>
    [Fact]
    public void Every_operator_api_shape_resolves_through_the_api_context()
    {

        foreach (CovenantPublicContract entry in CovenantPublicContractInventory.Contracts
            .Where(static entry => entry.Surface == CovenantContractSurface.OperatorApi))
        {

            Type type = Resolve(entry.WireTypeName)!;

            JsonTypeInfo? typeInfo = ArcanumJsonContext.Default.GetTypeInfo(type);

            Assert.True(
                typeInfo is not null,
                $"{entry.WireTypeName} is an operator-API shape but is not reachable from ArcanumJsonContext.");

            Assert.NotEmpty(typeInfo!.Properties);

        }

    }

    /// <summary>
    /// Recovery checkpoints belong to their owning Infrastructure context, never to the API context:
    /// a checkpoint that could be serialized by the wire context would be a durable payload whose
    /// shape is governed by a public contract's compatibility rules.
    /// </summary>
    [Fact]
    public void Every_recovery_checkpoint_resolves_through_its_infrastructure_context_and_not_the_wire_context()
    {

        IEnumerable<CovenantPublicContract> checkpoints = CovenantPublicContractInventory.Contracts
            .Where(static entry => entry.Surface == CovenantContractSurface.RecoveryCheckpoint);

        Assert.NotEmpty(checkpoints);

        foreach (CovenantPublicContract entry in checkpoints)
        {

            Type type = Resolve(entry.WireTypeName)!;

            Assert.True(
                CovenantRecoveryJsonContext.Default.GetTypeInfo(type) is not null,
                $"{entry.WireTypeName} is a recovery checkpoint but is not reachable from CovenantRecoveryJsonContext.");

            Assert.True(
                ArcanumJsonContext.Default.GetTypeInfo(type) is null,
                $"{entry.WireTypeName} is a durable checkpoint and must not be a public API payload.");

        }

    }

    /// <summary>
    /// No declared shape carries an untyped member. <c>object</c>, <c>dynamic</c>, and
    /// <c>JsonElement</c> are all ways to smuggle an anonymous payload through a typed contract, and
    /// under AOT the first two have no serializer at all.
    /// </summary>
    [Fact]
    public void No_declared_shape_carries_an_anonymous_payload()
    {

        List<string> offenders = [];

        foreach (CovenantPublicContract entry in CovenantPublicContractInventory.Contracts)
        {

            Type type = Resolve(entry.WireTypeName)!;

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {

                if (property.PropertyType == typeof(object)
                    || property.PropertyType == typeof(JsonElement)
                    || property.PropertyType == typeof(JsonDocument)
                    || property.PropertyType == typeof(JsonNode))
                {

                    offenders.Add($"{entry.WireTypeName}.{property.Name}");

                }

            }

        }

        Assert.Empty(offenders);

    }

    /// <summary>
    /// Every enum on a declared shape rejects numeric wire values. An integer enum on the wire ties
    /// the contract to a declaration order nobody promised to keep.
    /// </summary>
    [Fact]
    public void Every_enum_on_a_declared_shape_is_string_only()
    {

        List<string> offenders = [];

        foreach (Type enumType in DeclaredEnumTypes())
        {

            JsonConverterAttribute? converter = enumType.GetCustomAttribute<JsonConverterAttribute>();

            if (converter?.ConverterType is not { IsGenericType: true } converterType
                || converterType.GetGenericTypeDefinition() != typeof(StringOnlyJsonStringEnumConverter<>))
            {

                offenders.Add(enumType.FullName!);

            }

        }

        Assert.Empty(offenders);

    }

    [Fact]
    public void A_numeric_enum_value_is_actually_rejected_on_the_wire()
    {

        JsonException thrown = Assert.Throws<JsonException>(
            static () => JsonSerializer.Deserialize(
                """{"scope":1,"campaignId":null,"lane":null,"lifecycle":"Any","effectiveForCampaignId":null,"limit":50,"cursor":null}""",
                ArcanumJsonContext.Default.CovenantListRequest));

        Assert.NotNull(thrown);

    }

    /// <summary>
    /// One implementation per port, at most. Two concrete services behind one Covenant port would
    /// mean two answers to the same authenticated question, and which one a caller got would depend
    /// on registration order.
    /// </summary>
    [Fact]
    public void No_port_has_an_alternate_implementation()
    {

        foreach (CovenantServicePort port in CovenantPublicContractInventory.Ports)
        {

            Type portType = Resolve(port.PortTypeName)!;

            Type[] implementations =
            [
                .. ShippingAssemblies
                    .SelectMany(static assembly => assembly.GetTypes())
                    .Where(type => type is { IsClass: true, IsAbstract: false } && portType.IsAssignableFrom(type))
            ];

            Assert.True(
                implementations.Length <= 1,
                $"{port.PortTypeName} has {implementations.Length} implementations: "
                + string.Join(", ", implementations.Select(static type => type.FullName)));

        }

    }

    /// <summary>
    /// Each frozen operation lives on exactly one port. A method name repeated across two Covenant
    /// ports is the shape of an alternate interface growing beside the real one.
    /// </summary>
    [Fact]
    public void No_frozen_operation_appears_on_two_ports()
    {

        List<(string Port, string Signature)> operations = [];

        foreach (CovenantServicePort port in CovenantPublicContractInventory.Ports)
        {

            Type portType = Resolve(port.PortTypeName)!;

            foreach (MethodInfo method in portType.GetMethods())
            {

                operations.Add((
                    port.PortTypeName,
                    $"{method.Name}({string.Join(',', method.GetParameters().Select(static parameter => parameter.ParameterType.Name))})"));

            }

        }

        string[] duplicates =
        [
            .. operations
                .GroupBy(static operation => operation.Signature, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => $"{group.Key} on {string.Join(" and ", group.Select(static entry => entry.Port))}")
        ];

        Assert.Empty(duplicates);

    }

    /// <summary>
    /// Every declared shape names the port that owns it, and that port is itself declared. An entry
    /// pointing at a port nobody kept is an inventory describing a seam that no longer exists.
    /// </summary>
    [Fact]
    public void Every_declared_shape_names_a_declared_port()
    {

        HashSet<string> ports =
        [
            .. CovenantPublicContractInventory.Ports.Select(static port => port.PortTypeName)
        ];

        foreach (CovenantPublicContract entry in CovenantPublicContractInventory.Contracts
            .Where(static entry => entry.Surface != CovenantContractSurface.RecoveryCheckpoint))
        {

            Assert.Contains(entry.OwningPortTypeName, ports);

        }

    }

    private static IEnumerable<Type> DeclaredEnumTypes()
    {

        HashSet<Type> seen = [];

        foreach (CovenantPublicContract entry in CovenantPublicContractInventory.Contracts)
        {

            foreach (PropertyInfo property in Resolve(entry.WireTypeName)!
                .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {

                Type candidate = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

                if (candidate.IsArray)
                {

                    candidate = candidate.GetElementType()!;

                }

                if (candidate.IsEnum && seen.Add(candidate))
                {

                    yield return candidate;

                }

            }

        }

    }

    private static Type? Resolve(string fullName) =>
        ShippingAssemblies.Select(assembly => assembly.GetType(fullName, throwOnError: false)).FirstOrDefault(static type => type is not null);

}

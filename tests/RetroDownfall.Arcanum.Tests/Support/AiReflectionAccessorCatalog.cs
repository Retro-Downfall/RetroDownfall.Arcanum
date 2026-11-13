using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>Resolves runtime accessors only into captured images that the same scan inspects in full.</summary>
internal sealed class AiReflectionAccessorCatalog : IDisposable
{
    private static readonly HashSet<string> RuntimeModifierIdentities = new(StringComparer.Ordinal)
    {
        typeof(object).Assembly.GetName().FullName,
        typeof(AiReflectionAccessorCatalog).Assembly.GetReferencedAssemblies().Single(static assembly => assembly.Name == "System.Runtime").FullName,
    };

    private readonly List<PEReader> _readers = [];

    private readonly Dictionary<(string Assembly, string Type), List<TargetType>> _types = [];

    internal AiReflectionAccessorCatalog(IReadOnlyList<byte[]> images)
    {
        try
        {
            foreach (byte[] image in images)
            {
                PEReader pe = new(new MemoryStream(image, writable: false));

                _readers.Add(pe);

                MetadataReader reader = pe.GetMetadataReader();

                AiReflectionMetadata names = new(reader);

                string assembly = AiReflectionMetadata.AssemblyIdentity(reader);

                foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
                {
                    string type = names.Type(handle).Name;

                    if (!_types.TryGetValue((assembly, type), out List<TargetType>? targets))
                    {
                        targets = [];

                        _types.Add((assembly, type), targets);
                    }

                    targets.Add(new(reader, names, handle));
                }
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            Dispose();

            throw new InvalidDataException("Cannot inspect captured accessor target metadata.", exception);
        }
    }

    internal bool ResolvesInstanceMethod(AiReflectionMetadata names, MethodDefinition accessor)
    {
        if (!names.TryGetInstanceAccessorName(accessor, out string? name))
        {
            return false;
        }

        MethodSignature<AiReflectionType> signature = names.TargetSignature(accessor, []);

        if (signature.GenericParameterCount != 0 || signature.ParameterTypes.Length == 0)
        {
            return false;
        }

        AiReflectionType receiver = signature.ParameterTypes[0];

        string expectedReceiver = receiver.DefinitionName
            + (receiver.TypeArguments.Length == 0 ? "" : "<" + string.Join(", ", receiver.TypeArguments.Select(static type => type.Name)) + ">")
            + (receiver.IsByReference ? "&" : "");

        if (receiver.Name != expectedReceiver
            || !_types.TryGetValue((receiver.AssemblyIdentity, receiver.DefinitionName), out List<TargetType>? types)
            || types.Count != 1)
        {
            return false;
        }

        TargetType target = types[0];

        TypeDefinition definition = target.Reader.GetTypeDefinition(target.Handle);

        bool valueType = !definition.BaseType.IsNil && target.Names.Type(definition.BaseType).Name is "System.ValueType" or "System.Enum";

        if (receiver.IsByReference != valueType || definition.GetGenericParameters().Count != receiver.TypeArguments.Length)
        {
            return false;
        }

        MethodDefinition[] matches = [.. definition.GetMethods().Select(target.Reader.GetMethodDefinition).Where(method =>
            target.Reader.GetString(method.Name) == name
            && (method.Attributes & MethodAttributes.Static) == 0
            && Matches(target.Names.TargetSignature(method, receiver.TypeArguments), signature, method, name!))];

        return matches.Length == 1 && matches[0].RelativeVirtualAddress != 0
            && (matches[0].ImplAttributes & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL;
    }

    private static bool Matches(MethodSignature<AiReflectionType> target, MethodSignature<AiReflectionType> accessor, MethodDefinition method, string name)
    {
        AiReflectionType returnType = target.ReturnType;

        // The JSON generator calls init-only setters with a void accessor. Only the setter's
        // required IsExternalInit modifier is ignored; all other modifiers retain identity.
        if (name.StartsWith("set_", StringComparison.Ordinal) && (method.Attributes & MethodAttributes.SpecialName) != 0
            && returnType is { IsRequiredModifier: true, UnmodifiedType.Name: "System.Void", Modifier.Name: "System.Runtime.CompilerServices.IsExternalInit" }
            && RuntimeModifierIdentities.Contains(returnType.Modifier.AssemblyIdentity))
        {
            returnType = returnType.UnmodifiedType;
        }

        return target.Header.IsInstance && target.Header.CallingConvention == SignatureCallingConvention.Default
            && accessor.Header.CallingConvention == SignatureCallingConvention.Default
            && target.RequiredParameterCount == target.ParameterTypes.Length
            && accessor.RequiredParameterCount == accessor.ParameterTypes.Length
            && target.GenericParameterCount == 0
            && SameType(returnType, accessor.ReturnType)
            && target.ParameterTypes.Length == accessor.ParameterTypes.Length - 1
            && target.ParameterTypes.Zip(accessor.ParameterTypes.Skip(1)).All(static pair => SameType(pair.First, pair.Second));
    }

    private static bool SameType(AiReflectionType left, AiReflectionType right) =>
        !left.Name.StartsWith("delegate*", StringComparison.Ordinal)
        && left.Name == right.Name && left.Assembly == right.Assembly && left.AssemblyIdentity == right.AssemblyIdentity
        && left.IsByReference == right.IsByReference
        && left.TypeArguments.Length == right.TypeArguments.Length
        && left.TypeArguments.Zip(right.TypeArguments).All(static pair => SameType(pair.First, pair.Second))
        && ((left.UnmodifiedType is null && right.UnmodifiedType is null)
            || (left.UnmodifiedType is not null && right.UnmodifiedType is not null && SameType(left.UnmodifiedType, right.UnmodifiedType)))
        && ((left.Modifier is null && right.Modifier is null)
            || (left.Modifier is not null && right.Modifier is not null && left.IsRequiredModifier == right.IsRequiredModifier
                && SameType(left.Modifier, right.Modifier)));

    public void Dispose()
    {
        foreach (PEReader reader in _readers)
        {
            reader.Dispose();
        }
    }

    private sealed record TargetType(MetadataReader Reader, AiReflectionMetadata Names, TypeDefinitionHandle Handle);
}

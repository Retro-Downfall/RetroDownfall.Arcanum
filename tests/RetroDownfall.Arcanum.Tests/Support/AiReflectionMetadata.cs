using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed record AiReflectionType(string Assembly, string Name);

internal sealed record AiReflectionMethod(
    AiReflectionType Owner,
    string Name,
    MethodSignature<AiReflectionType> Signature,
    string Instantiation = "")
{
    internal string Display => $"[{Owner.Assembly}] {Owner.Name}.{Name}`{Signature.GenericParameterCount}{Instantiation}"
        + $"({string.Join(", ", Signature.ParameterTypes.Select(static type => type.Name))}) -> {Signature.ReturnType.Name}";
}

/// <summary>Retains assembly, generic, array, modifier and by-reference identity when decoding metadata signatures.</summary>
internal sealed class AiReflectionMetadata(MetadataReader metadata) : ISignatureTypeProvider<AiReflectionType, object?>
{
    internal bool IsRuntimeFieldAccessor(MethodDefinition method)
    {
        List<CustomAttribute> accessors = [];

        foreach (CustomAttributeHandle handle in method.GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);

            AiReflectionMethod constructor = Method(attribute.Constructor);

            if (constructor.Owner.Name == "System.Runtime.CompilerServices.UnsafeAccessorAttribute"
                && constructor.Owner.Assembly is "System.Runtime" or "System.Private.CoreLib")
            {
                accessors.Add(attribute);
            }
        }

        if (accessors.Count != 1)
        {
            return false;
        }

        BlobReader value = metadata.GetBlobReader(accessors[0].Value);

        // EF's compiled model uses CLR-supplied field accessors. A method accessor instead
        // needs a reviewed target-resolution rule; it must not become an uninspected call proxy.
        return value.ReadUInt16() == 1 && value.ReadInt32() == (int)UnsafeAccessorKind.Field;
    }

    internal bool IsMethod(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MethodDefinition or HandleKind.MethodSpecification => true,
        HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)handle).GetKind() == MemberReferenceKind.Method,
        _ => false,
    };

    internal AiReflectionMethod Method(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodSpecification:
                MethodSpecification specification = metadata.GetMethodSpecification((MethodSpecificationHandle)handle);

                if (specification.Method.Kind is not (HandleKind.MemberReference or HandleKind.MethodDefinition))
                {
                    throw new BadImageFormatException("Invalid generic method definition token.");
                }

                AiReflectionMethod definition = Method(specification.Method);

                ImmutableArray<AiReflectionType> arguments = specification.DecodeSignature(this, genericContext: null);

                if (arguments.Length != definition.Signature.GenericParameterCount)
                {
                    throw new BadImageFormatException("Generic method argument count differs from its signature.");
                }

                return definition with { Instantiation = "<" + string.Join(", ", arguments.Select(static type => type.Name)) + ">" };

            case HandleKind.MethodDefinition:
                MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);

                return new(Type(method.GetDeclaringType()), metadata.GetString(method.Name), method.DecodeSignature(this, genericContext: null));

            case HandleKind.MemberReference:
                MemberReference reference = metadata.GetMemberReference((MemberReferenceHandle)handle);

                AiReflectionType owner = reference.Parent.Kind switch
                {
                    HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification => Type(reference.Parent),
                    HandleKind.MethodDefinition => Method(reference.Parent).Owner,
                    HandleKind.ModuleReference => new(metadata.GetString(metadata.GetAssemblyDefinition().Name),
                        "<Module:" + metadata.GetString(metadata.GetModuleReference((ModuleReferenceHandle)reference.Parent).Name) + ">"),
                    _ => throw new BadImageFormatException("Unsupported method reference parent."),
                };

                return new(owner, metadata.GetString(reference.Name), reference.DecodeMethodSignature(this, genericContext: null));

            default:
                throw new BadImageFormatException("Unsupported method token.");
        }
    }

    internal void ValidateTypeOrFieldToken(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
                _ = Type(handle);
                break;

            case HandleKind.FieldDefinition:
                _ = metadata.GetFieldDefinition((FieldDefinitionHandle)handle).DecodeSignature(this, genericContext: null);
                break;

            case HandleKind.MemberReference:
                _ = metadata.GetMemberReference((MemberReferenceHandle)handle).DecodeFieldSignature(this, genericContext: null);
                break;

            default:
                throw new BadImageFormatException("Invalid ldtoken type/field reference.");
        }
    }

    private AiReflectionType Type(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(metadata, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(metadata, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(metadata, null, (TypeSpecificationHandle)handle, 0),
        _ => throw new BadImageFormatException("Unsupported type token."),
    };

    public AiReflectionType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);

        string name = reader.GetString(type.Name);

        if (type.GetDeclaringType() is { IsNil: false } parentHandle)
        {
            AiReflectionType parent = GetTypeFromDefinition(reader, parentHandle, rawTypeKind);

            return parent with { Name = parent.Name + "+" + name };
        }

        return new(reader.GetString(reader.GetAssemblyDefinition().Name), FullName(reader.GetString(type.Namespace), name));
    }

    public AiReflectionType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        TypeReference type = reader.GetTypeReference(handle);

        string name = reader.GetString(type.Name);

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            AiReflectionType parent = GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind);

            return parent with { Name = parent.Name + "+" + name };
        }

        string assembly = type.ResolutionScope.Kind switch
        {
            HandleKind.AssemblyReference => reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name),
            HandleKind.ModuleDefinition or HandleKind.ModuleReference => reader.GetString(reader.GetAssemblyDefinition().Name),
            _ => throw new BadImageFormatException("Unsupported type resolution scope."),
        };

        return new(assembly, FullName(reader.GetString(type.Namespace), name));
    }

    public AiReflectionType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public AiReflectionType GetArrayType(AiReflectionType elementType, ArrayShape shape) => elementType with
    {
        Name = elementType.Name + "[rank=" + shape.Rank + ";sizes=" + string.Join(",", shape.Sizes)
            + ";lower=" + string.Join(",", shape.LowerBounds) + "]",
    };

    public AiReflectionType GetSZArrayType(AiReflectionType elementType) => elementType with { Name = elementType.Name + "[]" };

    public AiReflectionType GetByReferenceType(AiReflectionType elementType) => elementType with { Name = elementType.Name + "&" };

    public AiReflectionType GetPointerType(AiReflectionType elementType) => elementType with { Name = elementType.Name + "*" };

    public AiReflectionType GetPinnedType(AiReflectionType elementType) => elementType with { Name = elementType.Name + " pinned" };

    public AiReflectionType GetGenericInstantiation(AiReflectionType genericType, ImmutableArray<AiReflectionType> typeArguments) => genericType with
    {
        Name = genericType.Name + "<" + string.Join(", ", typeArguments.Select(static argument => argument.Name)) + ">",
    };

    public AiReflectionType GetGenericMethodParameter(object? genericContext, int index) => new("", "!!" + index);

    public AiReflectionType GetGenericTypeParameter(object? genericContext, int index) => new("", "!" + index);

    public AiReflectionType GetModifiedType(AiReflectionType modifier, AiReflectionType unmodifiedType, bool isRequired) => unmodifiedType with
    {
        Name = unmodifiedType.Name + (isRequired ? " modreq(" : " modopt(") + modifier.Name + ")",
    };

    public AiReflectionType GetFunctionPointerType(MethodSignature<AiReflectionType> signature) => new("",
        "delegate*<" + string.Join(", ", signature.ParameterTypes.Select(static type => type.Name).Append(signature.ReturnType.Name)) + ">");

    public AiReflectionType GetPrimitiveType(PrimitiveTypeCode typeCode) => new("System.Runtime", "System." + typeCode);

    private static string FullName(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
}

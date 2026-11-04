using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed record AiReflectionViolation(string Assembly, string Caller, string Callee, int Offset)
{
    public override string ToString() =>
        $"{Assembly}: {Caller} at IL_{Offset:X4} references {Callee}; violates DESIGN §9.3 AI reflection boundary. Use hand-authored schema and generated JsonTypeInfo.";
}

/// <summary>Inspects compiled references without loading or executing the scanned assembly or its dependencies.</summary>
internal static class AiReflectionBoundaryScanner
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> Instructions = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode)field.GetValue(null)!)
        .Where(static instruction => instruction.OpCodeType != OpCodeType.Nternal)
        .ToDictionary(static instruction => unchecked((ushort)instruction.Value));

    internal static IReadOnlyList<AiReflectionViolation> FindForbiddenReferences(byte[] image)
    {
        string assembly = "<unreadable assembly>";

        string caller = "<metadata>";

        int offset = 0;

        try
        {
            using PEReader pe = new(new MemoryStream(image, writable: false));

            if (!pe.HasMetadata)
            {
                throw new BadImageFormatException("No managed metadata.");
            }

            MetadataReader metadata = pe.GetMetadataReader();

            if (!metadata.IsAssembly)
            {
                throw new BadImageFormatException("A complete managed assembly is required.");
            }

            assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);

            if (metadata.AssemblyFiles.Any(handle => metadata.GetAssemblyFile(handle).ContainsMetadata))
            {
                throw new BadImageFormatException("Multi-module assemblies require an explicit complete module inventory.");
            }

            AiReflectionMetadata names = new(metadata);

            List<AiReflectionViolation> violations = [];

            foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
            {
                MethodDefinition method = metadata.GetMethodDefinition(handle);

                caller = names.Method(handle).Display;

                offset = 0;

                if (method.RelativeVirtualAddress == 0)
                {
                    if ((method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0
                        && (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.Runtime
                        && (method.ImplAttributes & MethodImplAttributes.InternalCall) == 0
                        && !names.IsRuntimeFieldAccessor(method))
                    {
                        throw new BadImageFormatException("Ordinary managed method is missing its IL body.");
                    }

                    continue;
                }

                BlobReader il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();

                while (il.RemainingBytes > 0)
                {
                    offset = il.Offset;

                    ushort code = il.ReadByte();

                    if (code == 0xFE)
                    {
                        code = (ushort)(0xFE00 | il.ReadByte());
                    }

                    if (!Instructions.TryGetValue(code, out OpCode instruction))
                    {
                        throw new BadImageFormatException($"Unknown IL opcode 0x{code:X4}.");
                    }

                    if (instruction.OperandType is OperandType.InlineMethod or OperandType.InlineTok)
                    {
                        EntityHandle target = MetadataTokens.EntityHandle(il.ReadInt32());

                        if (target.IsNil)
                        {
                            throw new BadImageFormatException("Nil member token.");
                        }

                        if (names.IsMethod(target))
                        {
                            AiReflectionMethod called = names.Method(target);

                            if (IsForbidden(called))
                            {
                                violations.Add(new(assembly, caller, called.Display, offset));
                            }
                        }
                        else if (instruction.OperandType == OperandType.InlineTok)
                        {
                            names.ValidateTypeOrFieldToken(target);
                        }
                        else
                        {
                            throw new BadImageFormatException("Method instruction references a non-method token.");
                        }
                    }
                    else
                    {
                        SkipOperand(ref il, instruction.OperandType);
                    }
                }
            }

            return violations;
        }
        catch (Exception exception) when (exception is BadImageFormatException or ArgumentException
            or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException(
                $"Cannot inspect {assembly}: {caller} at IL_{offset:X4}: {exception.Message}", exception);
        }
    }

    private static bool IsForbidden(AiReflectionMethod method)
    {
        if (method.Owner.Assembly == "Microsoft.Extensions.AI.Abstractions")
        {
            return method.Owner.Name switch
            {
                "Microsoft.Extensions.AI.AIFunctionFactory" => method.Name == "Create",
                "Microsoft.Extensions.AI.AIJsonUtilities" => method.Name is "CreateJsonSchema" or "CreateFunctionJsonSchema",
                "Microsoft.Extensions.AI.ChatResponseFormat" => method.Name == "ForJsonSchema"
                    && !(method.Signature.GenericParameterCount == 0
                        && !method.Signature.Header.IsInstance
                        && method.Signature.ReturnType.Name == "Microsoft.Extensions.AI.ChatResponseFormatJson"
                        && method.Signature.ParameterTypes.Select(static type => type.Name).SequenceEqual(
                            ["System.Text.Json.JsonElement", "System.String", "System.String"], StringComparer.Ordinal)
                        && method.Signature.ParameterTypes[0].Assembly == "System.Text.Json"),
                _ => false,
            };
        }

        // Unknown overloads in these families remain forbidden until the independent pinned inventory is reviewed.
        return method.Owner.Assembly == "Microsoft.Extensions.AI"
            && method.Owner.Name == "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions"
            && method.Name == "GetResponseAsync";
    }

    private static void SkipOperand(ref BlobReader il, OperandType operand)
    {
        int length = operand switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineType or OperandType.ShortInlineR => 4,
            OperandType.InlineSwitch => checked(il.ReadInt32() * 4),
            _ => throw new BadImageFormatException($"Unknown operand kind: {operand}."),
        };

        if (length < 0 || length > il.RemainingBytes)
        {
            throw new BadImageFormatException("Truncated or invalid IL operand.");
        }

        il.Offset += length;
    }
}

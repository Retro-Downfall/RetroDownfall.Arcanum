using System.Reflection;
using System.Reflection.Emit;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Reads the method bodies of the loaded test assembly, one opcode at a time: the methods a body calls (generic instantiations resolved), the
/// string literals it loads, the types of its locals and the generic arguments of its calls. A semantic scan of compiled code, shared by the
/// environment isolation contract and by the checker's own source pins, so that no pin reads the bytes of a source file as text.
/// </summary>
internal static class ClrMethodBodyReader
{
    private static readonly Lazy<IReadOnlyDictionary<short, OpCode>> OpCodesByValue = new(LoadOpCodesByValue);

    /// <summary>
    /// Walks a method body one opcode at a time — a naive byte scan for <c>call</c> would resolve
    /// operand bytes as tokens and produce false accusations. Bails out of the whole method on the
    /// first unknown opcode rather than guessing at an operand length.
    /// </summary>
    internal static IEnumerable<MethodBase> CalledMethods(
        MethodBase method)
    {
        Module module = method.Module;

        Type[]? typeArguments = null;

        Type[]? methodArguments = null;

        try
        {
            typeArguments = method.DeclaringType?.IsGenericType == true
                ? method.DeclaringType.GetGenericArguments()
                : null;

            methodArguments = method.IsGenericMethodDefinition
                ? method.GetGenericArguments()
                : null;
        }
        catch (Exception exception) when (exception is NotSupportedException
            or InvalidOperationException
            or TypeLoadException)
        {
            yield break;
        }

        foreach ((OpCode opCode, int operand) in Instructions(method))
        {
            if (opCode.OperandType is not OperandType.InlineMethod)
            {
                continue;
            }

            MethodBase? called = ResolveMethod(module, operand, typeArguments, methodArguments);

            if (called is not null)
            {
                yield return called;
            }
        }
    }

    /// <summary>
    /// Every string literal a method loads. The C# compiler inlines <c>const string</c> values as
    /// <c>ldstr</c>, so a name spelled once as a constant and used in another method still shows up
    /// in the declaring class.
    /// </summary>
    internal static IEnumerable<string> LoadedStrings(
        MethodBase method)
    {
        Module module = method.Module;

        foreach ((OpCode opCode, int operand) in Instructions(method))
        {
            if (opCode.OperandType is not OperandType.InlineString)
            {
                continue;
            }

            string? literal;

            try
            {
                literal = module.ResolveString(operand);
            }
            catch (Exception exception) when (exception is ArgumentException
                or BadImageFormatException)
            {
                continue;
            }

            yield return literal;
        }
    }

    /// <summary>
    /// The opcode stream of a method body, with the 32-bit operand of every token-carrying
    /// instruction. Shared by the call-site and string-literal walks so neither re-implements the
    /// operand-length bookkeeping.
    /// </summary>
    private static IEnumerable<(OpCode OpCode, int Operand)> Instructions(
        MethodBase method)
    {
        byte[]? il;

        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception exception) when (exception is BadImageFormatException
            or NotSupportedException
            or InvalidOperationException
            or TypeLoadException)
        {
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        IReadOnlyDictionary<short, OpCode> lookup = OpCodesByValue.Value;

        int offset = 0;

        while (offset < il.Length)
        {
            short code = il[offset];

            offset++;

            if (code == 0xFE)
            {
                if (offset >= il.Length)
                {
                    yield break;
                }

                code = unchecked((short)(0xFE00 | il[offset]));

                offset++;
            }

            if (!lookup.TryGetValue(code, out OpCode opCode))
            {
                yield break;
            }

            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineString
                && offset + 4 <= il.Length)
            {
                yield return (opCode, BitConverter.ToInt32(il, offset));
            }

            int operandSize = OperandSize(opCode, il, offset);

            if (operandSize < 0)
            {
                yield break;
            }

            offset += operandSize;
        }
    }

    private static int OperandSize(
        OpCode opCode,
        byte[] il,
        int offset) =>
        opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineI
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => offset + 4 <= il.Length
                ? 4 + (4 * BitConverter.ToInt32(il, offset))
                : -1,
            _ => -1,
        };

    private static MethodBase? ResolveMethod(
        Module module,
        int token,
        Type[]? typeArguments,
        Type[]? methodArguments)
    {
        try
        {
            return module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (Exception exception) when (exception is ArgumentException
            or BadImageFormatException
            or MissingMethodException
            or TypeLoadException
            or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The generic arguments a call is made with: those of the method, and those of the type that declares it.
    /// </summary>
    internal static IEnumerable<Type> GenericArgumentsOf(
        MethodBase called)
    {
        Type[] arguments;

        try
        {
            arguments =
            [
                .. called.IsGenericMethod && !called.IsGenericMethodDefinition ? called.GetGenericArguments() : [],
                .. called.DeclaringType is { IsGenericType: true, IsGenericTypeDefinition: false } declaring
                    ? declaring.GetGenericArguments()
                    : [],
            ];
        }
        catch (Exception exception) when (exception is NotSupportedException
            or InvalidOperationException
            or TypeLoadException)
        {
            return [];
        }

        return arguments;
    }

    /// <summary>
    /// The types of the locals of a method body.
    /// </summary>
    internal static IEnumerable<Type> LocalTypes(
        MethodBase method)
    {
        try
        {
            return method.GetMethodBody()?.LocalVariables.Select(static local => local.LocalType).ToArray() ?? [];
        }
        catch (Exception exception) when (exception is BadImageFormatException
            or NotSupportedException
            or InvalidOperationException
            or TypeLoadException)
        {
            return [];
        }
    }

    private static IReadOnlyDictionary<short, OpCode> LoadOpCodesByValue()
    {
        Dictionary<short, OpCode> lookup = [];

        foreach (FieldInfo field in typeof(OpCodes).GetFields(
            BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                lookup[opCode.Value] = opCode;
            }
        }

        return lookup;
    }
}

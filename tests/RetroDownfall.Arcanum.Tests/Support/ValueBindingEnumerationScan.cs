using System.Reflection;

using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The semantic reader of the compiled checker behind the Constant Value Identity guard. A member's value bindings are a dictionary from a
/// symbol to the source that is bound to it, and a source names its caller. A walk that enumerates the dictionary to follow its sources to
/// their callers would read a caller that the state does not name when two callers reach a callee with the same constant, so it has to skip a
/// constant. The scan reads the method bodies of the compiled assembly, so it sees every spelling of an enumeration alike (a foreach, a
/// <c>.Values</c> read, a LINQ call, a copy of the dictionary, a lambda, an alias in a local, the keys copied out and every source then read
/// through a lookup) and reports each method, closures and state machines folded into the method that wrote them, that enumerates the
/// dictionary, with whether it reads a source's caller and whether it calls the guard. A method reads a caller when it does, and when it
/// hands a source, a pair of one or the dictionary itself to a checker method that reads one: the walk then follows the source to its
/// caller in the helper.
/// </summary>
internal static class ValueBindingEnumerationScan
{
    private const string SourceType = "BoundValueSource";

    private const string KeyType = "ISymbol";

    // A point lookup or a mutation of the dictionary reads one entry by its own key (or none), and enumerates nothing; nor does reading the key
    // or the source of an entry a method was handed. The keys alone enumerate nothing either, until a source is read for each of them through a
    // lookup (see Facts.Enumerates).
    private static readonly HashSet<string> NotEnumerations = new(StringComparer.Ordinal)
    {
        "TryGetValue", "ContainsKey", "get_Item", "set_Item", "get_Count", "Add", "TryAdd", "Remove", "get_Keys", "get_Key", "get_Value",
    };

    private sealed class Facts
    {
        internal bool Enumerates { get; set; }

        internal bool ReadsCaller { get; set; }

        internal bool Guarded { get; set; }

        internal bool ReadsKeys { get; set; }

        internal bool ReadsSourcesByKey { get; set; }

        // The checker methods, by logical name, that this method hands something built from a source to.
        internal HashSet<string> Delegates { get; } = new(StringComparer.Ordinal);
    }

    internal sealed record Enumeration(string Method, bool ReadsCaller, bool Guarded);

    internal static IReadOnlyList<Enumeration> Enumerations(Assembly assembly, Func<Type, bool> include, string guard)
    {
        Dictionary<string, Facts> methods = new(StringComparer.Ordinal);

        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (Type type in assembly.GetTypes().Where(include))
        {
            foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                string logical = LogicalMethod(method);

                if (!methods.TryGetValue(logical, out Facts? facts))
                {
                    methods[logical] = facts = new();
                }

                foreach (MethodBase called in ClrMethodBodyReader.CalledMethods(method))
                {
                    if (called.Name == guard)
                    {
                        facts.Guarded = true;
                    }

                    if (called.DeclaringType?.Name == SourceType)
                    {
                        facts.ReadsCaller |= called.Name is "get_Caller" or "Deconstruct";

                        continue;
                    }

                    facts.Enumerates |= Enumerates(called);

                    if (Mentions(called))
                    {
                        facts.ReadsKeys |= called.Name == "get_Keys";

                        facts.ReadsSourcesByKey |= called.Name is "get_Item" or "TryGetValue";
                    }

                    if (called.DeclaringType is { } owner && include(owner) && called.GetParameters().Any(static parameter => HoldsSource(parameter.ParameterType)))
                    {
                        facts.Delegates.Add(LogicalMethod(called));
                    }
                }
            }
        }

        // The keys copied out and every source then read through a lookup is a walk over the sources.
        foreach (Facts facts in methods.Values)
        {
            facts.Enumerates |= facts.ReadsKeys && facts.ReadsSourcesByKey;
        }

        // A method that hands a source to a checker method that reads a caller reads one, to a fixed point through the helpers.
        for (bool grew = true; grew;)
        {
            grew = false;

            foreach (Facts facts in methods.Values.Where(static facts => !facts.ReadsCaller))
            {
                if (facts.Delegates.Any(helper => methods.TryGetValue(helper, out Facts? reached) && reached.ReadsCaller))
                {
                    facts.ReadsCaller = true;

                    grew = true;
                }
            }
        }

        return [.. methods.Where(static pair => pair.Value.Enumerates).Select(static pair => new Enumeration(pair.Key, pair.Value.ReadsCaller, pair.Value.Guarded)).OrderBy(static item => item.Method, StringComparer.Ordinal)];
    }

    private static bool Mentions(MethodBase called) =>
        called.DeclaringType is { } declaring && IsBindings(declaring)
        || ClrMethodBodyReader.GenericArgumentsOf(called).Any(IsBindings);

    private static bool Enumerates(MethodBase called)
    {
        if (!Mentions(called) || NotEnumerations.Contains(called.Name))
        {
            return false;
        }

        // A constructor enumerates only when it copies: it is handed a collection of the bindings.
        return !called.IsConstructor || called.GetParameters().Any(parameter => IsBindings(parameter.ParameterType));
    }

    // A value a helper can follow to a caller: a source, or anything built from the bindings (a pair, a value collection, the dictionary).
    private static bool HoldsSource(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            return HoldsSource(element);
        }

        return type.Name == SourceType || IsBindings(type) || type.IsGenericType && type.GetGenericArguments().Any(HoldsSource);
    }

    // The dictionary of bindings, or anything built from its entries: a pair, a value collection, an enumerable of either.
    private static bool IsBindings(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            return IsBindings(element);
        }

        if (!type.IsGenericType)
        {
            return false;
        }

        Type[] arguments = type.GetGenericArguments();

        return arguments is [{ Name: KeyType }, { Name: SourceType }] || arguments.Any(IsBindings);
    }

    // The method a compiler-generated member belongs to: a lambda, a local function and a state machine are part of the method that wrote them.
    private static string LogicalMethod(MethodBase method)
    {
        Type outer = method.DeclaringType!;

        string name = method.Name;

        Match generated = Regex.Match(name, "^<(.+?)>", RegexOptions.CultureInvariant);

        if (generated.Success)
        {
            name = generated.Groups[1].Value;
        }

        while (outer.DeclaringType is { } parent && (outer.Name.Contains('<', StringComparison.Ordinal) || outer.Name.StartsWith("<>", StringComparison.Ordinal)))
        {
            if (!generated.Success && Regex.Match(outer.Name, "^<(.+?)>", RegexOptions.CultureInvariant) is { Success: true } machine)
            {
                name = machine.Groups[1].Value;
            }

            outer = parent;
        }

        return outer.FullName + "." + name;
    }
}

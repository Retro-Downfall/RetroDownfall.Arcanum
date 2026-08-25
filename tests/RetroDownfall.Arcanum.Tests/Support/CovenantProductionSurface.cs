using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The production types the Covenant enforcement suites are scoped to.
/// </summary>
/// <remarks>
/// Three assemblies and two namespace families rather than the whole repository. The rules these
/// suites enforce are repository-wide truths, but the evidence for them is not: a UX view model or a
/// CLI request record answers to a binder rather than to a caller, and reporting those would bury the
/// tier the rules were written for under offenders nobody can act on.
/// </remarks>
internal static class CovenantProductionSurface
{

    private static readonly Assembly[] Assemblies =
    [
        typeof(CovenantOperationScope).Assembly,

        typeof(CovenantDispatchGate).Assembly,

        typeof(CovenantOperationGate).Assembly,
    ];

    /// <summary>
    /// Every authored type in a <c>Covenant</c> or <c>Intelligence</c> namespace of the Core, Api and
    /// Infrastructure assemblies, plus every type named for the Covenant wherever it lives.
    /// </summary>
    internal static IReadOnlyList<Type> Types()
    {

        List<Type> types = [];

        foreach (Assembly assembly in Assemblies)
        {

            foreach (Type type in assembly.GetTypes())
            {

                // Generated types are excluded because their source is not on disk under src at all:
                // a source generator writes them into obj, so an authored-source scan would find no
                // call site for any of their members and report every one of them.
                if (type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
                    || type.GetCustomAttribute<GeneratedCodeAttribute>() is not null
                    || type.Name.Contains('<', StringComparison.Ordinal))
                {

                    continue;

                }

                string @namespace = type.Namespace ?? string.Empty;

                if (@namespace.Contains(".Covenant", StringComparison.Ordinal)
                    || @namespace.Contains(".Intelligence", StringComparison.Ordinal)
                    || type.Name.StartsWith("Covenant", StringComparison.Ordinal))
                {

                    types.Add(type);

                }

            }

        }

        Assert.NotEmpty(types);

        return types;

    }

    /// <summary>
    /// Every method name declared anywhere in those three assemblies, mapped to the distinct parameter
    /// name sequences declared under it.
    /// </summary>
    /// <remarks>
    /// A name that maps to more than one sequence cannot be attributed to a single declaration by
    /// reading source, because the call site names the method and not the type that declares it. Read
    /// across whole assemblies rather than across a scoped subset: a collision with a type outside the
    /// scope conflates the call sites exactly as badly as one inside it, and only a scan that saw both
    /// would notice.
    /// </remarks>
    internal static IReadOnlyDictionary<string, HashSet<string>> MethodSignaturesByName()
    {

        Dictionary<string, HashSet<string>> signatures = new(StringComparer.Ordinal);

        foreach (Assembly assembly in Assemblies)
        {

            foreach (Type type in assembly.GetTypes())
            {

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {

                    if (!signatures.TryGetValue(method.Name, out HashSet<string>? shapes))
                    {

                        shapes = new HashSet<string>(StringComparer.Ordinal);

                        signatures[method.Name] = shapes;

                    }

                    _ = shapes.Add(string.Join(
                        ',',
                        method.GetParameters().Select(static parameter => parameter.Name)));

                }

            }

        }

        return signatures;

    }

}

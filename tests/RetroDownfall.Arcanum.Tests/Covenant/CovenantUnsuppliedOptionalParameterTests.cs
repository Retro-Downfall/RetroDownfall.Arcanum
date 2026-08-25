using System.Reflection;
using System.Runtime.CompilerServices;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Optional parameters on the Covenant and intelligence surface that no production caller ever
/// supplies.
/// </summary>
/// <remarks>
/// An optional parameter nothing under <c>src</c> passes is a branch only a test can reach. The
/// argument is hand-built by definition — there is no production caller to build it — so the branch it
/// selects is dead in every shipped configuration however green the suite around it is, and the test
/// that exercises it is testing an API surface rather than a journey. Three of the four defects this
/// repository found the hard way lived behind exactly that shape, and each had a passing test with a
/// working revert-proof standing over it.
///
/// <para>The default is usually <c>null</c>, because a nullable seam is the cheapest way to make a
/// type constructible from a test without composing what production composes. But the rule is about
/// supply rather than about nullability: a <c>bool</c> whose non-default arm no caller ever selects is
/// the same dead branch wearing a different type.</para>
/// </remarks>
public sealed class CovenantUnsuppliedOptionalParameterTests
{

    /// <summary>
    /// A member whose optional parameter is at <c>Position</c>, and the parameter's name.
    /// </summary>
    private sealed record OptionalParameter(MethodInfo Member, int Position, string Name)
    {

        internal string Describe() =>
            $"{Member.DeclaringType?.Name}.{Member.Name}(… {Name} …)";

    }

    /// <summary>
    /// The enforcement. An optional parameter reaches this list only if every authored call site under
    /// <c>src</c> stops short of it and none names it.
    /// </summary>
    [Fact]
    public void Every_optional_parameter_on_the_covenant_surface_is_supplied_by_some_production_caller()
    {

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        List<string> offenders = [];

        foreach (OptionalParameter parameter in AttributableOptionalParameters())
        {

            IReadOnlyList<CSharpCallSite> callSites =
                CSharpCallSiteReader.Find(sources, parameter.Member.Name);

            bool supplied = callSites.Any(site =>
                site.ArgumentCount > parameter.Position
                || site.NamedArguments.Contains(parameter.Name, StringComparer.Ordinal));

            if (!supplied)
            {

                offenders.Add(
                    $"{parameter.Describe()} — {callSites.Count} production call site(s), none of "
                    + "which passes it");

            }

        }

        Assert.True(
            offenders.Count == 0,
            "An optional parameter on the Covenant or intelligence surface is never supplied by any "
            + "caller under src. Whatever branch it selects is unreachable in every shipped "
            + "configuration, so a test that passes it is exercising a shape production cannot "
            + "produce — which is how a green suite came to stand over a feature that could not "
            + "bootstrap. Delete the parameter, or give it the production caller it was added for:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// The reader the rule above rests on, read against source it can be checked against by eye.
    /// </summary>
    /// <remarks>
    /// Without this, a reader that silently found nothing would report every optional parameter in the
    /// repository as an offender, and a reader that silently found everything would report none — and
    /// the second failure is invisible, because a rule that never fires looks exactly like a rule
    /// nothing violates. The declaration, the omitting call, the positional call and the named call
    /// are all present below so that both directions are pinned at once.
    /// </remarks>
    [Fact]
    public void The_call_site_reader_separates_a_supplied_optional_argument_from_an_omitted_one()
    {

        ProductionSource[] sources =
        [
            new ProductionSource(
                "synthetic/Declaration.cs",
                "internal static Result Admit(Scope scope, Receipt? receipt = null) => default;\n"),

            new ProductionSource(
                "synthetic/Omits.cs",
                "var a = Admit(scope);\nreturn Admit(Build<string, int>(one, two));\n"),

            new ProductionSource(
                "synthetic/Supplies.cs",
                "var b = gate.Admit(scope, minted);\n"),

            new ProductionSource(
                "synthetic/Names.cs",
                "var c = gate.Admit(scope, receipt: minted);\nstring m = \"Admit(a, b, c)\";\n"),
        ];

        IReadOnlyList<CSharpCallSite> sites = CSharpCallSiteReader.Find(sources, "Admit");

        // Four calls, not five: the declaration is a declaration and the quoted one is a message.
        Assert.Equal(4, sites.Count);

        Assert.DoesNotContain(sites, static site => site.RelativePath.EndsWith(
            "Declaration.cs",
            StringComparison.Ordinal));

        Assert.All(
            sites.Where(static site => site.RelativePath.EndsWith("Omits.cs", StringComparison.Ordinal)),
            static site => Assert.Equal(1, site.ArgumentCount));

        Assert.Equal(
            2,
            Assert.Single(
                sites,
                static site => site.RelativePath.EndsWith("Supplies.cs", StringComparison.Ordinal))
                .ArgumentCount);

        Assert.Contains(
            "receipt",
            Assert.Single(
                sites,
                static site => site.RelativePath.EndsWith("Names.cs", StringComparison.Ordinal))
                .NamedArguments);

    }

    /// <summary>
    /// The optional parameters whose member name a source scan can attribute to one declaration.
    /// </summary>
    /// <remarks>
    /// Three exclusions, each a limit of reading source rather than a judgement that the excluded shape
    /// is safe.
    ///
    /// <para>Constructors are out. Target-typed <c>new(…)</c> is this repository's house form, and it
    /// names no type at the call site at all, so a constructor's callers cannot be found by name. A
    /// scan that fell back to <c>new TypeName(</c> would miss most of them and report parameters as
    /// unsupplied that are supplied on every turn.</para>
    ///
    /// <para>Overloads are out, in the same type or across the surface: two declarations sharing a name
    /// but not a parameter list cannot be told apart by counting arguments, and guessing would resolve
    /// in the direction that hides offenders. Declarations that share both — an interface method and
    /// its implementations — are one contract and are kept.</para>
    ///
    /// <para><c>CancellationToken</c> parameters are out. Defaulting one is the language's own idiom
    /// for a token an awaiting caller may not have, and a parameter nobody passes there is a
    /// convenience rather than a branch.</para>
    /// </remarks>
    private static IReadOnlyList<OptionalParameter> AttributableOptionalParameters()
    {

        // Ambiguity is read across every type of the three assemblies rather than across the scoped
        // surface alone, because a colliding declaration outside the scope conflates the call sites
        // just as thoroughly as one inside it — and a scan that only looked at the scope would count
        // some other type's callers as this one's.
        IReadOnlyDictionary<string, HashSet<string>> signatures =
            CovenantProductionSurface.MethodSignaturesByName();

        List<MethodInfo> declared = [];

        foreach (Type type in CovenantProductionSurface.Types())
        {

            if (type.IsEnum || typeof(Delegate).IsAssignableFrom(type))
            {

                continue;

            }

            declared.AddRange(type
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName
                    && !method.Name.Contains('<', StringComparison.Ordinal)
                    && method.GetCustomAttribute<CompilerGeneratedAttribute>() is null
                    && method.GetParameters().Any(static parameter =>
                        parameter.IsOptional && parameter.HasDefaultValue)));

        }

        List<OptionalParameter> attributable = [];

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (MethodInfo method in declared)
        {

            if (signatures[method.Name].Count > 1)
            {

                continue;

            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {

                if (!parameter.IsOptional
                    || !parameter.HasDefaultValue
                    || parameter.ParameterType == typeof(CancellationToken))
                {

                    continue;

                }

                if (seen.Add($"{method.Name}.{parameter.Name}"))
                {

                    attributable.Add(new OptionalParameter(
                        method,
                        parameter.Position,
                        parameter.Name!));

                }

            }

        }

        Assert.NotEmpty(attributable);

        return attributable;

    }

}

using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Collections;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The Single Gate Census, production half. The hosted-producer inventory grants a Work origin to an acquisition through the gate
/// contract, and trusts it only while the analysed source declares no implementation of the contract beside the reviewed gate. These
/// pins hold the facts that trust rests on in the production source itself: the reviewed gate is the one implementation of the
/// contract, the container resolves the contract to it and to nothing else, nothing else constructs it, and the work lease and the
/// external-effect group have no implementation outside the gate's own nested types, so a lease that exists was minted by the gate.
/// </summary>
/// <remarks>
/// Each pin is a function over any set of compilations, and each is run on a synthetic source that breaks it in every way it names:
/// a pin that found nothing wrong in a source built to be wrong would pass for ever.
/// </remarks>
[Collection(HostedProducerAnalysisCollection.Name)]
[Trait("Category", "HostedProducerAnalysis")]
public sealed class SingleGateCensusTests(ITestOutputHelper output) : IClassFixture<SemanticModelScope>
{
    private const string ProductionData = "RetroDownfall.Arcanum.Infrastructure.Data";

    private const string ReviewedSource = "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs";

    private static IReadOnlyList<Compilation> Production() => [.. HostedGrimoireProducerInventory.ProductionCompilations];

    [Fact]
    public void The_production_source_declares_exactly_the_reviewed_gate_and_nothing_else_that_implements_the_contract()
    {
        IReadOnlyList<Compilation> production = Production();

        // The inventory's own census, the one every discovery consults, finds nothing.
        Assert.Empty(HostedGrimoireProducerInventory.FindUnreviewedGateImplementations([.. HostedGrimoireProducerInventory.ProductionCompilations]));

        Assert.Empty(Pins.GateFindings(production, ProductionData, ReviewedSource));

        Assert.Equal([ProductionData + ".GrimoireConnectionAdmissionGate"], Pins.Implementations(production, ProductionData + ".IGrimoireConnectionAdmissionGate"));
    }

    [Fact]
    public void The_work_lease_and_the_external_effect_group_are_implemented_only_by_the_gates_own_nested_types()
    {
        IReadOnlyList<Compilation> production = Production();

        Assert.Empty(Pins.LeaseFindings(production, ProductionData));

        Assert.Equal([ProductionData + ".GrimoireConnectionAdmissionGate.WorkLease"], Pins.Implementations(production, ProductionData + ".IGrimoireWorkLease"));

        Assert.Equal([ProductionData + ".GrimoireConnectionAdmissionGate.ExternalEffectGroup"], Pins.Implementations(production, ProductionData + ".IGrimoireExternalEffectGroup"));
    }

    [Fact]
    public void The_container_resolves_the_gate_contract_to_the_reviewed_gate_and_only_the_container_constructs_it()
    {
        IReadOnlyList<Compilation> production = Production();

        string[] findings = Pins.RegistrationFindings(production, ProductionData);

        foreach (string finding in findings)
        {
            output.WriteLine("GATE_REGISTRATION_FINDING " + finding);
        }

        Assert.Empty(findings);

        // Not vacuous: the two registrations exist, one creating the reviewed gate and one resolving it by the contract.
        Assert.Equal(
            [
                ProductionData + ".GrimoireConnectionAdmissionGate: creates-reviewed-gate",
                ProductionData + ".IGrimoireConnectionAdmissionGate: resolves-reviewed-gate",
            ],
            Pins.Registrations(production, ProductionData).Select(static registration => registration.Service + ": " + registration.Shape).Order(StringComparer.Ordinal));
    }

    // The clean source: the reviewed gate with its nested lease and group, registered the two ways the container registers it, and a
    // consumer that resolves it for some other service.
    private const string Clean = """
        #nullable enable
        using System;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.DependencyInjection.Extensions;

        namespace Fixture.Data
        {
            public interface IGrimoireWorkLease : IAsyncDisposable
            {
            }

            public interface IGrimoireExternalEffectGroup : IAsyncDisposable
            {
            }

            public interface IGrimoireConnectionAdmissionGate
            {
                bool TryAcquireWorkLease(out IGrimoireWorkLease? lease);
            }

            public sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
            {
                public bool TryAcquireWorkLease(out IGrimoireWorkLease? lease)
                {
                    lease = new WorkLease();

                    return true;
                }

                private sealed class WorkLease : IGrimoireWorkLease
                {
                    public ValueTask DisposeAsync() => default;
                }

                private sealed class ExternalEffectGroup : IGrimoireExternalEffectGroup
                {
                    public ValueTask DisposeAsync() => default;
                }
            }
        }

        namespace Fixture.Composition
        {
            using Fixture.Data;

            public static class Wiring
            {
                public static void Reviewed(IServiceCollection services)
                {
                    services.AddSingleton(static sp => new GrimoireConnectionAdmissionGate());

                    services.AddSingleton<IGrimoireConnectionAdmissionGate>(static sp => sp.GetRequiredService<GrimoireConnectionAdmissionGate>());
                }

                // Consumption is not registration: it resolves the reviewed gate for some other service.
                public static void Consumer(IServiceCollection services) => services.AddSingleton<IDisposable>(static sp => sp.GetRequiredService<GrimoireConnectionAdmissionGate>() is { } ? new Disposable() : new Disposable());
            }

            public sealed class Disposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
        """;

    // Everything the pins name, added to the clean source: a second gate, a lease and a group that are not the gate's own nested types, a
    // lookalike gate in another namespace, every other way of registering a gate, and a construction outside the registration.
    private const string Extras = """

        namespace Fixture.Data
        {
            public sealed class SecondGate : IGrimoireConnectionAdmissionGate
            {
                public bool TryAcquireWorkLease(out IGrimoireWorkLease? lease)
                {
                    lease = new OutsideLease();

                    return true;
                }
            }

            public sealed class OutsideLease : IGrimoireWorkLease
            {
                public ValueTask DisposeAsync() => default;
            }

            public sealed class OutsideGroup : IGrimoireExternalEffectGroup
            {
                public ValueTask DisposeAsync() => default;
            }
        }

        namespace Fixture.Other
        {
            public sealed class GrimoireConnectionAdmissionGate : Fixture.Data.IGrimoireConnectionAdmissionGate
            {
                public bool TryAcquireWorkLease(out Fixture.Data.IGrimoireWorkLease? lease)
                {
                    lease = null;

                    return false;
                }
            }
        }

        namespace Fixture.Composition
        {
            using Fixture.Data;

            public static class Broken
            {
                public static void SecondByGenerics(IServiceCollection services) => services.AddSingleton<IGrimoireConnectionAdmissionGate, SecondGate>();

                public static void SecondByInstance(IServiceCollection services) => services.AddSingleton<IGrimoireConnectionAdmissionGate>(new SecondGate());

                public static void SecondByTypes(IServiceCollection services) => services.AddSingleton(typeof(IGrimoireConnectionAdmissionGate), typeof(SecondGate));

                public static void SecondByFactory(IServiceCollection services) => services.AddSingleton<IGrimoireConnectionAdmissionGate>(static sp => new SecondGate());

                public static void SecondByDescriptor(IServiceCollection services) => services.Replace(ServiceDescriptor.Singleton<IGrimoireConnectionAdmissionGate>(static sp => new SecondGate()));

                public static void SecondByTryAdd(IServiceCollection services) => services.TryAddSingleton<IGrimoireConnectionAdmissionGate>(static sp => new SecondGate());

                public static void ReviewedAgain(IServiceCollection services) => services.AddSingleton<GrimoireConnectionAdmissionGate>(static sp => new GrimoireConnectionAdmissionGate());

                // A construction outside the registration.
                public static GrimoireConnectionAdmissionGate Make() => new();
            }
        }
        """;

    private static IReadOnlyList<Compilation> SyntheticCompilation(string source)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SyntheticGate",
            [CSharpSyntaxTree.ParseText(source, path: "src/Fixture.cs")],
            TrustedPlatformReferences.Shared,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return [compilation];
    }

    [Fact]
    public void The_pins_find_nothing_wrong_in_the_clean_source()
    {
        IReadOnlyList<Compilation> clean = SyntheticCompilation(Clean);

        Assert.Empty(Pins.GateFindings(clean, "Fixture.Data", "src/Fixture.cs"));

        Assert.Empty(Pins.LeaseFindings(clean, "Fixture.Data"));

        Assert.Empty(Pins.RegistrationFindings(clean, "Fixture.Data"));

        Assert.Equal(
            ["Fixture.Data.GrimoireConnectionAdmissionGate: creates-reviewed-gate", "Fixture.Data.IGrimoireConnectionAdmissionGate: resolves-reviewed-gate"],
            Pins.Registrations(clean, "Fixture.Data").Select(static registration => registration.Service + ": " + registration.Shape).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_gate_pin_finds_every_other_implementation_wherever_it_is_declared()
    {
        IReadOnlyList<Compilation> broken = SyntheticCompilation(Clean + Extras);

        string[] findings = Pins.GateFindings(broken, "Fixture.Data", "src/Fixture.cs");

        // The second gate is found, and the lookalike in another namespace is not the reviewed gate.
        Assert.Contains(findings, static finding => finding.Contains("Fixture.Data.SecondGate", StringComparison.Ordinal));

        Assert.Contains(findings, static finding => finding.Contains("Fixture.Other.GrimoireConnectionAdmissionGate", StringComparison.Ordinal));

        Assert.DoesNotContain(findings, static finding => finding.StartsWith("'Fixture.Data.GrimoireConnectionAdmissionGate'", StringComparison.Ordinal));

        // A reviewed gate that is declared elsewhere than the reviewed file, or is not sealed, is a finding too.
        Assert.Contains(Pins.GateFindings(SyntheticCompilation(Clean), "Fixture.Data", "src/Elsewhere.cs"), static finding => finding.Contains("not in 'src/Elsewhere.cs' alone", StringComparison.Ordinal));

        Assert.Contains(
            Pins.GateFindings(SyntheticCompilation(Clean.Replace("public sealed class GrimoireConnectionAdmissionGate", "public class GrimoireConnectionAdmissionGate", StringComparison.Ordinal)), "Fixture.Data", "src/Fixture.cs"),
            static finding => finding.Contains("is not sealed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_lease_pin_finds_a_lease_or_a_group_implemented_outside_the_gate()
    {
        string[] findings = Pins.LeaseFindings(SyntheticCompilation(Clean + Extras), "Fixture.Data");

        Assert.Contains(findings, static finding => finding.Contains("Fixture.Data.OutsideLease", StringComparison.Ordinal));

        Assert.Contains(findings, static finding => finding.Contains("Fixture.Data.OutsideGroup", StringComparison.Ordinal));

        // The gate's own nested lease and group are not findings.
        Assert.DoesNotContain(findings, static finding => finding.Contains("GrimoireConnectionAdmissionGate.WorkLease", StringComparison.Ordinal));

        Assert.DoesNotContain(findings, static finding => finding.Contains("GrimoireConnectionAdmissionGate.ExternalEffectGroup", StringComparison.Ordinal));
    }

    [Fact]
    public void The_registration_pin_finds_every_way_of_registering_a_gate_and_every_construction_outside_the_registration()
    {
        string[] findings = Pins.RegistrationFindings(SyntheticCompilation(Clean + Extras), "Fixture.Data");

        foreach (string method in new[] { "SecondByGenerics", "SecondByInstance", "SecondByTypes", "SecondByFactory", "SecondByDescriptor", "SecondByTryAdd" })
        {
            Assert.Contains(findings, finding => finding.Contains("Fixture.Composition.Broken." + method, StringComparison.Ordinal));
        }

        // A second registration of the reviewed gate is a finding, whatever its shape.
        Assert.Contains(findings, static finding => finding.Contains("'Fixture.Data.GrimoireConnectionAdmissionGate' is registered 2 times", StringComparison.Ordinal));

        // A construction outside the registration is a finding; the registration's own construction is not.
        Assert.Contains(findings, static finding => finding.Contains("constructed outside the container registration", StringComparison.Ordinal)
            && finding.Contains("Fixture.Composition.Broken.Make", StringComparison.Ordinal));

        // A resolution for another service is consumption, not registration.
        Assert.DoesNotContain(findings, static finding => finding.Contains("Fixture.Composition.Wiring.Consumer", StringComparison.Ordinal));
    }

    /// <summary>The functions behind each pin, over any compilations.</summary>
    internal static class Pins
    {
        /// <summary>The types that implement a contract, nested ones included, qualified and ordered.</summary>
        internal static string[] Implementations(IReadOnlyList<Compilation> compilations, string contract) =>
            AdmittedEntryCensusTests.Census.Implementations(compilations, contract);

        /// <summary>
        /// Every way the source breaks the single-gate fact: an implementation of the contract that is not the reviewed gate, a reviewed
        /// gate that is not sealed or not declared in the one reviewed file.
        /// </summary>
        internal static string[] GateFindings(IReadOnlyList<Compilation> compilations, string data, string reviewedSource)
        {
            string reviewed = data + ".GrimoireConnectionAdmissionGate";

            List<string> findings = [];

            foreach (string implementation in Implementations(compilations, data + ".IGrimoireConnectionAdmissionGate"))
            {
                if (implementation != reviewed)
                {
                    findings.Add($"'{implementation}' implements the admission gate and is not the reviewed gate.");
                }
            }

            INamedTypeSymbol? gate = compilations.Select(compilation => compilation.GetTypeByMetadataName(reviewed)).FirstOrDefault(static type => type is { DeclaringSyntaxReferences.IsEmpty: false });

            if (gate is null)
            {
                findings.Add($"The reviewed gate '{reviewed}' is not declared.");

                return [.. findings];
            }

            if (!gate.IsSealed)
            {
                findings.Add($"The reviewed gate '{reviewed}' is not sealed.");
            }

            string[] paths = [.. gate.DeclaringSyntaxReferences.Select(static reference => reference.SyntaxTree.FilePath).Distinct(StringComparer.Ordinal)];

            if (paths is not [string path] || !path.Replace('\\', '/').EndsWith(reviewedSource, StringComparison.Ordinal))
            {
                findings.Add($"The reviewed gate '{reviewed}' is declared in [{string.Join(", ", paths)}], not in '{reviewedSource}' alone.");
            }

            return [.. findings];
        }

        /// <summary>A lease or an effect group implemented by a type that is not a type nested in the reviewed gate.</summary>
        internal static string[] LeaseFindings(IReadOnlyList<Compilation> compilations, string data)
        {
            string reviewed = data + ".GrimoireConnectionAdmissionGate";

            List<string> findings = [];

            foreach (string contract in new[] { data + ".IGrimoireWorkLease", data + ".IGrimoireExternalEffectGroup" })
            {
                foreach (string implementation in Implementations(compilations, contract))
                {
                    if (!implementation.StartsWith(reviewed + ".", StringComparison.Ordinal))
                    {
                        findings.Add($"'{implementation}' implements '{contract}' outside the types nested in the reviewed gate.");
                    }
                }
            }

            return [.. findings];
        }

        /// <summary>A container registration that names a gate type: its service type and the shape of how it is supplied.</summary>
        internal sealed record Registration(string Enclosing, string Service, string Shape);

        /// <summary>
        /// Every call to a container registration API, or construction of a service descriptor, that names the contract or the reviewed gate as a
        /// generic argument or a typeof operand. Resolving a service from a provider is consumption and is never a registration.
        /// </summary>
        internal static IReadOnlyList<Registration> Registrations(IReadOnlyList<Compilation> compilations, string data)
        {
            string contract = data + ".IGrimoireConnectionAdmissionGate";

            string reviewed = data + ".GrimoireConnectionAdmissionGate";

            return
            [
                .. ContainerRegistrationScanner.Find(compilations, type => AuthorityCarrierScanner.Qualified(type) == contract || AuthorityCarrierScanner.Qualified(type) == reviewed)
                    .Select(registration => new Registration(registration.Enclosing, AuthorityCarrierScanner.Qualified(registration.Watched), FactoryShape(registration.Model, registration.Node, reviewed))),
            ];
        }

        /// <summary>
        /// The registrations are exactly two, the reviewed gate created by a factory and the contract resolved to the reviewed gate; and the
        /// reviewed gate is constructed nowhere but inside the registration that creates it.
        /// </summary>
        internal static string[] RegistrationFindings(IReadOnlyList<Compilation> compilations, string data)
        {
            string contract = data + ".IGrimoireConnectionAdmissionGate";

            string reviewed = data + ".GrimoireConnectionAdmissionGate";

            List<string> findings = [];

            IReadOnlyList<Registration> registrations = Registrations(compilations, data);

            foreach (Registration registration in registrations)
            {
                bool expected = registration.Service == reviewed && registration.Shape == "creates-reviewed-gate"
                    || registration.Service == contract && registration.Shape == "resolves-reviewed-gate";

                if (!expected)
                {
                    findings.Add($"{registration.Enclosing} registers '{registration.Service}' as '{registration.Shape}'.");
                }
            }

            foreach (string service in new[] { reviewed, contract })
            {
                int count = registrations.Count(registration => registration.Service == service);

                if (count != 1)
                {
                    findings.Add($"'{service}' is registered {count} times, not once.");
                }
            }

            string[] creators = [.. registrations.Where(registration => registration.Service == reviewed && registration.Shape == "creates-reviewed-gate").Select(static registration => registration.Enclosing)];

            foreach (Compilation compilation in compilations)
            {
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    SemanticModel model = compilation.GetSemanticModel(tree);

                    foreach (SyntaxNode creation in tree.GetRoot().DescendantNodes().Where(static node => node is BaseObjectCreationExpressionSyntax))
                    {
                        if (model.GetOperation(creation) is not IObjectCreationOperation { Type: { } created }
                            || AuthorityCarrierScanner.Qualified(created) != reviewed)
                        {
                            continue;
                        }

                        string enclosing = AuthorityCarrierScanner.Qualified(ContainerRegistrationScanner.EnclosingMember(model.GetEnclosingSymbol(creation.SpanStart)));

                        if (!creators.Contains(enclosing, StringComparer.Ordinal))
                        {
                            findings.Add($"The reviewed gate is constructed outside the container registration, in {enclosing}.");
                        }
                    }
                }
            }

            return [.. findings.Order(StringComparer.Ordinal)];
        }

        // How the registration supplies the service: a factory that creates the reviewed gate, a factory that resolves it from the provider by
        // its own type, or anything else (a generic implementation type, an instance, a typeof pair, a descriptor).
        private static string FactoryShape(SemanticModel model, SyntaxNode registration, string reviewed)
        {
            IEnumerable<ArgumentSyntax> arguments = registration is InvocationExpressionSyntax invocation
                ? invocation.ArgumentList.Arguments
                : registration is BaseObjectCreationExpressionSyntax { ArgumentList: { } list } ? list.Arguments : [];

            foreach (ArgumentSyntax argument in arguments)
            {
                if (argument.Expression is not AnonymousFunctionExpressionSyntax lambda
                    || model.GetOperation(lambda) is not IAnonymousFunctionOperation function)
                {
                    continue;
                }

                IReturnOperation[] returns = [.. function.Body.Descendants().OfType<IReturnOperation>()];

                if (returns is not [{ ReturnedValue: { } returned }])
                {
                    continue;
                }

                while (returned is IConversionOperation conversion)
                {
                    returned = conversion.Operand;
                }

                if (returned is IObjectCreationOperation { Type: { } created } && AuthorityCarrierScanner.Qualified(created) == reviewed)
                {
                    return "creates-reviewed-gate";
                }

                if (returned is IInvocationOperation
                    {
                        TargetMethod: { Name: "GetRequiredService", TypeArguments: [{ } resolved] },
                        Arguments: [{ Value: IParameterReferenceOperation parameter }],
                    }
                    && AuthorityCarrierScanner.Qualified(resolved) == reviewed
                    && function.Symbol.Parameters is [{ } supplied]
                    && SymbolEqualityComparer.Default.Equals(parameter.Parameter, supplied))
                {
                    return "resolves-reviewed-gate";
                }
            }

            return "other";
        }
    }
}

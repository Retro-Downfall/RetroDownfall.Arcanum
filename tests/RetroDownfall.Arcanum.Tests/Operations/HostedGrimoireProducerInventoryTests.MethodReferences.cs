using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // The one matcher for "who calls this method", used by the Admitted Entry Census and by the Lease-Handoff chain pin. These rows hold the
    // union of what each of them matched before they shared it: an alias interface, an explicit implementation, a nested implementation, a call
    // inside a lambda and a local function, a method group, the concrete type, and a name that is not a reference (nameof, another method
    // of the same name that is related to nothing).
    private const string MethodReferenceSource = """
        using System;
        using System.Threading.Tasks;

        namespace Fixture;

        public interface IDoor { Task GoAsync(); }

        public interface IAlias { Task GoAsync(); }

        public interface IOther { Task GoAsync(); }

        public class Door : IDoor, IAlias { public virtual Task GoAsync() => Task.CompletedTask; }

        public class Derived : Door { public override Task GoAsync() => Task.CompletedTask; }

        public sealed class Explicit : IDoor { Task IDoor.GoAsync() => Task.CompletedTask; }

        public sealed class Outer { private sealed class Hidden : IDoor { public Task GoAsync() => Task.CompletedTask; } }

        public sealed class Unrelated : IOther { public Task GoAsync() => Task.CompletedTask; public Task Self() => GoAsync(); }

        public sealed class Callers(IDoor door, IAlias alias, IOther other, Door concrete, Derived derived, Explicit explicitly)
        {
            public Task ViaDoor() => door.GoAsync();

            public Task ViaAlias() => alias.GoAsync();

            public Task ViaConcrete() => concrete.GoAsync();

            public Task ViaDerived() => derived.GoAsync();

            public Task ViaExplicit() => ((IDoor)explicitly).GoAsync();

            public Task ViaLambda() => Task.Run(() => door.GoAsync());

            public Task ViaLocalFunction() { Task Inner() => door.GoAsync(); return Inner(); }

            public Func<Task> ViaGroup() => door.GoAsync;

            public Task ViaOther() => other.GoAsync();

            public string Named() => nameof(IDoor.GoAsync);
        }
        """;

    private static CSharpCompilation MethodReferenceCompilation()
    {
        CSharpCompilation compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(MethodReferenceSource)],
            LeaseHandoffFrameworkReferences(), new(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static string MethodReferenceMember(HostedGrimoireProducerInventory.MethodReference reference) =>
        reference.Name.Ancestors().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().First().Identifier.ValueText
        + (reference.Invoked ? "" : " (reference)");

    [Theory]
    [InlineData("Fixture.IDoor")]
    [InlineData("Fixture.IAlias")]
    [InlineData("Fixture.Door")]
    [InlineData("Fixture.Derived")]
    public void TheSharedMatcherFollowsTheSlotTheAliasTheImplementationsTheOverridesTheLambdasAndTheMethodGroups(string link)
    {
        string[] members = [.. HostedGrimoireProducerInventory.MethodReferencesTo([MethodReferenceCompilation()], link, "GoAsync")
            .Select(MethodReferenceMember).Order(StringComparer.Ordinal)];

        // Every route to the door, once each: a call through the slot, through the alias interface the same implementation satisfies, through the
        // concrete type, through an override, through an explicit implementation's slot, inside a lambda and a local function, and a method group
        // (reported as a reference, not as a call). The other port's method of the same name, its own call, and the nameof are not the door.
        Assert.Equal(
            ["ViaAlias", "ViaConcrete", "ViaDerived", "ViaDoor", "ViaExplicit", "ViaGroup (reference)", "ViaLambda", "ViaLocalFunction"],
            members);
    }

    [Fact]
    public void TheSharedMatcherDoesNotTreatAnotherPortOfTheSameNameAsTheDoorAndRefusesAPinOnADoorThatIsNotDeclared()
    {
        CSharpCompilation compilation = MethodReferenceCompilation();

        string[] other = [.. HostedGrimoireProducerInventory.MethodReferencesTo([compilation], "Fixture.IOther", "GoAsync").Select(MethodReferenceMember).Order(StringComparer.Ordinal)];

        Assert.Equal(["Self", "ViaOther"], other);

        // A pin on a door that is not declared would match nothing and pass for ever, so it is refused.
        Assert.Throws<InvalidOperationException>(() => HostedGrimoireProducerInventory.MethodReferencesTo([compilation], "Fixture.IDoor", "NoSuchMethodAsync"));

        Assert.Throws<InvalidOperationException>(() => HostedGrimoireProducerInventory.MethodReferencesTo([compilation], "Fixture.NoSuchPort", "GoAsync"));
    }

    // The doors a class runs through its base class. Implementing an interface by a method the class inherits (the base declares the method and
    // does not implement the interface), and closing a generic interface in the implementer's own base list, are the two ways a method reaches a
    // slot without being declared by the type that names the slot. A call through the slot, through the base-typed receiver, through the derived
    // type and through the closed concrete type are each a call of the door; the old Admitted Entry Census matched all of them.
    private const string InheritedImplementationSource = """
        using System;
        using System.Threading.Tasks;

        namespace Fixture;

        public interface IDoor { Task GoAsync(); }

        public class Base { public virtual Task GoAsync() => Task.CompletedTask; }

        public class Inherited : Base, IDoor { }

        public sealed class Overriding : Inherited { public override Task GoAsync() => Task.CompletedTask; }

        public interface IGen<T> { Task GoAsync(T value); }

        public sealed class Closed : IGen<int> { public Task GoAsync(int value) => Task.CompletedTask; }

        public sealed class Open<T> : IGen<T> { public Task GoAsync(T value) => Task.CompletedTask; }

        public class GenBase<T> { public Task GoAsync(T value) => Task.CompletedTask; }

        public sealed class ClosedViaBase : GenBase<string>, IGen<string> { }

        public sealed class Unrelated { public Task GoAsync() => Task.CompletedTask; }

        public sealed class Callers(IDoor door, Base baseTyped, Inherited inherited, Overriding overriding, IGen<int> gen, Closed closed, Open<long> open, ClosedViaBase viaBase, GenBase<string> genBase, Unrelated unrelated)
        {
            public Task ViaDoor() => door.GoAsync();

            public Task ViaBaseTyped() => baseTyped.GoAsync();

            public Task ViaInherited() => inherited.GoAsync();

            public Task ViaOverriding() => overriding.GoAsync();

            public Task ViaGen() => gen.GoAsync(1);

            public Task ViaClosed() => closed.GoAsync(1);

            public Task ViaOpen() => open.GoAsync(1);

            public Task ViaClosedViaBase() => viaBase.GoAsync("a");

            public Task ViaGenBase() => genBase.GoAsync("a");

            public Task ViaUnrelated() => unrelated.GoAsync();
        }
        """;

    private static CSharpCompilation InheritedImplementationCompilation()
    {
        CSharpCompilation compilation = CSharpCompilation.Create("Fixture", [CSharpSyntaxTree.ParseText(InheritedImplementationSource)],
            LeaseHandoffFrameworkReferences(), new(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    [Theory]
    [InlineData("Fixture.IDoor")]
    [InlineData("Fixture.Base")]
    public void TheSharedMatcherFollowsAnImplementationTheClassInheritsFromItsBase(string link)
    {
        string[] members = [.. HostedGrimoireProducerInventory.MethodReferencesTo([InheritedImplementationCompilation()], link, "GoAsync")
            .Select(MethodReferenceMember).Order(StringComparer.Ordinal)];

        // The slot, the base-typed receiver, the derived type and the override are one door: the base declares the method the derived class
        // runs for the slot, so a call through any of them can reach the same body. The unrelated class's method of the same name is not.
        Assert.Equal(["ViaBaseTyped", "ViaDoor", "ViaInherited", "ViaOverriding"], members);
    }

    [Theory]
    [InlineData("Fixture.IGen`1")]
    [InlineData("Fixture.Closed")]
    [InlineData("Fixture.Open`1")]
    [InlineData("Fixture.GenBase`1")]
    public void TheSharedMatcherFollowsAGenericSlotThatTheImplementerClosesInItsBaseList(string link)
    {
        string[] members = [.. HostedGrimoireProducerInventory.MethodReferencesTo([InheritedImplementationCompilation()], link, "GoAsync")
            .Select(MethodReferenceMember).Order(StringComparer.Ordinal)];

        // The slot of the open definition, the type that closes it, the open implementation and the class that implements the closed slot by a
        // method of its generic base are one door. The matching is by original definition, so a call of the generic base's method on any
        // instantiation is reported too: it can only report more callers than the closed slot has, never fewer.
        Assert.Equal(["ViaClosed", "ViaClosedViaBase", "ViaGen", "ViaGenBase", "ViaOpen"], members);
    }

    [Fact]
    public void TheSharedMatcherFindsEveryImplementationOfAContractWhereverItIsDeclared()
    {
        string[] implementers = [.. HostedGrimoireProducerInventory.ImplementersOf([MethodReferenceCompilation()], "Fixture.IDoor").Select(static type => type.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["Derived", "Door", "Explicit", "Hidden"], implementers);
    }
}

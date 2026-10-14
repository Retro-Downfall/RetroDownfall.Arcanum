using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private static HostedProducerAdmittedStateObservation[] CanonicalizationStatesOf(
        CSharpCompilation compilation,
        HostedProducerAdmittedStateObservation[] states,
        string typeName,
        string memberName)
    {
        if (memberName.StartsWith('~'))
        {
            // A lambda or a local function is no member of its type: its states are found by the name its method key carries.
            return [.. states.Where(state => state.Method.Contains(memberName[1..], StringComparison.Ordinal))];
        }

        INamedTypeSymbol type = Assert.IsAssignableFrom<INamedTypeSymbol>(compilation.GetTypeByMetadataName(typeName));

        IMethodSymbol[] methods =
        [
            .. type.GetMembers(memberName).OfType<IMethodSymbol>(),
            .. type.GetMembers(memberName).OfType<IPropertySymbol>().Select(static property => property.GetMethod).OfType<IMethodSymbol>(),
        ];

        string[] keys = [.. methods.Select(static method => method.OriginalDefinition.GetDocumentationCommentId() ?? method.ToDisplayString())];

        Assert.NotEmpty(keys);

        return [.. states.Where(state => keys.Contains(state.Method))];
    }

    private static (HostedProducerDiscovery<HostedProducerSite> Result, HostedProducerAdmittedStateObservation[] Members) CanonicalizationDiscover(
        string body, string helper, string typeName, string memberName)
    {
        var (compilation, result, states) = ClosedValueDiscover(body, helper);

        return (result, CanonicalizationStatesOf(compilation, states, typeName, memberName));
    }

    // The flag decides which way the helper goes, so a caller's value for it is part of the helper's environment.
    private const string ConstantGateHelper = """
        public static class Gate
        {
            public static bool Probe(bool flag)
            {
                if (flag)
                {
                    return true;
                }

                return false;
            }
        }
        """;

    private static string ConstantGateSites(string argument, int count) =>
        string.Concat(Enumerable.Repeat("_ = Gate.Probe(" + argument + "); ", count));

    // A resource passed on is bound by where it was created, never by what it is.
    private const string ResourceSinkHelper = """
        public static class Sink
        {
            public static void Use(System.IDisposable resource) { using (resource) { } }
        }
        """;

    [Fact]
    public void ConstantBooleanSourcesAtManySitesShareOneState()
    {
        var (result, probe) = CanonicalizationDiscover(ConstantGateSites("false", 10), ConstantGateHelper, "Gate", "Probe");

        Assert.Empty(result.Diagnostics);

        Assert.Single(probe);
    }

    [Fact]
    public void ConstantsOfDifferentValuesKeepSeparateStates()
    {
        var (result, probe) = CanonicalizationDiscover(ConstantGateSites("false", 10) + ConstantGateSites("true", 10), ConstantGateHelper, "Gate", "Probe");

        Assert.Empty(result.Diagnostics);

        Assert.Equal(2, probe.Length);

        Assert.Equal(2, probe.Select(static state => state.EvaluationEnvironmentToken).Distinct().Count());
    }

    [Fact]
    public void ASourceThatIsNotAConstantKeepsItsPosition()
    {
        string sites = string.Concat(Enumerable.Repeat("Sink.Use(new System.IO.MemoryStream()); ", 3));

        var (result, probe) = CanonicalizationDiscover(sites, ResourceSinkHelper, "Sink", "Use");

        Assert.Empty(result.Diagnostics);

        Assert.Equal(3, probe.Length);
    }

    private static ExpressionSyntax SoleArgumentOf(CSharpCompilation compilation, string method) =>
        compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(declaration => declaration.Identifier.ValueText == method)
            .DescendantNodes().OfType<InvocationExpressionSyntax>().Single(static call => call.Expression.ToString() == "Use").ArgumentList.Arguments.Single().Expression;

    [Theory]
    [InlineData("false", "false", true)]
    [InlineData("false", "true", false)]
    [InlineData("0", "0", true)]
    [InlineData("0", "1", false)]
    [InlineData("0", "0L", false)]
    [InlineData("1.0f", "1.0", false)]
    [InlineData("\"a\"", "\"a\"", true)]
    [InlineData("\"a\"", "\"b\"", false)]
    [InlineData("'a'", "97", false)]
    [InlineData("null", "null", true)]
    [InlineData("Limit", "3", true)]
    [InlineData("Limit", "4", false)]
    [InlineData("Mode.On", "Mode.On", true)]
    [InlineData("Mode.On", "Mode.Off", false)]
    [InlineData("Mode.Off", "0", false)]
    public void AConstantIsNamedByItsValueAndItsType(string left, string right, bool same)
    {
        string source = "class Holder { const int Limit = 3; enum Mode { Off, On } static void Use(object value) { } static void First() { Use(" + left + "); } static void Second() { Use(" + right + "); } }";

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        string? first = HostedGrimoireProducerInventory.ProbeConstantValueIdentity(compilation, SoleArgumentOf(compilation, "First"));

        string? second = HostedGrimoireProducerInventory.ProbeConstantValueIdentity(compilation, SoleArgumentOf(compilation, "Second"));

        Assert.NotNull(first);

        Assert.NotNull(second);

        Assert.Equal(same, first == second);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("System.DateTime.MinValue")]
    [InlineData("new object()")]
    [InlineData("System.Environment.TickCount")]
    [InlineData("typeof(int)")]
    [InlineData("Compute()")]
    [InlineData("(object)Compute()")]
    public void AnExpressionThatIsNotAConstantKeepsItsPosition(string expression)
    {
        string source = "class Holder { static int Compute() => 1; static void Use(object value) { } static void First(object value) { Use(" + expression + "); } }";

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Null(HostedGrimoireProducerInventory.ProbeConstantValueIdentity(compilation, SoleArgumentOf(compilation, "First")));
    }

    [Theory]
    [InlineData("static int Ordinary(int value) => value;", false)]
    [InlineData("int Property => 1;", false)]
    [InlineData("int Indexed { get { return 1; } }", false)]
    [InlineData("Holder() { }", false)]
    [InlineData("public static int operator +(Holder left, Holder right) => 1;", false)]
    [InlineData("public static implicit operator int(Holder value) => 1;", false)]
    [InlineData("int Outer(int value) { int Local() => value; return Local(); }", true)]
    [InlineData("int Outer(int value) { System.Func<int> work = () => value; return work(); }", true)]
    public void OnlyALambdaOrALocalFunctionCanBeNestedInAMethod(string member, bool canBeNested)
    {
        CSharpCompilation compilation = Compile("sealed class Holder { " + member + " }");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = compilation.SyntaxTrees.Single().GetRoot();

        SyntaxNode[] declared =
        [
            .. root.DescendantNodes().Where(static node => node is AccessorDeclarationSyntax { Body: not null } or ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax }
                or MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax),
        ];

        // Every declaration in the member is judged: a method that contains a lambda or a local function is itself not nestable.
        SyntaxNode? nested = root.DescendantNodes().FirstOrDefault(static node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);

        if (canBeNested)
        {
            Assert.NotNull(nested);

            Assert.True(HostedGrimoireProducerInventory.ProbeMemberCanBeNestedInAMethod(compilation, nested!));

            Assert.All(declared.OfType<MethodDeclarationSyntax>(), method => Assert.False(HostedGrimoireProducerInventory.ProbeMemberCanBeNestedInAMethod(compilation, method)));
        }
        else
        {
            Assert.NotEmpty(declared);

            Assert.All(declared, node => Assert.False(HostedGrimoireProducerInventory.ProbeMemberCanBeNestedInAMethod(compilation, node)));
        }
    }

    // An instance call passes its caller's whole context on to the members of the receiver. A parameter of the calling
    // method is no name in an accessor, so its binding multiplies the accessor's states by every distinct binding of the
    // callers above it and cannot change what the accessor does.
    private const string ForeignParameterHelper = """
        public sealed class Handles
        {
            private readonly string _path;

            private readonly System.IO.FileStream _stream;

            private Handles(string path, System.IO.FileStream stream) { _path = path; _stream = stream; }

            public static Handles Open(string path) => new(path, new System.IO.FileStream(path, System.IO.FileMode.Open));

            private System.IO.FileStream Stream => _stream;

            public int Child(bool create, bool writable) { _ = Stream; return create ? 1 : writable ? 2 : 3; }

            public int A() => Child(false, false);

            public int B() => Child(true, false);

            public int C() => Child(false, true);
        }
        """;

    private const string ForeignParameterBody = "var h = Handles.Open(\"p\"); _ = h.A(); _ = h.B(); _ = h.C();";

    [Fact]
    public void AnAccessorKeepsNoBindingForAParameterOfTheMethodThatCalledIt()
    {
        var (result, accessor) = CanonicalizationDiscover(ForeignParameterBody, ForeignParameterHelper, "Handles", "Stream");

        Assert.Single(accessor);

        // The file stream is opened with no admitted Work in this fixture; the projection changes no finding about it.
        Assert.Equal(["HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING"], result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnOrdinaryMethodKeepsItsOwnParameterBindings()
    {
        var (_, child) = CanonicalizationDiscover(ForeignParameterBody, ForeignParameterHelper, "Handles", "Child");

        // Called with (false, false), (true, false) and (false, true): the flags decide which way the method goes.
        Assert.Equal(3, child.Length);
    }

    [Fact]
    public void ALambdaKeepsTheBindingsOfTheMethodItIsDeclaredIn()
    {
        const string helper = """
            public static class Nest
            {
                public static int Run(bool flag) => Invoke(() => flag ? 1 : 2);

                public static int Invoke(System.Func<int> work) => work();

                public static int A() => Run(false);

                public static int B() => Run(true);
            }
            """;

        var (_, lambda) = CanonicalizationDiscover("_ = Nest.A(); _ = Nest.B();", helper, "Nest", "~M:Nest.");

        // The lambda reads the flag of Run, the method that declares it, so each value of it is a state of its own.
        HostedProducerAdmittedStateObservation[] bodies = [.. lambda.Where(static state => state.Method == "M:Nest.")];

        Assert.Equal(2, bodies.Length);
    }

    [Fact]
    public void AnAccessorKeepsTheBindingsOfThePrimaryConstructorOfItsType()
    {
        const string helper = """
            public sealed class Cfg(bool strict, string path)
            {
                private readonly System.IO.FileStream _stream = new(path, System.IO.FileMode.Open);

                private bool Strict => strict;

                public int Read() => Strict ? 1 : 2;
            }
            """;

        var (_, accessor) = CanonicalizationDiscover("_ = new Cfg(true, \"p\").Read(); _ = new Cfg(false, \"p\").Read();", helper, "Cfg", "Strict");

        // A primary constructor parameter is a name in every member of the type, so each value of it is a state of its own.
        Assert.Equal(2, accessor.Length);
    }

    private const string CommandParameterHelper = """
        public static class Params
        {
            public static void Add(System.Data.Common.DbCommand command, string name, object value)
            {
                System.Data.Common.DbParameter parameter = command.CreateParameter();

                parameter.ParameterName = name;

                parameter.Value = value;

                command.Parameters.Add(parameter);
            }

            public static void Close(System.Data.Common.DbCommand command)
            {
                command.Dispose();
            }
        }

        internal static class Commands
        {
            internal static System.Data.Common.DbCommand Current { get; set; } = null!;
        }
        """;

    private static string CommandParameterSites(string call, int count) =>
        string.Concat(Enumerable.Repeat(call + " ", count));

    private (HostedProducerDiscovery<HostedProducerSite> Result, HostedProducerAdmittedStateObservation[] Members) ProviderObservationDiscover(
        string body, string helper, string memberName, bool production)
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body + " await Task.CompletedTask;", helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> observed = new();

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSitesUnderPremises(
            production ? HostedGrimoireProducerInventory.ProductionPremises : HostedProducerPremises.None,
            [compilation],
            new(["Worker"], []),
            [new("Worker", [OrdinaryRoot()])],
            [],
            admittedStateObserver: observed.Enqueue);

        foreach (HostedProducerInventoryDiagnostic diagnostic in result.Diagnostics)
        {
            output.WriteLine("PROVIDER diag " + diagnostic.Code + " " + diagnostic.Detail);
        }

        return (result, CanonicalizationStatesOf(compilation, [.. observed], "Params", memberName));
    }

    // A helper that links a source to the token it is handed, with the token supplied by a different call chain at each site.
    private const string TokenSinkHelper = """
        public static class Linker
        {
            public static void Link(System.Threading.CancellationToken token)
            {
                using System.Threading.CancellationTokenSource linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token);
            }

            public static void LinkSource(System.Threading.CancellationTokenSource source)
            {
                using System.Threading.CancellationTokenSource linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(source.Token);
            }

            public static void Settle(System.Threading.CancellationTokenRegistration registration)
            {
                using (registration)
                {
                }
            }
        }

        internal static class Handed
        {
            internal static System.Threading.CancellationToken Token { get; set; }

            internal static System.Threading.CancellationTokenSource Source { get; set; } = null!;

            internal static System.Threading.CancellationTokenRegistration Registration { get; set; }
        }
        """;

    private const int TokenSiteCount = 6;

    [Fact]
    public void ACancellationTokenIsNeverBoundToTheCallChainThatSuppliedIt()
    {
        var (compilation, result, states) = ClosedValueDiscover(CommandParameterSites("Linker.Link(Handed.Token);", TokenSiteCount), TokenSinkHelper);

        Assert.Empty(result.Diagnostics);

        Assert.Single(CanonicalizationStatesOf(compilation, states, "Linker", "Link"));
    }

    [Theory]
    [InlineData("Linker.LinkSource(Handed.Source);", "LinkSource")]
    [InlineData("Linker.Settle(Handed.Registration);", "Settle")]
    public void ADisposableThatLooksLikeATokenKeepsItsProvenance(string call, string member)
    {
        var (compilation, _, states) = ClosedValueDiscover(CommandParameterSites(call, TokenSiteCount), TokenSinkHelper);

        // The source and the registration are what a disposal resolves against, so each site keeps a state of its own.
        Assert.True(CanonicalizationStatesOf(compilation, states, "Linker", member).Length >= TokenSiteCount, "Each supplier must keep a state of its own.");
    }

    // A payload that is only moved, and payloads that are not. Each helper is called with five different concrete payload types.
    private const string PayloadHelper = """
        public sealed class Payload { }

        public sealed class Box<T>
        {
            private readonly T _value;

            private Box(T value) { _value = value; }

            public static Box<T> Make(T value) => new(value);

            public static Box<T> Wrap(T value) => Make(value);
        }

        public static class Describe
        {
            public static string Text<T>(T value) => value!.ToString()!;

            public static string Forward<T>(T value) => Text(value);

            public static object Cast<T>(T value) => (object)value!;
        }
        """;

    [Theory]
    [InlineData("_ = Box<int>.Make(1); _ = Box<string>.Make(\"a\"); _ = Box<System.Guid>.Make(System.Guid.Empty); _ = Box<Payload>.Make(new Payload()); _ = Box<long>.Make(2L);", "Box`1", "Make")]
    [InlineData("_ = Box<int>.Wrap(1); _ = Box<string>.Wrap(\"a\"); _ = Box<System.Guid>.Wrap(System.Guid.Empty); _ = Box<Payload>.Wrap(new Payload()); _ = Box<long>.Wrap(2L);", "Box`1", "Wrap")]
    public void APayloadThatIsOnlyMovedIsOneStateWhateverItsType(string body, string type, string member)
    {
        var (result, states) = CanonicalizationDiscover(body, PayloadHelper, type, member);

        Assert.Empty(result.Diagnostics);

        Assert.Single(states);
    }

    [Theory]
    [InlineData("_ = Describe.Text(1); _ = Describe.Text(\"a\"); _ = Describe.Text(System.Guid.Empty); _ = Describe.Text(new Payload()); _ = Describe.Text(2L);", "Text")]
    [InlineData("_ = Describe.Forward(1); _ = Describe.Forward(\"a\"); _ = Describe.Forward(System.Guid.Empty); _ = Describe.Forward(new Payload()); _ = Describe.Forward(2L);", "Forward")]
    [InlineData("_ = Describe.Cast(1); _ = Describe.Cast(\"a\"); _ = Describe.Cast(System.Guid.Empty); _ = Describe.Cast(new Payload()); _ = Describe.Cast(2L);", "Cast")]
    public void APayloadThatIsReadKeepsAStatePerType(string body, string member)
    {
        var (_, states) = CanonicalizationDiscover(body, PayloadHelper, "Describe", member);

        // The receiver, the forwarded receiver and the conversion each read the type, so each type is a state of its own.
        Assert.Equal(5, states.Length);
    }

    // Six commands passed to a helper that only asks each for a parameter: the helper does nothing to a command that its
    // provenance could change, so the provider being closed makes the six one state. The same six passed to a helper that
    // disposes its command stay six, because there the provenance is what a disposal resolves against.
    private const int CommandParameterSiteCount = 6;

    [Fact]
    public void AHelperThatOnlyAsksACommandForAParameterIsOneStateWhileTheProviderIsClosed()
    {
        var (result, helper) = ProviderObservationDiscover(
            CommandParameterSites("Params.Add(Commands.Current, \"@a\", 1);", CommandParameterSiteCount), CommandParameterHelper, "Add", production: true);

        Assert.Empty(result.Diagnostics);

        Assert.Single(helper);
    }

    [Fact]
    public void WithoutTheClosedProviderPremiseTheSameHelperKeepsAStatePerCommand()
    {
        var (result, helper) = ProviderObservationDiscover(
            CommandParameterSites("Params.Add(Commands.Current, \"@a\", 1);", CommandParameterSiteCount), CommandParameterHelper, "Add", production: false);

        // The call itself is refused without the premise, as before; what changes is only that nothing is trusted about its receiver.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "System.Data.Common.DbCommand.CreateParameter");

        Assert.True(helper.Length >= CommandParameterSiteCount, "Each command must keep a state of its own.");
    }

    [Fact]
    public void AnAuthoredCommandSubclassWithdrawsTheTrustInCreateParameter()
    {
        var (result, helper) = ProviderObservationDiscover(
            CommandParameterSites("Params.Add(Commands.Current, \"@a\", 1);", CommandParameterSiteCount), CommandParameterHelper + "\n" + DispatchAuthoredProviders, "Add", production: true);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_PREMISE_VIOLATED");

        Assert.True(helper.Length >= CommandParameterSiteCount, "Each command must keep a state of its own.");
    }

    [Fact]
    public void AHelperThatDisposesItsCommandKeepsAStatePerCommand()
    {
        var (_, helper) = ProviderObservationDiscover(
            CommandParameterSites("Params.Close(Commands.Current);", CommandParameterSiteCount), CommandParameterHelper, "Close", production: true);

        Assert.True(helper.Length >= CommandParameterSiteCount, "Each command must keep a state of its own.");
    }
}

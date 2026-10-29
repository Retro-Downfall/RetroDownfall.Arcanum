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

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production, observed.Enqueue);

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

    // ---- Foreign Parameter Projection and the two questions that read every admission binding of a member.
    //
    // Whether a member holds an admission seed, and whether it may contain an admission origin, ask whether any parameter of
    // the member is bound to an admitted origin. A parameter of an ordinary method is no name in a member that cannot be
    // nested in a method, so a binding for it names an origin the member can neither reach nor run code that reaches: it is
    // no seed, whether or not the traversal projected it away before asking.
    private static (bool Seed, bool MayContain) AdmissionBindingProbe(string source, string memberSelector, string parameterSelector)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = compilation.SyntaxTrees.Single().GetRoot();

        SyntaxNode member = memberSelector switch
        {
            "accessor" => root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single(static property => property.Identifier.ValueText == "Value").ExpressionBody!,
            "constructor" => root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Single(),
            "operator" => root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Single(),
            "conversion" => root.DescendantNodes().OfType<ConversionOperatorDeclarationSyntax>().Single(),
            "explicit-interface" => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.ExplicitInterfaceSpecifier is not null),
            "lambda" => root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Single(),
            "local-function" => root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(),
            "method" => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Own"),
            _ => throw new ArgumentOutOfRangeException(nameof(memberSelector)),
        };

        ParameterSyntax parameter = parameterSelector switch
        {
            "caller" => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(static method => method.Identifier.ValueText == "Via").ParameterList.Parameters.Single(),
            "own" => member.DescendantNodesAndSelf().OfType<ParameterSyntax>().First(),
            _ => throw new ArgumentOutOfRangeException(nameof(parameterSelector)),
        };

        return HostedGrimoireProducerInventory.ProbeAdmissionBinding(compilation, member, parameter);
    }

    private const string ForeignAdmissionMembers = """
        interface IRun { int Run(); }

        sealed class Box : IRun
        {
            public int Value => 1;

            public static Box operator +(Box left, Box right) => left;

            public static implicit operator int(Box box) => 1;

            int IRun.Run() => 1;
        }

        static class Runner
        {
            public static int Via(object lease) => new Box().Value;
        }
        """;

    [Theory]
    [InlineData("accessor")]
    [InlineData("operator")]
    [InlineData("conversion")]
    [InlineData("explicit-interface")]
    public void AnAdmittedOriginBoundToAParameterOfAnotherMethodIsNoSeedInAMemberThatCannotBeNested(string member)
    {
        (bool seed, bool mayContain) = AdmissionBindingProbe(ForeignAdmissionMembers, member, "caller");

        Assert.False(seed);

        Assert.False(mayContain);
    }

    [Fact]
    public void AnAdmittedOriginBoundToAParameterOfAnotherMethodIsNoSeedInAConstructor()
    {
        const string source = "sealed class Box { public Box() { } } static class Runner { public static int Via(object lease) => new Box() is null ? 0 : 1; }";

        (bool seed, bool mayContain) = AdmissionBindingProbe(source, "constructor", "caller");

        Assert.False(seed);

        Assert.False(mayContain);
    }

    [Theory]
    [InlineData("lambda", "System.Func<int> work = () => 1; return work();")]
    [InlineData("local-function", "int Work() => 1; return Work();")]
    public void AnAdmittedOriginBoundToAParameterOfTheMethodThatDeclaresAMemberIsASeedInIt(string member, string body)
    {
        string source = "static class Runner { public static int Via(object lease) { " + body + " } }";

        (bool seed, bool mayContain) = AdmissionBindingProbe(source, member, "caller");

        // A lambda and a local function may be declared inside the owner, so they may name its parameters.
        Assert.True(seed);

        Assert.True(mayContain);
    }

    [Fact]
    public void AnAdmittedOriginBoundToAMembersOwnParameterIsASeedInIt()
    {
        (bool seed, bool mayContain) = AdmissionBindingProbe("static class Runner { public static int Own(object lease) => 1; }", "method", "own");

        Assert.True(seed);

        Assert.True(mayContain);

        (bool constructorSeed, bool constructorMayContain) = AdmissionBindingProbe("sealed class Box { public Box(object lease) { } }", "constructor", "own");

        Assert.True(constructorSeed);

        Assert.True(constructorMayContain);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 2)]
    public void NestedOwnParameterProbe_OnlyUsedOwnValuesSplitStates(bool lambda, bool used, int expected)
    {
        string helper = "public static class NestedOwn { public static void Run() { "
            + (lambda ? "System.Func<bool, int> Target = own => " : "int Target(bool own) => ")
            + (used ? "own ? 1 : 2;" : "1;")
            + " _ = Target(false); _ = Target(true); } }";

        var (result, nested) = DiscoverNestedOwnProbe(helper, "NestedOwn.Run();");

        Assert.Empty(result.Diagnostics);

        Assert.Equal(expected, nested.Length);
    }

    [Theory]
    [InlineData(false, false, true, false, 1)]
    [InlineData(true, false, true, false, 1)]
    [InlineData(false, true, false, false, 1)]
    [InlineData(true, true, false, false, 1)]
    [InlineData(false, true, true, false, 1)]
    [InlineData(true, true, true, false, 1)]
    [InlineData(false, false, true, true, 2)]
    [InlineData(true, false, true, true, 2)]
    [InlineData(false, true, false, true, 2)]
    [InlineData(true, true, false, true, 2)]
    [InlineData(false, true, true, true, 2)]
    [InlineData(true, true, true, true, 2)]
    public void NestedOwnParameterProbe_GenericAndCrossCompilationBindingsRemainExact(
        bool lambda, bool generic, bool crossCompilation, bool used, int expected)
    {
        string helper = "public static class NestedOwn" + (generic ? "<T>" : "") + " { public static void Run() { "
            + (lambda ? "System.Func<bool, int> Target = own => " : "int Target(bool own) => ")
            + (used ? "own ? 1 : 2;" : "1;")
            + " _ = Target(false); _ = Target(true); } }";

        var (result, nested) = DiscoverNestedOwnProbe(helper, "NestedOwn" + (generic ? "<int>" : "") + ".Run();", crossCompilation);

        Assert.Empty(result.Diagnostics);

        Assert.Equal(expected, nested.Length);
    }

    [Theory]
    [InlineData(true, "outer")]
    [InlineData(false, "nested-helper")]
    [InlineData(true, "nested-helper")]
    [InlineData(false, "forwarded")]
    [InlineData(true, "forwarded")]
    public void NestedOwnParameterProbe_CapturedAndForwardedValuesStayDistinct(bool lambda, string shape)
    {
        string targetBody = shape switch
        {
            "outer" => "return outer ? 1 : 2;",
            "nested-helper" => "int Later() => own ? 1 : 2; return Later();",
            _ => "return Read(own);",
        };

        string helper = "public static class NestedOwn { private static int Read(bool value) => value ? 1 : 2; public static void Run(bool outer) { "
            + (lambda ? "System.Func<bool, int> Target = own => " : "int Target(bool own) ")
            + "{ " + targetBody + " }" + (lambda ? ";" : "")
            + " _ = Target(false); " + (shape == "outer" ? "" : "_ = Target(true);") + " } }";

        var (result, nested) = DiscoverNestedOwnProbe(helper,
            shape == "outer" ? "NestedOwn.Run(false); NestedOwn.Run(true);" : "NestedOwn.Run(false);");

        Assert.Empty(result.Diagnostics);

        Assert.Equal(2, nested.Length);
    }

    [Theory]
    [InlineData(false, "ref")]
    [InlineData(true, "ref")]
    [InlineData(false, "out")]
    [InlineData(true, "out")]
    [InlineData(false, "in")]
    [InlineData(true, "in")]
    public void NestedOwnParameterProbe_ByReferenceBindingsRemainConservative(bool lambda, string modifier)
    {
        string helper = "public delegate int OwnReference(" + modifier + " bool value); public static class NestedOwn { public static void Run() { "
            + (lambda ? "OwnReference Target = (" + modifier + " bool own) => " : "int Target(" + modifier + " bool own) ")
            + "{ " + (modifier == "out" ? "own = false; " : "") + "return 1; }" + (lambda ? ";" : "")
            + " bool left = false; bool right = true; _ = Target(" + modifier + " left); _ = Target(" + modifier + " right); } }";

        var (result, nested) = DiscoverNestedOwnProbe(helper, "NestedOwn.Run();");

        Assert.Empty(result.Diagnostics);

        Assert.NotEmpty(nested);

        AssertNestedProjectionBindings(helper, "own", admittedFirst: false, retainEmpty: true);

        AssertNestedProjectionBindings(helper, "own", admittedFirst: true, retainEmpty: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedOwnParameterProbe_AdmissionFrontiersRemainDistinct(bool lambda)
    {
        string helper = "public static class NestedOwn { public static void Run() { "
            + (lambda ? "System.Func<bool, int> Target = own => " : "int Target(bool own) => ")
            + "1; _ = Target(false); _ = Target(true); } }";

        var (result, nested) = DiscoverNestedOwnProbe(helper, "NestedOwn.Run(); " + R2Admission + " NestedOwn.Run();");

        Assert.Empty(result.Diagnostics);

        Assert.Equal(2, nested.Select(static state => (state.WorkFrontier, state.EffectFrontier)).Distinct().Count());

        Assert.Contains(nested, static state => state.WorkFrontier is null && state.EffectFrontier is null);

        Assert.Contains(nested, static state => state.WorkFrontier is not null && state.EffectFrontier is not null);

        Assert.Equal(2, nested.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedOwnParameterProbe_RecoveryMembershipSurvivesAcrossRootOrder(bool exactFirst) =>
        AssertProjectionPreservesRecoveryMembershipAcrossRootOrder(exactFirst, nested: true);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void NestedOwnParameterProbe_AdmissionAnswersAndEnclosingBindingsSurviveProjection(
        bool lambda, bool admittedFirst, bool enclosing)
    {
        string helper = "public static class NestedOwn { public static void Run(object outer) { "
            + (lambda ? "System.Func<object, int> Target = own => " : "int Target(object own) => ")
            + "1; _ = Target(outer); } }";

        AssertNestedProjectionBindings(helper, enclosing ? "outer" : "own", admittedFirst, retainEmpty: enclosing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedOwnParameterProbe_SelectedRegionsKeepIndependentAuthority(bool lambda)
    {
        string helper = "public static class NestedOwn { public static void Run() { "
            + (lambda ? "System.Func<bool, int> Target = own => " : "int Target(bool own) ")
            + "{ System.IO.File.Exists(\"nested-selected\"); return 1; }" + (lambda ? ";" : "")
            + " _ = Target(false); _ = Target(true); } }";

        string source = R2Source(R2Admission + " NestedOwn.Run(); NestedOwn.Run(); await Task.CompletedTask;", helper);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerOperationEntry[] roots = [OrdinaryRoot(CallRoot(source, "NestedOwn.Run", 0)), OrdinaryRoot(CallRoot(source, "NestedOwn.Run", 1))];

        System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> observed = new();

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", roots)], [], admittedStateObserver: observed.Enqueue);

        Assert.Empty(result.Diagnostics);

        ParameterSyntax own = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<ParameterSyntax>()
            .Single(static parameter => parameter.Identifier.ValueText == "own");

        SyntaxNode declaration = own.Ancestors().First(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

        int targetStart = declaration is LambdaExpressionSyntax anonymous ? anonymous.Body.SpanStart : declaration.SpanStart;

        foreach (HostedProducerOperationEntry root in roots)
        {
            HostedProducerSite site = Assert.Single(result.Items, site => site.AuthorityOperationId == root.OperationId
                && site.Callee == "System.IO.File.Exists");

            Assert.NotNull(site.WorkFrontierCapsuleId);

            Assert.NotNull(site.EffectFrontierCapsuleId);

            Assert.Single(observed, state => state.RootOperation == root.OperationId
                && state.Method.StartsWith("M:NestedOwn", StringComparison.Ordinal)
                && state.SpanStart == targetStart);
        }
    }

    private static void AssertNestedProjectionBindings(string source, string binding, bool admittedFirst, bool retainEmpty)
    {
        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = compilation.SyntaxTrees.Single().GetRoot();

        ParameterSyntax own = root.DescendantNodes().OfType<ParameterSyntax>().Single(static parameter => parameter.Identifier.ValueText == "own");

        SyntaxNode declaration = own.Ancestors().First(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

        ParameterSyntax bound = root.DescendantNodes().OfType<ParameterSyntax>().Single(parameter => parameter.Identifier.ValueText == binding);

        IParameterSymbol parameter = compilation.GetSemanticModel(bound.SyntaxTree).GetDeclaredSymbol(bound)!;

        var projected = HostedGrimoireProducerInventory.ProbeNestedAdmissionProjection(compilation, declaration, parameter, admittedFirst);

        Assert.Equal(2, projected.Length);

        Assert.All(projected, state =>
        {
            Assert.True(state.Complete);

            Assert.Equal(state.Admitted, state.SeedBefore);

            Assert.Equal(state.Admitted, state.MayBefore);

            Assert.Equal(state.Admitted, state.SeedAfter);

            Assert.Equal(state.Admitted, state.MayAfter);

            Assert.Equal(state.Admitted || retainEmpty, state.Retained);
        });
    }

    private static (HostedProducerDiscovery<HostedProducerSite> Result, HostedProducerAdmittedStateObservation[] States)
        DiscoverNestedOwnProbe(string helper, string body, bool crossCompilation = false)
    {
        CSharpCompilation compilation;

        HostedProducerDiscovery<HostedProducerSite> result;

        HostedProducerAdmittedStateObservation[] states;

        if (crossCompilation)
        {
            compilation = CompileWithProductionReferencePack(helper).WithAssemblyName("NestedOwnLibrary");

            CSharpCompilation caller = CompileWithProductionReferencePack(R2Source(body + " await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api")
                .AddReferences(compilation.ToMetadataReference());

            Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            Assert.Empty(caller.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> observed = new();

            result = HostedGrimoireProducerInventory.DiscoverProducerSites(
                [compilation, caller], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], [], admittedStateObserver: observed.Enqueue);

            states = [.. observed];
        }
        else
        {
            (compilation, result, states) = ClosedValueDiscover(body, helper);
        }

        SyntaxNode root = compilation.SyntaxTrees.Single().GetRoot();

        ParameterSyntax parameter = root.DescendantNodes().OfType<ParameterSyntax>()
            .Single(static candidate => candidate.Identifier.ValueText == "own");

        SyntaxNode declaration = parameter.Ancestors().First(static node =>
            node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

        int bodyStart = declaration is LambdaExpressionSyntax anonymous ? anonymous.Body.SpanStart : declaration.SpanStart;

        HostedProducerAdmittedStateObservation[] nested = [.. states.Where(state => state.SpanStart == bodyStart
            && state.Method.StartsWith("M:NestedOwn", StringComparison.Ordinal))];

        return (result, nested);
    }

    [Theory]
    [InlineData(true, "own", true)]
    [InlineData(false, "own", true)]
    [InlineData(true, "parent", true)]
    [InlineData(false, "parent", true)]
    [InlineData(true, "ancestor", true)]
    [InlineData(false, "ancestor", true)]
    [InlineData(true, "construction", true)]
    [InlineData(false, "construction", true)]
    [InlineData(true, "caller", false)]
    [InlineData(false, "caller", false)]
    [InlineData(true, "siblingLocal", false)]
    [InlineData(false, "siblingLocal", false)]
    [InlineData(true, "siblingLambda", false)]
    [InlineData(false, "siblingLambda", false)]
    public void LexicalAncestorProbe_AdmissionIgnoresOnlyParametersOutsideTheTargetScope(
        bool lambda, string binding, bool retained)
    {
        string target = lambda
            ? "System.Func<object, int> Target = own => 0;"
            : "int Target(object own) => 0;";

        string source = """
            sealed class Holder(object construction)
            {
                public static int Via(object caller) => 0;

                public int Outer(object ancestor)
                {
                    int Sibling(object siblingLocal) => 0;

                    System.Func<object, int> sibling = siblingLambda => 0;

                    int Parent(object parent)
                    {
                        TARGET

                        return Target(parent);
                    }

                    return Parent(ancestor) + Sibling(construction) + sibling(construction);
                }
            }
            """.Replace("TARGET", target, StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = compilation.SyntaxTrees.Single().GetRoot();

        ParameterSyntax own = root.DescendantNodes().OfType<ParameterSyntax>().Single(static parameter => parameter.Identifier.ValueText == "own");

        SyntaxNode declaration = own.Ancestors().First(static node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

        ParameterSyntax parameter = root.DescendantNodes().OfType<ParameterSyntax>().Single(parameter => parameter.Identifier.ValueText == binding);

        (bool seed, bool mayContain) = HostedGrimoireProducerInventory.ProbeAdmissionBinding(compilation, declaration, parameter);

        Assert.Equal(retained, seed);

        Assert.Equal(retained, mayContain);
    }

    [Theory]
    [InlineData(false, false, false, 1)]
    [InlineData(true, false, false, 2)]
    [InlineData(false, true, false, 1)]
    [InlineData(true, true, false, 2)]
    [InlineData(false, false, true, 1)]
    [InlineData(true, false, true, 2)]
    [InlineData(false, true, true, 1)]
    [InlineData(true, true, true, 2)]
    public void LexicalAncestorProbe_CallbackRetainsItsCapturedFlagButNotItsInvokerFlag(
        bool captures, bool generic, bool crossCompilation, int expected)
    {
        string helper = """
            public sealed class CallbackHolder(System.Func<int> callback) : System.IDisposable
            {
                private readonly System.IO.MemoryStream stream = new();

                public int Call(bool invoker)
                {
                    int value = callback();

                    return invoker ? value : 0;
                }

                public void Dispose() => stream.Dispose();
            }

            public static class NestedFactory
            {
                public static void Run(bool captured)
                {
                    using var holder = new CallbackHolder(() => VALUE);

                    _ = holder.Call(false);

                    _ = holder.Call(true);
                }
            }
            """.Replace("VALUE", captures ? "captured ? 1 : 2" : "1", StringComparison.Ordinal);

        if (generic)
        {
            helper = helper.Replace("class NestedFactory", "class NestedFactory<T>", StringComparison.Ordinal);
        }

        string factory = generic ? "NestedFactory<int>" : "NestedFactory";

        string body = factory + ".Run(false); " + factory + ".Run(true);";

        HostedProducerDiscovery<HostedProducerSite> result;

        HostedProducerAdmittedStateObservation[] states;

        if (crossCompilation)
        {
            CSharpCompilation library = CompileWithProductionReferencePack(helper).WithAssemblyName("NestedScopeLibrary");

            CSharpCompilation caller = CompileWithProductionReferencePack(R2Source(body + " await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api")
                .AddReferences(library.ToMetadataReference());

            Assert.Empty(library.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            Assert.Empty(caller.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

            System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> observations = new();

            result = HostedGrimoireProducerInventory.DiscoverProducerSites(
                [library, caller], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], [], admittedStateObserver: observations.Enqueue);

            states = [.. observations];
        }
        else
        {
            (_, result, states) = ClosedValueDiscover(body, helper);
        }

        HostedProducerAdmittedStateObservation[] callbacks = [.. states.Where(static state => state.Method.StartsWith("M:NestedFactory", StringComparison.Ordinal)
            && state.Method.EndsWith(".", StringComparison.Ordinal))];

        Assert.Equal(["HOSTED_SITE_EFFECT_FRONTIER_MISSING", "HOSTED_SITE_WORK_FRONTIER_MISSING"], result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal));

        Assert.All(result.Diagnostics, static diagnostic => Assert.Equal("System.IO.Stream.Dispose", diagnostic.Detail));

        Assert.Equal(expected, callbacks.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LexicalAncestorProbe_AnOwnerFromAnotherCompilationStaysConservative(bool retargetedGeneric)
    {
        CSharpCompilation origin = Compile("public sealed class Foreign<T> { public static void Own(T binding) { } }")
            .WithAssemblyName("ForeignBindings");

        CSharpCompilation target = Compile("public static class Target { public static int Run() { System.Func<int> callback = () => 0; return callback(); } }")
            .AddReferences(origin.ToMetadataReference());

        Assert.Empty(target.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        INamedTypeSymbol foreign = Assert.IsAssignableFrom<INamedTypeSymbol>((retargetedGeneric ? target : origin).GetTypeByMetadataName("Foreign`1"));

        if (retargetedGeneric)
        {
            foreign = foreign.Construct(target.GetSpecialType(SpecialType.System_Object));
        }

        IParameterSymbol parameter = Assert.Single(Assert.Single(foreign.GetMembers("Own").OfType<IMethodSymbol>()).Parameters);

        SyntaxNode declaration = target.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Single();

        (bool seed, bool mayContain) = HostedGrimoireProducerInventory.ProbeAdmissionBinding(target, declaration, parameter);

        Assert.True(seed);

        Assert.True(mayContain);
    }

    [Theory]
    [InlineData("ref")]
    [InlineData("out")]
    [InlineData("in")]
    public void LexicalAncestorProbe_ForeignByReferenceParametersRemainConservative(string modifier)
    {
        string source = "static class Owner { public static void Other(" + modifier + " object foreign) { "
            + (modifier == "out" ? "foreign = null!;" : "")
            + " } public static int Run() { System.Func<int> callback = () => 0; return callback(); } }";

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxNode root = compilation.SyntaxTrees.Single().GetRoot();

        SyntaxNode declaration = root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Single();

        ParameterSyntax parameter = root.DescendantNodes().OfType<ParameterSyntax>().Single();

        (bool seed, bool mayContain) = HostedGrimoireProducerInventory.ProbeAdmissionBinding(compilation, declaration, parameter);

        Assert.True(seed);

        Assert.True(mayContain);
    }

    // The lease an accessor or a constructor is entered with is a name only in the method that passed it, so what the
    // traversal concludes about a callback declared in the accessor does not depend on whether such a lease was passed.
    private static string[] ForeignLeaseVerdict(string members, string call)
    {
        const string lease = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease";

        string helper = members + " public static class Runner { public static int ViaLease(" + lease + " lease) { " + call + " } public static int Bare() { " + call + " } }";

        string[] verdicts = [];

        foreach (string entry in new[] { "Runner.ViaLease(lease);", "Runner.Bare();" })
        {
            var (_, result, _) = ClosedValueDiscover(R2Admission + entry, helper);

            string[] verdict =
            [
                .. result.Diagnostics.Select(static diagnostic => diagnostic.Code + " " + diagnostic.Detail),
                .. result.Items.Where(static site => site.Callee is "System.IO.File.Delete").Select(static site => "send " + site.Member + " work=" + (site.WorkFrontierCapsuleId is not null) + " effect=" + (site.EffectFrontierCapsuleId is not null)),
            ];

            if (verdicts.Length == 0)
            {
                verdicts = [.. verdict.Order(StringComparer.Ordinal)];
            }
            else
            {
                Assert.Equal(verdicts, verdict.Order(StringComparer.Ordinal));
            }
        }

        return verdicts;
    }

    [Theory]
    [InlineData("public sealed class Probe { public int Value { get { System.Array.ForEach(new int[1], x => { }); return 1; } } }", "return new Probe().Value;", false)]
    [InlineData("public sealed class Probe { public int Value { get { System.Array.ForEach(new int[1], x => System.IO.File.Delete(\"p\")); return 1; } } }", "return new Probe().Value;", true)]
    [InlineData("public sealed class Probe { public Probe() { System.Array.ForEach(new int[1], x => { }); } }", "_ = new Probe(); return 1;", false)]
    [InlineData("public sealed class Probe { public Probe() { System.Array.ForEach(new int[1], x => System.IO.File.Delete(\"p\")); } }", "_ = new Probe(); return 1;", true)]
    [InlineData("public sealed class Probe { public int Value { get { var lazy = new System.Lazy<int>(() => 1); return lazy.Value; } } }", "return new Probe().Value;", false)]
    public void AForeignLeaseChangesNothingAMemberThatCannotNameItConcludesAboutACallback(string members, string call, bool callbackRefused)
    {
        string[] verdict = ForeignLeaseVerdict(members, call);

        // A callback with a producer site in it is refused as an opaque callback, in both entries and whether or not a lease was passed.
        Assert.Equal(callbackRefused, verdict.Any(static line => line.StartsWith("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", StringComparison.Ordinal)));
    }

    // ---- Inert Payload Parameter and a payload that is stored and then read through the field.
    //
    // A parameter that is only stored in a field of this is moved, but the field is read by the members of the type, and a
    // member that disposes or dispatches on it decides something from the payload's type. The traversal resolves such a
    // field from the value source the caller bound to it, never from the concrete type bound to the parameter, and a field
    // of a type-parameter type resolves to nothing for any payload. These fixtures pin that this holds, so that a later
    // improvement to the typing of the field cannot start to use a binding the rule dropped without these failing first.
    private const string StoredPayloadHelper = """
        public interface IRes : System.IDisposable { }

        public sealed class QuietRes : IRes { public void Dispose() { } }

        public sealed class LoudRes : IRes { public void Dispose() { System.IO.File.Delete("loud"); } }

        public interface IRun { void Go(); }

        public sealed class QuietRun : IRun { public void Go() { } }

        public sealed class LoudRun : IRun { public void Go() { System.IO.File.Delete("loud"); } }

        public sealed class DisposeNow<T> where T : System.IDisposable
        {
            private readonly T _value;

            public DisposeNow(T value) { _value = value; _value.Dispose(); }
        }

        public sealed class DisposeLater<T> where T : System.IDisposable
        {
            private readonly T _value;

            public DisposeLater(T value) { _value = value; }

            public void Release() { _value.Dispose(); }
        }

        public sealed class Holder<T> : System.IDisposable where T : System.IDisposable
        {
            private readonly T _value;

            public Holder(T value) { _value = value; }

            public void Dispose() { _value.Dispose(); }
        }

        public sealed class RunNow<T> where T : IRun
        {
            private readonly T _value;

            public RunNow(T value) { _value = value; _value.Go(); }
        }

        public sealed class RunLater<T> where T : IRun
        {
            private readonly T _value;

            public RunLater(T value) { _value = value; }

            public void Go() { _value.Go(); }
        }
        """;

    [Theory]
    [InlineData("_ = new DisposeNow<QuietRes>(new QuietRes());", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("_ = new DisposeNow<LoudRes>(new LoudRes());", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("_ = new DisposeNow<QuietRes>(new QuietRes()); _ = new DisposeNow<LoudRes>(new LoudRes());", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("new DisposeLater<QuietRes>(new QuietRes()).Release();", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("new DisposeLater<LoudRes>(new LoudRes()).Release();", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("new DisposeLater<QuietRes>(new QuietRes()).Release(); new DisposeLater<LoudRes>(new LoudRes()).Release();", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("using (var holder = new Holder<QuietRes>(new QuietRes())) { }", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("using (var holder = new Holder<LoudRes>(new LoudRes())) { }", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("using (var quiet = new Holder<QuietRes>(new QuietRes())) { } using (var loud = new Holder<LoudRes>(new LoudRes())) { }", "HOSTED_DISPOSAL_TARGET_UNRESOLVED")]
    [InlineData("_ = new RunNow<QuietRun>(new QuietRun());", "HOSTED_CALL_TARGET_UNRESOLVED")]
    [InlineData("_ = new RunNow<LoudRun>(new LoudRun());", "HOSTED_CALL_TARGET_UNRESOLVED")]
    [InlineData("new RunLater<QuietRun>(new QuietRun()).Go();", "HOSTED_CALL_TARGET_UNRESOLVED")]
    [InlineData("new RunLater<LoudRun>(new LoudRun()).Go();", "HOSTED_CALL_TARGET_UNRESOLVED")]
    public void APayloadStoredInAFieldAndThenDisposedOrDispatchedOnStaysRefusedForEveryType(string body, string code)
    {
        var (_, result, _) = ClosedValueDiscover(body, StoredPayloadHelper);

        // Each payload type alone is refused, so the verdict never depends on the type and no type can be the one that is accepted.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    // ---- Verdict twins. Each rule keeps a binding when a verdict depends on it, so each pairs the state count with a send
    // that is reached, or not, because of the value the binding holds.
    private static string[] CanonicalizationSendVerdict(HostedProducerDiscovery<HostedProducerSite> result, string callee) =>
    [
        .. result.Items.Where(site => site.Callee == callee).Select(static site => site.EnclosingType + "." + site.Member + " work=" + (site.WorkFrontierCapsuleId is not null) + " effect=" + (site.EffectFrontierCapsuleId is not null)).Distinct().Order(StringComparer.Ordinal),
        .. result.Diagnostics.Select(static diagnostic => diagnostic.Code).Distinct().Order(StringComparer.Ordinal),
    ];

    private const string ReachHelper = """
        public static class Reach
        {
            public static void Send(bool enabled)
            {
                if (!enabled)
                {
                    return;
                }

                System.IO.File.Delete("reach");
            }
        }
        """;

    private static string ReachSites(string argument, int count) =>
        string.Concat(Enumerable.Repeat("Reach.Send(" + argument + "); ", count));

    [Fact]
    public void ConstantsOfDifferentValuesReachDifferentSends()
    {
        const string send = "System.IO.File.Delete";

        var (_, disabled, disabledStates) = ClosedValueDiscover(R2Admission + ReachSites("false", 10), ReachHelper);

        var (_, mixed, mixedStates) = ClosedValueDiscover(R2Admission + ReachSites("false", 10) + ReachSites("true", 1), ReachHelper);

        var (_, enabled, enabledStates) = ClosedValueDiscover(R2Admission + ReachSites("true", 10), ReachHelper);

        // Ten sites that pass false reach no send, and ten that pass true reach it once: the value the constant holds decides it.
        Assert.Equal([], CanonicalizationSendVerdict(disabled, send));

        Assert.Equal(["Reach.Send work=True effect=True"], CanonicalizationSendVerdict(enabled, send));

        // One site that passes true among ten that pass false still reaches the send, so the two values are two states.
        Assert.Equal(CanonicalizationSendVerdict(enabled, send), CanonicalizationSendVerdict(mixed, send));

        Assert.Equal(1, disabledStates.Count(static state => state.Method.Contains("Reach.Send", StringComparison.Ordinal) && state.Lifecycle));

        Assert.Equal(1, enabledStates.Count(static state => state.Method.Contains("Reach.Send", StringComparison.Ordinal) && state.Lifecycle));

        Assert.Equal(2, mixedStates.Count(static state => state.Method.Contains("Reach.Send", StringComparison.Ordinal) && state.Lifecycle));
    }

    // A lambda reads the flag of the method it is declared in, so a flag that is false reaches no send and a flag that is true reaches one.
    private const string NestedGateHelper = """
        public static class Nest
        {
            public static int Run(bool flag) => Invoke(() => { if (flag) { System.IO.File.Delete("nest"); } return 1; });

            public static int Invoke(System.Func<int> work) => work();

            public static int Off() => Run(false);

            public static int On() => Run(true);
        }
        """;

    [Fact]
    public void ALambdaThatReadsAFlagOfItsOwnerReachesTheSendOnlyForTheFlagThatSetsIt()
    {
        const string send = "System.IO.File.Delete";

        var (_, off, _) = ClosedValueDiscover(R2Admission + "_ = Nest.Off();", NestedGateHelper);

        var (_, both, _) = ClosedValueDiscover(R2Admission + "_ = Nest.Off(); _ = Nest.On();", NestedGateHelper);

        Assert.Empty(off.Diagnostics);

        Assert.DoesNotContain(off.Items, static site => site.Callee == send);

        // The lambda that is reached with true is not the state of the one reached with false, so the send is found.
        Assert.Empty(both.Diagnostics);

        HostedProducerSite reached = Assert.Single(both.Items, static site => site.Callee == send);

        Assert.NotNull(reached.WorkFrontierCapsuleId);

        Assert.NotNull(reached.EffectFrontierCapsuleId);
    }

    // An accessor that holds a send reaches it whatever flags the methods above it were called with, because it cannot read them.
    private const string AccessorSendHelper = """
        public sealed class Handles
        {
            private readonly System.IO.FileStream _stream;

            private Handles(System.IO.FileStream stream) { _stream = stream; }

            public static Handles Open(string path) => new(new System.IO.FileStream(path, System.IO.FileMode.Open));

            private System.IO.FileStream Stream { get { System.IO.File.Delete("accessor"); return _stream; } }

            public int Child(bool create, bool writable) { _ = Stream; return create ? 1 : writable ? 2 : 3; }

            public int A() => Child(false, false);

            public int B() => Child(true, false);

            public int C() => Child(false, true);
        }
        """;

    [Fact]
    public void AnAccessorReachesTheSameSendWhicheverCallersAboveItAreVisited()
    {
        const string send = "System.IO.File.Delete";

        var (_, one, _) = ClosedValueDiscover(R2Admission + "var h = Handles.Open(\"p\"); _ = h.A();", AccessorSendHelper);

        var (_, three, _) = ClosedValueDiscover(R2Admission + "var h = Handles.Open(\"p\"); _ = h.A(); _ = h.B(); _ = h.C();", AccessorSendHelper);

        string[] verdict = CanonicalizationSendVerdict(three, send);

        Assert.Equal(["Handles.get_Stream work=True effect=True"], verdict);

        Assert.Equal(CanonicalizationSendVerdict(one, send), verdict);
    }

    // A helper that links a source to a token, with the token supplied by a different call chain at each site.
    private const string TokenSendHelper = """
        public static class Linker
        {
            public static void Link(System.Threading.CancellationToken token)
            {
                System.IO.File.Delete("link");

                using System.Threading.CancellationTokenSource linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token);
            }
        }

        internal static class Handed
        {
            internal static System.Threading.CancellationToken Token { get; set; }

            internal static System.Threading.CancellationToken Other => System.Threading.CancellationToken.None;
        }
        """;

    [Fact]
    public void AHelperThatIsHandedATokenFromManyChainsReachesTheSendOnceAndAsItDoesFromOne()
    {
        const string send = "System.IO.File.Delete";

        var (_, one, _) = ClosedValueDiscover(R2Admission + "Linker.Link(Handed.Token);", TokenSendHelper);

        var (_, many, manyStates) = ClosedValueDiscover(R2Admission + "Linker.Link(Handed.Token); Linker.Link(Handed.Other); Linker.Link(default); Linker.Link(System.Threading.CancellationToken.None); Linker.Link(new System.Threading.CancellationTokenSource().Token);", TokenSendHelper);

        Assert.Equal(["Linker.Link work=True effect=True"], CanonicalizationSendVerdict(many, send));

        Assert.Equal(CanonicalizationSendVerdict(one, send), CanonicalizationSendVerdict(many, send));

        Assert.Equal(1, manyStates.Count(static state => state.Method.Contains("Linker.Link", StringComparison.Ordinal) && state.Lifecycle));
    }

    private const string PayloadSendHelper = """
        public sealed class QuietPayload { }

        public sealed class LoudPayload { public override string ToString() { System.IO.File.Delete("loud"); return "loud"; } }

        public sealed class Box<T>
        {
            private readonly T _value;

            private Box(T value) { _value = value; }

            public static Box<T> Make(T value) => new(value);
        }

        public static class Describe
        {
            public static string Text<T>(T value) => value!.ToString()!;
        }
        """;

    [Fact]
    public void APayloadThatIsReadReachesTheSendOfItsTypeAndAPayloadThatIsMovedReachesNone()
    {
        const string send = "System.IO.File.Delete";

        var (_, read, readStates) = ClosedValueDiscover(R2Admission + "_ = Describe.Text(new QuietPayload()); _ = Describe.Text(new LoudPayload());", PayloadSendHelper);

        var (_, moved, movedStates) = ClosedValueDiscover(R2Admission + "_ = Box<QuietPayload>.Make(new QuietPayload()); _ = Box<LoudPayload>.Make(new LoudPayload());", PayloadSendHelper);

        // The receiver reads the type, so the loud payload's override is found, and the two types are two states.
        Assert.Equal(["LoudPayload.ToString work=True effect=True"], CanonicalizationSendVerdict(read, send));

        Assert.Equal(2, readStates.Count(static state => state.Method.Contains("Describe.Text", StringComparison.Ordinal) && state.Lifecycle));

        // Moved into a field and never read, no override runs, and the two types are one state.
        Assert.Equal([], CanonicalizationSendVerdict(moved, send));

        Assert.Equal(1, movedStates.Count(static state => state.Method.Contains("Box`1.Make", StringComparison.Ordinal) && state.Lifecycle));
    }

    // Constant Value Identity merges the states of a callee that is reached with the same constant from different callers, and
    // the environment keeps only the first caller. A walk that enumerates the sources a member's value bindings hold, to follow
    // them to their callers, would read a caller that the state does not name, so it has to skip a constant. The scan reads the
    // compiled checker, so a foreach, a .Values read, a LINQ call, a copy, an alias and a lambda are one thing to it: every method
    // that enumerates the value-binding dictionary and reads a source's caller either calls the guard or is one of the reviewed readers
    // below, each of which only names a source (an identity) or compares or copies sources whole. A method that enumerates the dictionary
    // and never reads a caller cannot follow a source to it. A method reads a caller when it hands a source, a pair, a collection of
    // sources or the dictionary to a checker method that reads one, so a walk that follows each source to its caller in a helper is found
    // too. The scan follows calls and not values, so a method that copies the dictionary and, separately, hands a source it looked up by
    // key (or was given) to a comparing helper is listed as well; its row says so.
    private const string ConstantGuard = "TryConstantValueIdentity";

    private static readonly (string Method, string Reason)[] ReviewedValueBindingReaders =
    [
        ("BindDeclaredRootFactoryContext", "copies the entries into the factory's context whole, each with its own source and caller"),
        ("BindDemandedReceiverReadonlyFieldContext", "rebinds the demanded entries whole, each with its own source and caller"),
        ("BindExactConstructedReceiverPropertyContext", "copies the entries whole into a new dictionary and adds the property values the exact construction proves; the one source it hands to a reading helper is the exact receiver it was given, not an entry of the dictionary"),
        ("CleanupBindingIdentity", "names each source by its caller's identity in a cache key; a constant stays distinct by caller, which is never fewer states"),
        ("EvaluationEnvironmentLocalBindingIdentity", "names each source by its caller's identity in a state identity; a constant stays distinct by caller, which is never fewer states"),
        ("HasReviewedValueConversions", "copies the captured entries and reviews the conversion of the one source bound to the parameter it is asked about"),
    ];

    private static bool IsTheChecker(Type type) =>
        (type.FullName ?? "").StartsWith("RetroDownfall.Arcanum.Tests.Support.HostedGrimoireProducerInventory", StringComparison.Ordinal);

    [Fact]
    public void EveryEnumerationOfAMembersValueBindingsIsGuardedAgainstAConstant()
    {
        IReadOnlyList<ValueBindingEnumerationScan.Enumeration> enumerations = ValueBindingEnumerationScan.Enumerations(typeof(HostedGrimoireProducerInventory).Assembly, IsTheChecker, ConstantGuard);

        // Not vacuous: the checker enumerates its bindings in many places, and the walk that follows a source to its caller is guarded.
        Assert.NotEmpty(enumerations);

        Assert.Contains(enumerations, static item => item.Guarded && item.ReadsCaller && item.Method.EndsWith(".EvaluationEnvironmentIdentity", StringComparison.Ordinal));

        string[] unreviewed = [.. enumerations.Where(static item => item.ReadsCaller && !item.Guarded)
            .Select(static item => item.Method[(item.Method.LastIndexOf('.') + 1)..])
            .Except(ReviewedValueBindingReaders.Select(static reader => reader.Method), StringComparer.Ordinal)];

        Assert.Empty(unreviewed);

        // A reviewed reader that no longer enumerates, or that is now guarded, is a stale row.
        Assert.Equal(
            ReviewedValueBindingReaders.Select(static reader => reader.Method).Order(StringComparer.Ordinal),
            enumerations.Where(static item => item.ReadsCaller && !item.Guarded).Select(static item => item.Method[(item.Method.LastIndexOf('.') + 1)..]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    // The twin: methods of the compiled test assembly that enumerate a dictionary of bound sources in every spelling, with the guard and without.
    [Fact]
    public void TheValueBindingScanSeesEveryWayOfEnumeratingTheBindingsAndOnlyThose()
    {
        IReadOnlyList<ValueBindingEnumerationScan.Enumeration> enumerations = ValueBindingEnumerationScan.Enumerations(
            typeof(BindingsProbe).Assembly, static type => type == typeof(BindingsProbe) || type.DeclaringType == typeof(BindingsProbe), "Guard");

        static string Short(ValueBindingEnumerationScan.Enumeration item) => item.Method[(item.Method.LastIndexOf('.') + 1)..];

        string[] unguardedReaders = [.. enumerations.Where(static item => item.ReadsCaller && !item.Guarded).Select(Short).Order(StringComparer.Ordinal)];

        Assert.Equal(
            [
                "ByAlias",
                "ByCopiedKeys",
                "ByCopyAndForeach",
                "ByDeconstruction",
                "ByForeach",
                "ByKeysAndTryGetValue",
                "ByLambdaInLinq",
                "ByValues",
                "EnumerateAndDelegate",
                "EnumerateAndDelegateAMethodGroup",
                "EnumerateAndDelegateInALambda",
                "EnumerateAndDelegatePairs",
                "EnumerateAndDelegateThroughTwoHelpers",
            ],
            unguardedReaders);

        // The guarded walk is found and guarded, in every spelling; the walk that never reads a caller and the point lookup are not readers at all.
        Assert.Contains(enumerations, static item => Short(item) == "Guarded" && item.Guarded && item.ReadsCaller);

        Assert.Contains(enumerations, static item => Short(item) == "GuardedDelegation" && item.Guarded && item.ReadsCaller);

        Assert.Contains(enumerations, static item => Short(item) == "CopyOnly" && !item.ReadsCaller);

        // A walk that hands each source to a helper that never reads a caller cannot follow a source to one.
        Assert.Contains(enumerations, static item => Short(item) == "EnumerateAndDelegateWithoutReading" && !item.ReadsCaller);

        Assert.DoesNotContain(enumerations, static item => Short(item) == "LookupOnly");

        // The keys alone are no walk over the sources, and neither is a helper that reads a caller of the source it is handed.
        Assert.DoesNotContain(enumerations, static item => Short(item) is "KeysOnly" or "ReadsTheCaller" or "ReadsTheCallerOfThePair" or "ReadsTheCallerThroughAnother");
    }

    internal sealed record BoundValueSource(string Caller);

    internal static class BindingsProbe
    {
        internal static void Guard() { }

        internal static int ByForeach(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            int total = 0;

            foreach (KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> pair in bindings)
            {
                total += pair.Value.Caller.Length;
            }

            return total;
        }

        internal static int ByDeconstruction(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            int total = 0;

            foreach ((Microsoft.CodeAnalysis.ISymbol _, BoundValueSource source) in bindings)
            {
                source.Deconstruct(out string caller);

                total += caller.Length;
            }

            return total;
        }

        internal static int ByValues(Dictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings) => bindings.Values.Sum(static source => source.Caller.Length);

        internal static IEnumerable<string> ByLambdaInLinq(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings) => bindings.Select(static pair => pair.Value.Caller);

        internal static int ByAlias(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            IEnumerable<KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource>> alias = bindings;

            return alias.Count(static pair => pair.Value.Caller.Length > 0);
        }

        internal static int ByCopyAndForeach(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            Dictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> copy = new(bindings, Microsoft.CodeAnalysis.SymbolEqualityComparer.Default);

            int total = 0;

            foreach (BoundValueSource source in copy.Values)
            {
                total += source.Caller.Length;
            }

            return total;
        }

        internal static int Guarded(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            Guard();

            return bindings.Values.Sum(static source => source.Caller.Length);
        }

        internal static Dictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> CopyOnly(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings) =>
            bindings.ToDictionary(static pair => pair.Key, static pair => pair.Value, Microsoft.CodeAnalysis.SymbolEqualityComparer.Default);

        internal static int LookupOnly(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings, Microsoft.CodeAnalysis.ISymbol key) =>
            bindings.TryGetValue(key, out BoundValueSource? source) ? source.Caller.Length : bindings.ContainsKey(key) ? 1 : 0;

        // Helpers a walk hands its sources to: one reads the caller, one reads it through another helper, one never does.
        internal static int ReadsTheCaller(BoundValueSource source) => source.Caller.Length;

        internal static int ReadsTheCallerOfThePair(KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> pair) => pair.Value.Caller.Length;

        internal static int ReadsTheCallerThroughAnother(BoundValueSource source) => ReadsTheCaller(source);

        internal static int NamesTheSource(BoundValueSource source) => source.GetHashCode();

        internal static int EnumerateAndDelegate(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            int total = 0;

            foreach (KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> pair in bindings)
            {
                total += ReadsTheCaller(pair.Value);
            }

            return total;
        }

        internal static int EnumerateAndDelegatePairs(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            int total = 0;

            foreach (KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> pair in bindings)
            {
                total += ReadsTheCallerOfThePair(pair);
            }

            return total;
        }

        internal static int EnumerateAndDelegateThroughTwoHelpers(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            int total = 0;

            foreach (KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> pair in bindings)
            {
                total += ReadsTheCallerThroughAnother(pair.Value);
            }

            return total;
        }

        internal static int EnumerateAndDelegateInALambda(Dictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings) => bindings.Values.Sum(static source => ReadsTheCaller(source));

        internal static IEnumerable<int> EnumerateAndDelegateAMethodGroup(Dictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings) => bindings.Values.Select(ReadsTheCaller);

        internal static int EnumerateAndDelegateWithoutReading(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            int total = 0;

            foreach (KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> pair in bindings)
            {
                total += NamesTheSource(pair.Value);
            }

            return total;
        }

        internal static int GuardedDelegation(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            Guard();

            int total = 0;

            foreach (KeyValuePair<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> pair in bindings)
            {
                total += ReadsTheCaller(pair.Value);
            }

            return total;
        }

        // The keys copied out and every source then read through a lookup is a walk over the sources like any other.
        internal static int ByCopiedKeys(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            Microsoft.CodeAnalysis.ISymbol[] keys = [.. bindings.Keys];

            int total = 0;

            foreach (Microsoft.CodeAnalysis.ISymbol key in keys)
            {
                total += bindings[key].Caller.Length;
            }

            return total;
        }

        internal static int ByKeysAndTryGetValue(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings)
        {
            int total = 0;

            foreach (Microsoft.CodeAnalysis.ISymbol key in bindings.Keys.ToList())
            {
                if (bindings.TryGetValue(key, out BoundValueSource? source))
                {
                    total += source.Caller.Length;
                }
            }

            return total;
        }

        internal static int KeysOnly(IReadOnlyDictionary<Microsoft.CodeAnalysis.ISymbol, BoundValueSource> bindings) => bindings.Keys.Count();
    }
}

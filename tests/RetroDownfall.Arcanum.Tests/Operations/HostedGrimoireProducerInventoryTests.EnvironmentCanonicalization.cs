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

    // ---- Constant Value Identity and the proof that an inherited group belongs to the owner that opened it.
    //
    // A per-unit recovery handler opens a physical group in the member that owns it, and a helper that member calls
    // inherits that group through the frontier alone. The proof that the helper's group is the retained recovery lease's reads
    // the helper's source edges to find the member that holds the group. A constant is no such edge: it carries no instance,
    // and a state that names it by value is reached from every caller that passes it.
    private static string PerUnitInheritedGroupFixture(bool sourced, bool ownerFirst)
    {
        string source = PerUnitRecoveryMatrixFixture("same-lease");

        void ReplaceOne(string original, string replacement)
        {
            Assert.Equal(1, source.Split(original, StringSplitOptions.None).Length - 1);

            source = source.Replace(original, replacement, StringComparison.Ordinal);
        }

        // A list is a source whose provenance a callee demands, so it is bound by where it was created and is a real edge.
        string argument = sourced ? "new System.Collections.Generic.List<int>()" : "true";

        string parameter = sourced ? "System.Collections.Generic.List<int> units" : "bool enabled";

        string guard = sourced ? "if (units.Count < 0) return; " : "if (!enabled) return; ";

        string calls = ownerFirst
            ? "await DeleteAsync(" + argument + "); await ForwardAsync();"
            : "await ForwardAsync(); await DeleteAsync(" + argument + ");";

        // The owner opens the group and calls the helper itself and through a method that holds no group and no lease.
        ReplaceOne("System.IO.File.Delete(\"per-unit-first\");", calls);

        ReplaceOne("private sealed class WrongGroup : IDisposable",
            "private async Task ForwardAsync() { await DeleteAsync(" + argument + "); } "
            + "private async Task DeleteAsync(" + parameter + ") { await Task.CompletedTask; " + guard + "System.IO.File.Delete(\"per-unit-first\"); } "
            + "private sealed class WrongGroup : IDisposable");

        return source;
    }

    private static (HostedProducerDiscovery<HostedProducerSite> Result, HostedProducerAdmittedStateObservation[] Helper) PerUnitInheritedGroupDiscover(bool sourced, bool ownerFirst)
    {
        CSharpCompilation compilation = Compile(PerUnitInheritedGroupFixture(sourced, ownerFirst));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        System.Collections.Concurrent.ConcurrentQueue<HostedProducerAdmittedStateObservation> observed = new();

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture([compilation], admittedStateObserver: observed.Enqueue);

        return (result, [.. observed.Where(static state => state.Method.Contains(".PerUnitRecoveryHandler.DeleteAsync(", StringComparison.Ordinal))]);
    }

    // What the traversal concluded about the helper's send, apart from which call path it was reported under.
    private static string[] PerUnitInheritedGroupVerdict(HostedProducerDiscovery<HostedProducerSite> result) =>
    [
        .. result.Items
            .Where(static site => site.Member == "DeleteAsync" && site.Callee == "System.IO.File.Delete")
            .Select(static site => "send work=" + (site.WorkFrontierCapsuleId is not null) + " effect=" + (site.EffectFrontierCapsuleId is not null)),
        .. result.Diagnostics.Select(static diagnostic => diagnostic.Code),
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHelperReachedOnlyWithAConstantHasNoSourceEdgeToTheOwnerOfItsGroup(bool ownerFirst)
    {
        var (result, helper) = PerUnitInheritedGroupDiscover(sourced: false, ownerFirst);

        // The owner's group reaches the helper through the frontier, and nothing but a constant joins the helper to the owner.
        HostedProducerSite send = Assert.Single(result.Items, static site => site.Member == "DeleteAsync" && site.Callee == "System.IO.File.Delete");

        Assert.NotNull(send.WorkFrontierCapsuleId);

        Assert.Null(send.EffectFrontierCapsuleId);

        Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN");

        // The constant is still one state for the two callers: the proof stopped reading it, the identity did not stop naming it by value.
        Assert.Single(helper.Select(static state => state.EvaluationEnvironmentToken).Distinct());
    }

    [Fact]
    public void TheVerdictOnAConstantDoesNotDependOnWhichCallerReachesTheHelperFirst()
    {
        string[] ownerFirst = PerUnitInheritedGroupVerdict(PerUnitInheritedGroupDiscover(sourced: false, ownerFirst: true).Result);

        string[] forwardFirst = PerUnitInheritedGroupVerdict(PerUnitInheritedGroupDiscover(sourced: false, ownerFirst: false).Result);

        Assert.Equal(forwardFirst.Order(StringComparer.Ordinal), ownerFirst.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASourceThatCarriesAnInstanceStillReachesTheOwnerOfItsGroup(bool ownerFirst)
    {
        var (result, helper) = PerUnitInheritedGroupDiscover(sourced: true, ownerFirst);

        HostedProducerSite[] sends = [.. result.Items.Where(static site => site.Member == "DeleteAsync" && site.Callee == "System.IO.File.Delete")];

        // The list the owner created is a real edge to it, so the owner's own call is proved. The call made through the method
        // that holds no group has no edge to the owner and is refused: the two are two states, each with the verdict it earned.
        Assert.Contains(sends, static site => site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is not null);

        Assert.Contains(sends, static site => site.WorkFrontierCapsuleId is not null && site.EffectFrontierCapsuleId is null);

        Assert.Single(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_RECOVERY_PER_UNIT_HANDOFF_UNPROVEN");

        Assert.True(helper.Select(static state => state.EvaluationEnvironmentToken).Distinct().Count() >= 2, "Each source keeps a state of its own.");
    }

    [Fact]
    public void TheVerdictOnASourceDoesNotDependOnWhichCallerReachesTheHelperFirstEither()
    {
        string[] ownerFirst = PerUnitInheritedGroupVerdict(PerUnitInheritedGroupDiscover(sourced: true, ownerFirst: true).Result);

        string[] forwardFirst = PerUnitInheritedGroupVerdict(PerUnitInheritedGroupDiscover(sourced: true, ownerFirst: false).Result);

        Assert.Equal(forwardFirst.Order(StringComparer.Ordinal), ownerFirst.Order(StringComparer.Ordinal));
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
        ("BindPerUnitReconcilerEntryContext", "copies the entries whole into a new dictionary and adds one entry; it compares the one source it looks up by key with the source the authenticated factory edge supplies, through the reviewed comparison, and refuses on any difference"),
        ("BindPerUnitRecoveryReceiverContext", "drops the protected entries from a copy and keeps the others whole; for the caller's constructor-parameter entries it compares each source with the generic bound's source for the same key through PerUnitSameBoundSource, which refuses unless the expression and the caller's identity are equal, and keeps the source whole"),
        ("CleanupBindingIdentity", "names each source by its caller's identity in a cache key; a constant stays distinct by caller, which is never fewer states"),
        ("EvaluationEnvironmentLocalBindingIdentity", "names each source by its caller's identity in a state identity; a constant stays distinct by caller, which is never fewer states"),
        ("HasReviewedValueConversions", "copies the captured entries and reviews the conversion of the one source bound to the parameter it is asked about"),
        ("PerUnitMaterializationValueCompatible", "compares two sources by caller identity and expression span and refuses on any difference"),
        ("PreservePerUnitReconcilerFieldContext", "keeps the field sources of a constructor context whole and refuses on any disagreement"),
        ("TryBindPerUnitRecoveryHandoff", "copies the entries whole into a new dictionary; each source it compares it looks up by key, through PerUnitSameBoundSource, which refuses unless the expression and the caller's identity are equal"),
    ];

    private static bool IsTheChecker(Type type) =>
        (type.FullName ?? "").StartsWith("RetroDownfall.Arcanum.Tests.Support.HostedGrimoireProducerInventory", StringComparison.Ordinal);

    [Fact]
    public void EveryEnumerationOfAMembersValueBindingsIsGuardedAgainstAConstant()
    {
        IReadOnlyList<ValueBindingEnumerationScan.Enumeration> enumerations = ValueBindingEnumerationScan.Enumerations(typeof(HostedGrimoireProducerInventory).Assembly, IsTheChecker, ConstantGuard);

        // Not vacuous: the checker enumerates its bindings in many places, and the walk that follows a source to its caller is guarded.
        Assert.NotEmpty(enumerations);

        Assert.Contains(enumerations, static item => item.Guarded && item.ReadsCaller && item.Method.EndsWith(".PerUnitPhysicalGroupSharesWork", StringComparison.Ordinal));

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

using System.Reflection;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The compile-time inventory: no seam may let a caller omit its authority classification.
/// </summary>
/// <remarks>
/// These assertions are the enforcement mechanism behind the whole invocation-authority design. The
/// property being protected is not "the parameter exists" but "the parameter cannot be skipped": an
/// optional parameter, a default argument, or a legacy overload would each restore exactly the
/// inference-by-omission this slice removed (§10.12).
/// </remarks>
public sealed class ArcanumInvocationContextInventoryTests
{

    public static TheoryData<Type, string> RequiredSeams() => new()
    {
        { typeof(IArcanumIntelligenceProvider), nameof(IArcanumIntelligenceProvider.ExecutePromptAsync) },
        { typeof(IArcanumIntelligenceProvider), nameof(IArcanumIntelligenceProvider.StreamPromptAsync) },
        { typeof(IContextPreviewService), nameof(IContextPreviewService.PreviewContextAsync) },
        { typeof(ITurnExecutionFacade), nameof(ITurnExecutionFacade.ExecuteBufferedAsync) },
        { typeof(ITurnExecutionFacade), nameof(ITurnExecutionFacade.ExecuteIntelligenceStreamAsync) },
        { typeof(ITurnExecutionFacade), nameof(ITurnExecutionFacade.ExecuteOpenAiSseAsync) },
    };

    [Theory]
    [MemberData(nameof(RequiredSeams))]
    public void EverySeam_RequiresAnInvocationContextWithNoDefault(Type seam, string method)
    {

        MethodInfo[] overloads = [.. seam.GetMethods().Where(candidate => candidate.Name == method)];

        _ = Assert.Single(overloads);

        ParameterInfo parameter = Assert.Single(
            overloads[0].GetParameters(),
            candidate => candidate.ParameterType == typeof(ArcanumInvocationContext));

        Assert.False(parameter.IsOptional, $"{seam.Name}.{method} makes its invocation context optional.");
        Assert.False(parameter.HasDefaultValue);

    }

    [Theory]
    [MemberData(nameof(RequiredSeams))]
    public void EverySeamImplementation_KeepsTheContextRequired(Type seam, string method)
    {

        IEnumerable<Type> implementations = new[]
        {
            typeof(IArcanumIntelligenceProvider).Assembly,
            typeof(ITurnExecutionFacade).Assembly,
            typeof(ArcanumInvocationContextInventoryTests).Assembly,
        }
        .Distinct()
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => !type.IsInterface && !type.IsAbstract && seam.IsAssignableFrom(type));

        foreach (Type implementation in implementations)
        {

            foreach (MethodInfo candidate in implementation
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name.EndsWith(method, StringComparison.Ordinal)))
            {

                ParameterInfo? parameter = Array.Find(
                    candidate.GetParameters(),
                    p => p.ParameterType == typeof(ArcanumInvocationContext));

                if (parameter is null)
                {
                    continue;
                }

                Assert.False(
                    parameter.IsOptional,
                    $"{implementation.FullName}.{candidate.Name} makes its invocation context optional.");

            }

        }

    }

    [Fact]
    public void NoSeamExposesALegacyOverloadWithoutAContext()
    {

        foreach ((Type seam, string method) in RequiredSeams().Select(row => ((Type)row[0], (string)row[1])))
        {

            foreach (MethodInfo candidate in seam.GetMethods().Where(candidate => candidate.Name == method))
            {

                Assert.Contains(
                    candidate.GetParameters(),
                    parameter => parameter.ParameterType == typeof(ArcanumInvocationContext));

            }

        }

    }

    [Fact]
    public void EveryProductionInferenceCaller_SelectsAnInvocationSurface()
    {

        string root = FindRepositoryRoot();

        List<string> unclassified = [];

        // Core, Infrastructure, and Api only. The CLI reaches inference over HTTP through
        // ArcanumApiClient, whose methods share these names but cross a process boundary where the
        // invocation context deliberately does not travel — it is server-owned.
        IEnumerable<string> files = InferenceProjects.SelectMany(project => Directory.EnumerateFiles(
            Path.Combine(root, "src", project),
            "*.cs",
            SearchOption.AllDirectories));

        foreach (string file in files)
        {

            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(file);

            foreach (string seam in InferenceSeams)
            {

                foreach (string arguments in CallArguments(source, "." + seam + "("))
                {

                    bool classified = arguments.Contains("ArcanumInvocationContext.None", StringComparison.Ordinal)
                        || arguments.Contains("ArcanumInvocationContexts.", StringComparison.Ordinal)
                        || arguments.Contains("invocationContext", StringComparison.Ordinal);

                    if (!classified)
                    {
                        unclassified.Add($"{Path.GetRelativePath(root, file)}: {seam}");
                    }

                }

            }

        }

        Assert.True(
            unclassified.Count == 0,
            "These production inference calls do not select an invocation surface:\n"
            + string.Join("\n", unclassified));

    }

    private static readonly string[] InferenceProjects =
    [
        "RetroDownfall.Arcanum.Core",
        "RetroDownfall.Arcanum.Infrastructure",
        "RetroDownfall.Arcanum.Api",
    ];

    /// <summary>
    /// The invocation seams whose names are unambiguous across the inference projects.
    /// </summary>
    /// <remarks>
    /// <c>ExecuteBufferedAsync</c> is deliberately absent. <c>IModelCallExecutor</c> declares a method
    /// of the same name for a single provider call inside a turn, which is a lower boundary that
    /// carries no authority of its own. Including it would make this inventory report every model call
    /// as an unclassified turn. The facade's own overload is still enforced, by the required parameter
    /// and by the reflection assertions above.
    /// </remarks>
    private static readonly string[] InferenceSeams =
    [
        "ExecutePromptAsync",
        "StreamPromptAsync",
        "PreviewContextAsync",
        "ExecuteIntelligenceStreamAsync",
        "ExecuteOpenAiSseAsync",
    ];

    /// <summary>
    /// The argument text of every call to <paramref name="callee"/>, with nesting and strings honoured.
    /// </summary>
    /// <remarks>
    /// A regular expression over a whole file would match the method declarations too, and would stop
    /// at the first comma inside a nested call. Scanning for the balanced closing parenthesis is the
    /// only way this inventory can distinguish a call that classifies its surface from one that merely
    /// mentions a similar name.
    /// </remarks>
    private static IEnumerable<string> CallArguments(string source, string callee)
    {

        int index = 0;

        while ((index = source.IndexOf(callee, index, StringComparison.Ordinal)) >= 0)
        {

            int start = index + callee.Length;

            int depth = 1;

            int cursor = start;

            bool inString = false;

            while (cursor < source.Length && depth > 0)
            {

                char value = source[cursor];

                if (inString)
                {
                    if (value == '\\')
                    {
                        cursor += 2;

                        continue;
                    }

                    if (value == '"')
                    {
                        inString = false;
                    }
                }
                else if (value == '"')
                {
                    inString = true;
                }
                else if (value == '(')
                {
                    depth++;
                }
                else if (value == ')')
                {
                    depth--;
                }

                cursor++;

            }

            // A declaration, not a call: its parameter list names the type rather than passing a value.
            string arguments = source[start..Math.Max(start, cursor - 1)];

            if (!arguments.Contains("PingRequest request", StringComparison.Ordinal)
                && !arguments.Contains("ContextPreviewRequest request", StringComparison.Ordinal))
            {
                yield return arguments;
            }

            index = cursor;

        }

    }

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

    [Fact]
    public void TurnExecutionRequest_CarriesTheContextAsARequiredMember()
    {

        Type request = typeof(ITurnExecutionFacade).Assembly
            .GetType("RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.TurnExecutionRequest")!;

        ConstructorInfo constructor = Assert.Single(request.GetConstructors());

        ParameterInfo parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => candidate.ParameterType == typeof(ArcanumInvocationContext));

        Assert.False(parameter.IsOptional);

        // Position matters only in that it precedes the response mode, so an argument list that
        // forgot it cannot silently bind the mode into the context slot.
        Assert.Equal(1, parameter.Position);

    }

}

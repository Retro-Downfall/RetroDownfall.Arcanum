using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Pins the rule behind <c>GrimoireDatabaseBootstrapper</c>'s lock-required overloads: no production call
/// site bootstraps the Grimoire without the installation maintenance lock. A null lock silently skips
/// topology recovery and Covenant authority preparation, which are exactly the steps that need exclusive
/// ownership, so the only caller allowed to pass none is the clearly named test seam.
/// </summary>
public sealed class GrimoireBootstrapLockCallSiteTests
{
    private const string BootstrapperFile = "GrimoireDatabaseBootstrapper.cs";

    private const string EntryPoint = "EnsureInitializedAsync";

    private const string TestSeam = "EnsureInitializedWithoutInstallationLockForTestsAsync";

    private const int LockArgumentIndex = 5;

    [Fact]
    public void No_src_call_site_bootstraps_the_grimoire_without_the_installation_lock()
    {
        string source = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src");

        List<string> violations = [];

        int productionCallSites = 0;

        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);

            if (relative.Split(Path.DirectorySeparatorChar).Any(static part => part is "bin" or "obj"))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            if (!text.Contains(EntryPoint, StringComparison.Ordinal)
                && !text.Contains(TestSeam, StringComparison.Ordinal))
            {
                continue;
            }

            CallSiteScan scan = Scan(Path.GetFileName(file), text);

            productionCallSites += scan.LockedCallSites;

            violations.AddRange(scan.Violations.Select(violation => $"{relative}: {violation}"));
        }

        Assert.Empty(violations);

        // Guards against a scan that matched nothing: the host's start-up and the CLI's exclusive operations.
        Assert.True(
            productionCallSites >= 2,
            $"Expected the host and CLI bootstrap call sites, found {productionCallSites}.");
    }

    /// <summary>
    /// The scan above reads call-site text, so a variable that holds null, or a path that reaches the shared
    /// implementation another way, would slip past it. The type is the stronger guarantee: with the lock
    /// non-nullable on every entry point the compiler refuses a possibly-null argument, and the one shape that
    /// accepts no lock is private to the bootstrapper.
    /// </summary>
    [Fact]
    public void Every_non_private_bootstrap_entry_point_requires_a_non_null_lock_by_type()
    {
        NullabilityInfoContext nullability = new();

        MethodInfo[] entryPoints =
        [
            .. typeof(GrimoireDatabaseBootstrapper)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static method => method.Name == EntryPoint && !method.IsPrivate),
        ];

        Assert.NotEmpty(entryPoints);

        foreach (MethodInfo method in entryPoints)
        {
            ParameterInfo lockParameter = Assert.Single(
                method.GetParameters(),
                static parameter => parameter.Name == "heldInstallationLock");

            Assert.True(
                nullability.Create(lockParameter).WriteState is NullabilityState.NotNull,
                $"{method} declares its installation lock as nullable.");
        }

        // The shared implementation that does accept a missing lock must not be reachable from the rest of src.
        MethodInfo[] nullableCores =
        [
            .. typeof(GrimoireDatabaseBootstrapper)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static method => method.GetParameters().Any(
                    static parameter => parameter.Name == "heldInstallationLock"
                        && new NullabilityInfoContext().Create(parameter).WriteState is NullabilityState.Nullable)),
        ];

        Assert.All(nullableCores, static method => Assert.True(method.IsPrivate, $"{method} accepts no lock but is not private."));
    }

    [Fact]
    public void The_scan_flags_a_null_lock_passed_by_name_or_by_position()
    {
        CallSiteScan named = Scan(
            "Caller.cs",
            """
            class Caller
            {
                void Run() => GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    secrets, passphrase, scopes, path, directory,
                    heldInstallationLock: null,
                    expectedInstallationId: null,
                    postRestoreTopology: null,
                    restoreDisclosureWriterAfterAuthenticatedTransition: false,
                    token);
            }
            """);

        _ = Assert.Single(named.Violations);

        CallSiteScan positional = Scan(
            "Caller.cs",
            """
            class Caller
            {
                void Run() => GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    secrets, passphrase, scopes, path, directory, null, null, token);
            }
            """);

        _ = Assert.Single(positional.Violations);

        CallSiteScan defaulted = Scan(
            "Caller.cs",
            """
            class Caller
            {
                void Run() => GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    secrets, passphrase, scopes, path, directory, default, null, token);
            }
            """);

        _ = Assert.Single(defaulted.Violations);
    }

    [Fact]
    public void The_scan_flags_a_call_that_has_no_lock_argument_at_all()
    {
        CallSiteScan scan = Scan(
            "Caller.cs",
            """
            class Caller
            {
                void Run() => GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    secrets, passphrase, scopes, token);
            }
            """);

        _ = Assert.Single(scan.Violations);
    }

    [Fact]
    public void The_scan_flags_any_src_use_of_the_test_seam_except_its_own_declaration()
    {
        CallSiteScan caller = Scan(
            "Caller.cs",
            """
            class Caller
            {
                void Run() => GrimoireDatabaseBootstrapper.EnsureInitializedWithoutInstallationLockForTestsAsync(
                    secrets, passphrase, scopes, path, directory, token);
            }
            """);

        _ = Assert.Single(caller.Violations);

        CallSiteScan seam = Scan(
            BootstrapperFile,
            """
            static class GrimoireDatabaseBootstrapper
            {
                internal static Task EnsureInitializedWithoutInstallationLockForTestsAsync(a, b) =>
                    EnsureInitializedAsync(
                        secrets, passphrase, scopes, path, directory,
                        heldInstallationLock: null,
                        expectedInstallationId: null,
                        postRestoreTopology: null,
                        false,
                        token);
            }
            """);

        Assert.Empty(seam.Violations);
    }

    [Fact]
    public void The_scan_accepts_a_call_that_passes_the_lock_it_holds()
    {
        CallSiteScan scan = Scan(
            "Caller.cs",
            """
            class Caller
            {
                void Run(ArcanumMaintenanceLock held) => GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                    secrets, passphrase, scopes, path, directory, held, null, null, false, token);
            }
            """);

        Assert.Empty(scan.Violations);

        Assert.Equal(1, scan.LockedCallSites);
    }

    private static CallSiteScan Scan(string fileName, string text)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(text).GetRoot();

        List<string> violations = [];

        int locked = 0;

        foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? name = InvokedName(invocation, out string? receiver);

            string enclosing = invocation.Ancestors()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault()?.Identifier.ValueText ?? string.Empty;

            if (name == TestSeam)
            {
                if (enclosing != TestSeam)
                {
                    violations.Add($"line {Line(invocation)} calls the test-only seam {TestSeam}.");
                }

                continue;
            }

            if (name != EntryPoint)
            {
                continue;
            }

            bool bootstrapper = fileName == BootstrapperFile
                || receiver is not null && receiver.EndsWith("GrimoireDatabaseBootstrapper", StringComparison.Ordinal);

            if (!bootstrapper || enclosing == TestSeam)
            {
                continue;
            }

            ArgumentSyntax? lockArgument = LockArgument(invocation);

            if (lockArgument is null)
            {
                violations.Add($"line {Line(invocation)} calls {EntryPoint} without an installation lock argument.");

                continue;
            }

            if (IsNullOrDefault(lockArgument.Expression))
            {
                violations.Add($"line {Line(invocation)} passes a null installation lock to {EntryPoint}.");

                continue;
            }

            // The bootstrapper's own delegators forward a lock they were given; only callers elsewhere count.
            if (fileName != BootstrapperFile)
            {
                locked++;
            }
        }

        return new CallSiteScan(violations, locked);
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation, out string? receiver)
    {
        receiver = null;

        switch (invocation.Expression)
        {
            case IdentifierNameSyntax identifier:
                return identifier.Identifier.ValueText;

            case MemberAccessExpressionSyntax member:
                receiver = member.Expression.ToString();

                return member.Name.Identifier.ValueText;

            default:
                return null;
        }
    }

    private static ArgumentSyntax? LockArgument(InvocationExpressionSyntax invocation)
    {
        SeparatedSyntaxList<ArgumentSyntax> arguments = invocation.ArgumentList.Arguments;

        ArgumentSyntax? named = arguments.FirstOrDefault(
            static argument => argument.NameColon?.Name.Identifier.ValueText == "heldInstallationLock");

        if (named is not null)
        {
            return named;
        }

        // The lock-carrying overloads take it sixth, after the path pair; a shorter call has none.
        return arguments.Count > LockArgumentIndex
            ? arguments[LockArgumentIndex]
            : null;
    }

    private static bool IsNullOrDefault(ExpressionSyntax expression) =>
        expression.Kind() is SyntaxKind.NullLiteralExpression
            or SyntaxKind.DefaultLiteralExpression
            or SyntaxKind.DefaultExpression;

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private sealed record CallSiteScan(IReadOnlyList<string> Violations, int LockedCallSites);
}

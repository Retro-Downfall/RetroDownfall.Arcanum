using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Source-level guard on the Keychain item references <c>MacOsCredentialStore</c> is handed.
/// </summary>
/// <remarks>
/// Security.framework writes a <b>retained</b> <c>SecKeychainItemRef</c> into the last out-parameter
/// of <c>SecKeychainFindGenericPassword</c> and <c>SecKeychainAddGenericPassword</c>, and Apple's
/// contract makes the caller responsible for releasing it. There is no GC integration behind these
/// raw <c>nint</c> handles and no <c>SafeHandle</c> wrapper, so a discarded reference is a CFType
/// retained for the life of the process — and <c>out _</c> is a call-site discard only: the
/// generated stub still passes the address of a real local, so the framework still writes into it.
/// <para>
/// An inventory assertion rather than a behavior test, because a retain count is not observable from
/// managed code: the only suite that reaches the real Keychain is opt-in behind
/// <c>ARCANUM_TEST_OS_CREDENTIAL_STORE</c> and asserts round-tripped values, so nothing in the suite
/// can see this class of leak. What can be seen is the discipline, and the add path was the one call
/// site in the file that did not follow it while the lookup paths beside it did.
/// </para>
/// <para>
/// The scanner reads the file's syntax tree rather than its text, so a release that is only written
/// in a comment or a string, a release that sits before the call it is meant to pair with, and a
/// release that lives on an error branch alone are all things it can tell apart from a release. The
/// earlier forms asked whether <c>CFRelease(itemRef)</c> appeared anywhere in the file, and then
/// anywhere in the method, and either answer let a leak on one path hide behind a release on another.
/// </para>
/// </remarks>
public sealed class MacOsCredentialStoreHandleOwnershipTests
{
    private const string SourceFileName = "MacOsCredentialStore.cs";

    private const string ReleaseFunction = "CFRelease";

    private const string SuccessResult = "OsCredentialStoreResult.Ok(";

    private static readonly string[] ItemRefFunctions =
    [
        "SecKeychainFindGenericPassword",
        "SecKeychainFindGenericPasswordMetadata",
        "SecKeychainAddGenericPassword",
        "TryGetItemRef",
    ];

    [Fact]
    public void Every_keychain_item_ref_is_released_in_the_method_that_receives_it()
    {
        IReadOnlyList<string> violations = UnreleasedItemRefs(MacOsCredentialStoreSource());

        Assert.Empty(violations);
    }

    /// <summary>
    /// A scanner that quietly stopped finding calls would report nothing and pass for the wrong reason.
    /// </summary>
    [Fact]
    public void The_scanner_inspects_every_call_that_receives_a_retained_reference()
    {
        string[] receiving =
        [
            .. ReceivingCalls(Parse(MacOsCredentialStoreSource()))
                .Select(static call => $"{call.MethodName} -> {call.Function}")
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "Delete -> TryGetItemRef",
                "ProbePresence -> SecKeychainFindGenericPasswordMetadata",
                "Set -> SecKeychainAddGenericPassword",
                "TryGet -> SecKeychainFindGenericPassword",
                "TryGetItemRef -> SecKeychainFindGenericPasswordMetadata",
                "UpdateExisting -> TryGetItemRef",
            ],
            receiving);
    }

    /// <summary>
    /// A native function that returns an item reference but is missing from the scanner's list would
    /// be a call site nobody inspects.
    /// </summary>
    [Fact]
    public void Every_native_function_that_returns_an_item_reference_is_one_the_scanner_knows()
    {
        string[] declared =
        [
            .. Parse(MacOsCredentialStoreSource())
                .DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Body is null)
                .Where(static method => method.ParameterList.Parameters.Any(static parameter =>
                    parameter.Modifiers.Any(SyntaxKind.OutKeyword)
                    && parameter.Identifier.ValueText.Contains("itemRef", StringComparison.OrdinalIgnoreCase)))
                .Select(static method => method.Identifier.ValueText)
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(declared);

        Assert.All(declared, function => Assert.Contains(function, ItemRefFunctions));
    }

    [Fact]
    public void The_ownership_scanner_goes_red_when_a_release_is_removed_from_its_method()
    {
        string source = MacOsCredentialStoreSource();

        MethodDeclarationSyntax tryGet = MethodNamed(source, "TryGet");

        string mutated = Apply(source, (ReleaseOf(tryGet, "itemRef").Span, string.Empty));

        // The same file still releases itemRef in other methods, which is exactly what the old
        // file-wide Contains check accepted.
        Assert.Contains("CFRelease(itemRef)", mutated, StringComparison.Ordinal);

        AssertSingleViolation(mutated, "TryGet");
    }

    [Fact]
    public void The_ownership_scanner_goes_red_when_a_reference_is_discarded()
    {
        string source = MacOsCredentialStoreSource();

        InvocationExpressionSyntax probe = ReceivingCalls(Parse(source))
            .Single(static call => call.MethodName == "ProbePresence")
            .Call;

        string mutated = Apply(source, (probe.ArgumentList.Arguments[^1].Span, "out _"));

        AssertSingleViolation(mutated, "ProbePresence");
    }

    [Fact]
    public void The_ownership_scanner_does_not_count_a_release_that_is_only_a_comment()
    {
        string source = MacOsCredentialStoreSource();

        ExpressionStatementSyntax release = ReleaseOf(MethodNamed(source, "TryGet"), "itemRef");

        AssertSingleViolation(Apply(source, (release.Span, "// CFRelease(itemRef);")), "TryGet");
    }

    [Fact]
    public void The_ownership_scanner_does_not_count_a_release_placed_before_the_call()
    {
        string source = MacOsCredentialStoreSource();

        ReceivingCall receiving = ReceivingCalls(Parse(source)).Single(static call => call.MethodName == "TryGet");

        ExpressionStatementSyntax release = ReleaseOf(receiving.Method, "itemRef");

        StatementSyntax received = receiving.Call.FirstAncestorOrSelf<StatementSyntax>()!;

        string mutated = Apply(
            source,
            (release.Span, string.Empty),
            (new TextSpan(received.SpanStart, 0), "CFRelease(itemRef);\n        "));

        AssertSingleViolation(mutated, "TryGet");
    }

    [Fact]
    public void The_ownership_scanner_does_not_count_a_release_that_runs_on_an_error_branch_only()
    {
        string source = MacOsCredentialStoreSource();

        IfStatementSyntax guard = ReleaseOf(MethodNamed(source, "TryGet"), "itemRef")
            .FirstAncestorOrSelf<IfStatementSyntax>()!;

        string mutated = Apply(
            source,
            (guard.Span, "if (status != ErrSecSuccess)\n        {\n            CFRelease(itemRef);\n        }"));

        AssertSingleViolation(mutated, "TryGet");
    }

    [Fact]
    public void The_ownership_scanner_goes_red_when_a_return_can_leave_before_the_release()
    {
        string source = MacOsCredentialStoreSource();

        IfStatementSyntax guard = ReleaseOf(MethodNamed(source, "TryGet"), "itemRef")
            .FirstAncestorOrSelf<IfStatementSyntax>()!;

        string mutated = Apply(
            source,
            (
                new TextSpan(guard.SpanStart, 0),
                "if (status != ErrSecSuccess)\n        {\n            return OsCredentialStoreResult.NotFound();\n        }\n\n        "));

        AssertSingleViolation(mutated, "TryGet");
    }

    /// <summary>
    /// <c>Set</c> releases the reference on its duplicate-item path and again in a <c>finally</c>; the
    /// first alone must not satisfy the scanner for the paths that reach the second.
    /// </summary>
    [Fact]
    public void The_ownership_scanner_goes_red_when_a_finally_release_is_removed()
    {
        string source = MacOsCredentialStoreSource();

        MethodDeclarationSyntax set = MethodNamed(source, "Set");

        ExpressionStatementSyntax inFinally = set.Body!
            .DescendantNodes()
            .OfType<FinallyClauseSyntax>()
            .Single()
            .DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .Single(static statement => IsRelease(statement, "addedRef"));

        string mutated = Apply(source, (inFinally.Span, string.Empty));

        // The duplicate-item path still releases it.
        Assert.Contains("CFRelease(addedRef)", mutated, StringComparison.Ordinal);

        AssertSingleViolation(mutated, "Set");
    }

    [Fact]
    public void The_ownership_scanner_goes_red_when_a_reference_obtained_through_the_helper_is_never_released()
    {
        string source = MacOsCredentialStoreSource();

        ExpressionStatementSyntax release = ReleaseOf(MethodNamed(source, "UpdateExisting"), "itemRef");

        AssertSingleViolation(Apply(source, (release.Span, string.Empty)), "UpdateExisting");
    }

    /// <summary>
    /// A caller may return before its protected region only because the helper hands back a zero
    /// reference on every failure, so the helper's returns are part of what this guard pins.
    /// </summary>
    [Fact]
    public void The_ownership_scanner_goes_red_when_a_failed_lookup_leaves_a_nonzero_reference()
    {
        string source = MacOsCredentialStoreSource();

        MethodDeclarationSyntax helper = MethodNamed(source, "TryGetItemRef");

        ReturnStatementSyntax notFound = helper.Body!
            .DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Single(static statement => statement.Expression!.ToString().StartsWith(
                "OsCredentialStoreResult.NotFound(",
                StringComparison.Ordinal));

        BlockSyntax block = (BlockSyntax)notFound.Parent!;

        StatementSyntax zeroing = block.Statements[block.Statements.IndexOf(notFound) - 1];

        Assert.Equal("itemRef = nint.Zero;", zeroing.ToString());

        AssertSingleViolation(Apply(source, (zeroing.Span, string.Empty)), "TryGetItemRef");
    }

    /// <summary>
    /// The exit before the protected region is allowed only for a condition that proves the lookup
    /// failed. A guard that returns while the lookup succeeded is holding a reference.
    /// </summary>
    [Fact]
    public void The_ownership_scanner_goes_red_when_an_early_return_can_happen_after_a_successful_lookup()
    {
        string source = MacOsCredentialStoreSource();

        IfStatementSyntax notFoundGuard = MethodNamed(source, "Delete")
            .Body!
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .First(static statement => statement.Condition.ToString().Contains("NotFound", StringComparison.Ordinal));

        string mutated = Apply(
            source,
            (notFoundGuard.Condition.Span, "existing.Status == OsCredentialStoreStatus.Ok"));

        AssertSingleViolation(mutated, "Delete");
    }

    /// <summary>
    /// An update or delete only needs the item reference. Reading the password to get one makes the
    /// operation depend on an authorization the caller never asked for, and a denied or headless read
    /// then fails an update that never needed the secret.
    /// </summary>
    [Fact]
    public void TryGetItemRef_obtains_the_reference_without_reading_the_password()
    {
        MethodDeclarationSyntax method = MethodNamed(MacOsCredentialStoreSource(), "TryGetItemRef");

        Assert.Contains(
            method.ParameterList.Parameters,
            static parameter => parameter.Modifiers.Any(SyntaxKind.OutKeyword)
                && parameter.Identifier.ValueText == "itemRef");

        AssertMetadataLookupWithoutPassword(method);
    }

    [Fact]
    public void Presence_probe_requests_an_item_reference_without_password_bytes()
    {
        MethodDeclarationSyntax method = MethodNamed(MacOsCredentialStoreSource(), "ProbePresence");

        AssertMetadataLookupWithoutPassword(method);

        // The probe must not be the password-reading lookup under another name.
        Assert.DoesNotContain("TryGet", CalledNames(method));
    }

    private static void AssertMetadataLookupWithoutPassword(MethodDeclarationSyntax method)
    {
        string[] called = CalledNames(method);

        Assert.DoesNotContain("SecKeychainFindGenericPassword", called);

        Assert.DoesNotContain("SecKeychainItemFreeContent", called);

        InvocationExpressionSyntax lookup = Assert.Single(
            method.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => CalledName(call) == "SecKeychainFindGenericPasswordMetadata");

        // (keychain, serviceLength, service, accountLength, account, passwordLength, passwordData, itemRef)
        Assert.Equal(8, lookup.ArgumentList.Arguments.Count);

        Assert.Equal("nint.Zero", lookup.ArgumentList.Arguments[5].ToString());

        Assert.Equal("nint.Zero", lookup.ArgumentList.Arguments[6].ToString());
    }

    private static string MacOsCredentialStoreSource() =>
        ProductionSourceInventory.Sources()
            .Single(static source => source.Is(SourceFileName))
            .Text;

    private static void AssertSingleViolation(string source, string method)
    {
        string violation = Assert.Single(UnreleasedItemRefs(source));

        Assert.StartsWith($"{method}:", violation, StringComparison.Ordinal);
    }

    private sealed record ReceivingCall(
        MethodDeclarationSyntax Method,
        InvocationExpressionSyntax Call,
        string Function,
        string? Reference)
    {
        internal string MethodName => Method.Identifier.ValueText;
    }

    private static CompilationUnitSyntax Parse(string source) =>
        CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

    private static MethodDeclarationSyntax MethodNamed(string source, string name) =>
        Parse(source)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Body is not null && method.Identifier.ValueText == name);

    /// <summary>
    /// The release statement for <paramref name="reference"/> in <paramref name="method"/>; the last
    /// one when the method releases it on more than one path.
    /// </summary>
    private static ExpressionStatementSyntax ReleaseOf(MethodDeclarationSyntax method, string reference) =>
        method.Body!
            .DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .Last(statement => IsRelease(statement, reference));

    /// <summary>Applies text edits against one parse's spans, last position first so none shifts another.</summary>
    private static string Apply(string source, params (TextSpan Span, string Text)[] edits)
    {
        string edited = source;

        foreach ((TextSpan span, string text) in edits.OrderByDescending(static edit => edit.Span.Start))
        {
            edited = string.Concat(edited.AsSpan(0, span.Start), text, edited.AsSpan(span.End));
        }

        return edited;
    }

    private static string[] CalledNames(MethodDeclarationSyntax method) =>
        [.. method.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(CalledName)];

    private static string CalledName(InvocationExpressionSyntax call) =>
        call.Expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => string.Empty,
        };

    /// <summary>
    /// Every call whose last argument receives a retained item reference, with the local or
    /// out-parameter that receives it, or null when the call discards it.
    /// </summary>
    private static IReadOnlyList<ReceivingCall> ReceivingCalls(CompilationUnitSyntax root)
    {
        List<ReceivingCall> calls = [];

        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // [LibraryImport] declarations have no body and receive nothing themselves.
            if (method.Body is not { } body)
            {
                continue;
            }

            foreach (InvocationExpressionSyntax call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string function = CalledName(call);

                if (!ItemRefFunctions.Contains(function) || call.ArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                calls.Add(new ReceivingCall(method, call, function, ReceivedReference(call.ArgumentList.Arguments[^1])));
            }
        }

        return calls;
    }

    private static string? ReceivedReference(ArgumentSyntax argument) =>
        argument.Expression switch
        {
            DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax single } =>
                single.Identifier.ValueText,

            IdentifierNameSyntax { Identifier.ValueText: not "_" } name => name.Identifier.ValueText,

            _ => null,
        };

    /// <summary>
    /// Every receiving call whose reference can outlive its method on some path: not released after
    /// the call in its own block or in an enclosing <c>finally</c>, or with a way out of the method
    /// before a release that dominates it. A method that hands the reference to its caller through an
    /// <c>out nint</c> parameter is instead held to the contract its callers rely on.
    /// </summary>
    private static IReadOnlyList<string> UnreleasedItemRefs(string source)
    {
        IReadOnlyList<ReceivingCall> receiving = ReceivingCalls(Parse(source));

        // The helpers whose out parameter carries the reference to their caller, and so promise a
        // zero reference on every failure: their callers may return early on a failed lookup.
        HashSet<string> handsToCaller =
        [
            .. receiving
                .Where(static call => call.Reference is { } reference && HandsToCaller(call.Method, reference))
                .Select(static call => call.MethodName),
        ];

        List<string> violations = [];

        foreach (ReceivingCall call in receiving)
        {
            string? problem = Problem(call, handsToCaller.Contains(call.Function));

            if (problem is not null)
            {
                violations.Add($"{call.MethodName}: {call.Function} {problem}");
            }
        }

        return violations;
    }

    private static string? Problem(ReceivingCall receiving, bool lookupGuaranteesZeroOnFailure)
    {
        if (receiving.Reference is not { } reference)
        {
            return "discards the retained item reference.";
        }

        if (HandsToCaller(receiving.Method, reference))
        {
            return FailureKeepsReference(receiving.Method, reference) is { } failure
                ? $"hands {reference} to its caller, but {failure}."
                : null;
        }

        StatementSyntax? received = receiving.Call.FirstAncestorOrSelf<StatementSyntax>();

        if (received?.Parent is not BlockSyntax)
        {
            return $"receives {reference} in a statement the scanner cannot place.";
        }

        if (!ReleaseFollows(received, reference))
        {
            return $"receives {reference}, but no release follows it in its block or in an enclosing finally.";
        }

        string? lookupResult = lookupGuaranteesZeroOnFailure
            && receiving.Call.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
                ? declarator.Identifier.ValueText
                : null;

        foreach (SyntaxNode exit in receiving.Method.Body!
                     .DescendantNodes()
                     .Where(node => node is ReturnStatementSyntax or ThrowStatementSyntax
                         && node.SpanStart > received.Span.End))
        {
            if (!ReleasedBefore(exit, receiving.Method, received, reference, lookupResult))
            {
                int line = exit.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                return $"receives {reference}, but the exit at line {line} can leave the method without releasing it.";
            }
        }

        return null;
    }

    private static bool HandsToCaller(MethodDeclarationSyntax method, string reference) =>
        method.ParameterList.Parameters.Any(parameter =>
            parameter.Identifier.ValueText == reference && parameter.Modifiers.Any(SyntaxKind.OutKeyword));

    /// <summary>
    /// A failing return of a method that hands a reference to its caller must have set that reference
    /// to zero immediately before, because its caller will not release a reference on a failure.
    /// </summary>
    private static string? FailureKeepsReference(MethodDeclarationSyntax method, string reference)
    {
        foreach (ReturnStatementSyntax exit in method.Body!.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (exit.Expression?.ToString().StartsWith(SuccessResult, StringComparison.Ordinal) == true)
            {
                continue;
            }

            if (exit.Parent is BlockSyntax block
                && block.Statements.IndexOf(exit) is > 0 and var index
                && block.Statements[index - 1] is ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax target } assignment,
                }
                && target.Identifier.ValueText == reference
                && assignment.Right.ToString() == "nint.Zero")
            {
                continue;
            }

            int line = exit.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            return $"the failing return at line {line} does not first set {reference} to nint.Zero";
        }

        return null;
    }

    /// <summary>
    /// Whether a release of <paramref name="reference"/> follows <paramref name="received"/> in its
    /// own block or an enclosing one, or in the <c>finally</c> of a <c>try</c> that encloses or follows
    /// it. A release anywhere else in the method — before the call, on a branch — does not follow it.
    /// </summary>
    private static bool ReleaseFollows(StatementSyntax received, string reference)
    {
        for (SyntaxNode? node = received; node is not null and not MethodDeclarationSyntax; node = node.Parent)
        {
            if (node.Parent is BlockSyntax block)
            {
                foreach (StatementSyntax later in block.Statements.Where(statement => statement.SpanStart > received.Span.End))
                {
                    if (ReleasesWhenHeld(later, reference)
                        || later is TryStatementSyntax tried && FinallyReleases(tried, reference))
                    {
                        return true;
                    }
                }
            }

            if (node is TryStatementSyntax enclosing
                && FinallyReleases(enclosing, reference)
                && !enclosing.Finally!.Span.Contains(received.Span))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a release has already happened, on every path to <paramref name="exit"/>, by the time
    /// it runs: an earlier sibling statement in a block that contains it, or the <c>finally</c> of a
    /// <c>try</c> that contains it, or — for a reference from a helper that guarantees zero on
    /// failure — a branch whose condition proves the lookup failed.
    /// </summary>
    private static bool ReleasedBefore(
        SyntaxNode exit,
        MethodDeclarationSyntax method,
        StatementSyntax received,
        string reference,
        string? lookupResult)
    {
        for (SyntaxNode node = exit; node != method.Body && node.Parent is not null; node = node.Parent)
        {
            if (node.Parent is BlockSyntax block
                && block.Statements.Any(earlier =>
                    earlier.SpanStart > received.Span.End
                    && earlier.SpanStart < node.SpanStart
                    && ReleasesWhenHeld(earlier, reference)))
            {
                return true;
            }

            if (node is TryStatementSyntax tried
                && FinallyReleases(tried, reference)
                && !tried.Finally!.Span.Contains(exit.Span))
            {
                return true;
            }

            if (node is IfStatementSyntax guard
                && lookupResult is not null
                && guard.Statement.Span.Contains(exit.Span)
                && ConditionProvesLookupFailed(guard.Condition, lookupResult))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A condition on the helper's result that is not an equality with success. It is deliberately
    /// narrow: <c>result.Status == OsCredentialStoreStatus.Ok</c> would admit exactly the exit this
    /// rule exists to refuse.
    /// </summary>
    private static bool ConditionProvesLookupFailed(ExpressionSyntax condition, string lookupResult)
    {
        string text = condition.ToString();

        return text.Contains($"{lookupResult}.Status", StringComparison.Ordinal)
            && !Regex.IsMatch(text, @"\.Status\s*(==|is)\s*OsCredentialStoreStatus\.Ok\b");
    }

    private static bool FinallyReleases(TryStatementSyntax tried, string reference) =>
        tried.Finally?.Block.Statements.Any(statement => ReleasesWhenHeld(statement, reference)) == true;

    /// <summary>
    /// An unconditional <c>CFRelease(reference)</c>, or the same call under the one guard that cannot
    /// leak: a reference that is not zero is the only one there is anything to release for.
    /// </summary>
    private static bool ReleasesWhenHeld(StatementSyntax statement, string reference)
    {
        if (IsRelease(statement, reference))
        {
            return true;
        }

        return statement is IfStatementSyntax { Else: null, Condition: BinaryExpressionSyntax guard } conditional
            && guard.IsKind(SyntaxKind.NotEqualsExpression)
            && guard.Left is IdentifierNameSyntax left
            && left.Identifier.ValueText == reference
            && guard.Right.ToString() == "nint.Zero"
            && (conditional.Statement is BlockSyntax block ? block.Statements : [conditional.Statement])
                .Any(inner => IsRelease(inner, reference));
    }

    private static bool IsRelease(StatementSyntax statement, string reference) =>
        statement is ExpressionStatementSyntax
        {
            Expression: InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: ReleaseFunction },
            } invocation,
        }
        && invocation.ArgumentList.Arguments.Count == 1
        && invocation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax argument
        && argument.Identifier.ValueText == reference;
}

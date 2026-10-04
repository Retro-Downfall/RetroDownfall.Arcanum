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
/// </remarks>
public sealed class MacOsCredentialStoreHandleOwnershipTests
{
    private const string SourceFileName = "MacOsCredentialStore.cs";

    /// <summary>
    /// The earlier form of this guard asked only whether <c>CFRelease(itemRef)</c> appeared somewhere
    /// in the file, so a release in <c>TryGet</c> satisfied a leak in <c>Set</c>. The ownership is
    /// per method: the method that receives the reference releases it, or hands it on through an
    /// <c>out nint</c> parameter whose caller is itself scanned.
    /// </summary>
    [Fact]
    public void Every_keychain_item_ref_is_released_in_the_method_that_receives_it()
    {
        IReadOnlyList<string> violations = UnreleasedItemRefs(MacOsCredentialStoreSource());

        Assert.Empty(violations);
    }

    [Fact]
    public void The_ownership_scanner_goes_red_when_a_release_is_removed_from_its_method()
    {
        string source = MacOsCredentialStoreSource();

        int tryGet = source.IndexOf("internal static OsCredentialStoreResult TryGet(", StringComparison.Ordinal);

        Assert.True(tryGet >= 0, "TryGet was not found.");

        int release = source.IndexOf("CFRelease(itemRef);", tryGet, StringComparison.Ordinal);

        Assert.True(release >= 0, "TryGet no longer releases itemRef.");

        string withoutRelease = source.Remove(release, "CFRelease(itemRef);".Length);

        // The same file still contains CFRelease(itemRef) in other methods, which is exactly what the
        // old file-wide Contains check accepted.
        Assert.Contains("CFRelease(itemRef)", withoutRelease, StringComparison.Ordinal);

        string violation = Assert.Single(UnreleasedItemRefs(withoutRelease));

        Assert.Contains("TryGet", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ownership_scanner_goes_red_when_a_reference_is_discarded()
    {
        string source = MacOsCredentialStoreSource();

        int probe = source.IndexOf("internal static OsCredentialStoreStatus ProbePresence", StringComparison.Ordinal);

        int captured = source.IndexOf("out nint itemRef);", probe, StringComparison.Ordinal);

        string discarded = source.Remove(captured, "out nint itemRef);".Length).Insert(captured, "out _);");

        Assert.Contains(
            UnreleasedItemRefs(discarded),
            static violation => violation.Contains("ProbePresence", StringComparison.Ordinal));
    }

    /// <summary>
    /// An update or delete only needs the item reference. Reading the password to get one makes the
    /// operation depend on an authorization the caller never asked for, and a denied or headless read
    /// then fails an update that never needed the secret.
    /// </summary>
    [Fact]
    public void TryGetItemRef_obtains_the_reference_without_reading_the_password()
    {
        (string signature, string body) = MethodNamed(MacOsCredentialStoreSource(), "TryGetItemRef");

        Assert.Contains("out nint itemRef", signature, StringComparison.Ordinal);

        Assert.Contains("SecKeychainFindGenericPasswordMetadata(", body, StringComparison.Ordinal);

        Assert.DoesNotContain("SecKeychainFindGenericPassword(", body, StringComparison.Ordinal);

        Assert.DoesNotContain("SecKeychainItemFreeContent", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Presence_probe_requests_an_item_reference_without_password_bytes()
    {
        string source = MacOsCredentialStoreSource();

        Assert.Contains(
            "internal static OsCredentialStoreStatus ProbePresence",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            "SecKeychainFindGenericPasswordMetadata(\n            nint.Zero,",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            "accountBytes,\n            nint.Zero,\n            nint.Zero,\n            out nint itemRef)",
            source,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "ProbePresence(string service, string account) => TryGet",
            source,
            StringComparison.Ordinal);
    }

    private static string MacOsCredentialStoreSource() =>
        ProductionSourceInventory.Sources()
            .Single(static source => source.Is(SourceFileName))
            .Text;

    private static readonly string[] ItemRefFunctions =
    [
        "SecKeychainFindGenericPassword",
        "SecKeychainFindGenericPasswordMetadata",
        "SecKeychainAddGenericPassword",
        "TryGetItemRef",
    ];

    /// <summary>
    /// Every call whose last argument receives a retained item reference and whose enclosing method
    /// neither releases it nor hands it to its own caller through an <c>out nint</c> parameter.
    /// </summary>
    private static IReadOnlyList<string> UnreleasedItemRefs(string source)
    {
        List<string> violations = [];

        foreach ((string name, string signature, string body) in MethodsOf(source))
        {
            foreach (string function in ItemRefFunctions)
            {
                foreach (string argument in ItemRefArgumentsOfCallsTo(body, function))
                {
                    if (argument is "out _" or "_")
                    {
                        violations.Add($"{name}: {function} discards the retained item reference.");

                        continue;
                    }

                    string reference = argument.Replace("out ", string.Empty, StringComparison.Ordinal)
                        .Replace("nint ", string.Empty, StringComparison.Ordinal);

                    bool released = body.Contains($"CFRelease({reference})", StringComparison.Ordinal);

                    bool handedToCaller = signature.Contains($"out nint {reference}", StringComparison.Ordinal);

                    if (!released && !handedToCaller)
                    {
                        violations.Add($"{name}: {function} receives {reference} but the method never releases it.");
                    }
                }
            }
        }

        return violations;
    }

    private static (string Signature, string Body) MethodNamed(string source, string name)
    {
        (string Name, string Signature, string Body) method = MethodsOf(source).Single(
            candidate => candidate.Name == name);

        return (method.Signature, method.Body);
    }

    /// <summary>
    /// The members of the store class that have a body, split on the file's own layout: a member
    /// starts at a four-space-indented line and its body is the block between the matching
    /// four-space-indented braces. <c>[LibraryImport]</c> declarations end in a semicolon and have no body.
    /// </summary>
    private static List<(string Name, string Signature, string Body)> MethodsOf(string source)
    {
        List<(string Name, string Signature, string Body)> methods = [];

        string[] lines = source.Split('\n');

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];

            if (!IsMemberStart(line))
            {
                continue;
            }

            int open = index;

            while (open < lines.Length && lines[open] != "    {" && !lines[open].TrimEnd().EndsWith(';'))
            {
                open++;
            }

            if (open >= lines.Length || lines[open] != "    {")
            {
                index = Math.Max(index, open);

                continue;
            }

            int close = open + 1;

            while (close < lines.Length && lines[close] != "    }")
            {
                close++;
            }

            string signature = string.Join('\n', lines[index..open]);

            string body = string.Join('\n', lines[(open + 1)..close]);

            int paren = signature.IndexOf('(', StringComparison.Ordinal);

            string name = signature[..paren].Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

            methods.Add((name, signature, body));

            index = close;
        }

        return methods;
    }

    private static bool IsMemberStart(string line) =>
        line.StartsWith("    ", StringComparison.Ordinal)
        && line.Length > 4
        && line[4] != ' '
        && line[4] != '{'
        && line[4] != '}'
        && line[4] != '['
        && !line.StartsWith("    //", StringComparison.Ordinal)
        && !line.StartsWith("    ///", StringComparison.Ordinal)
        && line.Contains('(', StringComparison.Ordinal);

    /// <summary>
    /// The last argument of every call to <paramref name="function"/> within <paramref name="source"/>.
    /// </summary>
    private static IReadOnlyList<string> ItemRefArgumentsOfCallsTo(string source, string function)
    {
        List<string> arguments = [];

        for (int index = source.IndexOf(function, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(function, index + function.Length, StringComparison.Ordinal))
        {
            // A longer name sharing this prefix (…Metadata) is a different function.
            int open = index + function.Length;

            while (open < source.Length && char.IsWhiteSpace(source[open]))
            {
                open++;
            }

            // The name also appears inside failure messages, where it is prose rather than a call.
            if (open >= source.Length || source[open] != '(')
            {
                continue;
            }

            int lineStart = source.LastIndexOf('\n', index) + 1;

            if (source[lineStart..index].Contains('"', StringComparison.Ordinal))
            {
                continue;
            }

            arguments.Add(Collapse(TopLevelArguments(source, open)[^1]));
        }

        return arguments;
    }

    /// <summary>Splits the parenthesized argument list opening at <paramref name="open"/>.</summary>
    private static List<string> TopLevelArguments(string source, int open)
    {
        List<string> arguments = [];

        int depth = 0;

        int start = open + 1;

        for (int index = open; index < source.Length; index++)
        {
            char value = source[index];

            if (value == '(')
            {
                depth++;

                continue;
            }

            if (value == ')')
            {
                depth--;

                if (depth == 0)
                {
                    arguments.Add(source[start..index]);

                    return arguments;
                }

                continue;
            }

            if (value == ',' && depth == 1)
            {
                arguments.Add(source[start..index]);

                start = index + 1;
            }
        }

        throw new InvalidOperationException($"Unbalanced argument list in {SourceFileName}.");
    }

    private static string Collapse(string argument) =>
        string.Join(' ', argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

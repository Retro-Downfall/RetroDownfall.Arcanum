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

    [Theory]
    [InlineData("SecKeychainFindGenericPassword")]
    [InlineData("SecKeychainAddGenericPassword")]
    public void Every_keychain_item_ref_is_captured_and_released(string function)
    {

        string source = MacOsCredentialStoreSource();

        IReadOnlyList<string> itemRefArguments = ItemRefArgumentsOfCallsTo(source, function);

        Assert.NotEmpty(itemRefArguments);

        foreach (string argument in itemRefArguments)
        {

            Assert.False(
                argument is "out _" or "_",
                $"{function} writes a retained SecKeychainItemRef into its last argument, so a call "
                + "that discards it leaks a CFType for the life of the process. Capture the "
                + "reference and CFRelease it, the way TryGet already does.");

            string name = argument.Replace("out ", string.Empty, StringComparison.Ordinal)
                .Replace("nint ", string.Empty, StringComparison.Ordinal);

            Assert.Contains($"CFRelease({name})", source, StringComparison.Ordinal);

        }

    }

    private static string MacOsCredentialStoreSource() =>
        ProductionSourceInventory.Sources()
            .Single(static source => source.Is(SourceFileName))
            .Text;

    /// <summary>
    /// The last argument of every call to <paramref name="function"/>, skipping its own
    /// <c>[LibraryImport]</c> declaration.
    /// </summary>
    private static IReadOnlyList<string> ItemRefArgumentsOfCallsTo(string source, string function)
    {

        List<string> arguments = [];

        for (int index = source.IndexOf(function, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(function, index + function.Length, StringComparison.Ordinal))
        {

            int lineStart = source.LastIndexOf('\n', index) + 1;

            if (source[lineStart..index].Contains("partial", StringComparison.Ordinal))
            {

                continue;

            }

            int open = index + function.Length;

            while (open < source.Length && char.IsWhiteSpace(source[open]))
            {

                open++;

            }

            // The name also appears inside the failure messages, where it is prose rather than a call.
            if (open >= source.Length || source[open] != '(')
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

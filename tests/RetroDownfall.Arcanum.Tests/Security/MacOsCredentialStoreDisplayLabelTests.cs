using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Pins the human-readable metadata on the two Keychain entries whose first-use authorization
/// prompts otherwise look indistinguishable, without broadening the identity or access boundary.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacOsCredentialStoreDisplayLabelTests
{
    [Theory]
    [InlineData("arcanum", "master-api-key", "Arcanum — Server authentication")]
    [InlineData("arcanum", "file-encryption-master-key", "Arcanum — Attachment and file encryption")]
    [InlineData("Arcanum", "master-api-key", null)]
    [InlineData("other", "master-api-key", null)]
    [InlineData("arcanum", "inference-provider-OPENAI-api-key", null)]
    [InlineData("arcanum", "host-process-tools-taint", null)]
    public void Display_label_policy_is_exactly_scoped(
        string service,
        string account,
        string? expected)
    {
        string? actual = MacOsCredentialStore.DisplayLabelFor(service, account);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("master-api-key", "Arcanum — Server authentication")]
    [InlineData("file-encryption-master-key", "Arcanum — Attachment and file encryption")]
    public void Successful_secret_write_stays_successful_when_cosmetic_label_write_fails(
        string account,
        string expectedLabel)
    {
        string? appliedLabel = null;

        Func<nint, string, int> rejectedLabelWrite = (itemRef, label) =>
        {
            Assert.Equal((nint)37, itemRef);

            appliedLabel = label;

            return -50;
        };

        OsCredentialStoreResult result = MacOsCredentialStore.CompleteSuccessfulWrite(
            "arcanum",
            account,
            "stored-secret",
            (nint)37,
            rejectedLabelWrite);

        Assert.Equal(expectedLabel, appliedLabel);

        Assert.Equal(OsCredentialStoreStatus.Ok, result.Status);

        Assert.Equal("stored-secret", result.Value);
    }

    [Fact]
    public void Successful_secret_write_stays_successful_when_cosmetic_interop_is_unavailable()
    {
        Func<nint, string, int> unavailableLabelWrite = static (_, _) =>
            throw new EntryPointNotFoundException("cosmetic Keychain metadata API unavailable");

        OsCredentialStoreResult result = MacOsCredentialStore.CompleteSuccessfulWrite(
            "arcanum",
            "master-api-key",
            "stored-secret",
            (nint)37,
            unavailableLabelWrite);

        Assert.Equal(OsCredentialStoreStatus.Ok, result.Status);

        Assert.Equal("stored-secret", result.Value);
    }

    [Fact]
    public void Unrelated_successful_secret_write_does_not_attempt_a_label_change()
    {
        Func<nint, string, int> forbiddenLabelWrite = static (_, _) =>
            throw new InvalidOperationException("An unrelated credential must not be relabeled.");

        OsCredentialStoreResult result = MacOsCredentialStore.CompleteSuccessfulWrite(
            "arcanum",
            "campaign-root-identity-key",
            "stored-secret",
            (nint)37,
            forbiddenLabelWrite);

        Assert.Equal(OsCredentialStoreStatus.Ok, result.Status);

        Assert.Equal("stored-secret", result.Value);
    }

    [Fact]
    public void Label_update_changes_only_the_label_attribute_and_not_secret_data_or_access_control()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Secrets",
            "Security",
            "MacOsCredentialStore.cs"));

        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        MethodDeclarationSyntax method = Assert.Single(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static candidate => candidate.Identifier.ValueText == "TrySetDisplayLabel");

        InvocationExpressionSyntax modify = Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.Expression.ToString() == "SecKeychainItemModifyAttributes");

        var arguments = modify.ArgumentList.Arguments;

        Assert.Equal(4, arguments.Count);

        Assert.Equal("0", arguments[2].Expression.ToString());

        Assert.Equal("nint.Zero", arguments[3].Expression.ToString());

        Assert.Contains("Tag = LabelItemAttributeTag", method.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("SecAccess", source, StringComparison.Ordinal);

        Assert.DoesNotContain("SecACL", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}

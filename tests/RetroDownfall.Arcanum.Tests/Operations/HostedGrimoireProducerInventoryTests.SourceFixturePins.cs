using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string FixtureManifestPath = "src/RetroDownfall.Arcanum.Core/Covenant/CovenantPolicyV1Manifest.cs";

    private const string FixtureWrapperPath = "src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigests.cs";

    private const string FixtureModelsPath = "src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigestModels.cs";

    private const string FixtureRecordDeclaration = "public sealed record SensitivityDigestInput(ContentSensitivity Level, GenerationProvenanceMode ProvenanceMode, ImmutableArray<Guid> ExactGenerationIds, ImmutableArray<byte> GenerationBloom);";

    private static readonly string[] FixtureManifestMethods =
    [
        "GetCode", "GetSupportedByte", "IsSupported", "AllowsZeroThroughOne", "AllowsZeroThroughTwo", "AllowsZeroThroughThree", "IsPositivePolicyEnum", "RequirePositiveCode", "GetMaximum", "RequireRange",
    ];

    private static string ReviewedFixtureHash(string path) => path switch
    {
        "src/RetroDownfall.Arcanum.Core/Tower/SessionCampaignBinding.cs" => "76C9BA2788216A3A7697E44BB169008A64DF4ADC33DD40DE63274FAF7E38CADD",
        "src/RetroDownfall.Arcanum.Core/Intelligence/CovenantContextPolicy.cs" => "9BE8C84DE91FD541534FD557E39C13B9EC52AD8AC6C543D348385147D17F6359",
        "src/RetroDownfall.Arcanum.Core/Intelligence/InvocationAttendance.cs" => "F05E67D031FF69306DAF9E11092EF4D3F47DA221DABC0E93395063C4FEAD4322",
        "src/RetroDownfall.Arcanum.Core/Covenant/GenerationProvenance.cs" => "494A7BA401ADA5D7ABD8B51E6B1C68D9E69E78B4FF147502DA16574C7723B788",
        "src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnums.cs" => "1AFCCD64D53DF63620E0051CACBC4D208A336BC775F76EF9763565E17570B027",
        "src/RetroDownfall.Arcanum.Core/Covenant/ContentSensitivity.cs" => "BD73D0448CB681FD9A467F8654F007A7E9E11596A4148AA2ED0B6FDA74A63084",
        "src/RetroDownfall.Arcanum.Core/Serialization/StringOnlyJsonStringEnumConverter.cs" => "A637ED2325AB8AF0789AB20F424DEB8CD3F37BE5278F0597CF281901FE712273",
        "src/RetroDownfall.Arcanum.Core/Covenant/CovenantPolicyV1Manifest.cs" => "63B1222E680CEDCEB8F9A4F93F98AEBBAD6D5674C11B2850BBB0F7DB08D7640C",
        "src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigests.cs" => "A5726522632805205611697EF702D6BA33577B3957E12168CFDC5D2E6C983BA2",
        "src/RetroDownfall.Arcanum.Core/Covenant/CovenantDigestModels.cs" => "FF6B9BA7F8D88C885609DC24F1B3C5FA03F3B6AE43A5027407A415133495F49A",
        _ => throw new InvalidOperationException("Unreviewed production fixture source: " + path),
    };

    private static string[] ReviewedFixtureEnums(string path) => path switch
    {
        "src/RetroDownfall.Arcanum.Core/Tower/SessionCampaignBinding.cs" => ["SessionCampaignBindingKind"],
        "src/RetroDownfall.Arcanum.Core/Intelligence/CovenantContextPolicy.cs" => ["CovenantContextPolicy"],
        "src/RetroDownfall.Arcanum.Core/Intelligence/InvocationAttendance.cs" => ["InvocationAttendance"],
        "src/RetroDownfall.Arcanum.Core/Covenant/GenerationProvenance.cs" => ["GenerationProvenanceMode"],
        "src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnums.cs" => ["CovenantScope", "CovenantLane", "CovenantOperation", "CovenantCurationKind", "CovenantOrigin", "CovenantMutationKind", "CovenantPlacement", "CovenantPlanDecision", "CovenantAdmissionDecision", "CovenantAuthorizationMode", "CovenantMutationOutcome", "CovenantFinalOutcome", "AssistantFinalizationOrigin", "CovenantWardDecision", "CovenantProviderRole", "CovenantProviderDispatchMode", "CovenantProviderContentPart", "CovenantToolRiskIdentity", "SensitiveArtifactKind", "CovenantEgressDestination", "CovenantDisclosureSubjectKind", "CovenantDisclosureRevocability", "CovenantDisclosureCountKind", "SessionTurnSurface", "CovenantToolPolicyCode", "CovenantMaintenanceStep", "CovenantMaintenanceCheckpoint", "SessionTurnClaimState", "BackupDisclosurePhase", "CampaignPathIdentityOperation", "ProviderToolChoice", "ProviderResponseFormat", "CovenantReasoningEffort", "CovenantReasoningOutput", "CovenantReasoningWireDialect", "CovenantTriStateBoolean", "CovenantImageDetail", "CovenantPromptAttribution", "CovenantMaterializationContainer", "CovenantMaterializationOccurrence", "CovenantMaterializationSourceRange", "CovenantCursorEndpoint", "CovenantCursorScopeSelection", "CovenantLifecycle", "CovenantCursorSort"],
        "src/RetroDownfall.Arcanum.Core/Covenant/ContentSensitivity.cs" => ["ContentSensitivity"],
        _ => throw new InvalidOperationException("Unreviewed enum fixture source: " + path),
    };

    private static void AssertReviewedFixturePin(string path, string source)
    {
        Assert.Equal(ReviewedFixtureHash(path), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))));
    }

    private static string ReadReviewedFixtureSource(string path)
    {
        string original = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), path));

        AssertReviewedFixturePin(path, original);

        return original;
    }

    private static (CompilationUnitSyntax Root, FileScopedNamespaceDeclarationSyntax Namespace) ParseFixtureSource(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);

        Assert.Empty(tree.GetDiagnostics());

        CompilationUnitSyntax root = Assert.IsType<CompilationUnitSyntax>(tree.GetRoot());

        FileScopedNamespaceDeclarationSyntax ns = Assert.IsType<FileScopedNamespaceDeclarationSyntax>(Assert.Single(root.Members));

        Assert.StartsWith("RetroDownfall.Arcanum.Core.", ns.Name.ToString(), StringComparison.Ordinal);

        return (root, ns);
    }

    private static MemberDeclarationSyntax[] SelectFixtureDeclarations(FileScopedNamespaceDeclarationSyntax ns, string[] names)
    {
        Assert.NotEmpty(names);

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());

        MemberDeclarationSyntax[] selected = ns.Members.Where(member => names.Contains(FixtureDeclarationName(member), StringComparer.Ordinal)).ToArray();

        Assert.Equal(names, selected.Select(FixtureDeclarationName));

        return selected;
    }

    private static string FixtureDeclarationName(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax declaration => declaration.Identifier.ValueText,
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        _ => throw new InvalidOperationException("Unreviewed fixture declaration kind: " + member.Kind()),
    };

    private static string FixtureFragment(CompilationUnitSyntax root, FileScopedNamespaceDeclarationSyntax ns, IEnumerable<MemberDeclarationSyntax> declarations) =>
        root.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(ns.WithMembers(SyntaxFactory.List(declarations)))).ToFullString();

    private Dictionary<string, string> ExtractReviewedManifestInputs(string trace, Action<string>? report = null)
    {
        report ??= output.WriteLine;

        string[] paths =
        [
            "src/RetroDownfall.Arcanum.Core/Tower/SessionCampaignBinding.cs",
            "src/RetroDownfall.Arcanum.Core/Intelligence/CovenantContextPolicy.cs",
            "src/RetroDownfall.Arcanum.Core/Intelligence/InvocationAttendance.cs",
            "src/RetroDownfall.Arcanum.Core/Covenant/GenerationProvenance.cs",
            "src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnums.cs",
            "src/RetroDownfall.Arcanum.Core/Covenant/ContentSensitivity.cs",
            "src/RetroDownfall.Arcanum.Core/Serialization/StringOnlyJsonStringEnumConverter.cs",
            FixtureManifestPath,
        ];

        Dictionary<string, string> extracted = new(StringComparer.Ordinal);

        foreach (string path in paths)
        {
            extracted.Add(path, ExtractReviewedManifestInput(path, trace, report));
        }

        return extracted;
    }

    private static string ExtractReviewedManifestInput(string path, string trace, Action<string> report)
    {
        string original = ReadReviewedFixtureSource(path);

        (CompilationUnitSyntax root, FileScopedNamespaceDeclarationSyntax ns) = ParseFixtureSource(original);

        MemberDeclarationSyntax[] declarations;

        string inventory;

        if (path == FixtureManifestPath)
        {
            ClassDeclarationSyntax manifest = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(SelectFixtureDeclarations(ns, ["CovenantPolicyV1Manifest"])));

            MethodDeclarationSyntax[] methods = manifest.Members.OfType<MethodDeclarationSyntax>().Where(method => FixtureManifestMethods.Contains(method.Identifier.ValueText, StringComparer.Ordinal)).ToArray();

            Assert.Equal(FixtureManifestMethods, methods.Select(static method => method.Identifier.ValueText));

            Assert.All(methods, static method => Assert.Contains(method.Modifiers, static token => token.IsKind(SyntaxKind.StaticKeyword)));

            declarations = [manifest.WithMembers(SyntaxFactory.List<MemberDeclarationSyntax>(methods))];

            inventory = "CovenantPolicyV1Manifest/" + string.Join(",", FixtureManifestMethods);
        }
        else if (path.EndsWith("/StringOnlyJsonStringEnumConverter.cs", StringComparison.Ordinal))
        {
            declarations = SelectFixtureDeclarations(ns, ["StringOnlyJsonStringEnumConverter"]);

            Assert.IsType<ClassDeclarationSyntax>(Assert.Single(declarations));

            inventory = "StringOnlyJsonStringEnumConverter";
        }
        else
        {
            string[] names = ReviewedFixtureEnums(path);

            Assert.Equal(names, ns.Members.OfType<EnumDeclarationSyntax>().Select(static declaration => declaration.Identifier.ValueText));

            declarations = SelectFixtureDeclarations(ns, names);

            Assert.All(declarations, static declaration => Assert.IsType<EnumDeclarationSyntax>(declaration));

            inventory = string.Join(",", names);
        }

        string source = FixtureFragment(root, ns, declarations);

        report($"{trace}_EXTRACTION={path}|inputSha256={ReviewedFixtureHash(path)}|declarations={inventory}|extractedSha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))}");

        return source;
    }

    private static (string Header, string Code) ExtractReviewedCodeWrapper()
    {
        string original = ReadReviewedFixtureSource(FixtureWrapperPath);

        (_, FileScopedNamespaceDeclarationSyntax ns) = ParseFixtureSource(original);

        ClassDeclarationSyntax wrapper = Assert.IsType<ClassDeclarationSyntax>(Assert.Single(SelectFixtureDeclarations(ns, ["CovenantDigests"])));

        Assert.Contains(wrapper.Modifiers, static token => token.IsKind(SyntaxKind.StaticKeyword));

        MethodDeclarationSyntax code = Assert.Single(wrapper.Members.OfType<MethodDeclarationSyntax>(), static method => method.Identifier.ValueText == "Code");

        Assert.Equal("private static uint Code<TEnum>(TEnum value, string parameterName)", code.WithBody(null).WithConstraintClauses(default).ToString().Trim());

        Assert.Equal("where TEnum : struct, Enum", Assert.Single(code.ConstraintClauses).ToString());

        return (original[..wrapper.OpenBraceToken.Span.End] + "\n", code.ToFullString());
    }

    private string ExtractReviewedSensitivityRecord(string trace)
    {
        string original = ReadReviewedFixtureSource(FixtureModelsPath);

        (CompilationUnitSyntax root, FileScopedNamespaceDeclarationSyntax ns) = ParseFixtureSource(original);

        RecordDeclarationSyntax record = Assert.IsType<RecordDeclarationSyntax>(Assert.Single(SelectFixtureDeclarations(ns, ["SensitivityDigestInput"])));

        Assert.Equal(FixtureRecordDeclaration, record.ToString());

        string source = FixtureFragment(root, ns, [record]);

        output.WriteLine($"{trace}_RECORD_EXTRACTION={FixtureModelsPath}|inputSha256={ReviewedFixtureHash(FixtureModelsPath)}|declarations=SensitivityDigestInput|extractedSha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))}");

        return source;
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("wrapper")]
    [InlineData("record")]
    [InlineData("enums")]
    public void ReviewedFixtureSyntaxSelectionIgnoresLineOffsets(string shape)
    {
        string path = shape == "manifest" ? FixtureManifestPath : shape == "wrapper" ? FixtureWrapperPath : shape == "enums" ? "src/RetroDownfall.Arcanum.Core/Covenant/CovenantEnums.cs" : FixtureModelsPath;

        string[] names = shape == "manifest" ? ["CovenantPolicyV1Manifest"] : shape == "wrapper" ? ["CovenantDigests"] : shape == "enums" ? ReviewedFixtureEnums(path) : ["SensitivityDigestInput"];

        string original = ReadReviewedFixtureSource(path);

        (_, FileScopedNamespaceDeclarationSyntax exact) = ParseFixtureSource(original);

        (_, FileScopedNamespaceDeclarationSyntax shifted) = ParseFixtureSource("// in-memory offset control\n\n\n" + original);

        Assert.Equal(names, SelectFixtureDeclarations(exact, names).Select(FixtureDeclarationName));

        Assert.True(SelectFixtureDeclarations(exact, names).Zip(SelectFixtureDeclarations(shifted, names), static (first, second) => SyntaxFactory.AreEquivalent(first, second)).All(static equivalent => equivalent));

        // Real production extraction still checks the whole source; trivia is not an authorized replacement image.
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(Record.Exception(() => AssertReviewedFixturePin(path, "\n" + original)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public void ReviewedFixtureDeclarationInventoryRefusesDrift(string shape)
    {
        (_, FileScopedNamespaceDeclarationSyntax ns) = ParseFixtureSource(ReadReviewedFixtureSource(FixtureModelsPath));

        string[] names = shape == "missing" ? ["SensitivityDigestInput", "UnreviewedRecord"] : ["SensitivityDigestInput", "SensitivityDigestInput"];

        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(Record.Exception(() => SelectFixtureDeclarations(ns, names)));
    }
}

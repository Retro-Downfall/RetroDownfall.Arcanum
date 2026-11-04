using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class AiReflectionProductionClosureTests
{
    private static readonly string[] ManagedProjects =
    [
        "RetroDownfall.Arcanum.Api",
        "RetroDownfall.Arcanum.Api.DevHost",
        "RetroDownfall.Arcanum.Cli",
        "RetroDownfall.Arcanum.Core",
        "RetroDownfall.Arcanum.Infrastructure",
        "RetroDownfall.Arcanum.Secrets",
        "RetroDownfall.Compendium.Ux",
        "RetroDownfall.TheForge.Core",
        "RetroDownfall.TheForge.Ux",
    ];

    private static readonly ConcurrentDictionary<string, byte[]> CleanAssemblies = new(StringComparer.Ordinal);

    private static readonly IReadOnlyList<MetadataReference> FixtureReferences = TrustedPlatformReferences.Shared
        .Concat([MetadataReference.CreateFromFile(typeof(AIFunctionFactory).Assembly.Location)])
        .DistinctBy(static reference => reference.FilePath)
        .ToArray();

    public static TheoryData<string> EveryManagedProject => new(ManagedProjects);

    [Fact]
    public void Every_compiled_production_project_is_free_of_unsafe_ai_reflection_entry_points()
    {
        IReadOnlyList<AiReflectionAssembly> assemblies = AiReflectionProductionClosure.ReadManifest(
            Path.Combine(AppContext.BaseDirectory, "ai-reflection-assemblies.txt"));

        IReadOnlyList<AiReflectionViolation> violations = AiReflectionProductionClosure.Scan(
            NativeSqlCipherTestPaths.RepositoryRoot(), assemblies);

        Assert.True(violations.Count == 0, string.Join(System.Environment.NewLine, violations));
    }

    [Fact]
    public void Complete_clean_compiled_closure_is_accepted()
    {
        using ClosureFixture fixture = new();

        Assert.Empty(AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Theory]
    [MemberData(nameof(EveryManagedProject))]
    public void A_seeded_compiled_violation_in_each_managed_project_is_rejected(string project)
    {
        using ClosureFixture fixture = new();

        AiReflectionAssembly assembly = fixture.Assemblies.Single(candidate =>
            Path.GetFileNameWithoutExtension(candidate.ProjectPath) == project);

        File.WriteAllBytes(assembly.AssemblyPath, CompileAssembly(project, forbidden: true));

        AiReflectionViolation violation = Assert.Single(
            AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));

        Assert.Contains(project, violation.Assembly, StringComparison.Ordinal);

        Assert.Contains("Fixture.Probe", violation.Caller, StringComparison.Ordinal);

        Assert.Contains("AIFunctionFactory.Create", violation.Callee, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryManagedProject))]
    public void Omitting_any_managed_project_from_the_compiled_closure_fails(string project)
    {
        using ClosureFixture fixture = new();

        AiReflectionAssembly[] incomplete = [.. fixture.Assemblies.Where(candidate =>
            Path.GetFileNameWithoutExtension(candidate.ProjectPath) != project)];

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, incomplete));
    }

    [Fact]
    public void A_new_nested_production_project_requires_a_compiled_scan_input()
    {
        using ClosureFixture fixture = new();

        fixture.AddProject("nested/New.Production", "New.Production");

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void An_extra_project_outside_the_discovered_production_set_fails()
    {
        using ClosureFixture fixture = new();

        string project = fixture.AddProject("../tests/Extra.Tests", "Extra.Tests");

        AiReflectionAssembly[] extra = [.. fixture.Assemblies, new(project, fixture.Assemblies[0].AssemblyPath)];

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, extra));
    }

    [Fact]
    public void A_duplicate_project_manifest_row_fails()
    {
        using ClosureFixture fixture = new();

        AiReflectionAssembly[] duplicate = [.. fixture.Assemblies, fixture.Assemblies[0]];

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, duplicate));
    }

    [Fact]
    public void A_duplicate_compiled_output_path_fails()
    {
        using ClosureFixture fixture = new();

        fixture.Assemblies[1] = fixture.Assemblies[1] with { AssemblyPath = fixture.Assemblies[0].AssemblyPath };

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void A_manifest_project_path_with_a_different_source_identity_fails()
    {
        using ClosureFixture fixture = new();

        fixture.Assemblies[0] = fixture.Assemblies[0] with
        {
            ProjectPath = Path.Combine(fixture.Root, "src", "RetroDownfall.Arcanum.Api", "Unrelated.csproj"),
        };

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void A_compiled_assembly_with_the_wrong_project_identity_fails()
    {
        using ClosureFixture fixture = new();

        File.WriteAllBytes(fixture.Assemblies[0].AssemblyPath, CompileAssembly("Unrelated.Production", forbidden: false));

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void A_missing_compiled_assembly_fails()
    {
        using ClosureFixture fixture = new();

        File.Delete(fixture.Assemblies[0].AssemblyPath);

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void An_output_without_managed_pe_metadata_fails()
    {
        using ClosureFixture fixture = new();

        File.WriteAllText(fixture.Assemblies[0].AssemblyPath, "not a managed PE image");

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void Build_output_project_files_are_not_authored_production_projects()
    {
        using ClosureFixture fixture = new();

        fixture.AddProject("RetroDownfall.Arcanum.Core/obj/generated", "Generated");

        fixture.AddProject("RetroDownfall.Arcanum.Core/bin/generated", "Generated");

        Assert.Empty(AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Theory]
    [InlineData("<EnableDefaultItems>true</EnableDefaultItems>")]
    [InlineData("<EnableDefaultItems>false</EnableDefaultItems><EnableDefaultCompileItems>true</EnableDefaultCompileItems>")]
    [InlineData("<EnableDefaultItems>false</EnableDefaultItems><IncludeBuildOutput>true</IncludeBuildOutput>")]
    public void The_native_asset_exclusion_refuses_managed_build_settings(string properties)
    {
        using ClosureFixture fixture = new();

        File.WriteAllText(fixture.NativeProject, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void The_native_asset_exclusion_refuses_explicit_compile_inputs()
    {
        using ClosureFixture fixture = new();

        string project = File.ReadAllText(fixture.NativeProject).Replace(
            "</Project>", "<ItemGroup><Compile Include=\"../Managed.cs\" /></ItemGroup></Project>", StringComparison.Ordinal);

        File.WriteAllText(fixture.NativeProject, project);

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void The_native_asset_exclusion_refuses_authored_managed_source()
    {
        using ClosureFixture fixture = new();

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.NativeProject)!, "Managed.cs"), "public sealed class Managed { }");

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
    }

    [Fact]
    public void The_manifest_preserves_exact_absolute_same_build_paths()
    {
        using ClosureFixture fixture = new();

        string manifest = Path.Combine(fixture.Root, "assemblies.txt");

        File.WriteAllLines(manifest, fixture.Assemblies.Select(assembly => $"{assembly.ProjectPath}|{assembly.AssemblyPath}"));

        Assert.Equal(fixture.Assemblies, AiReflectionProductionClosure.ReadManifest(manifest));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.csproj|relative.dll")]
    [InlineData("/absolute/project.csproj")]
    [InlineData("/absolute/project.csproj|/absolute/output.dll|extra")]
    [InlineData("|/absolute/output.dll")]
    [InlineData("/absolute/project.csproj|")]
    public void An_empty_or_malformed_manifest_fails(string contents)
    {
        using ClosureFixture fixture = new();

        string manifest = Path.Combine(fixture.Root, "assemblies.txt");

        File.WriteAllText(manifest, contents);

        Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.ReadManifest(manifest));
    }

    private static byte[] CompileAssembly(string assemblyName, bool forbidden)
    {
        string source = forbidden
            ? "using System; using Microsoft.Extensions.AI; public static class Fixture { public static AIFunction Probe() => AIFunctionFactory.Create((Func<int>)(() => 1)); }"
            : "public static class Fixture { public static int Probe() => 1; }";

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            FixtureReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using MemoryStream output = new();

        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(output);

        Assert.True(result.Success, string.Join(System.Environment.NewLine, result.Diagnostics));

        return output.ToArray();
    }

    private sealed class ClosureFixture : IDisposable
    {
        public ClosureFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "arcanum-ai-reflection-closure-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Root);

            Assemblies = [.. ManagedProjects.Select(project =>
            {
                string source = AddProject(project, project);

                string assembly = Path.Combine(Root, "compiled", project + ".dll");

                Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);

                File.WriteAllBytes(assembly, CleanAssemblies.GetOrAdd(project, static name => CompileAssembly(name, forbidden: false)));

                return new AiReflectionAssembly(source, assembly);
            })];

            NativeProject = AddProject("RetroDownfall.Arcanum.NativeSqlCipher", "RetroDownfall.Arcanum.NativeSqlCipher");

            File.WriteAllText(NativeProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><EnableDefaultItems>false</EnableDefaultItems><IncludeBuildOutput>false</IncludeBuildOutput></PropertyGroup></Project>");
        }

        public string Root { get; }

        public AiReflectionAssembly[] Assemblies { get; }

        public string NativeProject { get; }

        public string AddProject(string relativeDirectory, string name)
        {
            string directory = Path.GetFullPath(Path.Combine(Root, "src", relativeDirectory));

            Directory.CreateDirectory(directory);

            string project = Path.Combine(directory, name + ".csproj");

            File.WriteAllText(project, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>{name}</AssemblyName></PropertyGroup></Project>");

            return project;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

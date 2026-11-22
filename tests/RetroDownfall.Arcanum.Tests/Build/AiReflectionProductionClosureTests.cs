using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class AiReflectionProductionClosureTests
{
    private const int WindowsSharingViolation = unchecked((int)0x80070020);

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
    [InlineData("valid")]
    [InlineData("generic")]
    [InlineData("wrong-generic")]
    [InlineData("wrong-version")]
    [InlineData("missing-body")]
    [InlineData("ambiguous")]
    [InlineData("uninventoried")]
    [InlineData("forbidden")]
    public void Instance_accessors_resolve_only_exact_targets_in_the_complete_scanned_closure(string control)
    {
        using ClosureFixture fixture = new();

        string targetName = control == "uninventoried" ? "Uninventoried.Target" : "RetroDownfall.Arcanum.Core";

        bool generic = control is "generic" or "wrong-generic";

        byte[] Target(string version) => CompileSource(targetName, $$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]
            public sealed class AccessorHolder{{(generic ? "<T>" : "")}}
            {
                public {{(generic ? "T" : "string")}} Value { get; init; } = default!;
                public void Other(string value) { }
                public object Execute() => {{(control == "forbidden" ? "Microsoft.Extensions.AI.AIFunctionFactory.Create((System.Action)(() => { }))" : "new object()")}};
            }
            """, FixtureReferences);

        byte[] referenceTarget = Target("1.0.0.0");

        byte[] caller = CompileSource("RetroDownfall.Arcanum.Api", $$"""
            public static class Fixture
            {
                [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "{{(control == "forbidden" ? "Execute" : "set_Value")}}")]
                public static extern {{(control == "forbidden" ? "object Probe(AccessorHolder instance)" : "void Probe(AccessorHolder" + (generic ? "<string>" : "") + " instance, " + (control == "wrong-generic" ? "int" : "string") + " value)")}};
            }
            """, [.. FixtureReferences.Where(reference => Path.GetFileNameWithoutExtension(reference.Display) != targetName), MetadataReference.CreateFromImage(referenceTarget)]);

        byte[] target = control == "wrong-version" ? Target("2.0.0.0") : referenceTarget;

        if (control is "missing-body" or "ambiguous")
        {
            using PEReader pe = new(new MemoryStream(target, writable: false));

            MetadataReader metadata = pe.GetMetadataReader();

            MethodDefinitionHandle setter = Assert.Single(metadata.MethodDefinitions, handle =>
                metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "set_Value");

            int Row(MethodDefinitionHandle handle) => pe.PEHeaders.MetadataStartOffset
                + metadata.GetTableMetadataOffset(TableIndex.MethodDef)
                + (MetadataTokens.GetRowNumber(handle) - 1) * metadata.GetTableRowSize(TableIndex.MethodDef);

            if (control == "missing-body")
            {
                BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(Row(setter), sizeof(int)), 0);
            }
            else
            {
                MethodDefinitionHandle other = Assert.Single(metadata.MethodDefinitions, handle =>
                    metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Other");

                int namesAndSignatureSize = (metadata.GetHeapSize(HeapIndex.String) > ushort.MaxValue ? 4 : 2)
                    + (metadata.GetHeapSize(HeapIndex.Blob) > ushort.MaxValue ? 4 : 2);

                target.AsSpan(Row(setter) + 6, sizeof(ushort) + namesAndSignatureSize).CopyTo(target.AsSpan(Row(other) + 6));
            }
        }

        AiReflectionAssembly api = fixture.Assemblies.Single(assembly => Path.GetFileNameWithoutExtension(assembly.ProjectPath) == "RetroDownfall.Arcanum.Api");

        AiReflectionAssembly core = fixture.Assemblies.Single(assembly => Path.GetFileNameWithoutExtension(assembly.ProjectPath) == "RetroDownfall.Arcanum.Core");

        File.WriteAllBytes(api.AssemblyPath, caller);

        if (control != "uninventoried")
        {
            File.WriteAllBytes(core.AssemblyPath, target);
        }

        if (control is "valid" or "generic")
        {
            Assert.Empty(AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
        }
        else if (control == "forbidden")
        {
            AiReflectionViolation violation = Assert.Single(AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));

            Assert.Contains("AccessorHolder.Execute", violation.Caller, StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));
        }
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

    [Fact]
    public void Closure_fixture_removes_generated_projects_after_transient_windows_sharing()
    {
        int attempts = 0;

        int pauses = 0;

        ClosureFixture fixture = new(
            isWindows: true,
            delete: path =>
            {
                if (++attempts <= 2)
                {
                    throw new IOException("Scratch directory is temporarily shared.", WindowsSharingViolation);
                }

                Directory.Delete(path, recursive: true);
            },
            pauseBeforeRetry: () => pauses++);

        try
        {
            fixture.AddProject("RetroDownfall.Arcanum.Core/obj/generated", "Generated");

            fixture.AddProject("RetroDownfall.Arcanum.Core/bin/generated", "Generated");

            Assert.Empty(AiReflectionProductionClosure.Scan(fixture.Root, fixture.Assemblies));

            fixture.Dispose();

            Assert.False(Directory.Exists(fixture.Root));

            Assert.Equal(3, attempts);

            Assert.Equal(2, pauses);
        }
        finally
        {
            if (Directory.Exists(fixture.Root))
            {
                Directory.Delete(fixture.Root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(true, WindowsSharingViolation, 5)]
    [InlineData(false, WindowsSharingViolation, 1)]
    [InlineData(true, unchecked((int)0x80070021), 1)]
    [InlineData(true, unchecked((int)0x80070005), 1)]
    public void Closure_fixture_rethrows_persistent_or_unrelated_delete_errors(bool isWindows, int hresult, int expectedAttempts)
    {
        int attempts = 0;

        int pauses = 0;

        IOException expected = new("The scratch directory cannot be removed.", hresult);

        ClosureFixture fixture = new(
            isWindows,
            delete: _ =>
            {
                attempts++;

                throw expected;
            },
            pauseBeforeRetry: () => pauses++);

        try
        {
            Assert.Same(expected, Assert.Throws<IOException>(fixture.Dispose));

            Assert.True(Directory.Exists(fixture.Root));

            Assert.Equal(expectedAttempts, attempts);

            Assert.Equal(expectedAttempts - 1, pauses);
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
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

        return CompileSource(assemblyName, source, FixtureReferences);
    }

    private static byte[] CompileSource(string assemblyName, string source, IEnumerable<MetadataReference> references)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using MemoryStream output = new();

        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(output);

        Assert.True(result.Success, string.Join(System.Environment.NewLine, result.Diagnostics));

        return output.ToArray();
    }

    private sealed class ClosureFixture : IDisposable
    {
        private readonly bool _isWindows;

        private readonly Action<string> _delete;

        private readonly Action _pauseBeforeRetry;

        public ClosureFixture(bool? isWindows = null, Action<string>? delete = null, Action? pauseBeforeRetry = null)
        {
            _isWindows = isWindows ?? OperatingSystem.IsWindows();

            _delete = delete ?? (static path => Directory.Delete(path, recursive: true));

            _pauseBeforeRetry = pauseBeforeRetry ?? (static () => Thread.Sleep(250));

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

        public void Dispose()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    _delete(Root);

                    return;
                }
                catch (IOException error) when (_isWindows && error.HResult == WindowsSharingViolation && attempt < 4)
                {
                    _pauseBeforeRetry();
                }
            }
        }
    }
}

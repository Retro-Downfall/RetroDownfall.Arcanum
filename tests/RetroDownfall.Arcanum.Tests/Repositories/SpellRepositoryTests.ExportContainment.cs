using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed partial class SpellRepositoryTests
{
    [SkippableFact]
    public async Task Export_reads_scripts_through_an_inside_root_directory_link()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spell = await WriteExportableSpellAsync("export-linked-scripts");

        string scripts = Path.Combine(spell, "scripts");

        string shared = Path.Combine(_workspaceRoot, "shared-scripts");

        Directory.CreateDirectory(shared);

        await File.WriteAllTextAsync(Path.Combine(shared, "shared.sh"), "safe shared script");

        bool linked = false;

        try
        {
            try
            {
                Directory.CreateSymbolicLink(scripts, shared);

                linked = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Skip.If(true, "This host does not permit directory symbolic links.");
            }

            SpellExportDto? exported = await CreateRepository().ExportAsync("export-linked-scripts", _workspaceRoot, CancellationToken.None);

            Assert.NotNull(exported);

            SpellExportScriptDto script = Assert.Single(exported.Scripts);

            Assert.Equal("shared.sh", script.FileName);

            Assert.Equal("safe shared script", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(script.Base64Content)));

            Assert.Empty(exported.OmittedScripts!);
        }
        finally
        {
            if (linked)
            {
                Directory.Delete(scripts);
            }
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_does_not_disclose_names_from_an_external_scripts_directory(bool pivotDuringRead)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spell = Path.Combine(_workspaceRoot, "export-names");

        string scripts = Path.Combine(spell, "scripts");

        string external = Path.Combine(Path.GetTempPath(), "spell-external-names-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(scripts);

        Directory.CreateDirectory(external);

        string specPath = Path.Combine(spell, "SPELL.md");

        await File.WriteAllTextAsync(specPath, "---\nname: export-names\n---\nSAFE BODY");

        await File.WriteAllTextAsync(Path.Combine(external, "CONFIDENTIAL_EXTERNAL_FILENAME.sh"), "external content");

        var repository = CreateRepository();

        bool pivoted = false;

        try
        {
            Assert.NotNull(await repository.GetAsync("export-names", _workspaceRoot, CancellationToken.None));

            void Pivot()
            {
                Directory.Move(scripts, scripts + "-original");

                try
                {
                    Directory.CreateSymbolicLink(scripts, external);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    Directory.Move(scripts + "-original", scripts);

                    Skip.If(true, "This host does not permit directory symbolic links.");
                }

                pivoted = true;
            }

            if (pivotDuringRead)
            {
                SecureFileReader.AfterOpenForTests = path =>
                {
                    if (path == specPath && !pivoted)
                    {
                        Pivot();
                    }
                };
            }
            else
            {
                Pivot();
            }

            SpellExportDto? exported = await repository.ExportAsync("export-names", _workspaceRoot, CancellationToken.None);

            Assert.True(pivoted);

            Assert.NotNull(exported);

            Assert.Empty(exported.Scripts);

            Assert.DoesNotContain("CONFIDENTIAL_EXTERNAL_FILENAME.sh", exported.OmittedScripts!);

            Assert.Equal(0, exported.OmittedScriptCount);
        }
        finally
        {
            SecureFileReader.AfterOpenForTests = null;

            if (pivoted)
            {
                Directory.Delete(scripts);
            }

            Directory.Delete(external, recursive: true);
        }
    }

    [SkippableTheory]
    [InlineData("script", false)]
    [InlineData("script", true)]
    [InlineData("sidecar", false)]
    [InlineData("sidecar", true)]
    [InlineData("spec", false)]
    [InlineData("spec", true)]
    public async Task Export_refuses_parent_pivot_at_file_admission(string artifact, bool builtin)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string home = Path.Combine(Path.GetTempPath(), "spell-export-home-" + Guid.NewGuid().ToString("N"));

        string? previousHome = System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        string? previousEnvironment = System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", home);

        System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        string root = builtin ? ArcanumPaths.GlobalSpellsDirectory : _workspaceRoot;

        string spell = Path.Combine(root, "export-contained");

        string scripts = Path.Combine(spell, "scripts");

        string external = Path.Combine(home, "external");

        Directory.CreateDirectory(scripts);

        Directory.CreateDirectory(external);

        string specPath = Path.Combine(spell, "SPELL.md");

        string target = artifact == "script" ? Path.Combine(scripts, "run.sh") : Path.Combine(spell, "SPELL.json");

        await File.WriteAllTextAsync(specPath, "---\nname: export-contained\ndescription: safe\n---\nSAFE BODY");

        await File.WriteAllTextAsync(Path.Combine(spell, "SPELL.json"), "{\"name\":\"export-contained\",\"description\":\"SAFE SIDECAR\",\"tags\":[]}");

        await File.WriteAllTextAsync(Path.Combine(scripts, "run.sh"), "SAFE SCRIPT");

        await File.WriteAllTextAsync(Path.Combine(external, "run.sh"), "EXTERNAL SECRET");

        await File.WriteAllTextAsync(Path.Combine(external, "SPELL.json"), "{\"name\":\"export-contained\",\"description\":\"EXTERNAL SECRET\",\"tags\":[]}");

        await File.WriteAllTextAsync(Path.Combine(external, "SPELL.md"), "EXTERNAL SECRET");

        var repository = CreateRepository();

        string? workspace = builtin ? null : _workspaceRoot;

        bool pivoted = false;

        string pivot = artifact == "script" ? scripts : spell;

        try
        {
            // Warm hydration so the seam below belongs to the export's separate read group.
            Assert.NotNull(await repository.GetAsync("export-contained", workspace, CancellationToken.None));

            void Pivot()
            {
                Directory.Move(pivot, pivot + "-original");

                try
                {
                    Directory.CreateSymbolicLink(pivot, external);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    Directory.Move(pivot + "-original", pivot);

                    Skip.If(true, "This host does not permit directory symbolic links.");
                }

                pivoted = true;
            }

            if (artifact == "spec")
            {
                // This seam fires at the export's actual bounded read, after warm-cache hydration.
                SecureFileReader.AfterOpenForTests = path =>
                {
                    if (path == specPath && !pivoted)
                    {
                        Pivot();
                    }
                };
            }
            else
            {
                FileHandleIdentityInterop.TryGetPathMetadataForTests = path =>
                {
                    bool success = FileHandleIdentityInterop.TryGetPathMetadataIgnoringTestSeam(path, out FileHandleMetadata metadata);

                    if (path == target && !pivoted)
                    {
                        Pivot();
                    }

                    return success ? metadata : null;
                };
            }

            SpellExportDto? exported = await repository.ExportAsync("export-contained", workspace, CancellationToken.None);

            Assert.True(pivoted);

            if (artifact == "spec")
            {
                Assert.Null(exported);
            }
            else
            {
                Assert.NotNull(exported);

                if (artifact == "sidecar")
                {
                    Assert.Null(exported.Metadata);
                }
                else
                {
                    Assert.Empty(exported.Scripts);

                    Assert.Contains("run.sh", exported.OmittedScripts!);
                }
            }
        }
        finally
        {
            SecureFileReader.AfterOpenForTests = null;

            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

            if (pivoted)
            {
                Directory.Delete(pivot);
            }

            System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", previousHome);

            System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousEnvironment);

            Directory.Delete(home, recursive: true);
        }
    }
}

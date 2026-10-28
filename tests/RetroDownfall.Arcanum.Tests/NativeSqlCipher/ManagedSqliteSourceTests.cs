using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

public sealed class ManagedSqliteSourceTests
{
    [Fact]
    public void Self_built_provider_preserves_the_original_public_contract_and_assembly_identity()
    {
        Assembly assembly = typeof(SqliteConnection).Assembly;

        string[] expected = File.ReadAllLines(Path.Combine(VendorRoot, "provenance", "original-public-api.txt"));

        Assert.Equal(expected, DescribePublicApi(assembly));

        Assert.Equal("10.0.10+arcanum.null-origin.1", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

        Assert.Contains("self-built", assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company, StringComparison.Ordinal);

        Assert.Contains(assembly.GetCustomAttributes<AssemblyMetadataAttribute>(), static attribute =>
            attribute.Key == "Arcanum.ManagedSqlite.SelfBuilt"
            && attribute.Value!.Contains("not a Microsoft-signed release", StringComparison.Ordinal));
    }

    [Fact]
    public void Runtime_dependency_closure_selects_one_source_provider_instead_of_a_second_package_assembly()
    {
        using System.Text.Json.JsonDocument assets = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "RetroDownfall.Arcanum.Tests.deps.json")));

        System.Text.Json.JsonProperty provider = Assert.Single(assets.RootElement.GetProperty("libraries").EnumerateObject(),
            static item => item.Name.StartsWith("Microsoft.Data.Sqlite.Core/", StringComparison.Ordinal));

        Assert.Equal("project", provider.Value.GetProperty("type").GetString());

        Assert.Same(typeof(SqliteConnection).Assembly, typeof(SqliteDataReader).Assembly);

        Assert.Single(AppDomain.CurrentDomain.GetAssemblies(), static assembly => assembly.GetName().Name == "Microsoft.Data.Sqlite");
    }

    [Theory]
    [InlineData("LICENSE.txt")]
    [InlineData("source-manifest.json")]
    [InlineData("README.md")]
    public void Self_built_provider_delivers_its_original_license_and_auditable_source_attribution(string name)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "licenses", "Microsoft.Data.Sqlite.Core", name);

        Assert.True(File.Exists(output), "The self-built managed provider attribution is absent: " + name);

        Assert.Equal(File.ReadAllBytes(Path.Combine(VendorRoot, name)), File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("changed")]
    [InlineData("extra")]
    public async Task Actual_build_validation_accepts_only_the_exact_reviewed_source_inventory(string shape)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "arcanum-managed-sqlite-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(temporary);

        try
        {
            foreach (string file in Directory.EnumerateFiles(VendorRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(VendorRoot, file);

                if (relative.Split(Path.DirectorySeparatorChar).Any(static item => item is "obj" or "bin"))
                {
                    continue;
                }

                string destination = Path.Combine(temporary, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                File.Copy(file, destination);
            }

            if (shape == "changed")
            {
                File.AppendAllText(Path.Combine(temporary, "upstream", "SqliteDataRecord.cs"), "\n");
            }
            else if (shape == "extra")
            {
                File.WriteAllText(Path.Combine(temporary, "upstream", "Foreign.cs"), "namespace Foreign; public sealed class Unreviewed { }");
            }

            ProcessStartInfo start = new(global::System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = temporary,
            };

            foreach (string argument in new[]
            {
                "msbuild", Path.Combine(temporary, "Microsoft.Data.Sqlite.Core.csproj"),
                shape == "healthy" ? "-t:VerifyManagedSqliteSources" : "-t:Build", "-m:1", "-nodeReuse:false", "-v:minimal",
            })
            {
                start.ArgumentList.Add(argument);
            }

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            Task<string> output = process.StandardOutput.ReadToEndAsync();

            Task<string> error = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(CancellationToken.None);

                throw;
            }

            string observed = await output + await error;

            if (shape == "healthy")
            {
                Assert.True(process.ExitCode == 0, observed);
            }
            else
            {
                Assert.NotEqual(0, process.ExitCode);

                Assert.Contains(shape == "extra" ? "ARCSQLM001" : "hash", observed, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string VendorRoot => Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "vendor", "Microsoft.Data.Sqlite.Core");

    private static string[] DescribePublicApi(Assembly assembly)
    {
        List<string> rows = ["Assembly: " + assembly.FullName];

        foreach (Type type in assembly.GetExportedTypes())
        {
            rows.Add("Type: " + type.FullName + " : " + type.BaseType);

            foreach (Type contract in type.GetInterfaces())
            {
                rows.Add("Interface: " + type.FullName + " : " + contract);
            }

            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags))
                .Where(static method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly))
            {
                rows.Add("Method: " + type.FullName + " : " + method + " : "
                    + (method.Attributes & (MethodAttributes.MemberAccessMask | MethodAttributes.Static | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.Abstract)));
            }

            foreach (FieldInfo field in type.GetFields(flags).Where(static field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
            {
                rows.Add("Field: " + type.FullName + " : " + field + " : "
                    + (field.Attributes & (FieldAttributes.FieldAccessMask | FieldAttributes.Static | FieldAttributes.InitOnly | FieldAttributes.Literal))
                    + " : " + (field.IsLiteral ? Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : "-"));
            }
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}

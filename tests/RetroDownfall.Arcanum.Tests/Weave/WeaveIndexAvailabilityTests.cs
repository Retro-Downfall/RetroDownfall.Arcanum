using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;
using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed class WeaveIndexAvailabilityTests
{

    [Fact]
    public void Default_IsCompleteManagedMode()
    {

        WeaveIndexAvailability availability = new();

        Assert.Equal(WeaveIndexAvailability.ModeManaged, availability.Mode);

        Assert.Contains("managed", availability.Diagnostic, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Availability_has_no_mutable_acceleration_mode()
    {

        WeaveIndexAvailability availability = new();

        Assert.Null(typeof(WeaveIndexAvailability).GetMethod("SetAvailable"));

        Assert.Null(typeof(WeaveIndexAvailability).GetProperty("IsVecAvailable"));

        Assert.Null(typeof(WeaveIndexAvailability).GetField("ModeVec0"));

        Assert.Equal("managed", availability.Mode);

    }

    [Theory]
    [InlineData(false, null, null, "disabled")]
    [InlineData(true, null, "model", "unavailable")]
    [InlineData(true, "provider", null, "unavailable")]
    [InlineData(true, "provider", "model", "managed")]
    public void Status_reports_managed_search_only_after_configuration_is_ready(
        bool enabled, string? provider, string? model, string expectedMode)
    {
        var status = EmbeddingsVectorStatus.Resolve(
            new EmbeddingSettings { Enabled = enabled, Provider = provider, Model = model },
            new WeaveIndexAvailability());

        Assert.Equal(enabled, status.Enabled);

        Assert.Equal(expectedMode, status.Mode);

        Assert.Equal(0, status.ManagedRowBudget);

        Assert.DoesNotContain("fallback", status.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retrieval_call_sites_name_the_durable_blob_table()
    {
        Assert.Equal("saga_memory_embeddings", SagaStorageKeys.EmbeddingTable);

        string root = TestRepositoryPaths.RepositoryRoot();

        string[] retiredMirrors =
        [
            "entry_embeddings_vec",
            "workspace_file_embeddings_vec",
            "session_attachment_embeddings_vec",
            "saga_memory_embeddings_vec",
            "tapestry_node_embeddings_vec",
        ];

        Regex mirrorCall = new("Search(?:Scoped|CampaignScoped)?Async\\s*\\(\\s*\"[^\"]+_vec\"", RegexOptions.CultureInvariant);

        foreach (string folder in new[] { "src", "tests" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                string source = File.ReadAllText(file);

                Assert.False(mirrorCall.IsMatch(source), file);

                string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

                if (folder == "src"
                    && !relative.StartsWith("src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/", StringComparison.Ordinal)
                    && relative != "src/RetroDownfall.Arcanum.Core/Memory/MemoryErasureEffectFacts.cs")
                {
                    foreach (string mirror in retiredMirrors)
                    {
                        Assert.DoesNotContain('"' + mirror + '"', source, StringComparison.Ordinal);
                    }
                }
            }
        }
    }

}

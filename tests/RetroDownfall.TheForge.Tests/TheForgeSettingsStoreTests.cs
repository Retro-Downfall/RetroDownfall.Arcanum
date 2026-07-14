using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TheForgeSettingsStoreTests
{

    [Fact]
    public async Task SavePatch_PreservesUnrelatedSettings_WhenFileReadable()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-settings-{Guid.NewGuid():N}.json");

        try
        {

            TheForgeSettingsStore store = new(path);

            await store.SaveAsync(new TheForgeSettings
            {
                BaseUrl = "http://example.test:9",
                Theme = "light",
                AutoConnect = false,
                LayoutState = null,
            });

            await store.SavePatchAsync(s => s with { LayoutState = "{\"schemaVersion\":1}" });

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.Equal("http://example.test:9", loaded.BaseUrl);

            Assert.Equal("light", loaded.Theme);

            Assert.False(loaded.AutoConnect);

            Assert.Equal("{\"schemaVersion\":1}", loaded.LayoutState);

        }
        finally
        {

            TryDelete(path);

        }

    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsDefaultsWithoutThrowing()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-corrupt-{Guid.NewGuid():N}.json");

        try
        {

            await File.WriteAllTextAsync(path, "{ not-valid-json");

            TheForgeSettingsStore store = new(path);

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.Equal(new TheForgeSettings().BaseUrl, loaded.BaseUrl);

            Assert.Null(loaded.LayoutState);

            await store.SavePatchAsync(s => s with { LayoutState = "recovered" });

            TheForgeSettings after = await store.LoadAsync();

            Assert.Equal("recovered", after.LayoutState);

        }
        finally
        {

            TryDelete(path);

        }

    }

    [Fact]
    public async Task RoundTrip_LayoutState()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-roundtrip-{Guid.NewGuid():N}.json");

        try
        {

            TheForgeSettingsStore store = new(path);

            await store.SaveAsync(new TheForgeSettings { LayoutState = "abc" });

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.Equal("abc", loaded.LayoutState);

        }
        finally
        {

            TryDelete(path);

        }

    }

    private static void TryDelete(string path)
    {

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch (IOException)
        {

            // Best-effort temp cleanup.
        }

    }

}

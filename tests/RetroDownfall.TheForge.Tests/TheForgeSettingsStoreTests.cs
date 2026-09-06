using System.Runtime.Versioning;
using RetroDownfall.TheForge.Core.IO;
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

            TheForgeSettingsStore store = new(
                path,
                ImmediateTheForgeLocalMutationRunner.Instance);

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

            TheForgeSettingsStore store = new(
                path,
                ImmediateTheForgeLocalMutationRunner.Instance);

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

            TheForgeSettingsStore store = new(
                path,
                ImmediateTheForgeLocalMutationRunner.Instance);

            await store.SaveAsync(new TheForgeSettings { LayoutState = "abc" });

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.Equal("abc", loaded.LayoutState);

        }
        finally
        {

            TryDelete(path);

        }

    }

    [Fact]
    public async Task Load_uses_legacy_settings_without_renaming_or_rewriting_them()
    {

        string dir = Path.Combine(Path.GetTempPath(), $"forge-migrate-{Guid.NewGuid():N}");

        Directory.CreateDirectory(dir);

        string legacy = Path.Combine(dir, TheForgeSettingsStore.LegacyFileName);

        string modern = Path.Combine(dir, TheForgeSettingsStore.FileName);

        try
        {

            await File.WriteAllTextAsync(legacy, "{\"theme\":\"dark\"}");

            TheForgeSettingsStore store = new(
                modern,
                ImmediateTheForgeLocalMutationRunner.Instance);

            TheForgeSettings loaded = await store.LoadAsync();

            Assert.Equal("dark", loaded.Theme);

            Assert.True(File.Exists(legacy));

            Assert.False(File.Exists(modern));

        }
        finally
        {

            TryDelete(legacy);

            TryDelete(modern);

            try
            {

                Directory.Delete(dir, recursive: true);

            }
            catch (IOException)
            {

                // Best-effort.
            }

        }

    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void OwnerOnlyTempFile_IsRestrictedWhileStillOpenForWriting()
    {

        // File.GetUnixFileMode throws on Windows, and UnixCreateMode has no Windows equivalent; the
        // creation-time mode contract this test targets is POSIX-only.
        // [UnsupportedOSPlatform] above only quiets CA1416 for the GetUnixFileMode call below —
        // xUnit still discovers this method on Windows, so a runtime exit is still needed, and a
        // skip is the honest one: a plain return reported Passed there without asserting anything.
        Skip.If(OperatingSystem.IsWindows(), "The temp-file mode race this asserts on is POSIX-only.");

        string path = Path.Combine(Path.GetTempPath(), $"forge-settings-mode-{Guid.NewGuid():N}.tmp");

        try
        {

            using FileStream stream =
                TheForgeOwnerOnlyPermissions.CreateOwnerOnlyWriteThroughFile(path);

            const UnixFileMode GroupOrOtherAccess =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            UnixFileMode modeWhileOpen = File.GetUnixFileMode(path);

            Assert.Equal((UnixFileMode)0, modeWhileOpen & GroupOrOtherAccess);

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

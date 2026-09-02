using System.Runtime.Versioning;
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

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task SaveAsync_NeverExposesTheTempFileToGroupOrOtherWhileWriting()
    {

        if (OperatingSystem.IsWindows())
        {

            // File.GetUnixFileMode throws on Windows; TrySetUnixFileMode already no-ops there and the
            // ordering race this test targets is POSIX-only (Windows has no equivalent ACL fix here).
            // [UnsupportedOSPlatform] above only quiets CA1416 for the GetUnixFileMode call below —
            // xUnit still discovers and runs this method on Windows, so the runtime guard stays.
            return;

        }

        string path = Path.Combine(Path.GetTempPath(), $"forge-settings-mode-{Guid.NewGuid():N}.json");

        string directory = Path.GetDirectoryName(path)!;

        string tempFilePattern = Path.GetFileName(path) + ".*.tmp";

        try
        {

            TheForgeSettingsStore store = new(
                path,
                ImmediateTheForgeLocalMutationRunner.Instance);

            List<UnixFileMode> samples = [];

            using ManualResetEventSlim stop = new(initialState: false);

            Thread sampler = new(() =>
            {

                while (!stop.IsSet)
                {

                    foreach (string candidate in Directory.EnumerateFiles(directory, tempFilePattern))
                    {

                        try
                        {

                            samples.Add(File.GetUnixFileMode(candidate));

                        }
                        catch (IOException)
                        {

                            // The store moved or deleted the temp file between the listing and the
                            // stat call — the race this test targets, not a sample of it.
                        }

                    }

                }

            });

            sampler.Start();

            try
            {

                // A large payload (the legacy plaintext ApiKey round-trips through here too, per
                // SavePatchAsync) stretches the buffered-write duration so the sampler thread has a
                // real window to observe the temp file's mode while content is still being written.
                await store.SaveAsync(new TheForgeSettings
                {
                    ApiKey = "test-master-api-key",
                    LayoutState = new string('x', 10_000_000),
                });

            }
            finally
            {

                stop.Set();

                sampler.Join();

            }

            const UnixFileMode GroupOrOtherAccess =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            Assert.NotEmpty(samples);

            Assert.DoesNotContain(samples, mode => (mode & GroupOrOtherAccess) != 0);

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

using System.Diagnostics;
using SystemProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A real case-sensitive volume, for the tests that need two directories whose names differ only by case. The default macOS volume
/// folds case, so there <c>proj</c> and <c>Proj</c> are one directory and a case-sensitivity bug cannot be shown; here they are two.
/// </summary>
/// <remarks>
/// A small case-sensitive APFS disk image, attached with no browser entry at a mount point inside a private temporary directory. It is
/// created and detached by <c>hdiutil</c>, needs no privilege, and is only available on macOS: everywhere else, and wherever the image
/// cannot be created or attached, <see cref="TryCreateAsync"/> answers <see langword="null"/> and the test skips.
/// </remarks>
internal sealed class CaseSensitiveVolume : IAsyncDisposable
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(90);

    private readonly string _scratch;

    private readonly string _mountPoint;

    private CaseSensitiveVolume(string scratch, string mountPoint)
    {
        _scratch = scratch;

        _mountPoint = mountPoint;
    }

    /// <summary>The root directory of the volume, spelled physically (every symbolic link resolved).</summary>
    internal string Root => _mountPoint;

    internal static async Task<CaseSensitiveVolume?> TryCreateAsync()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/hdiutil"))
        {
            return null;
        }

        string scratch = Directory.CreateTempSubdirectory("arcanum-casesensitive-").FullName;

        string image = Path.Combine(scratch, "volume.dmg");

        string mountPoint = Directory.CreateDirectory(Path.Combine(PhysicalTestPaths.Resolve(scratch), "mnt")).FullName;

        bool attached = false;

        bool created = false;

        try
        {
            if (!await RunAsync("create", "-size", "32m", "-fs", "Case-sensitive APFS", "-volname", "ArcanumCS", "-type", "UDIF", "-ov", image)
                || !await RunAsync("attach", image, "-mountpoint", mountPoint, "-nobrowse", "-noverify"))
            {
                return null;
            }

            attached = true;

            // Two directories whose names differ only by case are two directories here, or this is not the volume that was asked for.
            _ = Directory.CreateDirectory(Path.Combine(mountPoint, "probe"));

            _ = Directory.CreateDirectory(Path.Combine(mountPoint, "PROBE"));

            if (Directory.GetDirectories(mountPoint).Length != 2)
            {
                return null;
            }

            Directory.Delete(Path.Combine(mountPoint, "probe"));

            Directory.Delete(Path.Combine(mountPoint, "PROBE"));

            created = true;

            return new CaseSensitiveVolume(scratch, mountPoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            if (!created)
            {
                if (attached)
                {
                    await DetachAsync(mountPoint);
                }

                _ = TestDirectoryCleanup.TryDelete(scratch, nameof(CaseSensitiveVolume));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DetachAsync(_mountPoint);

        _ = TestDirectoryCleanup.TryDelete(_scratch, nameof(CaseSensitiveVolume));
    }

    private static async Task DetachAsync(string mountPoint)
    {
        if (!await RunAsync("detach", mountPoint))
        {
            _ = await RunAsync("detach", mountPoint, "-force");
        }
    }

    private static async Task<bool> RunAsync(params string[] arguments)
    {
        ProcessStartInfo start = new("/usr/bin/hdiutil")
        {
            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using SystemProcess? process = SystemProcess.Start(start);

        if (process is null)
        {
            return false;
        }

        // The tool's output is not needed, but it must be drained or a full pipe would stall it.
        Task<string> output = process.StandardOutput.ReadToEndAsync();

        Task<string> error = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeout = new(ToolTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);

            return false;
        }

        _ = await output;

        _ = await error;

        return process.ExitCode == 0;
    }
}

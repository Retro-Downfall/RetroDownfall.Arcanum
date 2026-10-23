using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// Which executable the document worker re-launches, and the identity that binds the launch to it: captured once, then
/// checked again immediately before every start, so a binary replaced while the host runs is never started as the worker.
/// </summary>
public sealed class DocumentWorkerLaunchTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("arcanum-worker-launch-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForHostExecutable_WithoutAProcessPath_IsNoLaunch(string? processPath)
    {
        Assert.Null(DocumentWorkerLaunch.ForHostExecutable(processPath));
    }

    [Fact]
    public void ForHostExecutable_MissingExecutable_IsNoLaunch()
    {
        Assert.Null(DocumentWorkerLaunch.ForHostExecutable(Path.Combine(_directory, "arcanum")));
    }

    [Fact]
    public void ForHostExecutable_ADirectory_IsNoLaunch()
    {
        Assert.Null(DocumentWorkerLaunch.ForHostExecutable(_directory));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData("DOTNET")]
    public void ForHostExecutable_TheDotnetMuxer_IsNoLaunch(string fileName)
    {
        // A host started as `dotnet host.dll` reports the muxer as its process path, and the muxer would read the worker
        // arguments as an SDK command. It is not this host's executable, so it is never launched as its worker.
        Assert.Null(DocumentWorkerLaunch.ForHostExecutable(CreateExecutable(fileName)));
    }

    [SkippableFact]
    public void ForHostExecutable_ALinkNamedDotnet_IsNoLaunch()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows lane does not grant.");

        string link = Path.Combine(_directory, "dotnet");

        File.CreateSymbolicLink(link, CreateExecutable("host-real"));

        Assert.Null(DocumentWorkerLaunch.ForHostExecutable(link));
    }

    [SkippableFact]
    public void ForHostExecutable_ALinkToTheDotnetMuxer_IsNoLaunch()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows lane does not grant.");

        string link = Path.Combine(_directory, "host");

        File.CreateSymbolicLink(link, CreateExecutable("dotnet"));

        Assert.Null(DocumentWorkerLaunch.ForHostExecutable(link));
    }

    [Fact]
    public void ForHostExecutable_TheHostBinary_IsAClearedDocumentWorkerLaunchOfItsFullPath()
    {
        string executable = CreateExecutable("arcanum");

        DocumentWorkerLaunch launch = Assert.IsType<DocumentWorkerLaunch>(DocumentWorkerLaunch.ForHostExecutable(executable));

        Assert.Equal(Path.GetFullPath(executable), launch.Executable);

        Assert.Equal(ChildProcessEnvironmentProfile.DocumentWorker, launch.EnvironmentProfile);

        Assert.Empty(launch.Environment);

        Assert.Empty(launch.AdditionalReadExecuteRoots);

        Assert.Null(launch.MaxFileDescriptors);

        Assert.True(launch.IsUnchanged());
    }

    [SkippableFact]
    public void ForHostExecutable_ThroughASymbolicLink_LaunchesTheFileTheLinkNames()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows lane does not grant.");

        string target = CreateExecutable("arcanum-real");

        string link = Path.Combine(_directory, "arcanum");

        File.CreateSymbolicLink(link, target);

        DocumentWorkerLaunch launch = Assert.IsType<DocumentWorkerLaunch>(DocumentWorkerLaunch.ForHostExecutable(link));

        // The identity that is checked is the identity of the file that is started.
        Assert.Equal(Path.GetFullPath(target), launch.Executable);
    }

    [Fact]
    public void ForTests_KeepsItsEnvironmentUnderTheProfileThatRebuildsIt()
    {
        string executable = CreateExecutable("apphost");

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForTests(
            executable,
            new Dictionary<string, string> { ["DOTNET_ROOT"] = "/runtime" },
            ["/runtime"]);

        Assert.Equal(Path.GetFullPath(executable), launch.Executable);

        Assert.Equal(ChildProcessEnvironmentProfile.WorkspaceCheck, launch.EnvironmentProfile);

        Assert.Equal(new Dictionary<string, string> { ["DOTNET_ROOT"] = "/runtime" }, launch.Environment);

        Assert.Equal(["/runtime"], launch.AdditionalReadExecuteRoots);

        Assert.Empty(DocumentWorkerLaunch.ForTests(executable).Environment);

        Assert.Empty(DocumentWorkerLaunch.ForTests(executable).AdditionalReadExecuteRoots);
    }

    [Fact]
    public void ForTests_MissingExecutable_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => DocumentWorkerLaunch.ForTests(Path.Combine(_directory, "absent")));
    }

    [Fact]
    public void IsUnchanged_ExecutableReplacedByAnotherFileOfTheSameSizeAndTime_IsFalse()
    {
        string executable = CreateExecutable("arcanum");

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForHostExecutable(executable)!;

        // An update writes the new binary beside the old one and renames it over: the same path, a different file. The
        // replacement here even has the same size and time, so only the file identity tells them apart.
        string replacement = CreateExecutable("arcanum.new");

        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(executable));

        File.Move(replacement, executable, overwrite: true);

        Assert.False(launch.IsUnchanged());
    }

    [Fact]
    public void IsUnchanged_ExecutableRewrittenInPlaceToAnotherSizeAtTheSameTime_IsFalse()
    {
        string executable = CreateExecutable("arcanum");

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForHostExecutable(executable)!;

        DateTime written = File.GetLastWriteTimeUtc(executable);

        File.WriteAllText(executable, "#!/bin/sh\necho rewritten in place: the same file, other bytes\n");

        File.SetLastWriteTimeUtc(executable, written);

        Assert.False(launch.IsUnchanged());
    }

    [Fact]
    public void IsUnchanged_ExecutableRewrittenInPlaceToTheSameSizeLater_IsFalse()
    {
        string executable = CreateExecutable("arcanum");

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForHostExecutable(executable)!;

        DateTime written = File.GetLastWriteTimeUtc(executable);

        File.WriteAllText(executable, "#!/bin/sh\nexit 7\n");

        File.SetLastWriteTimeUtc(executable, written.AddSeconds(5));

        Assert.False(launch.IsUnchanged());
    }

    [Fact]
    public void IsUnchanged_ExecutableDeleted_IsFalse()
    {
        string executable = CreateExecutable("arcanum");

        DocumentWorkerLaunch launch = DocumentWorkerLaunch.ForHostExecutable(executable)!;

        File.Delete(executable);

        Assert.False(launch.IsUnchanged());
    }

    private string CreateExecutable(string fileName, string content = "#!/bin/sh\nexit 0\n")
    {
        string path = Path.Combine(_directory, fileName);

        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}

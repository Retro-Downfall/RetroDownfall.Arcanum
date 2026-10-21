using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Proves a race either installed its replacement or was prevented by Windows while a descendant
/// handle was open. Pre/post rename probes distinguish that lifetime from unrelated permissions.
/// </summary>
internal sealed class DirectoryPivotAttempt
{
    private readonly string _path;

    private readonly FileHandleIdentity _identity;

    internal DirectoryPivotAttempt(string path)
    {
        _path = path;

        _identity = CaptureIdentity();

        AssertMovable();
    }

    internal bool Attempted { get; private set; }

    internal bool Blocked { get; private set; }

    internal bool TryMoveAside()
    {
        Assert.False(Attempted);

        Attempted = true;

        try
        {
            Directory.Move(_path, _path + "-original");

            return true;
        }
        catch (IOException exception) when (OperatingSystem.IsWindows()
            && exception.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020))
        {
            Assert.Equal(_identity, CaptureIdentity());

            Assert.False(Path.Exists(_path + "-original"));

            Blocked = true;

            return false;
        }
    }

    internal void AssertCompleted(bool pivoted)
    {
        Assert.True(Attempted);

        Assert.True(pivoted ^ Blocked);

        if (Blocked)
        {
            Assert.Equal(_identity, CaptureIdentity());

            AssertMovable();
        }
    }

    private void AssertMovable()
    {
        string probe = _path + ".rename-probe-" + Guid.NewGuid().ToString("N");

        Directory.Move(_path, probe);

        Directory.Move(probe, _path);

        Assert.Equal(_identity, CaptureIdentity());
    }

    private FileHandleIdentity CaptureIdentity()
    {
        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataIgnoringTestSeam(_path, out FileHandleMetadata metadata));

        Assert.Equal(FileSystemObjectKind.Directory, metadata.Kind);

        return metadata.Identity;
    }
}

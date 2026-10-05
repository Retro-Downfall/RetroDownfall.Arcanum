using System.Security.Cryptography;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class GrimoireKdfSidecarTests : IDisposable
{
    private readonly string _tempDir;

    public GrimoireKdfSidecarTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"sidecar-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Create_ProducesVersion2AndSixteenByteSalt()
    {
        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        Assert.Equal(GrimoireKeyDerivation.KdfVersion2, sidecar.Version);

        byte[] salt = sidecar.GetSaltBytes();

        Assert.Equal(GrimoireKeyDerivation.SaltLengthBytes, salt.Length);
    }

    [Fact]
    public void WriteAndRead_RoundTripsSidecar()
    {
        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(dbPath, sidecar);

        GrimoireKdfSidecar read = GrimoireKdfSidecarFile.Read(dbPath);

        Assert.Equal(sidecar.Version, read.Version);

        Assert.Equal(sidecar.SaltBase64, read.SaltBase64);
    }

    [Fact]
    public void Read_WrongVersion_ThrowsNotSupportedException()
    {
        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        GrimoireKdfSidecar sidecar = new()
        {
            Version = 999,
            SaltBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GrimoireKeyDerivation.SaltLengthBytes)),
        };

        GrimoireKdfSidecarFile.Write(dbPath, sidecar);

        Assert.Throws<NotSupportedException>(() => GrimoireKdfSidecarFile.Read(dbPath));
    }

    [Fact]
    public void Read_WrongSaltLength_ThrowsInvalidDataException()
    {
        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        GrimoireKdfSidecar sidecar = new()
        {
            Version = GrimoireKeyDerivation.KdfVersion2,
            SaltBase64 = Convert.ToBase64String(new byte[8]),
        };

        GrimoireKdfSidecarFile.Write(dbPath, sidecar);

        Assert.Throws<InvalidDataException>(() => GrimoireKdfSidecarFile.Read(dbPath));
    }

    [Fact]
    public void Read_OversizedSidecar_FailsBeforeParsing()
    {
        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        string sidecarPath = GrimoireKdfSidecarFile.GetSidecarPath(dbPath);

        using (FileStream stream = new(sidecarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(GrimoireKdfSidecarFile.MaxSidecarBytes + 1L);
        }

        Assert.Throws<InvalidDataException>(() => GrimoireKdfSidecarFile.Read(dbPath));
    }

    /// <summary>
    /// Truncation and byte damage are the corruptions this file actually suffers — an unclean shutdown,
    /// a full disk mid-rename, a partial restore. They have to normalise to the same InvalidDataException
    /// the length, version and salt-size rejections already produce, because callers that degrade a
    /// damaged sidecar into a typed failure filter on exactly that type.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{\"v\":2,\"salt\":")]
    [InlineData("not json at all")]
    public void Read_MalformedJson_ThrowsInvalidDataException(string contents)
    {
        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        File.WriteAllText(GrimoireKdfSidecarFile.GetSidecarPath(dbPath), contents);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => GrimoireKdfSidecarFile.Read(dbPath));

        Assert.Contains("grimoire.db.kdf", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_NonBase64Salt_ThrowsInvalidDataException()
    {
        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        File.WriteAllText(
            GrimoireKdfSidecarFile.GetSidecarPath(dbPath),
            $"{{\"v\":{GrimoireKeyDerivation.KdfVersion2},\"salt\":\"not base64 at all!\"}}");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => GrimoireKdfSidecarFile.Read(dbPath));

        Assert.Contains("grimoire.db.kdf", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable sidecar is a file whose content is unknown, not a damaged one. Reporting a refused
    /// open as <see cref="InvalidDataException"/> sent the doctor to a backup restore that would replace a
    /// perfectly good file whose only fault is its permissions.
    /// </summary>
    [SkippableFact]
    public void Read_an_unreadable_sidecar_reports_the_permission_failure_not_damage()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix permission bits are what make the sidecar unreadable here.");

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        string sidecarPath = GrimoireKdfSidecarFile.GetSidecarPath(dbPath);

        GrimoireKdfSidecarFile.Write(dbPath, GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2));

        File.SetUnixFileMode(sidecarPath, UnixFileMode.None);

        try
        {
            Skip.If(CanOpenForRead(sidecarPath), "A superuser reads a mode 000 file, so there is no refusal to observe.");

            Assert.Throws<UnauthorizedAccessException>(() => GrimoireKdfSidecarFile.Read(dbPath));
        }
        finally
        {
            File.SetUnixFileMode(sidecarPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// A failed open that is neither a missing file nor a refused one (here the kernel rejecting an
    /// over-long name) is an I/O failure, not evidence about the sidecar's bytes.
    /// </summary>
    [SkippableFact]
    public void Read_a_failed_open_reports_an_io_failure_not_damage()
    {
        Skip.If(OperatingSystem.IsWindows(), "The over-long leaf name is a Unix errno path.");

        string dbPath = Path.Combine(_tempDir, new string('a', 300));

        Assert.Throws<IOException>(() => GrimoireKdfSidecarFile.Read(dbPath));
    }

    /// <summary>
    /// What really is wrong with the file's identity (a link, not a regular file) stays the damage report.
    /// </summary>
    [SkippableFact]
    public void Read_a_symbolic_link_is_still_reported_as_invalid_data()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        string target = Path.Combine(_tempDir, "elsewhere");

        File.WriteAllText(target, "{}");

        File.CreateSymbolicLink(GrimoireKdfSidecarFile.GetSidecarPath(dbPath), target);

        Assert.Throws<InvalidDataException>(() => GrimoireKdfSidecarFile.Read(dbPath));
    }

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Windows refuses a delete with <see cref="UnauthorizedAccessException"/>. Thrown out of the
    /// cleanup in a <c>finally</c>, it would replace the exception that explains why the write failed
    /// with one about tidying up afterwards. The seam reproduces that refusal on any platform.
    /// </summary>
    [Fact]
    public void Write_preserves_the_original_failure_when_temp_cleanup_is_denied()
    {
        string dbPath = Path.Combine(_tempDir, "grimoire.db");

        string sidecarPath = GrimoireKdfSidecarFile.GetSidecarPath(dbPath);

        // A non-empty directory squatting on the sidecar path lets the temp file be staged and
        // written, then fails the atomic replace, which is the only way the cleanup arm runs.
        Directory.CreateDirectory(sidecarPath);

        File.WriteAllText(Path.Combine(sidecarPath, "occupant"), "x");

        UnauthorizedAccessException cleanupFailure = new("test: temp cleanup denied");

        List<string> attemptedCleanups = [];

        OwnerOnlyAtomicFile.TempFileDeleteForTests = path =>
        {
            attemptedCleanups.Add(path);

            throw cleanupFailure;
        };

        try
        {
            Exception? failure = Record.Exception(
                () => GrimoireKdfSidecarFile.Write(
                    dbPath,
                    GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2)));

            Assert.NotNull(failure);

            Assert.NotSame(cleanupFailure, failure);

            Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());

            string attempted = Assert.Single(attemptedCleanups);

            Assert.StartsWith(sidecarPath + ".tmp.", attempted, StringComparison.Ordinal);
        }
        finally
        {
            OwnerOnlyAtomicFile.TempFileDeleteForTests = null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}

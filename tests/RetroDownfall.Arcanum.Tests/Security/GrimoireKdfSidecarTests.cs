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

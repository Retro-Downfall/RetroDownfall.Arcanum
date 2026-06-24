using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed record GrimoireKdfSidecar
{

    [JsonPropertyName("v")]
    public required int Version { get; init; }

    [JsonPropertyName("salt")]
    public required string SaltBase64 { get; init; }

    public byte[] GetSaltBytes() => Convert.FromBase64String(SaltBase64);

    public static GrimoireKdfSidecar Create(int version)
    {

        byte[] salt = new byte[GrimoireKeyDerivation.SaltLengthBytes];

        RandomNumberGenerator.Fill(salt);

        return new GrimoireKdfSidecar
        {
            Version = version,
            SaltBase64 = Convert.ToBase64String(salt),
        };

    }

}

public static partial class GrimoireKdfSidecarFile
{

    public static string GetSidecarPath(string databasePath) => databasePath + ".kdf";

    public static bool Exists(string databasePath) => File.Exists(GetSidecarPath(databasePath));

    public static GrimoireKdfSidecar Read(string databasePath)
    {

        string sidecarPath = GetSidecarPath(databasePath);

        string json = File.ReadAllText(sidecarPath);

        GrimoireKdfSidecar? sidecar = JsonSerializer.Deserialize(json, GrimoireKdfSidecarJsonContext.Default.GrimoireKdfSidecar);

        if (sidecar is null)
        {

            throw new InvalidDataException($"Grimoire KDF sidecar at {sidecarPath} is empty or malformed.");

        }

        if (sidecar.Version != GrimoireKeyDerivation.KdfVersion2)
        {

            throw new NotSupportedException(
                $"Grimoire KDF sidecar version {sidecar.Version} is not supported.");

        }

        byte[] salt = sidecar.GetSaltBytes();

        if (salt.Length != GrimoireKeyDerivation.SaltLengthBytes)
        {

            throw new InvalidDataException(
                $"Grimoire KDF sidecar salt must be {GrimoireKeyDerivation.SaltLengthBytes} bytes.");

        }

        return sidecar;

    }

    public static void Write(string databasePath, GrimoireKdfSidecar sidecar)
    {

        string sidecarPath = GetSidecarPath(databasePath);

        string directory = Path.GetDirectoryName(sidecarPath)
            ?? throw new InvalidOperationException("Invalid Grimoire database path.");

        Directory.CreateDirectory(directory);

        string tempPath = sidecarPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {

            File.WriteAllText(tempPath, JsonSerializer.Serialize(sidecar, GrimoireKdfSidecarJsonContext.Default.GrimoireKdfSidecar));

            File.Move(tempPath, sidecarPath, overwrite: true);

        }
        finally
        {

            if (File.Exists(tempPath))
            {

                try
                {

                    File.Delete(tempPath);

                }
                catch (IOException)
                {

                    // Best-effort cleanup.

                }

            }

        }

    }

    [JsonSerializable(typeof(GrimoireKdfSidecar))]
    internal sealed partial class GrimoireKdfSidecarJsonContext : JsonSerializerContext
    {

    }

}


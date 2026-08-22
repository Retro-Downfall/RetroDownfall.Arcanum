using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

public sealed record CliContextDocument(
    int Version,
    Guid? CampaignId,
    string? CampaignName,
    string? WorkspaceId,
    string? WorkspacePath,
    string? Model,
    Guid? SessionId)
{

    public const int CurrentVersion = 1;

    public static CliContextDocument Empty { get; } =
        new(CurrentVersion, null, null, null, null, null, null);

}

public interface ICliContextStore
{

    string FilePath { get; }

    CliContextDocument Load();

}

internal interface ICliContextExclusiveWriter
{

    void SaveUnderExclusive(CliContextDocument document);

}

public sealed class CliContextStore :
    ICliContextStore,
    ICliContextExclusiveWriter
{

    private readonly string _filePath;

    public CliContextStore()
        : this(Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-context.json"))
    {

    }

    public CliContextStore(string filePath)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _filePath = Path.GetFullPath(filePath);

    }

    public string FilePath => _filePath;

    public CliContextDocument Load()
    {

        try
        {

            if (!File.Exists(_filePath))
            {

                return CliContextDocument.Empty;

            }

            using FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            CliContextDocument? document =
                JsonSerializer.Deserialize(
                    stream,
                    CliContextJsonContext.Default.CliContextDocument);

            return document is { Version: CliContextDocument.CurrentVersion }
                ? Normalize(document)
                : CliContextDocument.Empty;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {

            return CliContextDocument.Empty;

        }

    }

    void ICliContextExclusiveWriter.SaveUnderExclusive(
        CliContextDocument document)
    {

        SaveCore(document);

    }

    private void SaveCore(CliContextDocument document)
    {

        ArgumentNullException.ThrowIfNull(document);

        CliContextDocument normalized = Normalize(
            document with { Version = CliContextDocument.CurrentVersion });

        string? directory = Path.GetDirectoryName(_filePath);

        if (string.IsNullOrWhiteSpace(directory))
        {

            throw new IOException("The CLI context path has no parent directory.");

        }

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        string tempPath = _filePath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {

            using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {

                JsonSerializer.Serialize(
                    stream,
                    normalized,
                    CliContextJsonContext.Default.CliContextDocument);

                stream.Flush(flushToDisk: true);

            }

            SecureFilePermissions.ApplyOwnerOnlyFile(tempPath);

            File.Move(tempPath, _filePath, overwrite: true);

            SecureFilePermissions.ApplyOwnerOnlyFile(_filePath);

        }
        finally
        {

            try
            {

                File.Delete(tempPath);

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

            }

        }

    }

    private static CliContextDocument Normalize(CliContextDocument document) =>
        document with
        {
            CampaignName = NormalizeText(document.CampaignName),
            WorkspaceId = NormalizeText(document.WorkspaceId) is { } workspaceId
                && NormalizeText(document.WorkspacePath) is not null
                    ? workspaceId
                    : null,
            WorkspacePath = NormalizeText(document.WorkspaceId) is not null
                ? NormalizeText(document.WorkspacePath)
                : null,
            Model = NormalizeText(document.Model),
        };

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CliContextDocument))]
internal sealed partial class CliContextJsonContext : JsonSerializerContext;

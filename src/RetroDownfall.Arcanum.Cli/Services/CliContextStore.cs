using System.Globalization;
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

    public CliContextDocument Load() => Inspect().Document;

    /// <summary>
    /// Reads the file and says why it could not be used, which <see cref="Load"/> deliberately does
    /// not: a read of an unusable file answers "empty", but a write must never be based on that
    /// answer (the next mutation would replace a newer Arcanum's context, or a file an operator
    /// damaged, with an empty one).
    /// </summary>
    internal CliContextInspection Inspect()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new CliContextInspection(CliContextFileState.Missing, CliContextDocument.Empty);
            }

            using FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            if (stream.Length == 0)
            {
                // An empty file carries nothing a write could lose.
                return new CliContextInspection(CliContextFileState.Missing, CliContextDocument.Empty);
            }

            using JsonDocument json = JsonDocument.Parse(stream);

            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new CliContextInspection(CliContextFileState.Unreadable, CliContextDocument.Empty);
            }

            int? version = json.RootElement.TryGetProperty("version", out JsonElement versionElement)
                && versionElement.ValueKind == JsonValueKind.Number
                && versionElement.TryGetInt32(out int parsedVersion)
                    ? parsedVersion
                    : null;

            if (version != CliContextDocument.CurrentVersion)
            {
                return new CliContextInspection(
                    CliContextFileState.UnsupportedVersion,
                    CliContextDocument.Empty,
                    version);
            }

            CliContextDocument? document = json.RootElement.Deserialize(
                CliContextJsonContext.Default.CliContextDocument);

            return document is null
                ? new CliContextInspection(CliContextFileState.Unreadable, CliContextDocument.Empty)
                : new CliContextInspection(CliContextFileState.Current, Normalize(document));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            return new CliContextInspection(CliContextFileState.Unreadable, CliContextDocument.Empty);
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

        RefuseToReplaceAnUnusableFile();

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        string tempPath = _filePath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            // Owner-only before the first byte on every platform (a create mode on Unix, a protected
            // ACL on Windows), not narrowed after the write.
            using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
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

    /// <summary>
    /// A write replaces the whole file, so it is only safe over a file this build understands (or no
    /// file at all). A newer format version or an unreadable file is left exactly as it is and the
    /// operator is told what to do about it.
    /// </summary>
    private void RefuseToReplaceAnUnusableFile()
    {
        CliContextInspection existing = Inspect();

        switch (existing.State)
        {
            case CliContextFileState.UnsupportedVersion:
                throw new CliContextFileUnusableException(
                    existing.FileVersion is { } version
                        ? $"The saved CLI context at {_filePath} was written in format version "
                            + $"{version.ToString(CultureInfo.InvariantCulture)}, which this build "
                            + $"(version {CliContextDocument.CurrentVersion.ToString(CultureInfo.InvariantCulture)}) "
                            + "does not understand. It was left unchanged. Update Arcanum, or move the "
                            + "file aside to start a new context."
                        : $"The saved CLI context at {_filePath} has no recognizable format version. "
                            + "It was left unchanged. Move the file aside to start a new context.");

            case CliContextFileState.Unreadable:
                throw new CliContextFileUnusableException(
                    $"The saved CLI context at {_filePath} could not be read, so it was left unchanged. "
                    + "Repair it, or move the file aside to start a new context.");
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

internal enum CliContextFileState
{
    Missing,

    Current,

    UnsupportedVersion,

    Unreadable,
}

internal readonly record struct CliContextInspection(
    CliContextFileState State,
    CliContextDocument Document,
    int? FileVersion = null);

/// <summary>
/// A refusal to replace a saved CLI context this build cannot use. It is an <see cref="IOException"/>
/// so every writer that already treats a failed context write as a reportable fault handles it, and
/// its message is the operator-facing explanation.
/// </summary>
internal sealed class CliContextFileUnusableException(string message) : IOException(message);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CliContextDocument))]
internal sealed partial class CliContextJsonContext : JsonSerializerContext;

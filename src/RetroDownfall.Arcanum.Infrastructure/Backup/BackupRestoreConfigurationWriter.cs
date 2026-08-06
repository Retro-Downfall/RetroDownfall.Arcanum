using System.Text;

using System.Text.Json;

using System.Text.Json.Nodes;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal sealed record BackupRestoreConfigurationOutcome(
    long RemappedValues,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Rewrites the staged <c>arcanum.json</c> so the restored configuration describes *this* machine.
/// </summary>
/// <remarks>
/// Two different jobs live here. Path remapping keeps a workspace root usable after a move. Dropping
/// <c>Host:ListenAny</c> is a security decision: the source operator acknowledged exposing that
/// installation on every network interface, and that acknowledgement is not transferable — a restore
/// onto a new machine, possibly a new network, must re-earn it.
/// </remarks>
internal static class BackupRestoreConfigurationWriter
{

    private const long MaximumConfigurationBytes = 8L * 1024 * 1024;

    public static async Task<BackupRestoreConfigurationOutcome> ApplyAsync(
        string configurationPath,
        BackupPathRemapper remapper,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        if (!File.Exists(configurationPath))
        {

            return new BackupRestoreConfigurationOutcome(0, []);

        }

        if (new FileInfo(configurationPath).Length > MaximumConfigurationBytes)
        {

            return new BackupRestoreConfigurationOutcome(
                0,
                ["The restored configuration exceeds the supported size bound and was left unmodified."]);

        }

        string original = await File.ReadAllTextAsync(configurationPath, cancellationToken)
            .ConfigureAwait(false);

        JsonNode? root;

        try
        {

            root = JsonNode.Parse(original);

        }
        catch (JsonException)
        {

            return new BackupRestoreConfigurationOutcome(
                0,
                ["The restored configuration is not valid JSON and was left unmodified."]);

        }

        if (root is not JsonObject document)
        {

            return new BackupRestoreConfigurationOutcome(
                0,
                ["The restored configuration is not a JSON object and was left unmodified."]);

        }

        List<string> warnings = [];

        long remapped = 0;

        if (document["Arcanum"] is JsonObject arcanum)
        {

            if (arcanum["Workspace"] is JsonObject workspace
                && workspace["DefaultRoot"]?.GetValue<string>() is string defaultRoot
                && defaultRoot.Length > 0)
            {

                if (remapper.TryRemap(
                        BackupPathMappingKind.WorkspaceRoot,
                        defaultRoot,
                        out string? mapped))
                {

                    workspace["DefaultRoot"] = JsonValue.Create(mapped);

                    remapped++;

                }
                else if (!Directory.Exists(defaultRoot))
                {

                    warnings.Add(
                        "The restored default workspace root does not exist on this machine and was "
                        + "left as recorded: " + defaultRoot);

                }

            }

            if (arcanum["Host"] is JsonObject host
                && host["ListenAny"] is JsonNode listenAny
                && listenAny.GetValueKind() == JsonValueKind.True)
            {

                host["ListenAny"] = JsonValue.Create(false);

                warnings.Add(
                    "Host:ListenAny was reset to false: an all-interfaces bind acknowledged on the "
                    + "source machine is not authorization on this one.");

            }

        }

        string updated = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        if (!string.Equals(updated, original, StringComparison.Ordinal))
        {

            await WriteOwnerOnlyAsync(configurationPath, updated, cancellationToken).ConfigureAwait(false);

        }

        return new BackupRestoreConfigurationOutcome(remapped, warnings);

    }

    private static async Task WriteOwnerOnlyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        await using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(temporaryPath))
        {

            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken)
                .ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            stream.Flush(flushToDisk: true);

        }

        File.Move(temporaryPath, path, overwrite: true);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

    }

}

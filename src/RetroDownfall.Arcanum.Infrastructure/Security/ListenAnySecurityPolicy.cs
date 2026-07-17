using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// First-run acknowledgement and banner text for all-interfaces HTTPS-only binding.
/// </summary>
public static class ListenAnySecurityPolicy
{

    public const string AcknowledgementEnvironmentVariable = "ARCANUM_LISTEN_ANY_ACK";

    public const string SecurityBanner =
        "SECURITY: Arcanum is binding to all network interfaces over HTTPS only. "
        + "Anyone on your network who obtains the API key has operator-equivalent power. "
        + "Prefer loopback binding or a reverse proxy for remote access. "
        + "Trust the TLS certificate in the OS store; Compendium self-signed certs are loopback-SAN only — remote clients need a certificate whose SAN includes the hostname or IP.";

    public const string InteractiveConfirmPrompt =
        "Bind Arcanum to all network interfaces over HTTPS only?";

    private static string AcknowledgementMarkerPath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, ".listen-any-acknowledged");

    public static bool IsListenAnyAcknowledged()
    {

        if (HasEnvironmentAcknowledgement())
        {

            return true;

        }

        return File.Exists(AcknowledgementMarkerPath);

    }

    public static bool HasEnvironmentAcknowledgement()
    {

        string? env = Environment.GetEnvironmentVariable(AcknowledgementEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(env))
        {

            return false;

        }

        return string.Equals(env.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(env.Trim(), bool.TrueString, StringComparison.OrdinalIgnoreCase);

    }

    public static bool IsHostAnyEnvironmentOverrideSet()
    {

        string? env = Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        return !string.IsNullOrWhiteSpace(env);

    }

    public static void PersistAcknowledgement()
    {

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(AcknowledgementMarkerPath, DateTimeOffset.UtcNow.ToString("O"));

        SecureFilePermissions.ApplyOwnerOnlyFile(AcknowledgementMarkerPath);

    }

    public static bool RequiresInteractiveConfirmation(bool configListenAny)
    {

        if (!ArcanumEnvironment.IsHostAnyEnabled(configListenAny))
        {

            return false;

        }

        if (IsListenAnyAcknowledged())
        {

            return false;

        }

        if (IsHostAnyEnvironmentOverrideSet())
        {

            return false;

        }

        return true;

    }

}

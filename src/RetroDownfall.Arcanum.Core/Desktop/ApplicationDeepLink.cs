using System.Text.Json;

using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Desktop;

/// <summary>Desktop application addressed by an Arcanum launch request.</summary>
public enum DesktopApplication
{

    CommandCenter = 0,

    TheForge = 1,

    Compendium = 2,

}

/// <summary>Opaque server resource carried by a desktop deep link.</summary>
public enum ApplicationResourceKind
{

    None = 0,

    Configuration = 1,

    Session = 2,

    Campaign = 3,

    Spell = 4,

    Prompt = 5,

    Apprentice = 6,

}

/// <summary>Safe initial desktop surface requested by a deep link.</summary>
public enum ApplicationInitialView
{

    CommandCenter = 0,

    Settings = 1,

    Workbench = 2,

    Atelier = 3,

    WarTable = 4,

}

/// <summary>
/// Versioned, credential-free desktop navigation envelope. Resource values are opaque server
/// identifiers; callers must not substitute client filesystem paths or resource content.
/// </summary>
public sealed record ApplicationDeepLink(
    int SchemaVersion,
    DesktopApplication TargetApplication,
    ApplicationResourceKind ResourceKind,
    string? ResourceId = null,
    string? ResourceScopeId = null,
    ApplicationInitialView? InitialView = null,
    string? ConnectionProfileId = null)
{

    public const int CurrentSchemaVersion = 1;

}

/// <summary>AOT-safe JSON codec for the single desktop deep-link process argument.</summary>
public static class ApplicationDeepLinkCodec
{

    public const string ArgumentName = "--arcanum-deep-link";

    public static string Encode(ApplicationDeepLink deepLink)
    {

        ArgumentNullException.ThrowIfNull(deepLink);

        Validate(deepLink);

        return JsonSerializer.Serialize(
            deepLink,
            ApplicationDeepLinkJsonContext.Default.ApplicationDeepLink);

    }

    public static ApplicationDeepLink Decode(string payload)
    {

        if (string.IsNullOrWhiteSpace(payload))
        {

            throw new FormatException("The deep-link payload is empty.");

        }

        ApplicationDeepLink? deepLink;

        try
        {

            deepLink = JsonSerializer.Deserialize(
                payload,
                ApplicationDeepLinkJsonContext.Default.ApplicationDeepLink);

        }
        catch (JsonException ex)
        {

            throw new FormatException("The deep-link payload is not valid JSON.", ex);

        }

        if (deepLink is null)
        {

            throw new FormatException("The deep-link payload is empty.");

        }

        Validate(deepLink);

        return deepLink;

    }

    public static ApplicationDeepLink? ParseArguments(
        IReadOnlyList<string> arguments,
        DesktopApplication expectedApplication)
    {

        ArgumentNullException.ThrowIfNull(arguments);

        int argumentIndex = -1;

        for (int index = 0; index < arguments.Count; index++)
        {

            if (string.Equals(arguments[index], ArgumentName, StringComparison.Ordinal))
            {

                argumentIndex = index;

                break;

            }

        }

        if (argumentIndex < 0)
        {

            return null;

        }

        if (argumentIndex == arguments.Count - 1)
        {

            throw new FormatException($"{ArgumentName} requires one JSON payload argument.");

        }

        ApplicationDeepLink deepLink = Decode(arguments[argumentIndex + 1]);

        if (deepLink.TargetApplication != expectedApplication)
        {

            throw new InvalidOperationException(
                $"The deep-link target {deepLink.TargetApplication} does not match {expectedApplication}.");

        }

        return deepLink;

    }

    private static void Validate(ApplicationDeepLink deepLink)
    {

        if (deepLink.SchemaVersion != ApplicationDeepLink.CurrentSchemaVersion)
        {

            throw new NotSupportedException(
                $"Deep-link schema version {deepLink.SchemaVersion} is not supported; expected {ApplicationDeepLink.CurrentSchemaVersion}.");

        }

        if (!Enum.IsDefined(deepLink.TargetApplication))
        {

            throw new FormatException("The deep-link target application is unknown.");

        }

        if (!Enum.IsDefined(deepLink.ResourceKind))
        {

            throw new FormatException("The deep-link resource kind is unknown.");

        }

        if (deepLink.InitialView is { } initialView && !Enum.IsDefined(initialView))
        {

            throw new FormatException("The deep-link initial view is unknown.");

        }

    }

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]

[JsonSerializable(typeof(ApplicationDeepLink))]

internal sealed partial class ApplicationDeepLinkJsonContext : JsonSerializerContext;

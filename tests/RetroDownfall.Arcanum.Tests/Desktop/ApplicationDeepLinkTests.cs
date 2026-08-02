using System.Reflection;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Desktop;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Desktop;

public sealed class ApplicationDeepLinkTests
{

    [Fact]

    public void Codec_round_trips_hostile_identifiers_as_one_argument()
    {

        const string hostileResourceId = "--profile=\"\u540d\u5b57\"; $(touch /tmp/not-created) path with spaces";

        const string hostileScopeId = "workspace:\u03b1/\u03b2 --another-option='quoted value'";

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Spell,
            hostileResourceId,
            hostileScopeId,
            ApplicationInitialView.Workbench,
            "default");

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        string[] arguments =
        [
            "--theme",
            "dark",
            ApplicationDeepLinkCodec.ArgumentName,
            payload,
        ];

        ApplicationDeepLink decoded = ApplicationDeepLinkCodec.Decode(payload);

        ApplicationDeepLink parsed = Assert.IsType<ApplicationDeepLink>(
            ApplicationDeepLinkCodec.ParseArguments(
                arguments,
                DesktopApplication.TheForge));

        Assert.Equal("--arcanum-deep-link", ApplicationDeepLinkCodec.ArgumentName);

        Assert.Equal(deepLink, decoded);

        Assert.Equal(deepLink, parsed);

        Assert.Equal(payload, arguments[3]);

        Assert.DoesNotContain('\n', payload);

        Assert.DoesNotContain('\r', payload);

        using JsonDocument document = JsonDocument.Parse(payload);

        Assert.Equal(
            ApplicationDeepLink.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());

    }

    [Fact]

    public void Codec_rejects_future_schema_versions()
    {

        ApplicationDeepLink futureDeepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion + 1,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Session,
            Guid.NewGuid().ToString("D"),
            InitialView: ApplicationInitialView.Workbench,
            ConnectionProfileId: "default");

        Exception exception = Assert.ThrowsAny<Exception>(() =>
        {

            string payload = ApplicationDeepLinkCodec.Encode(futureDeepLink);

            _ = ApplicationDeepLinkCodec.Decode(payload);

        });

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Parse_arguments_rejects_a_link_for_another_application()
    {

        ApplicationDeepLink theForgeLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Campaign,
            Guid.NewGuid().ToString("D"),
            InitialView: ApplicationInitialView.Atelier,
            ConnectionProfileId: "default");

        string payload = ApplicationDeepLinkCodec.Encode(theForgeLink);

        string[] arguments =
        [
            ApplicationDeepLinkCodec.ArgumentName,
            payload,
        ];

        Exception exception = Assert.ThrowsAny<Exception>(() =>
            ApplicationDeepLinkCodec.ParseArguments(
                arguments,
                DesktopApplication.Compendium));

        Assert.Contains("target", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Parse_arguments_without_a_deep_link_returns_null()
    {

        ApplicationDeepLink? parsed = ApplicationDeepLinkCodec.ParseArguments(
            ["--theme", "dark"],
            DesktopApplication.TheForge);

        Assert.Null(parsed);

    }

    [Fact]

    public void Envelope_has_only_safe_reference_fields()
    {

        string[] expectedProperties =
        [
            nameof(ApplicationDeepLink.ConnectionProfileId),
            nameof(ApplicationDeepLink.InitialView),
            nameof(ApplicationDeepLink.ResourceId),
            nameof(ApplicationDeepLink.ResourceKind),
            nameof(ApplicationDeepLink.ResourceScopeId),
            nameof(ApplicationDeepLink.SchemaVersion),
            nameof(ApplicationDeepLink.TargetApplication),
        ];

        string[] actualProperties = typeof(ApplicationDeepLink)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedProperties, actualProperties);

        string[] forbiddenFragments =
        [
            "ApiKey",
            "Attachment",
            "Content",
            "Credential",
            "Endpoint",
            "FileSystem",

            "LocalPath",
            "Path",
            "PromptContent",
            "Secret",
        ];

        foreach (string fragment in forbiddenFragments)
        {

            Assert.DoesNotContain(
                actualProperties,
                property => property.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        }

    }

}

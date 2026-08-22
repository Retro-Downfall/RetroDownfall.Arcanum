using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

public sealed class DeepLinkStartupTests
{

    [Fact]
    public void Parse_ValidDeepLink_DecodesAndStripsOnlyPrivateArguments()
    {

        ApplicationDeepLink link = NewLink(ApplicationInitialView.Settings);

        string payload = ApplicationDeepLinkCodec.Encode(link);

        string[] unrelatedArguments =
        [

            "--theme",

            "system default",

            "--renderer=名字",

        ];

        string[] arguments =
        [

            unrelatedArguments[0],

            unrelatedArguments[1],

            ApplicationDeepLinkCodec.ArgumentName,

            payload,

            unrelatedArguments[2],

        ];

        CompendiumStartupArguments startup = CompendiumDeepLinkStartup.Parse(arguments);

        Assert.Equal(link, startup.DeepLink);

        Assert.Equal(unrelatedArguments, startup.AvaloniaArguments);

        Assert.DoesNotContain(payload, startup.AvaloniaArguments);

    }

    [Fact]
    public void Parse_MalformedOrWrongTarget_StripsPayloadAndSafelyDiscardsLink()
    {

        ApplicationDeepLink wrongTarget = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Session,
            "11111111-1111-1111-1111-111111111111",
            InitialView: ApplicationInitialView.Workbench);

        string[] privatePayloads =
        [

            "{not-json:credential=must-not-surface",

            ApplicationDeepLinkCodec.Encode(wrongTarget),

        ];

        foreach (string privatePayload in privatePayloads)
        {

            string[] unrelatedArguments =
            [

                "--theme",

                "dark",

            ];

            string[] arguments =
            [

                unrelatedArguments[0],

                ApplicationDeepLinkCodec.ArgumentName,

                privatePayload,

                unrelatedArguments[1],

            ];

            CompendiumStartupArguments startup = CompendiumDeepLinkStartup.Parse(arguments);

            Assert.Null(startup.DeepLink);

            Assert.Equal(unrelatedArguments, startup.AvaloniaArguments);

            Assert.DoesNotContain(privatePayload, startup.AvaloniaArguments);

        }

    }

    [Fact]
    public void Apply_NullDeepLink_UsesEditionSection()
    {

        ConfigurationViewModel viewModel = CreateViewModel();

        viewModel.SelectedSection = ConfigSection.Security;

        CompendiumDeepLinkStartup.Apply(null, viewModel);

        Assert.Equal(ConfigSection.Edition, viewModel.SelectedSection);

    }

    [Fact]
    public void Apply_SettingsView_UsesEditionSection()
    {

        ConfigurationViewModel viewModel = CreateViewModel();

        viewModel.SelectedSection = ConfigSection.Host;

        ApplicationDeepLink link = NewLink(ApplicationInitialView.Settings);

        CompendiumDeepLinkStartup.Apply(link, viewModel);

        Assert.Equal(ConfigSection.Edition, viewModel.SelectedSection);

    }

    [Fact]
    public void Apply_DefaultView_UsesEditionSection()
    {

        ConfigurationViewModel viewModel = CreateViewModel();

        viewModel.SelectedSection = ConfigSection.Providers;

        ApplicationDeepLink link = NewLink(initialView: null);

        CompendiumDeepLinkStartup.Apply(link, viewModel);

        Assert.Equal(ConfigSection.Edition, viewModel.SelectedSection);

    }

    [Fact]
    public void Apply_UnknownView_FallsBackToEditionSection()
    {

        ConfigurationViewModel viewModel = CreateViewModel();

        viewModel.SelectedSection = ConfigSection.Workspaces;

        ApplicationDeepLink link = NewLink((ApplicationInitialView)999);

        CompendiumDeepLinkStartup.Apply(link, viewModel);

        Assert.Equal(ConfigSection.Edition, viewModel.SelectedSection);

    }

    [Fact]
    public void Apply_MismatchedTarget_FallsBackWithoutUsingOpaqueResourceData()
    {

        ConfigurationViewModel viewModel = CreateViewModel();

        viewModel.SelectedSection = ConfigSection.Cli;

        ApplicationDeepLink link = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Spell,
            "opaque-resource-id",
            "opaque-workspace-id",
            ApplicationInitialView.Workbench);

        CompendiumDeepLinkStartup.Apply(link, viewModel);

        Assert.Equal(ConfigSection.Edition, viewModel.SelectedSection);

    }

    [Fact]
    public void Apply_FutureSchema_FallsBackToEditionSection()
    {

        ConfigurationViewModel viewModel = CreateViewModel();

        viewModel.SelectedSection = ConfigSection.Retention;

        ApplicationDeepLink link = new(
            ApplicationDeepLink.CurrentSchemaVersion + 1,
            DesktopApplication.Compendium,
            ApplicationResourceKind.Configuration,
            InitialView: ApplicationInitialView.Settings);

        CompendiumDeepLinkStartup.Apply(link, viewModel);

        Assert.Equal(ConfigSection.Edition, viewModel.SelectedSection);

    }

    private static ApplicationDeepLink NewLink(ApplicationInitialView? initialView) =>
        new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.Compendium,
            ApplicationResourceKind.Configuration,
            InitialView: initialView);

    private static ConfigurationViewModel CreateViewModel() =>
        new(
            new InMemoryConfigurationStore(),
            new NoopDialogService(),
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance,
            ImmediateArcanumClientMutationBoundary.Instance);

    private sealed class InMemoryConfigurationStore : IArcanumConfigurationStore
    {

        public string ConfigurationFilePath => "memory://arcanum.json";

        public event EventHandler? ExternalChange
        {

            add { }

            remove { }

        }

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(new ArcanumSettings());

        public Task<ConfigurationWriteResult> WriteAsync(
            ArcanumSettings settings,
            CancellationToken ct = default) =>
            Task.FromResult(new ConfigurationWriteResult(true, [], null));

        public void Dispose()
        {
        }

    }

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") =>
            Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string accept = "Yes",
            string cancel = "No") =>
            Task.FromResult(true);

    }

}

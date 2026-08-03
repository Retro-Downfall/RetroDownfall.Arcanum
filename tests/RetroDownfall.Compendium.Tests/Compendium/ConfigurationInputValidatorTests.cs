using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Compendium.Ux.Models;

using RetroDownfall.Compendium.Ux.Services;

using RetroDownfall.Compendium.Ux.ViewModels;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

public sealed class ConfigurationInputValidatorTests
{

    [Theory]

    [InlineData("github_token")]

    [InlineData("GitHub_Token")]

    [InlineData("GITHUB_TOKEN")]

    public void Provider_build_accepts_every_portable_Core_environment_reference(
        string reference)
    {

        ProvidersSectionViewModel.ProviderViewModel provider = new(
            new ProviderSettings
            {

                Name = "Provider",

                CredentialEnvironmentVariable = reference,

            },
            new NoopDialogService());

        ProviderSettings built = provider.Build();

        Assert.Equal(reference, built.CredentialEnvironmentVariable);

    }

    [Fact]

    public void Generic_path_field_accepts_the_documented_tilde_certificate_path()
    {

        SettingDescriptor descriptor = Assert.Single(
            SettingDescriptors.All,
            static candidate => candidate.Key == "host.https.certificatePath");

        GenericSettingFieldViewModel field = new(
            descriptor,
            "~/.config/arcanum/certs/localhost.pfx");

        Assert.False(field.HasError);

    }

    [Fact]

    public void Host_build_accepts_the_supported_loopback_Cors_wildcard()
    {

        HostSectionViewModel host = new();

        host.LoadFrom(new HostSettings());

        host.ListenAny = false;

        host.CorsAllowedOrigins = "*";

        HostSettings built = host.Build();

        Assert.Equal(["*"], built.CorsAllowedOrigins);

    }

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(
            string title,
            string message,
            string cancel = "OK") =>
            Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string accept = "Yes",
            string cancel = "No") =>
            Task.FromResult(true);

    }

}
